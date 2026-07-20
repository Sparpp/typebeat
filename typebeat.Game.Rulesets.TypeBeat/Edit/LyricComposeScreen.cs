// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Events;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// type!beat's compose mode: the top waveform timeline carries per-line overview bars; the
    /// main area is a line list (sweeping text edits) beside the active line's fine-timing
    /// surface. The whole screen is organised around the mapper's loop — listen, nudge, listen:
    /// the active line follows the playhead unless a line is explicitly selected, R replays the
    /// active line with pre-roll and auto-pause, T stamps the focused word's start at the
    /// playhead, Enter stamps the active line's start.
    ///
    /// Clipboard (Ctrl+C/V via the editor's platform-action plumbing) carries TIMING patterns:
    /// with two or more lines multi-selected, copy takes their internal line timings; otherwise a
    /// word-unit selection copies its unit-run pattern; otherwise the active line's timings.
    /// Paste dispatches on the payload — line timings apply to the current line selection
    /// (broadcast/zip, rebased per target), a unit run applies at the focused word.
    /// </summary>
    [Cached]
    public partial class LyricComposeScreen : EditorScreenWithTimeline
    {
        [Cached]
        private readonly LyricEditState state = new LyricEditState();

        /// <summary>The shared active/selected-line interaction state (exposed for tests).</summary>
        public LyricEditState EditState => state;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        [Resolved]
        private EditorClipboard clipboard { get; set; } = null!;

        private LyricTimingClipboard.LineTimingsPayload? clipboardLines;
        private LyricTimingClipboard.UnitTimingsPayload? clipboardUnits;

        private LineListPanel lineList = null!;
        private TypeBeatHitObject? lastAutoScrolled;

        public LyricComposeScreen()
            : base(EditorScreenMode.Compose)
        {
        }

        protected override Drawable CreateTimelineContent() => new Container
        {
            RelativeSizeAxes = Axes.Both,
            Children = new Drawable[]
            {
                new LineOverviewPart(),
                new BeatdropMarkerPart(),
            },
        };

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

        protected override Drawable CreateMainContent() => new GridContainer
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

            updateActiveLine();
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

            // Same rebind for the multi-selection: map stale instances by index, drop vanished lines.
            if (state.MultiSelectedLines.Count > 0 && state.MultiSelectedLines.Any(o => !EditorBeatmap.HitObjects.Contains(o)))
            {
                var rebound = state.MultiSelectedLines
                                   .Select(o => EditorBeatmap.HitObjects.Contains(o) ? o : ordered.FirstOrDefault(n => n.LineIndex == o.LineIndex))
                                   .Where(o => o != null)
                                   .Select(o => o!)
                                   .ToList();

                state.MultiSelectedLines.Clear();

                foreach (var o in rebound)
                    state.MultiSelectedLines.Add(o);
            }

            var active = state.SelectedLine.Value;

            // Playback drives the surface: while the song is running the active line tracks the
            // playhead so the word blocks advance with the music, even if a line was selected.
            // A lingering selection is dropped once the song moves onto a different line, so
            // pausing keeps the line you just heard instead of snapping back. While paused, an
            // explicit selection wins; with none, the playhead line is shown.
            if (ordered.Count > 0 && (active == null || editorClock.IsRunning))
            {
                double now = editorClock.CurrentTime;
                var playheadLine = ordered.LastOrDefault(o => o.Line.StartTime <= now) ?? ordered[0];

                if (editorClock.IsRunning && active != null && active != playheadLine)
                {
                    state.SelectedLine.Value = null;
                    state.ClearMultiLineSelection(); // playback moved on — the whole selection is stale.
                }

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

                case Key.Escape when state.SelectedLine.Value != null || state.MultiSelectedLines.Count > 0:
                    // Back to playhead-follow, dropping any multi-selection with it.
                    state.SelectedLine.Value = null;
                    state.ClearMultiLineSelection();
                    return true;
            }

            return base.OnKeyDown(e);
        }
    }
}
