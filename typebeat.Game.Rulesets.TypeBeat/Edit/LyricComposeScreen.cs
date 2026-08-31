// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Compose.Components.Timeline;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// type!beat's compose mode: the top waveform timeline (solid waveform, half-strength beat
    /// ticks) sits directly above a thin minimal boundaries band (<see cref="LineBoundariesBand"/>:
    /// line boundaries + word-block ticks, window-mirrored from the waveform); the main area
    /// below is a line list (sweeping text edits) beside the active line's detail/action panel,
    /// which hosts the interactive word-block strip (<see cref="LyricTimeline"/>).
    /// The whole screen is organised around the mapper's loop (listen, nudge, listen):
    /// the active line follows the playhead unless a line is explicitly selected, R replays the
    /// active line with pre-roll and auto-pause, T stamps the focused word's start at the
    /// playhead, Enter stamps the active line's start, Delete removes what is selected (words
    /// within the active line, or the selected lines: see <see cref="deleteSelection"/>).
    ///
    /// Clipboard (Ctrl+C/V via the editor's platform-action plumbing) carries TIMING patterns:
    /// with two or more lines multi-selected, copy takes their internal line timings; otherwise a
    /// word-unit selection copies its unit-run pattern; otherwise the active line's timings.
    /// Paste dispatches on the payload: line timings apply to the current line selection
    /// (broadcast/zip, rebased per target), a unit run applies at the focused word.
    /// </summary>
    [Cached]
    public partial class LyricComposeScreen : EditorScreenWithTimeline, IKeyBindingHandler<PlatformAction>
    {
        [Cached]
        private readonly LyricEditState state = new LyricEditState();

        /// <summary>The shared active/selected-line interaction state (exposed for tests).</summary>
        public LyricEditState EditState => state;

        /// <summary>The tap-timing recording surface (exposed for tests).</summary>
        public TapTimingOverlay TapTiming => tapOverlay;

        /// <summary>Starts or finishes a tap-timing pass, as the bottom bar's Time button does (exposed for tests).</summary>
        public void ToggleTapTiming() => toggleTapTiming();

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        [Resolved]
        private EditorClipboard clipboard { get; set; } = null!;

        private LyricTimingClipboard.LineTimingsPayload? clipboardLines;
        private LyricTimingClipboard.UnitTimingsPayload? clipboardUnits;

        [Resolved]
        private EditorRulesetAction rulesetAction { get; set; } = null!;

        private LineListPanel lineList = null!;
        private TapTimingOverlay tapOverlay = null!;
        private TypeBeatHitObject? lastAutoScrolled;

        /// <summary>
        /// The sample both the word tick and the syllable sub-tick play (exposed for tests). The two
        /// streams used to play different samples (a metronome click for words, a lighter UI notch
        /// for syllables); that timbre split is gone, both streams now share this one sample and are
        /// told apart only by volume.
        /// </summary>
        public const string TickSampleName = @"UI/metronome-tick";

        /// <summary>Volume of the per-word editor tick, audible over the track without masking it.</summary>
        private const double tick_volume = 0.6;

        /// <summary>
        /// Volume of the syllable-boundary sub-tick, clearly subordinate to the word tick, so the
        /// word starts stay the dominant rhythm and the dotted-line subdivisions read as grace notes.
        /// </summary>
        private const double syllable_tick_volume = 0.35;

        /// <summary>Detects which word-unit starts the playhead swept across each running frame.</summary>
        private readonly EditorTickTracker tickTracker = new EditorTickTracker();

        /// <summary>Same crossing detection for syllable-subdivision boundaries (the dotted lines).</summary>
        private readonly EditorTickTracker syllableTickTracker = new EditorTickTracker();

        private Sample? tickSample;
        private Sample? syllableTickSample;

        public LyricComposeScreen()
            : base(EditorScreenMode.Compose)
        {
        }

        [BackgroundDependencyLoader]
        private void load(AudioManager audio)
        {
            // Word starts and syllable boundaries share the same editor metronome click; only the
            // volume tells them apart (see tick_volume / syllable_tick_volume above). Ships in
            // typebeat.Game.Resources under Samples/UI.
            tickSample = audio.Samples.Get(TickSampleName);
            syllableTickSample = audio.Samples.Get(TickSampleName);
        }

        protected override Drawable CreateTimelineContent() => new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                // No per-line bars here any more. The word-block strip directly beneath the
                // waveform carries the lines (select, add, drag); the waveform stays clean.
                new BeatdropMarkerPart(),
            },
        };

        protected override void ConfigureTimeline(TimelineArea timelineArea)
        {
            base.ConfigureTimeline(timelineArea);

            // The waveform is the primary reading surface in lyric compose: show it solid, and
            // pull the beat ticks back to half strength so they stop competing with it.
            timelineArea.Timeline.WaveformOpacityOverride = 1;
            timelineArea.Timeline.TickAlpha = 0.5f;

            // "snap to grid" is a property of the TOP timeline only (the beat ticks are drawn
            // there, and the strip below carries lyric structure rather than beats).
            timelineArea.Timeline.SnapDragSeekToBeat.BindTo(state.SnapToGrid);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // CanPaste tracks whether the clipboard currently holds one of our timing payloads
            // (parse once per content change, not per frame). CanCopy is kept fresh in Update.
            clipboard.Content.BindValueChanged(content =>
            {
                (clipboardLines, clipboardUnits) = LyricTimingClipboard.TryParse(content.NewValue);
                CanPaste.Value = clipboardLines != null || clipboardUnits != null;
            }, true);

            // The bottom bar's ruleset slot, immediately left of Test: tap timing. The editor owns
            // the button, so this screen only publishes a label + callback and withdraws on dispose.
            rulesetAction.Publish("Time", toggleTapTiming);
            tapOverlay.StateChanged += updateTapButton;

            if (changeHandler != null)
                changeHandler.OnStateChange += onHistoryStateChanged;
        }

        #region Undo view snap

        [Resolved(canBeNull: true)]
        private IEditorChangeHandler? changeHandler { get; set; }

        /// <summary>
        /// The map's shape as it was just before an undo/redo key press, or null when no history
        /// step is in flight. Undo restores a whole-beatmap SNAPSHOT (every hit object instance is
        /// replaced), so there is no per-object event to read the changed location from: the two
        /// shapes are diffed instead.
        /// </summary>
        private List<(double Time, string Tag)>? historyMarkers;

        public bool OnPressed(KeyBindingPressEvent<PlatformAction> e)
        {
            // Deliberately never handled here: the editor above performs the undo/redo. This only
            // photographs the map first, so the restore can be diffed against it. A dirty line text
            // box swallows the action before it ever reaches this screen (its own layered undo), so
            // no photograph is taken for those.
            if (e.Action == PlatformAction.Undo || e.Action == PlatformAction.Redo)
            {
                historyMarkers = mapMarkers();

                // Dropped again at the end of the frame: a press that restored nothing (nothing to
                // undo, or a transaction still open) must not pan on some later, unrelated edit.
                Schedule(() => historyMarkers = null);
            }

            // The Delete key. Taken as a PLATFORM ACTION rather than in OnKeyDown (where the other
            // editing hotkeys live) because a focused line-text box must keep its own forward
            // delete: the focused drawable sees the RAW key ahead of the platform-action container,
            // so an OnKeyDown case here would eat the character the box was about to remove. As an
            // action, a focused box's own delete wins and this is never reached.
            if (e.Action == PlatformAction.Delete && !e.Repeat)
                return deleteSelection();

            return false;
        }

        public void OnReleased(KeyBindingReleaseEvent<PlatformAction> e)
        {
        }

        /// <summary>
        /// Fired once the restored state has been applied. With a photograph in hand this pans the
        /// fine-timing strip to the earliest place the two states disagree, which is the edit being
        /// undone. The CARET is left alone: an undo is not a seek. States that are identical, or a
        /// state change that no undo/redo press armed, do nothing.
        /// </summary>
        private void onHistoryStateChanged()
        {
            if (historyMarkers is not List<(double Time, string Tag)> before)
                return;

            historyMarkers = null;

            if (earliestDifference(before, mapMarkers()) is double at)
                state.RequestViewSnap(at);
        }

        /// <summary>
        /// The whole sheet as timed, tagged markers: one per line (its text and window) and one per
        /// word (its text, end and subdivisions). Two markers compare equal only when nothing about
        /// that line or word moved or was retyped, so the symmetric difference of two snapshots is
        /// exactly the set of places an edit touched.
        /// </summary>
        private List<(double Time, string Tag)> mapMarkers()
        {
            var markers = new List<(double Time, string Tag)>();

            foreach (var hitObject in TypeBeatEditorOperations.OrderedLines(EditorBeatmap))
            {
                var line = hitObject.Line;
                markers.Add((line.StartTime, $"L|{line.RawText}|{line.EndTime}|{line.SingEndTime}"));

                foreach (var unit in line.Units)
                    markers.Add((unit.StartTime, $"U|{unit.Text}|{unit.EndTime}|{string.Join(',', unit.SyllableBoundaries)}"));
            }

            return markers;
        }

        /// <summary>
        /// The earliest time carried by a marker present in one snapshot and not the other, or null
        /// when the two describe the same sheet (a metadata-only undo, say, which has no location).
        /// </summary>
        private static double? earliestDifference(List<(double Time, string Tag)> before, List<(double Time, string Tag)> after)
        {
            var beforeSet = new HashSet<(double, string)>(before);
            var afterSet = new HashSet<(double, string)>(after);

            double? earliest = null;

            foreach (var marker in before)
            {
                if (!afterSet.Contains(marker))
                    earliest = earliest is double known ? Math.Min(known, marker.Time) : marker.Time;
            }

            foreach (var marker in after)
            {
                if (!beforeSet.Contains(marker))
                    earliest = earliest is double known ? Math.Min(known, marker.Time) : marker.Time;
            }

            return earliest;
        }

        #endregion

        private void updateTapButton()
        {
            rulesetAction.Text.Value = tapOverlay.Active ? "Finish" : "Time";
            rulesetAction.Armed.Value = tapOverlay.Active;
        }

        /// <summary>
        /// The "Time" button: starts a tap-timing pass, or finishes (commits) the running one.
        ///
        /// <para>SCOPE. The pass times the mapper's selection: a multi-line section from the line
        /// list, or the single selected line, and within a single selected line a word-block
        /// selection narrows it to that run of words. A ctrl-picked selection with gaps is filled in
        /// (the pass covers the contiguous span from its first line to its last), because taps are
        /// inherently continuous in time. With NOTHING selected the scope is the WHOLE SHEET, which
        /// is the fresh-paste case: paste lyrics, press Time, tap the song through.</para>
        /// </summary>
        private void toggleTapTiming()
        {
            // The main content loads asynchronously while the bottom bar is already up; a click in
            // that window has nothing to record into.
            if (!tapOverlay.IsLoaded)
                return;

            if (tapOverlay.Active)
            {
                tapOverlay.Commit();
                return;
            }

            var ordered = TypeBeatEditorOperations.OrderedLines(EditorBeatmap);

            if (ordered.Count == 0)
                return;

            var lines = ordered.Select(o => o.Line).ToList();
            var section = state.SelectedLinesInOrder(ordered);

            int firstLine = 0;
            int lastLine = ordered.Count - 1;
            int firstUnit = 0;
            int lastUnit = lines[^1].Units.Count - 1;

            if (section.Count > 0)
            {
                firstLine = ordered.IndexOf(section[0]);
                lastLine = ordered.IndexOf(section[^1]);
                lastUnit = lines[lastLine].Units.Count - 1;

                // A word-block selection inside one line narrows the pass to that run of words.
                if (firstLine == lastLine && state.ActiveLine.Value == section[0] && state.SelectedUnitIndices.Count > 0)
                {
                    firstUnit = state.SelectedUnitIndices.Min();
                    lastUnit = state.SelectedUnitIndices.Max();
                }
            }

            var queue = TapTimingBuilder.BuildQueue(lines, firstLine, firstUnit, lastLine, lastUnit);

            if (queue.Count == 0)
                return;

            // Start from the first queued WORD, not the line, so a mid-line word run pre-rolls to
            // the right place on a sheet that already carries timing.
            double startFrom = lines[firstLine].Units.Count > firstUnit
                ? lines[firstLine].Units[firstUnit].StartTime
                : lines[firstLine].StartTime;

            // The same span the queue was built from, in hit-object terms: while the pass runs every
            // surface hides the lines outside it, so the mapper reads only the section they are
            // timing. A whole-sheet pass (the fresh-paste default) hides nothing.
            tapOverlay.Begin(lines, queue, startFrom, new TapScope(ordered, firstLine, firstUnit, lastLine, lastUnit));
        }

        public override void Copy()
        {
            var active = state.ActiveLine.Value;

            // Two or more lines multi-selected: the user is operating on lines. A word-unit
            // selection only wins below that (it exists implicitly whenever a word is focused).
            if (state.MultiSelectedLines.Count >= 2)
            {
                var ordered = TypeBeatEditorOperations.OrderedLines(EditorBeatmap).Where(state.MultiSelectedLines.Contains).ToList();
                clipboard.Content.Value = LyricTimingClipboard.Serialize(TypeBeatEditorOperations.CopyLineTimings(ordered));
            }
            else if (active != null && state.SelectedUnitIndices.Count > 0)
            {
                var payload = TypeBeatEditorOperations.CopyUnitTimings(active, state.SelectedUnitIndices);

                if (payload != null)
                    clipboard.Content.Value = LyricTimingClipboard.Serialize(payload);
            }
            else if (active != null)
                clipboard.Content.Value = LyricTimingClipboard.Serialize(TypeBeatEditorOperations.CopyLineTimings(new[] { active }));
        }

        public override void Paste()
        {
            if (clipboardUnits is LyricTimingClipboard.UnitTimingsPayload unitRun)
            {
                if (state.ActiveLine.Value is TypeBeatHitObject line)
                    TypeBeatEditorOperations.PasteUnitTimings(EditorBeatmap, line, Math.Max(state.SelectedUnitIndex.Value, 0), unitRun);
            }
            else if (clipboardLines is LyricTimingClipboard.LineTimingsPayload lines)
            {
                var targets = state.MultiSelectedLines.Count > 0
                    ? TypeBeatEditorOperations.OrderedLines(EditorBeatmap).Where(state.MultiSelectedLines.Contains).ToList()
                    : state.ActiveLine.Value is TypeBeatHitObject single ? new List<TypeBeatHitObject> { single } : new List<TypeBeatHitObject>();

                TypeBeatEditorOperations.PasteLineTimings(EditorBeatmap, targets, lines);
            }
        }

        /// <summary>Height of the thin minimal boundaries band under the waveform timeline.</summary>
        private const float boundaries_band_height = 26;

        /// <summary>
        /// Horizontal inset matching the shared timeline's right-hand columns (35 zoom buttons
        /// + 120 controls in TimelineArea), so the band's extents line up with the waveform
        /// above it and both playheads sit on the same vertical while following playback.
        /// EditorScreenWithTimeline also declares a 90px outer column, but its grid row has no
        /// cell for it (GridContainer sizes columns from Content), so it never applies and must
        /// NOT be counted here.
        /// </summary>
        private const float timeline_right_inset = 155;

        protected override Drawable CreateMainContent() => new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                createComposeGrid(),
                // Recording surface for the "Time" button; hidden (and inert) until a pass starts.
                tapOverlay = new TapTimingOverlay(),
            },
        };

        private Drawable createComposeGrid() => new GridContainer
        {
            RelativeSizeAxes = Axes.Both,
            RowDimensions = new[]
            {
                new Dimension(GridSizeMode.Absolute, boundaries_band_height),
                new Dimension(GridSizeMode.Absolute, 6),
                new Dimension(),
            },
            Content = new[]
            {
                new Drawable[]
                {
                    // The minimal boundaries band sits directly beneath the waveform so line/word
                    // structure reads against the audio; the interactive word strip itself lives
                    // in the detail panel, so the panels keep their height.
                    new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Padding = new MarginPadding { Right = timeline_right_inset },
                        Child = new LineBoundariesBand(),
                    },
                },
                new[] { Empty() },
                new Drawable[]
                {
                    new GridContainer
                    {
                        RelativeSizeAxes = Axes.Both,
                        ColumnDimensions = new[]
                        {
                            new Dimension(GridSizeMode.Relative, 0.42f),
                            new Dimension(GridSizeMode.Absolute, 6),
                            new Dimension(),
                        },
                        Content = new[]
                        {
                            new[]
                            {
                                (Drawable)(lineList = new LineListPanel()),
                                Empty(),
                                new ActiveLineDetailPanel(),
                            },
                        },
                    },
                },
            },
        };

        protected override void Update()
        {
            base.Update();

            // Auto-pause for line/word replay.
            if (state.ReplayStopTime is double stop && editorClock.IsRunning && editorClock.CurrentTime >= stop)
            {
                editorClock.Stop();
                state.ReplayStopTime = null;
            }

            updateNoteTicks();
            updateActiveLine();
        }

        /// <summary>
        /// While the song plays, click as the playhead reaches the start of each note (word unit),
        /// and click the lighter sub-tick at each syllable-subdivision boundary (the dotted lines).
        /// A time that is both plays only the word tick (<see cref="EditorTickTimes.Collect"/>
        /// dedupes). Only crossings inside the current frame's window fire; paused/scrubbing and
        /// seek jumps are suppressed by <see cref="EditorTickTracker"/>. Live hit objects are
        /// polled every frame so concurrent line edits (undo storms rebuild instances) are always
        /// reflected.
        /// </summary>
        private void updateNoteTicks()
        {
            // During a tap-timing pass the map still holds the OLD timing; clicking it over the
            // mapper's taps would fight the ear they are timing by. The overlay clicks each tap instead.
            if (tapOverlay.Active)
            {
                tickTracker.Reset();
                syllableTickTracker.Reset();
                return;
            }

            if (!editorClock.IsRunning)
            {
                // Paused/stopped: forget the frame time so resuming (or a scrub target) never bursts.
                tickTracker.Reset();
                syllableTickTracker.Reset();
                return;
            }

            var (wordStarts, syllableBoundaries) = EditorTickTimes.Collect(
                EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Select(o => o.Line));

            double now = editorClock.CurrentTime;

            var crossedWords = tickTracker.Advance(now, wordStarts);
            var crossedSyllables = syllableTickTracker.Advance(now, syllableBoundaries);

            for (int i = 0; i < crossedWords.Count; i++)
                playTick(tickSample, tick_volume);

            for (int i = 0; i < crossedSyllables.Count; i++)
                playTick(syllableTickSample, syllable_tick_volume);
        }

        private static void playTick(Sample? sample, double volume)
        {
            var channel = sample?.GetChannel();

            if (channel == null)
                return;

            channel.Volume.Value = volume;
            channel.Play();
        }

        private void updateActiveLine()
        {
            // Copy is meaningful whenever any line exists to take timings from.
            CanCopy.Value = state.ActiveLine.Value != null || state.MultiSelectedLines.Count > 0;

            if (state.InteractionPinned)
                return;

            var ordered = TypeBeatEditorOperations.OrderedLines(EditorBeatmap);

            // Undo/redo replaces every hit object instance: re-bind a stale selection by index.
            if (state.SelectedLine.Value is TypeBeatHitObject selected && !EditorBeatmap.HitObjects.Contains(selected))
                state.SelectedLine.Value = ordered.FirstOrDefault(o => o.LineIndex == selected.LineIndex);

            // Same rebind for the multi-selection and its range anchor: map stale instances by
            // index, drop vanished lines.
            state.RebindMultiSelection(ordered, o => EditorBeatmap.HitObjects.Contains(o));

            var active = state.SelectedLine.Value;

            // Playback drives the surface: while the song is running the active line tracks the
            // playhead so the word blocks advance with the music, even if a line was selected.
            // A lingering PRIMARY selection is dropped once the song moves onto a different line,
            // so pausing keeps the line you just heard instead of snapping back. A multi-line
            // SECTION survives playback: the mapper marks a section then listens to it before
            // acting on it, and Escape (or any plain click) is the way to drop it. While paused,
            // an explicit selection wins; with none, the playhead line is shown.
            if (ordered.Count > 0 && (active == null || editorClock.IsRunning))
            {
                double now = editorClock.CurrentTime;
                var playheadLine = ordered.LastOrDefault(o => o.Line.StartTime <= now) ?? ordered[0];

                if (editorClock.IsRunning && active != null && active != playheadLine)
                    state.SelectedLine.Value = null;

                active = playheadLine;
            }

            if (state.ActiveLine.Value != active)
            {
                state.ActiveLine.Value = active;

                // Reset word focus on line change; keep the list following along.
                state.ClearUnitSelection();

                if (active != null && lastAutoScrolled != active)
                {
                    lastAutoScrolled = active;
                    lineList.ScrollToActive();
                }
            }
        }

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            // Record-then-commit: no stamping hotkey may mutate the beatmap mid-pass. The overlay
            // holds focus and normally eats these first; this is the belt-and-braces guard.
            if (tapOverlay.Active)
                return base.OnKeyDown(e);

            if (e.Repeat || e.ControlPressed || e.AltPressed || e.SuperPressed)
                return base.OnKeyDown(e);

            var line = state.ActiveLine.Value;

            switch (e.Key)
            {
                case Key.R when line != null:
                    editorClock.Seek(System.Math.Max(0, line.Line.StartTime - 600));
                    state.ReplayStopTime = line.Line.EndTime + 200;
                    editorClock.Start();
                    return true;

                case Key.Enter when line != null:
                case Key.KeypadEnter when line != null:
                    // Stamp the line boundary at the playhead (moves prev line's end too).
                    TypeBeatEditorOperations.SetLineStart(EditorBeatmap, line, editorClock.CurrentTime);
                    return true;

                case Key.T when line != null:
                {
                    // Tap-to-time: stamp the focused word (or the first) and advance focus.
                    int index = state.SelectedUnitIndex.Value < 0 ? 0 : state.SelectedUnitIndex.Value;

                    if (index < line.Line.Units.Count)
                    {
                        TypeBeatEditorOperations.StampUnitStart(EditorBeatmap, line, index, editorClock.CurrentTime);
                        state.SelectUnit(index + 1 < line.Line.Units.Count ? index + 1 : -1);
                    }

                    return true;
                }

                case Key.S when line != null && state.SelectedUnitIndex.Value > 0:
                    TypeBeatEditorOperations.SplitLine(EditorBeatmap, line, state.SelectedUnitIndex.Value);
                    return true;

                case Key.M when line != null:
                    TypeBeatEditorOperations.MergeWithNext(EditorBeatmap, line);
                    return true;

                case Key.D when line != null && (state.SelectedUnitIndices.Count > 0 || state.SelectedUnitIndex.Value >= 0):
                    subdivideSelectedWords(line);
                    return true;

                case Key.Escape when state.SelectedLine.Value != null || state.MultiSelectedLines.Count > 0:
                    // Back to playhead-follow, dropping any multi-selection with it.
                    state.SelectedLine.Value = null;
                    state.ClearMultiLineSelection();
                    return true;
            }

            return base.OnKeyDown(e);
        }

        /// <summary>
        /// Deletes the current selection, the same mutations the panel's own buttons make: selected
        /// WORDS go through <see cref="TypeBeatEditorOperations.RemoveWords"/> ("remove word"),
        /// selected LINES through <see cref="TypeBeatEditorOperations.DeleteLines"/> ("delete line"),
        /// either way as one undo step.
        ///
        /// <para>Precedence is <see cref="Copy"/>'s: two or more lines multi-selected means the user
        /// is operating on lines; below that a word selection wins (one exists implicitly whenever a
        /// word is focused); below that the explicitly selected line. Nothing EXPLICITLY selected
        /// deletes NOTHING: with no selection the active line is merely the one the playhead is
        /// passing through, and a stray Delete must not eat it.</para>
        ///
        /// <para>Returns whether anything was deleted, so an unhandled press stays available to the
        /// rest of the editor.</para>
        /// </summary>
        private bool deleteSelection()
        {
            // Record-then-commit: no hotkey may mutate the beatmap mid-pass (the overlay swallows
            // this action for the duration too; this is the belt-and-braces guard).
            if (tapOverlay.Active)
                return false;

            var active = state.ActiveLine.Value;

            if (state.MultiSelectedLines.Count < 2 && active != null)
            {
                int words = TypeBeatEditorOperations.WordCount(active.Line);
                int[] targets = state.SelectedUnitsInOrder(words);

                if (targets.Length > 0)
                {
                    // A selection covering the WHOLE line is refused exactly as the greyed-out
                    // "remove word" button refuses it (the format has no wordless line). It does not
                    // escalate to deleting the line: that is a bigger edit than the one asked for.
                    if (targets.Length >= words)
                        return false;

                    TypeBeatEditorOperations.RemoveWords(EditorBeatmap, active, targets);

                    // Keep the focus where the deletion happened, as the button does.
                    state.SelectUnit(Math.Min(targets[0], active.Line.Units.Count - 1));
                    return true;
                }
            }

            if (state.SelectedLine.Value == null && state.MultiSelectedLines.Count == 0)
                return false;

            var lines = state.SelectedLinesInOrder(TypeBeatEditorOperations.OrderedLines(EditorBeatmap));

            if (lines.Count == 0)
                return false;

            TypeBeatEditorOperations.DeleteLines(EditorBeatmap, lines);

            // Nothing selected is left to point at; back to playhead-follow, as "delete line" does.
            state.SelectedLine.Value = null;
            state.ClearMultiLineSelection();
            return true;
        }

        /// <summary>
        /// Adds one syllable subdivision to every selected word of <paramref name="line"/> (the
        /// primary word alone when there is no multi-selection), as a single undo. Each call bisects
        /// the widest remaining segment, so pressing D repeatedly keeps splitting.
        /// </summary>
        private void subdivideSelectedWords(TypeBeatHitObject line)
        {
            int[] targets = state.SelectedUnitIndices.Count > 0
                ? state.SelectedUnitIndices.OrderBy(i => i).ToArray()
                : state.SelectedUnitIndex.Value >= 0
                    ? new[] { state.SelectedUnitIndex.Value }
                    : System.Array.Empty<int>();

            if (targets.Length == 0)
                return;

            EditorBeatmap.BeginChange();

            foreach (int i in targets)
                TypeBeatEditorOperations.AddSyllableBoundary(EditorBeatmap, line, i);

            EditorBeatmap.EndChange();
        }

        protected override void Dispose(bool isDisposing)
        {
            // The bottom-bar button belongs to the editor, not to this screen: leaving compose just
            // empties the slot. Nothing has to be removed from the game's drawable tree.
            rulesetAction.Withdraw();

            if (changeHandler != null)
                changeHandler.OnStateChange -= onHistoryStateChanged;

            base.Dispose(isDisposing);
        }
    }
}
