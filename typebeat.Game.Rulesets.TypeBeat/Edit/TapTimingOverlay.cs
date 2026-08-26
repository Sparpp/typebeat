// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Input;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using osuTK;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The tap-timing recording surface: the visual lyric queue plus every key the pass uses.
    /// Hidden until the "Time" button in the bottom bar starts a session.
    ///
    /// <para>RECORD THEN COMMIT. Starting a session mutates nothing: it snapshots the current lines,
    /// builds a queue of the word slots the mapper selected, and starts the song. Each tap appends
    /// one song time to a plain list (<see cref="TapTimingSession"/>), draws a ghost marker on both
    /// timeline surfaces, and advances the queue so the next word snaps onto the next marker.
    /// Cancelling discards the list and leaves no trace, not even an undo entry. Committing runs the
    /// whole list through <see cref="TapTimingBuilder"/> once and lands it via
    /// <see cref="TypeBeatEditorOperations.ReplaceLines"/>: exactly one undo step.</para>
    ///
    /// <para>KEYS. Space, Enter or the keypad Enter tap the next syllable. P pauses and resumes.
    /// Backspace drops the last tap. Escape cancels the whole pass. The overlay takes keyboard
    /// FOCUS while recording, which is what lets it claim Space ahead of the bottom bar's
    /// play/pause. Seeking backwards (drag the waveform, click a timeline) drops every tap at or
    /// after the seek point, so rewind-and-retry needs no extra gesture.</para>
    /// </summary>
    public partial class TapTimingOverlay : CompositeDrawable, IKeyBindingHandler<PlatformAction>
    {
        /// <summary>How far before the first word the playhead is parked when a session starts.</summary>
        public const double PRE_ROLL_MS = 2000;

        /// <summary>Words shown behind and ahead of the one being tapped.</summary>
        private const int trail = 3;
        private const int lookahead = 8;

        /// <summary>Slack on the backward-seek check, to absorb clock jitter (see <see cref="Update"/>).</summary>
        private const double seek_tolerance_ms = 20;

        [Resolved]
        private EditorBeatmap editorBeatmap { get; set; } = null!;

        [Resolved]
        private LyricEditState state { get; set; } = null!;

        [Resolved]
        private EditorClock editorClock { get; set; } = null!;

        private readonly FillFlowContainer<WordChip> chips;
        private readonly OsuSpriteText status;

        private Sample? tapSample;

        /// <summary>The live session, or null when not recording.</summary>
        public TapTimingSession? Session { get; private set; }

        /// <summary>Whether a recording is in progress.</summary>
        public bool Active => Session != null;

        /// <summary>Raised whenever a session starts or ends, so the button label can follow.</summary>
        public Action? StateChanged;

        public TapTimingOverlay()
        {
            RelativeSizeAxes = Axes.X;
            Height = 92;
            Anchor = Anchor.BottomLeft;
            Origin = Anchor.BottomLeft;
            Alpha = 0;

            InternalChildren = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = TypeBeatStyle.PanelBackground,
                    Alpha = 0.94f,
                },
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Padding = new MarginPadding { Horizontal = 12, Vertical = 8 },
                    Spacing = new Vector2(0, 4),
                    Children = new Drawable[]
                    {
                        status = new OsuSpriteText
                        {
                            Font = TypeBeatStyle.Mono(13),
                            Colour = TypeBeatStyle.Caret,
                        },
                        chips = new FillFlowContainer<WordChip>
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 34,
                            Direction = FillDirection.Horizontal,
                            Spacing = new Vector2(6, 0),
                        },
                        new OsuSpriteText
                        {
                            Font = TypeBeatStyle.Mono(11),
                            Colour = TypeBeatStyle.UntypedChar,
                            Text = "space / enter = tap the next syllable    p = pause or resume    backspace = undo a tap    "
                                   + "seek back = drop the taps after that point    esc = cancel    Finish = commit (one undo step)",
                        },
                    },
                },
            };

            for (int i = 0; i < trail + 1 + lookahead; i++)
                chips.Add(new WordChip());
        }

        [BackgroundDependencyLoader]
        private void load(AudioManager audio)
        {
            // Same click the editor's word ticks use, so a tap sounds like the thing it is stamping.
            tapSample = audio.Samples.Get(@"UI/metronome-tick");
        }

        /// <summary>
        /// Starts a recording over <paramref name="queue"/>, taken against a snapshot of
        /// <paramref name="lines"/>. Parks the playhead a pre-roll before the first queued word and
        /// starts the song. Nothing is written to the beatmap.
        /// </summary>
        public void Begin(IReadOnlyList<LyricLine> lines, IReadOnlyList<TapTarget> queue, double startFrom, TapScope scope)
        {
            if (queue.Count == 0)
                return;

            Session = new TapTimingSession(lines, queue);
            state.TapSession = Session;

            // Every surface that renders lyric content hides what this scope does not cover, for as
            // long as the pass runs. Cleared in end(), which every exit path goes through.
            state.TapScope = scope;

            // Pin the surface: playhead-follow must not reshuffle the active line or drop the
            // section the mapper is timing while the song runs under the pass.
            state.BeginInteraction();

            // Shown immediately rather than faded in: focus is only granted to a PRESENT drawable,
            // and claiming focus (which is what wins Space back from the bottom bar's play/pause)
            // has to happen in the same frame the pass starts.
            Alpha = 1;

            editorClock.Seek(Math.Max(0, startFrom - PRE_ROLL_MS));
            editorClock.Start();

            GetContainingFocusManager()?.ChangeFocus(this);
            StateChanged?.Invoke();
        }

        /// <summary>Discards the recording. The beatmap was never touched, so there is nothing to undo.</summary>
        public void Cancel()
        {
            if (!Active)
                return;

            end();
        }

        /// <summary>
        /// Commits the recording as ONE undo step. A pass with no taps is a cancel (there is nothing
        /// to commit and an empty transaction would still cost the mapper an undo press).
        /// </summary>
        public void Commit()
        {
            if (Session is not TapTimingSession session)
                return;

            if (session.TappedCount == 0)
            {
                end();
                return;
            }

            var built = session.BuildCommit();

            editorClock.Stop();
            TypeBeatEditorOperations.ReplaceLines(editorBeatmap, built, TypeBeatEditorOperations.InferGranularity(built));

            end();
        }

        private void end()
        {
            Session = null;
            state.TapSession = null;
            // The hidden lines come back exactly here, so Finish, Escape and every other exit path
            // restore the sheet identically.
            state.TapScope = null;
            state.EndInteraction();

            this.FadeOut(120, Easing.OutQuint);

            // AcceptsFocus is false again, so contention hands focus back to whatever else wants it.
            if (HasFocus)
                GetContainingFocusManager()?.TriggerFocusContention(this);

            StateChanged?.Invoke();
        }

        public override bool AcceptsFocus => Active;

        // Keep the focus for the whole pass, so clicking a timeline to seek does not hand Space back
        // to the bottom bar's play/pause.
        public override bool RequestsFocus => Active;

        protected override void Update()
        {
            base.Update();

            if (Session is not TapTimingSession session)
                return;

            // A backward seek (waveform drag, timeline click, rewind) drops the taps it undid. The
            // tolerance is well under the minimum gap between two real taps, so it can only ever
            // absorb clock jitter, never a tap the mapper just made at the current time.
            session.TruncateFrom(editorClock.CurrentTime + seek_tolerance_ms);

            int done = session.TappedCount;
            int total = session.Queue.Count;
            var target = session.NextTarget;

            status.Text = session.QueueComplete
                ? $"tap timing: {done}/{total} taps, queue complete, press Finish to commit"
                : $"tap timing: {done}/{total} taps, line {(target?.LineIndex ?? 0) + 1}"
                  + (editorClock.IsRunning ? string.Empty : ", PAUSED");

            for (int i = 0; i < chips.Count; i++)
            {
                int index = done - trail + i;
                chips[i].Set(session, index, done);
            }
        }

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (!Active || e.Repeat)
                return base.OnKeyDown(e);

            switch (e.Key)
            {
                case Key.Space:
                case Key.Enter:
                case Key.KeypadEnter:
                    tap();
                    return true;

                case Key.P:
                    if (editorClock.IsRunning)
                        editorClock.Stop();
                    else
                        editorClock.Start();
                    return true;

                case Key.BackSpace:
                    Session?.UndoLastTap();
                    return true;

                case Key.Escape:
                    Cancel();
                    return true;
            }

            // Every other key is swallowed too: the compose screen's own stamping hotkeys (T, Enter,
            // S, M, D) would mutate the beatmap mid-pass, which is exactly what record-then-commit
            // exists to avoid.
            return true;
        }

        /// <summary>
        /// The blanket swallow above only protects the pass while the overlay HOLDS FOCUS (the
        /// focused drawable sees the raw key ahead of the platform-action container). Focus can be
        /// stolen mid-pass (clicking a line row's text box, for one), and then Ctrl+Z is dispatched
        /// as a platform action that never touches <see cref="OnKeyDown"/>: it would reach the
        /// editor, mutate the sheet under the recording, and be silently clobbered again when
        /// Finish commits the pre-pass snapshot. Record-then-commit means NOTHING may mutate the
        /// beatmap mid-pass, so the mutating actions are swallowed here for the duration
        /// regardless of focus; non-mutating ones (Copy, Save) pass through. Undo works again the
        /// moment the pass ends, whichever way it exits.
        /// </summary>
        public bool OnPressed(KeyBindingPressEvent<PlatformAction> e)
        {
            if (!Active)
                return false;

            switch (e.Action)
            {
                case PlatformAction.Undo:
                case PlatformAction.Redo:
                case PlatformAction.Cut:
                case PlatformAction.Paste:
                    return true;
            }

            return false;
        }

        public void OnReleased(KeyBindingReleaseEvent<PlatformAction> e)
        {
        }

        private void tap()
        {
            if (Session is not TapTimingSession session)
                return;

            if (!session.Tap(editorClock.CurrentTime))
                return;

            var channel = tapSample?.GetChannel();

            if (channel != null)
            {
                channel.Volume.Value = 0.6;
                channel.Play();
            }

            // The queue completing is the natural end of the pass: hold the song so the mapper is
            // not left listening past the section they timed.
            if (session.QueueComplete)
                editorClock.Stop();
        }

        /// <summary>
        /// One word in the visual queue. Past words carry the song time they snapped to, the current
        /// word is lit, and the words ahead recede. A word that opens a new lyric line is marked so
        /// the mapper can see the line change coming.
        /// </summary>
        private partial class WordChip : CompositeDrawable
        {
            private readonly Box background;
            private readonly OsuSpriteText text;
            private readonly OsuSpriteText time;

            public WordChip()
            {
                AutoSizeAxes = Axes.X;
                RelativeSizeAxes = Axes.Y;
                Masking = true;
                CornerRadius = 4;
                Alpha = 0;

                InternalChildren = new Drawable[]
                {
                    background = new Box { RelativeSizeAxes = Axes.Both, Colour = TypeBeatStyle.Background },
                    new FillFlowContainer
                    {
                        AutoSizeAxes = Axes.Both,
                        Direction = FillDirection.Vertical,
                        Padding = new MarginPadding { Horizontal = 8, Vertical = 3 },
                        Children = new Drawable[]
                        {
                            text = new OsuSpriteText { Font = TypeBeatStyle.Mono(15) },
                            time = new OsuSpriteText { Font = TypeBeatStyle.Mono(9), Colour = TypeBeatStyle.UntypedChar },
                        },
                    },
                };
            }

            public void Set(TapTimingSession session, int index, int tapped)
            {
                if (index < 0 || index >= session.Queue.Count)
                {
                    Alpha = 0;
                    return;
                }

                Alpha = 1;

                bool done = index < tapped;
                bool current = index == tapped;

                // Hyphen-split chips: a subdivided word appears as one chip per syllable, carrying
                // the exact char run that syllable drives, and every chip after the word's first is
                // prefixed with the hyphen that says "still the same word". So "remember me" with
                // remember split three ways reads  / rem  -emb  -er  me : four chips, four taps,
                // and the mapper can see the word continuing rather than a new word arriving.
                bool wordStart = session.StartsWord(index);

                text.Text = (session.StartsLine(index) ? "/ " : string.Empty)
                            + (wordStart ? string.Empty : "-")
                            + session.SyllableTextAt(index);

                text.Colour = current ? TypeBeatStyle.Caret : done ? TypeBeatStyle.SungAccent : TypeBeatStyle.TypedChar;

                // The tap time IS the snap: a word that has been tapped shows exactly where it landed.
                time.Text = done ? $"{session.Taps[index]:0}" : string.Empty;

                background.Colour = current ? TypeBeatStyle.PanelBackground.Lighten(0.6f) : TypeBeatStyle.Background;
                background.Alpha = done ? 0.5f : 0.9f;
            }
        }
    }
}
