// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.Scoring;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    // The sung highlight the stage feeds every frame and the judgement indicators drawn from it:
    // the indicator modes (fade-in and bottom-to-top fill), the approach bars, the
    // judgement-coloured bars, the pace bar reveal, the sung brightness and text pop-in.
    public partial class LyricLineDisplay
    {
        /// <summary>Index into the shown grouping's <see cref="SyllableGrouping.Groups"/> of the group currently being sung,
        /// -1 = none. Stage-fed (see <see cref="SetSungSyllable"/>); time-driven state, so it lives
        /// beside the sung sweep rather than in the pull-based cell states.</summary>
        private int sungSyllable = -1;
        private readonly bool[] litCells;
        private readonly float[] litAmounts;
        private readonly float[] indicatorProgress;
        private readonly float[] indicatorRelease;
        private JudgementIndicatorMode judgementIndicator;
        private bool approachBarsEnabled;
        private PaceBarRevealMode paceBarReveal = PaceBarRevealMode.Off;
        private bool judgementColouredBarsEnabled;
        private JudgementColourBar[] judgementColourBars = Array.Empty<JudgementColourBar>();
        private float revealedPaceWidth;
        private float paceColourGradient = 100f;
        private FloatingIndicatorBar[] floatingBars = Array.Empty<FloatingIndicatorBar>();
        public const double INDICATOR_RELEASE_DURATION_MS = 100;
        private const double approach_bar_fade_in_duration_ms = 100;
        private bool syllableFadeInEnabled;
        private bool syllableColourFillEnabled;
        private float syllableFadeInDuration = TypeBeatRulesetConfigManager.DEFAULT_SYLLABLE_FADE_IN_DURATION_MS;
        private GlyphColourFill[] glyphColourFills = Array.Empty<GlyphColourFill>();
        private readonly bool[] popInApplied;
        private bool textPopInEnabled;
        private float textPopInAmount = TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT;
        private double sungTime = double.NaN;

        public const float TEXT_POP_IN_MIN_SCALE = 1 - TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT / 100f;
        public const double TEXT_POP_IN_DURATION_MS = 140;
        private float sungBrightness = 50f;

        /// <summary>Light exactly one syllable, or clear all groups with -1.</summary>
        public void SetSungSyllable(int index)
        {
            sungSyllable = index;
            if (index < 0)
                sungTime = double.NaN;
            for (int i = 0; i < litCells.Length; i++)
            {
                bool lit = index >= 0 && grouping.IndexOf(i) == index;
                indicatorProgress[i] = indicatorRelease[i] = lit ? 1 : 0;
                setCellLit(i, lit);
            }
        }

        /// <summary>Absolute song-time indicators; pauses, rates and seeks cannot accumulate animation drift.
        /// Each cell's span and Great window are the judge's own, read on every feed.</summary>
        public void SetSungWindow(double time)
        {
            sungTime = time;
            int primary = -1;

            for (int i = 0; i < litCells.Length; i++)
            {
                var (start, end) = sungSpan(i);
                var windows = windowsFor(i);
                // The bottom-to-top fill rises letter by letter on each cell's own target, kept inside its
                // judged span. Its release stays on the span's end, since the cell grades Great until
                // then. Fade-in, None, pop-in and approach bars keep the span's start.
                double completion = indicatorCompletion(i, glyphFillMode);
                double approachStart = judgementIndicator == JudgementIndicatorMode.None ? start - windows.GreatEarly : completion - syllableFadeInDuration;
                float release = IndicatorRelease(time, end + windows.GreatLate);
                bool lit = Line.Cells[i].IsCountable && time >= approachStart && release > 0;
                float progress = lit ? judgementIndicator == JudgementIndicatorMode.None ? 1 : SyllableFadeInProgress(time, completion, syllableFadeInDuration) : 0;
                indicatorProgress[i] = progress;
                indicatorRelease[i] = lit ? release : 0;
                float amount = judgementIndicator == JudgementIndicatorMode.FadeIn ? ExponentialIndicatorProgress(progress) : progress;
                int group = grouping.IndexOf(i);
                if (lit && group >= 0 && (primary < 0 || time >= start && time <= end))
                    primary = group;
                setCellLit(i, lit, amount * release);
                applyTextPopIn(i);
            }
            sungSyllable = primary;
            updatePaceRail(time);
        }

        private SyncWindows windowsFor(int index) => judge.WindowsFor(Line.Cells[index]);

        public static float ExponentialIndicatorProgress(float progress) =>
            (float)((Math.Pow(2, 10 * Math.Clamp(progress, 0, 1)) - 1) / 1023);

        public static float IndicatorRelease(double time, double greatWindowEnd) =>
            (float)Math.Clamp(1 - (time - greatWindowEnd) / INDICATOR_RELEASE_DURATION_MS, 0, 1);

        private bool glyphFillMode => judgementIndicator == JudgementIndicatorMode.BottomToTopFill;

        private float textHighlightAmount(int index) => glyphFillMode && indicatorProgress[index] < 1 ? 0 : litAmounts[index];

        private (double start, double end) sungSpan(int index) => judge.JudgedSpanOf(Line, index);

        private void setCellLit(int index, bool lit, float amount = 1f)
        {
            amount = lit ? amount : 0f;
            if (litCells[index] == lit && litAmounts[index] == amount)
                return;
            litCells[index] = lit;
            litAmounts[index] = amount;
            // Only the untyped fill changes during the approach; judgement feedback keeps its colour.
            if (index < cells.Length && Line.Cells[index].State == CellState.Untyped && !Line.Cells[index].IsFreestyle)
            {
                float brightnessAmount = textHighlightAmount(index);
                cells[index].Colour = TypeBeatStyle.SungCharForBrightness(sungBrightness * brightnessAmount);
            }
        }

        /// <summary>Compatibility for older test scenes; the mode is the authoritative selection.</summary>
        public void SetSyllableFadeInEnabled(bool enabled) => SetJudgementIndicator(enabled
            ? syllableColourFillEnabled ? JudgementIndicatorMode.BottomToTopFill : JudgementIndicatorMode.FadeIn
            : JudgementIndicatorMode.None);

        public void SetJudgementIndicator(JudgementIndicatorMode mode)
        {
            if (judgementIndicator == mode)
                return;
            judgementIndicator = mode;
            syllableFadeInEnabled = mode != JudgementIndicatorMode.None;
            syllableColourFillEnabled = glyphFillMode;
            if (IsLoaded)
            {
                createIndicatorDrawables();
                measureAndLayout();
            }
            refreshApproach();
        }

        public void SetApproachBarsEnabled(bool enabled)
        {
            if (approachBarsEnabled == enabled)
                return;
            approachBarsEnabled = enabled;
            if (IsLoaded)
            {
                createIndicatorDrawables();
                measureAndLayout();
            }
        }

        public void SetPaceBarReveal(PaceBarRevealMode mode)
        {
            paceBarReveal = mode;
            if (IsLoaded)
                updatePaceRail(Time.Current);
        }

        public void SetJudgementColouredBarsEnabled(bool enabled)
        {
            judgementColouredBarsEnabled = enabled;
            if (IsLoaded)
                createIndicatorDrawables();
        }

        // The rail is revealed from absolute song time, including seeks and off-screen lines.
        // Only its mask changes; glyph positions, band geometry and flashlight fades stay fixed.
        private void updatePaceRail(double time)
        {
            if (paceRail.IsNull())
                return;
            bool revealing = paceBarReveal != PaceBarRevealMode.Off && approachBarsEnabled;
            int arrived = 0;
            if (revealing && !double.IsNaN(time))
            {
                for (int i = 0; i < Line.Cells.Count; i++)
                {
                    if (Line.Cells[i].IsCountable)
                    {
                        if (time < sungSpan(i).start)
                            break;
                        arrived = i + 1;
                    }
                    else if (arrived > 0)
                        arrived = i + 1;
                }
            }
            revealedPaceWidth = revealing ? cellX[Math.Min(arrived, cellX.Length - 1)] : FullSweepWidth;
            paceRail.Masking = revealing;
            paceRail.Width = revealedPaceWidth;
            unrevealedPaceBar.X = revealedPaceWidth;
            unrevealedPaceBar.Width = Math.Max(0, FullSweepWidth - revealedPaceWidth);
            // Share the rail's flashlight fade so the gray preview cannot reveal hidden lines.
            unrevealedPaceBar.Alpha = revealing && paceBarReveal == PaceBarRevealMode.ColourIn ? sweepFill.Alpha : 0;
        }

        public void SetPaceColourGradient(float percent)
        {
            paceColourGradient = Math.Clamp(percent, 0, 100);
            if (IsLoaded)
                measureAndLayout();
        }

        private void createIndicatorDrawables()
        {
            if (glyphFillMode)
                createGlyphColourFills();
            if (judgementColouredBarsEnabled && judgementColourBars.Length == 0)
            {
                judgementColourBars = new JudgementColourBar[cells.Length];
                for (int i = 0; i < cells.Length; i++)
                    content.Add(judgementColourBars[i] = new JudgementColourBar(this, i));
            }
            if (approachBarsEnabled && floatingBars.Length == 0)
            {
                floatingBars = new FloatingIndicatorBar[cells.Length];
                for (int i = 0; i < cells.Length; i++)
                    content.Add(floatingBars[i] = new FloatingIndicatorBar(this, i));
            }
        }

        public static float SyllableFadeInProgress(double time, double completion, double duration) =>
            duration <= 0 ? time >= completion ? 1f : 0f : (float)Math.Clamp((time - completion + duration) / duration, 0, 1);

        public void SetSyllableColourFillEnabled(bool enabled)
        {
            syllableColourFillEnabled = enabled;
            if (syllableFadeInEnabled)
                SetJudgementIndicator(enabled ? JudgementIndicatorMode.BottomToTopFill : JudgementIndicatorMode.FadeIn);
        }

        public void SetSyllableFadeInDuration(float milliseconds)
        {
            syllableFadeInDuration = Math.Clamp(milliseconds, 0, TypeBeatRulesetConfigManager.MAX_SYLLABLE_FADE_IN_DURATION_MS);
            refreshApproach();
        }

        private void refreshApproach()
        {
            if (!double.IsNaN(sungTime))
                SetSungWindow(sungTime);
            for (int i = 0; i < cells.Length; i++)
                RefreshCell(i);
        }

        private void createGlyphColourFills()
        {
            if (glyphColourFills.Length > 0)
                return;
            glyphColourFills = new GlyphColourFill[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                glyphColourFills[i] = new GlyphColourFill(this, i);
                content.Add(glyphColourFills[i]);
            }
        }

        // A second copy of the glyph is clipped in the selected direction. The mask contains text
        // only. Kept out of layout and built only on first use.
        private sealed partial class GlyphColourFill : Container
        {
            private readonly LyricLineDisplay owner;
            private readonly int index;
            private readonly Container clip;
            private readonly OsuSpriteText glyph;

            public GlyphColourFill(LyricLineDisplay owner, int index)
            {
                this.owner = owner;
                this.index = index;
                Origin = Anchor.Centre;
                Depth = -2;
                AlwaysPresent = true;
                BypassAutoSizeAxes = Axes.Both;
                Alpha = 0;
                Child = clip = new Container
                {
                    Masking = true,
                    Child = glyph = new OsuSpriteText
                    {
                        Font = owner.cells[index].Font,
                        Text = owner.cells[index].Text,
                        Shadow = false,
                    },
                };
            }

            protected override void UpdateAfterChildren()
            {
                base.UpdateAfterChildren();
                var source = owner.cells[index];
                float amount = owner.indicatorProgress[index];
                bool visible = owner.glyphFillMode && owner.litCells[index] && owner.indicatorRelease[index] > 0
                               && owner.Line.Cells[index].State == CellState.Untyped && !owner.Line.Cells[index].IsFreestyle
                               && amount > 0 && amount < 1;
                Alpha = visible ? source.Alpha : 0;
                if (!visible)
                    return;
                Position = source.Position;
                Size = source.DrawSize;
                Scale = source.Scale;
                if (!glyph.Text.Equals(source.Text))
                    glyph.Text = source.Text;
                glyph.Colour = TypeBeatStyle.SungCharForBrightness(owner.sungBrightness * owner.indicatorRelease[index]);
                var mask = IndicatorMaskGeometry(owner.judgementIndicator, source.DrawSize, amount);
                clip.Position = mask.Position;
                clip.Size = mask.Size;
                glyph.Position = -mask.Position;
            }

            public ColourInfo FillColour => glyph.Colour;
            public float FillFraction => Size.X > 0 && Size.Y > 0 ? clip.Width * clip.Height / (Size.X * Size.Y) : 0;
            public osu.Framework.Graphics.Primitives.Quad ClipScreenQuad => clip.ScreenSpaceDrawQuad;
        }

        /// <summary>Clip geometry within the glyph's unscaled bounds.</summary>
        public static (Vector2 Position, Vector2 Size) IndicatorMaskGeometry(JudgementIndicatorMode mode, Vector2 glyphSize, float progress)
        {
            float amount = Math.Clamp(progress, 0, 1);
            return mode switch
            {
                JudgementIndicatorMode.BottomToTopFill => (new Vector2(0, glyphSize.Y * (1 - amount)), new Vector2(glyphSize.X, glyphSize.Y * amount)),
                _ => (Vector2.Zero, Vector2.Zero),
            };
        }

        private sealed partial class FloatingIndicatorBar : Box
        {
            private readonly LyricLineDisplay owner;
            private readonly int index;

            public FloatingIndicatorBar(LyricLineDisplay owner, int index)
            {
                this.owner = owner;
                this.index = index;
                AlwaysPresent = true;
                BypassAutoSizeAxes = Axes.Both;
                Depth = -0.5f;
                Alpha = 0;
            }

            protected override void Update()
            {
                base.Update();
                Alpha = 0;
                if (!owner.approachBarsEnabled || index >= owner.cells.Length || !owner.Line.Cells[index].IsCountable)
                    return;
                var span = owner.sungSpan(index);
                var windows = owner.windowsFor(index);
                // Bars always land on the sung target, independently of text indicator alignment.
                double time = owner.sungTime;
                float release = IndicatorRelease(time, span.end + windows.GreatLate);
                if (double.IsNaN(time) || time < span.start - owner.syllableFadeInDuration || release <= 0)
                    return;
                float progress = SyllableFadeInProgress(time, span.start, owner.syllableFadeInDuration);
                float fadeIn = SyllableFadeInProgress(time, span.start - owner.syllableFadeInDuration + approach_bar_fade_in_duration_ms,
                    approach_bar_fade_in_duration_ms);
                // A point-judged cell keeps a bar of its own; cells judged on one shared span draw one.
                bool charTimed = span.start == span.end;
                if (!charTimed && index > 0 && owner.Line.Cells[index - 1].IsCountable
                    && owner.sungSpan(index - 1) == span && owner.windowsFor(index - 1) == windows)
                    return;
                int end = index + 1;
                if (!charTimed)
                    while (end < owner.cells.Length && owner.Line.Cells[end].IsCountable && owner.sungSpan(end) == span
                           && owner.windowsFor(end) == windows)
                        end++;
                float visible = 1;
                for (int i = index; i < end; i++)
                    visible = Math.Min(visible, owner.cells[i].Alpha);
                Alpha = fadeIn * release * visible;
                Position = new Vector2(owner.cellX[index], owner.glyphHeight + SWEEP_RAIL_OFFSET * owner.SizeRatio
                    + 24 * owner.SizeRatio * (1 - progress));
                Size = new Vector2(owner.cellX[end] - owner.cellX[index], SWEEP_RAIL_HEIGHT * owner.SizeRatio);
                Colour = owner.paceColourFor(index);
            }
        }

        private sealed partial class JudgementColourBar : Box
        {
            private readonly LyricLineDisplay owner;
            private readonly int index;

            public JudgementColourBar(LyricLineDisplay owner, int index)
            {
                this.owner = owner;
                this.index = index;
                AlwaysPresent = true;
                BypassAutoSizeAxes = Axes.Both;
                Depth = -1;
                Alpha = 0;
            }

            protected override void Update()
            {
                base.Update();
                Alpha = 0;
                if (!owner.judgementColouredBarsEnabled || !owner.Line.Cells[index].IsCountable)
                    return;
                var source = owner.Line.Cells[index];
                JudgementType? tier = source.State switch
                {
                    CellState.Correct => source.JudgedTier,
                    CellState.Wrong => source.JudgedTier ?? JudgementType.WrongChar,
                    CellState.Missed => source.JudgedTier ?? JudgementType.Miss,
                    _ => null,
                };
                if (tier == null)
                    return;
                float width = Math.Clamp(owner.revealedPaceWidth - owner.cellX[index], 0, owner.advances[index]);
                Position = new Vector2(owner.cellX[index], owner.sweepFill.Y);
                Size = new Vector2(width, SWEEP_RAIL_HEIGHT * owner.SizeRatio);
                Colour = JudgementBarColourFor(tier.Value);
                Alpha = width > 0 ? owner.sweepFill.Alpha * owner.cells[index].Alpha : 0;
            }
        }

        // Use the gameplay result mapping: correct off-time inputs score Meh, and a recoverable
        // wrong input has no result yet. Neither is a Miss.
        public static Color4 JudgementBarColourFor(JudgementType tier) => TypeBeatResultMapping.CellResult(tier, TypoRule.Deferred) switch
        {
            HitResult.Great => TypeBeatStyle.SungAccent,
            HitResult.Ok => TypeBeatStyle.PaceSlowAccent,
            HitResult.Meh => Color4.Orange,
            HitResult.Miss => TypeBeatStyle.ErrorChar,
            _ => TypeBeatStyle.UntypedChar,
        };

        private Color4 paceColourFor(int index)
        {
            if (selectedPaceBands?.Count == sweepTracks.Length)
                foreach (var band in selectedPaceBands)
                    if (index >= band.StartCell && index < band.EndCellExclusive)
                        return band.Colour;
            return UnderlinePace.NeutralColour;
        }

        /// <summary>Adjust the highlight colour during play without changing cell states.</summary>
        public void SetSungBrightness(float percent)
        {
            float value = Math.Clamp(percent, 0f, 100f);
            if (sungBrightness == value)
                return;
            sungBrightness = value;
            for (int i = 0; i < litCells.Length; i++)
                if (litCells[i] && Line.Cells[i].State == CellState.Untyped && !Line.Cells[i].IsFreestyle)
                    RefreshCell(i);
        }

        public void SetTextPopInEnabled(bool enabled)
        {
            textPopInEnabled = enabled;
            for (int i = 0; i < cells.Length; i++)
                applyTextPopIn(i);
        }

        public void SetTextPopInAmount(float amount)
        {
            textPopInAmount = Math.Clamp(amount, 0, TypeBeatRulesetConfigManager.MAX_TEXT_POP_IN_AMOUNT);
            for (int i = 0; i < cells.Length; i++)
                applyTextPopIn(i);
        }

        /// <summary>Absolute song-time easing, so pauses, playback rates and seeks cannot delay the finish.</summary>
        public static float TextPopInScale(double time, double windowOpens, float amount = TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT)
        {
            float minScale = 1 - Math.Clamp(amount, 0, TypeBeatRulesetConfigManager.MAX_TEXT_POP_IN_AMOUNT) / 100f;
            if (double.IsNaN(time))
                return minScale;
            double progress = Math.Clamp((time - windowOpens + TEXT_POP_IN_DURATION_MS) / TEXT_POP_IN_DURATION_MS, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);
            return (float)(minScale + (1 - minScale) * eased);
        }

        private void applyTextPopIn(int index)
        {
            if (index < 0 || index >= cells.Length)
                return;
            var source = Line.Cells[index];
            bool apply = textPopInEnabled && source.IsCountable && source.State == CellState.Untyped;
            if (apply)
            {
                cells[index].Scale = new Vector2(TextPopInScale(sungTime,
                    indicatorCompletion(index), textPopInAmount));
                popInApplied[index] = true;
            }
            else if (popInApplied[index])
            {
                cells[index].Scale = Vector2.One;
                popInApplied[index] = false;
            }
        }
    }
}
