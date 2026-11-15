// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.IO.Stores;
using osu.Framework.Text;
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
        private float indicatorAlignment = TypeBeatRulesetConfigManager.DEFAULT_ALIGNMENT_PERCENT;
        private float approachBarAlignment = TypeBeatRulesetConfigManager.DEFAULT_ALIGNMENT_PERCENT;
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
                // then. Fade-in and pop-in keep the span's start. Indicator Alignment moves only the
                // completion (and with it the lead-in's start), never the release.
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
            if (index < cells.Length && Line.Cells[index].State == CellState.Untyped && !Line.Cells[index].IsFreestyle && !Line.Cells[index].IsAutoCompleted)
            {
                float brightnessAmount = textHighlightAmount(index);
                cells[index].Colour = Line.Cells[index].IsOnset
                    ? TypeBeatStyle.ShorthandOnsetCharForBrightness(sungBrightness * brightnessAmount)
                    : TypeBeatStyle.SungCharForBrightness(sungBrightness * brightnessAmount);
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
                        // Revealed as each bar arrives, so Approach Bar Alignment moves the reveal too.
                        if (time < approachBarArrival(i, sungSpan(i).start))
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

        /// <summary>
        /// The moment an indicator or approach bar completes for an alignment (backlog 475):
        /// <paramref name="centre"/> at 100%, the opening of the Great window (<paramref name="centre"/>
        /// minus <paramref name="greatEarly"/>) at 0%, linear between.
        /// </summary>
        public static double AlignedCompletion(double centre, double greatEarly, float alignmentPercent) =>
            centre - Math.Max(0, greatEarly) * (1 - Math.Clamp(alignmentPercent, 0, 100) / 100.0);

        /// <summary>Where a cell's approach bar arrives, for a bar that would land on <paramref name="centre"/> at 100%.</summary>
        private double approachBarArrival(int index, double centre) =>
            AlignedCompletion(centre, windowsFor(index).GreatEarly, approachBarAlignment);

        /// <summary>Indicator Alignment: when the fade or fill and the text pop-in complete. Display only.</summary>
        public void SetIndicatorAlignment(float percent)
        {
            float value = Math.Clamp(percent, 0, 100);
            if (indicatorAlignment == value)
                return;
            indicatorAlignment = value;
            refreshApproach();
        }

        /// <summary>Approach Bar Alignment: where approach bars (and the pace bar reveal) arrive. Display only.</summary>
        public void SetApproachBarAlignment(float percent)
        {
            float value = Math.Clamp(percent, 0, 100);
            if (approachBarAlignment == value)
                return;
            approachBarAlignment = value;
            // The bars read it every frame; the reveal is fed with the sung window, so redraw it now.
            if (IsLoaded && !double.IsNaN(sungTime))
                updatePaceRail(sungTime);
        }

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

            [Resolved]
            private FontStore? fontStore { get; set; }

            // The glyph's ink, top and bottom in its unscaled bounds, for the text it was measured on.
            private (float Top, float Bottom)? ink;
            private string? inkText;

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
                               && !owner.Line.Cells[index].IsAutoCompleted
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

                // The fill rises over the letter's INK, not its box: the box is the font's whole line
                // height, so a lowercase letter looked completely filled at about half progress, long
                // before its target (backlog 467).
                string text = source.Text.ToString();
                if (inkText != text)
                {
                    inkText = text;
                    ink = InkExtent(fontStore, source, text);
                }

                var (inkTop, inkBottom) = ink ?? (0, source.DrawSize.Y);
                var mask = IndicatorMaskGeometry(owner.judgementIndicator, source.DrawSize, amount, inkTop, inkBottom);
                clip.Position = mask.Position;
                clip.Size = mask.Size;
                glyph.Position = -mask.Position;
            }

            public ColourInfo FillColour => glyph.Colour;
            /// <summary>How much of the letter's ink the fill covers, 0 to 1.</summary>
            public float FillFraction
            {
                get
                {
                    var (top, bottom) = ink ?? (0, Size.Y);
                    float covered = Math.Clamp(bottom - clip.Y, 0, bottom - top);
                    return bottom > top && clip.Width > 0 ? covered / (bottom - top) : 0;
                }
            }

            /// <summary>The letter's ink as last measured, in its unscaled bounds (test support).</summary>
            public (float Top, float Bottom)? Ink => ink;
            public osu.Framework.Graphics.Primitives.Quad ClipScreenQuad => clip.ScreenSpaceDrawQuad;
        }

        /// <summary>
        /// Clip geometry in the glyph's unscaled bounds. The fill rises from the bottom of the ink
        /// (<paramref name="inkBottom"/>) to its top (<paramref name="inkTop"/>), so it reads complete
        /// exactly at progress 1; without ink bounds it spans the whole box. The ink may reach past the
        /// box (a tall font's descenders, an accent above a capital), and the clip follows it there.
        /// </summary>
        public static (Vector2 Position, Vector2 Size) IndicatorMaskGeometry(JudgementIndicatorMode mode, Vector2 glyphSize, float progress,
                                                                             float inkTop = 0, float? inkBottom = null)
        {
            if (mode != JudgementIndicatorMode.BottomToTopFill)
                return (Vector2.Zero, Vector2.Zero);

            float amount = Math.Clamp(progress, 0, 1);
            float bottom = inkBottom ?? glyphSize.Y;
            float top = Math.Min(inkTop, bottom);

            // Anchored on whichever is lower, the box's bottom edge or the ink's (nothing is drawn below
            // the ink, so covering it from the start changes nothing); only the top moves.
            float floor = Math.Max(glyphSize.Y, bottom);
            float y = amount >= 1 ? Math.Min(0, top) : bottom - (bottom - top) * amount;
            return (new Vector2(0, y), new Vector2(glyphSize.X, floor - y));
        }

        /// <summary>
        /// The top and bottom of <paramref name="text"/>'s ink in <paramref name="source"/>'s unscaled
        /// bounds, laid out by the framework's own text builder exactly as the sprite lays it out. Null
        /// when there is no store or no ink (whitespace), and the fill then spans the box.
        /// </summary>
        internal static (float Top, float Bottom)? InkExtent(FontStore? store, OsuSpriteText source, string text)
        {
            if (store == null || string.IsNullOrEmpty(text))
                return null;

            var glyphs = new List<TextBuilderGlyph>();
            // Wrapped as OsuSpriteText wraps it, so an emoji measures as the placeholder the sprite draws.
            var builder = new TextBuilder(new SurrogatePlaceholderGlyphStore(store), source.Font, float.MaxValue, source.UseFullGlyphHeight,
                new Vector2(source.Padding.Left, source.Padding.Top), source.Spacing, glyphs, Array.Empty<char>(), '?', 'm');
            builder.AddText(text);

            var inked = glyphs.Where(g => g.Height > 0 && !char.IsWhiteSpace(g.Character)).ToList();
            if (inked.Count == 0)
                return null;

            return (inked.Min(g => g.DrawRectangle.Top), inked.Max(g => g.DrawRectangle.Bottom));
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
