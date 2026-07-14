// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/UI/LyricStage.cs.
// Constant names restyled; nullable annotations added for the fork's hard-error nullability.

using System;
using osu.Framework.Allocation;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The 3-line monkeytype stack (previous faded / active centre / next dimmed) with
    /// eased scroll on line change. Owns both carets and subscribes to engine events.
    /// Reads the inherited gameplay clock directly via <c>Time.Current</c> — in gameplay it
    /// must be mounted under the playfield's lyric-offset clock container so its notion of
    /// time matches the engine feed. It never calls <c>engine.Update</c>.
    /// </summary>
    public partial class LyricStage : CompositeDrawable
    {
        private const float line_gap = 96f;

        // The "get ready" cue: a bar under the next line's first char that depletes over the
        // final lead-in before its first word is sung. Sized/positioned by direct per-frame
        // sets (no transforms — must behave under frozen/scrubbed gameplay clocks).
        private const double approach_lead_ms = 1500;
        private const float approach_bar_max_width = 140;
        private const float approach_bar_height = 4;

        private readonly TypingEngine engine;

        private Container lineContainer = null!;
        private LyricLineDisplay[] displays = Array.Empty<LyricLineDisplay>();
        private Caret playerCaret = null!;
        private Caret sungCaret = null!;
        private Box approachBar = null!;
        private Container wrongKeyLayer = null!;

        private int wrongKeyPopupDirection = 1;

        // int.MinValue = nothing laid out; -1 = pre-roll; int.MaxValue = finished.
        private int laidOutFocus = int.MinValue;
        private bool pendingSnap;
        private bool caretsVisible;

        public LyricStage(TypingEngine engine)
        {
            this.engine = engine;
            RelativeSizeAxes = Axes.Both;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            var lines = engine.Lines;
            displays = new LyricLineDisplay[lines.Count];

            lineContainer = new Container { RelativeSizeAxes = Axes.Both };

            for (int i = 0; i < lines.Count; i++)
            {
                var d = new LyricLineDisplay(lines[i])
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Alpha = 0f,
                };
                displays[i] = d;
                lineContainer.Add(d);
            }

            // Carets are positioned via absolute points in this stage's top-left-origin
            // local space (from ToSpaceOfOtherDrawable), so they must anchor top-left.
            playerCaret = new Caret(TypeBeatStyle.Caret, TypeBeatStyle.CARET_DAMP_HALF_TIME, blinks: true)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Height = TypeBeatStyle.LYRIC_FONT_SIZE,
                Alpha = 0f,
            };
            sungCaret = new Caret(TypeBeatStyle.SungAccent, TypeBeatStyle.SUNG_DAMP_HALF_TIME, blinks: false)
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Height = TypeBeatStyle.LYRIC_FONT_SIZE,
                Alpha = 0f,
            };

            approachBar = new Box
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Colour = TypeBeatStyle.SungAccent,
                Height = approach_bar_height,
                Alpha = 0f,
            };

            wrongKeyLayer = new Container { RelativeSizeAxes = Axes.Both };

            InternalChildren = new Drawable[] { lineContainer, approachBar, sungCaret, playerCaret, wrongKeyLayer };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            engine.LineActivated += onLineActivated;
            engine.CharJudged += onCharJudged;
            engine.LineSealed += onLineSealed;
            engine.WrongKeyRejected += onWrongKeyRejected;
        }

        /// <summary>
        /// A rejected wrong key never enters the line — instead the offending letter pops up
        /// beside the caret (alternating sides), falls away and fades. Purely cosmetic juice;
        /// transforms run on the gameplay clock like every other stage animation.
        /// </summary>
        private void onWrongKeyRejected(char c)
        {
            wrongKeyPopupDirection = -wrongKeyPopupDirection;
            int dir = wrongKeyPopupDirection;

            var letter = new OsuSpriteText
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.Centre,
                Font = TypeBeatStyle.Mono(30),
                Colour = TypeBeatStyle.ErrorChar,
                Text = (c == ' ' ? '_' : c).ToString(),
                Position = playerCaret.Position + new Vector2(dir * 34, -4),
            };

            wrongKeyLayer.Add(letter);

            letter.MoveToOffset(new Vector2(dir * 18, -30), 140, Easing.OutQuint)
                  .Then()
                  .MoveToOffset(new Vector2(dir * 12, 110), 460, Easing.InQuad);
            letter.RotateTo(dir * 18, 600, Easing.OutQuint);
            letter.Delay(140).FadeOut(460, Easing.InQuad);
            letter.Expire();
        }

        private void onLineActivated(int index)
        {
            relayout(index, animate: true);
            pendingSnap = true;
        }

        private void onCharJudged(CharJudgement judgement)
        {
            if (judgement.LineIndex >= 0 && judgement.LineIndex < displays.Length)
            {
                var d = displays[judgement.LineIndex];
                d.RefreshCell(judgement.CellIndex);
                d.PlayJudgementFeedback(judgement);
            }

            playerCaret.NotifyTyped();
        }

        private void onLineSealed(LineSealResult result)
        {
            if (result.LineIndex >= 0 && result.LineIndex < displays.Length)
                refreshDisplayCells(result.LineIndex);
        }

        protected override void Update()
        {
            base.Update();

            int active = engine.ActiveLineIndex;

            if (active >= 0 && active < displays.Length)
            {
                // Safety net in case the activation event was missed (e.g. clock scrubbing in tests).
                if (laidOutFocus != active)
                {
                    relayout(active, animate: true);
                    pendingSnap = true;
                }

                var d = displays[active];

                // Player caret follows the typing caret index.
                int caretIndex = engine.CaretIndex;
                Vector2 playerPoint = d.ToSpaceOfOtherDrawable(d.PositionOfCell(caretIndex), this);
                playerCaret.Height = d.LineHeight;

                if (pendingSnap)
                {
                    playerCaret.SnapTo(playerPoint);
                    pendingSnap = false;
                }
                else
                {
                    playerCaret.MoveToTarget(playerPoint);
                }

                // Sung caret + underline sweep follow the vocal position.
                double sung = d.Line.SungPositionAt(Time.Current);
                d.SetSungPosition(sung);
                Vector2 sungPoint = d.ToSpaceOfOtherDrawable(d.SungPositionPoint(sung), this);
                sungCaret.Height = d.LineHeight;
                sungCaret.MoveToTarget(sungPoint);

                refreshVisible(active);
                setCaretsVisible(!engine.IsLineComplete && !engine.IsFinished);
            }
            else if (!engine.IsFinished)
            {
                // Pre-roll / gap before the first line: show line 0 dimmed as the upcoming line.
                if (laidOutFocus != -1)
                {
                    relayoutPreRoll();
                    laidOutFocus = -1;
                }

                setCaretsVisible(false);
            }
            else
            {
                if (laidOutFocus != int.MaxValue)
                {
                    foreach (var d in displays)
                        d.FadeTo(0f, TypeBeatStyle.SCREEN_FADE_DURATION, Easing.OutQuint);
                    laidOutFocus = int.MaxValue;
                }

                setCaretsVisible(false);
            }

            updateApproachCue();
        }

        /// <summary>
        /// Shows a depleting bar under the upcoming line's first typeable char during the final
        /// <see cref="approach_lead_ms"/> before it is sung — the "get ready" signal after
        /// pre-roll and between lines. Hidden outside that window (a sealed/past line's start
        /// is behind the clock, so stale cues can never appear).
        /// </summary>
        private void updateApproachCue()
        {
            int upcoming = engine.ActiveLineIndex == -1 ? 0 : engine.ActiveLineIndex + 1;

            if (!engine.IsFinished && upcoming < displays.Length)
            {
                var line = engine.Lines[upcoming];
                int firstCell = firstTypeableIndex(line);

                if (firstCell >= 0)
                {
                    double remaining = line.Cells[firstCell].TargetTime - Time.Current;

                    if (remaining > 0 && remaining <= approach_lead_ms)
                    {
                        var d = displays[upcoming];
                        Vector2 point = d.ToSpaceOfOtherDrawable(d.PositionOfCell(firstCell), this);

                        float progress = (float)(remaining / approach_lead_ms); // 1 -> 0 as it lands
                        approachBar.Position = new Vector2(point.X, point.Y + d.LineHeight + 6);
                        approachBar.Width = approach_bar_max_width * progress;
                        approachBar.Alpha = 0.85f - 0.35f * progress; // brightens as the word arrives
                        return;
                    }
                }
            }

            approachBar.Alpha = 0f;
        }

        private static int firstTypeableIndex(TypingLine line)
        {
            for (int i = 0; i < line.Cells.Count; i++)
            {
                if (line.Cells[i].IsTypeable)
                    return i;
            }

            return -1;
        }

        private void relayout(int active, bool animate)
        {
            // First-ever layout applies instantly: transforms here run on the gameplay
            // clock, which may not be running yet (pre-roll) or may be frozen (scrubbing).
            double dur = animate && laidOutFocus != int.MinValue ? TypeBeatStyle.LINE_SCROLL_DURATION : 0;

            for (int k = 0; k < displays.Length; k++)
            {
                var d = displays[k];

                switch (k - active)
                {
                    case 0:
                        d.SetLineDim(0f);
                        fade(d, 1f, dur);
                        move(d, 0f, dur);
                        break;

                    case -1:
                        d.SetLineDim(0.7f);
                        fade(d, 1f, dur);
                        move(d, -line_gap, dur);
                        break;

                    case 1:
                        d.SetLineDim(0.4f);
                        fade(d, 1f, dur);
                        move(d, line_gap, dur);
                        break;

                    case -2:
                        fade(d, 0f, dur);
                        move(d, -2 * line_gap, dur);
                        break;

                    case 2:
                        fade(d, 0f, dur);
                        move(d, 2 * line_gap, dur);
                        break;

                    default:
                        fade(d, 0f, 0);
                        break;
                }
            }

            laidOutFocus = active;
            refreshVisible(active);
        }

        private void relayoutPreRoll()
        {
            // Same first-layout rule as relayout(): the gameplay clock may be frozen or
            // not yet running, so the initial pre-roll state must not depend on transforms.
            double dur = laidOutFocus == int.MinValue ? 0 : TypeBeatStyle.SCREEN_FADE_DURATION;

            for (int k = 0; k < displays.Length; k++)
            {
                var d = displays[k];

                switch (k)
                {
                    case 0:
                        d.SetLineDim(0.4f);
                        fade(d, 1f, dur);
                        move(d, 0f, dur);
                        break;

                    case 1:
                        d.SetLineDim(0.6f);
                        fade(d, 1f, dur);
                        move(d, line_gap, dur);
                        break;

                    default:
                        fade(d, 0f, 0);
                        break;
                }

                refreshDisplayCells(k);
            }
        }

        private void refreshVisible(int active)
        {
            int from = Math.Max(0, active - 1);
            int to = Math.Min(displays.Length - 1, active + 1);
            for (int k = from; k <= to; k++)
                refreshDisplayCells(k);
        }

        private void refreshDisplayCells(int index)
        {
            if (index < 0 || index >= displays.Length)
                return;

            var d = displays[index];
            int count = d.Line.Cells.Count;
            for (int c = 0; c < count; c++)
                d.RefreshCell(c);
        }

        private void setCaretsVisible(bool show)
        {
            if (show == caretsVisible)
                return;

            caretsVisible = show;
            float target = show ? 1f : 0f;
            playerCaret.FadeTo(target, 120, Easing.OutQuint);
            sungCaret.FadeTo(target, 120, Easing.OutQuint);
        }

        private static void fade(LyricLineDisplay d, float alpha, double dur)
        {
            if (dur <= 0)
                d.Alpha = alpha;
            else
                d.FadeTo(alpha, dur, Easing.OutQuint);
        }

        private static void move(LyricLineDisplay d, float y, double dur)
        {
            if (dur <= 0)
                d.Y = y;
            else
                d.MoveToY(y, dur, Easing.OutQuint);
        }

        protected override void Dispose(bool isDisposing)
        {
            engine.LineActivated -= onLineActivated;
            engine.CharJudged -= onCharJudged;
            engine.LineSealed -= onLineSealed;
            engine.WrongKeyRejected -= onWrongKeyRejected;
            base.Dispose(isDisposing);
        }

        // --- Test-support accessors (public so cross-assembly test scenes can assert) ---

        public Vector2 PlayerCaretPosition => playerCaret.IsNotNull() ? playerCaret.Position : Vector2.Zero;
        public Vector2 SungCaretPosition => sungCaret.IsNotNull() ? sungCaret.Position : Vector2.Zero;
        public bool PlayerCaretVisible => playerCaret.IsNotNull() && playerCaret.Alpha > 0.5f;
        public bool ApproachCueVisible => approachBar.IsNotNull() && approachBar.Alpha > 0.1f;

        public LyricLineDisplay? DisplayAt(int index) => index >= 0 && index < displays.Length ? displays[index] : null;

        public LyricLineDisplay? ActiveDisplay
        {
            get
            {
                int active = engine.ActiveLineIndex;
                return active >= 0 && active < displays.Length ? displays[active] : null;
            }
        }
    }
}
