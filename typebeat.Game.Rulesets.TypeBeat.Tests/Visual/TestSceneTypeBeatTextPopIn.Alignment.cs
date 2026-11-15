// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Indicator Alignment and Approach Bar Alignment (backlog 475). At 100% (the default) everything
    /// completes where it did before the sliders came back; at 0% it completes GreatEarly earlier; and
    /// each slider moves only its own half. Display only, so nothing here types a key.
    /// </summary>
    public partial class TestSceneTypeBeatTextPopIn
    {
        private const float alignment_lead_in = 400f;

        private double greatEarly => engine.Windows.GreatEarly;

        private float expectedIndicator(JudgementIndicatorMode mode, double completion)
        {
            float progress = LyricLineDisplay.SyllableFadeInProgress(songClock.CurrentTime, completion, alignment_lead_in);
            return mode == JudgementIndicatorMode.FadeIn ? LyricLineDisplay.ExponentialIndicatorProgress(progress) : progress;
        }

        private bool barOnRail(int index) => display.FloatingBarAlpha(index) > 0
                                             && Math.Abs(display.FloatingBarScreenQuad(index).TopLeft.Y - display.RailScreenQuad.TopLeft.Y) < 0.01;

        private bool barBelowRail(int index) => display.FloatingBarAlpha(index) > 0
                                                && display.FloatingBarScreenQuad(index).TopLeft.Y > display.RailScreenQuad.TopLeft.Y + 0.01;

        [Test]
        public void AlignmentHelperRunsFromTheWindowOpeningToTheCentre()
        {
            AddAssert("100% is the centre", () => LyricLineDisplay.AlignedCompletion(1000, 150, 100) == 1000);
            AddAssert("0% is GreatEarly before it", () => LyricLineDisplay.AlignedCompletion(1000, 150, 0) == 850);
            AddAssert("linear between", () => Math.Abs(LyricLineDisplay.AlignedCompletion(1000, 150, 40) - 910) < 1e-9);
            AddAssert("out-of-range percentages clamp", () => LyricLineDisplay.AlignedCompletion(1000, 150, -5) == 850
                && LyricLineDisplay.AlignedCompletion(1000, 150, 500) == 1000);
        }

        [TestCase(JudgementIndicatorMode.FadeIn)]
        [TestCase(JudgementIndicatorMode.BottomToTopFill)]
        public void IndicatorAlignmentZeroCompletesAtTheWindowOpening(JudgementIndicatorMode mode)
        {
            load("cat");
            AddStep("configure indicator at 0%", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, mode);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, alignment_lead_in);
                config.SetValue(TypeBeatRulesetSetting.IndicatorAlignment, 0f);
            });

            at("before the early lead-in", () => 1000 - greatEarly - alignment_lead_in - 1);
            AddUntilStep("nothing is lit yet", () => display.CellSungHighlightAmount(0) == 0);
            at("halfway through the early lead-in", () => 1000 - greatEarly - alignment_lead_in / 2);
            AddUntilStep("the indicator is halfway, GreatEarly ahead of the centre",
                () => Math.Abs(display.CellSungHighlightAmount(0) - expectedIndicator(mode, 1000 - greatEarly)) < 0.0001
                      && display.CellSungHighlightAmount(0) > 0);
            at("shortly before the window opens", () => 1000 - greatEarly - 20);
            AddUntilStep("pop-in is still growing", () => display.CellVisualScale(0) < 1);
            at("the Great window opens", () => 1000 - greatEarly);
            AddUntilStep("indicator and pop-in complete at the opening", () => display.CellSungHighlightAmount(0) == 1 && display.CellVisualScale(0) == 1
                && colourIs(0, TypeBeatStyle.SungCharForBrightness(50)));

            if (mode == JudgementIndicatorMode.BottomToTopFill)
            {
                // Each later letter fills on its own target, moved earlier by the same share of GreatEarly.
                at("halfway through the second letter's early lead-in", () => display.Line.Cells[1].TargetTime - greatEarly - alignment_lead_in / 2);
                AddUntilStep("the second letter is half filled", () => Math.Abs(display.CellColourFillFraction(1) - 0.5f) < 0.0001);
                at("the second letter's early completion", () => display.Line.Cells[1].TargetTime - greatEarly);
                AddUntilStep("the second letter is filled", () => display.CellSungHighlightAmount(1) == 1);
            }

            // The release is untouched: it still waits for the Great window to close.
            at("Great window end", () => 5000 + engine.Windows.GreatLate);
            AddUntilStep("the last letter is still fully lit", () => display.CellSungHighlightAmount(2) == 1);
            at("halfway through the release", () => 5000 + engine.Windows.GreatLate + LyricLineDisplay.INDICATOR_RELEASE_DURATION_MS / 2);
            AddUntilStep("the release halves on its usual schedule", () => Math.Abs(display.CellSungHighlightAmount(2) - 0.5f) < 0.0001);

            AddStep("move the slider back to the centre", () => config.SetValue(TypeBeatRulesetSetting.IndicatorAlignment, 100f));
            at("the Great window opens again", () => 1000 - greatEarly);
            AddUntilStep("at 100% the opening is mid lead-in again", () => display.CellSungHighlightAmount(0) < 1 && display.CellVisualScale(0) < 1
                && Math.Abs(display.CellSungHighlightAmount(0) - expectedIndicator(mode, 1000)) < 0.0001);
            at("the centre", () => 1000);
            AddUntilStep("at 100% everything completes at the centre", () => display.CellSungHighlightAmount(0) == 1 && display.CellVisualScale(0) == 1);
        }

        [Test]
        public void ApproachBarAlignmentZeroArrivesAtTheWindowOpening()
        {
            load("cat");
            AddStep("configure bars at 0%", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, alignment_lead_in);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
                config.SetValue(TypeBeatRulesetSetting.ApproachBarAlignment, 0f);
            });

            at("before the bar's early lead-in", () => 1000 - greatEarly - alignment_lead_in - 1);
            AddUntilStep("the bar is hidden", () => display.FloatingBarAlpha(0) == 0);
            at("halfway through the bar's fade-in", () => 1000 - greatEarly - alignment_lead_in + 50);
            AddUntilStep("the bar fades in on the early schedule", () => Math.Abs(display.FloatingBarAlpha(0) - 0.5f) < 0.0001);
            at("just before the window opens", () => 1000 - greatEarly - 1);
            AddUntilStep("the bar is still rising", () => barBelowRail(0));
            at("the Great window opens", () => 1000 - greatEarly);
            AddUntilStep("the bar reaches the underline GreatEarly before the span start", () => barOnRail(0));

            // Indicator Alignment stays on the centre: the text has not completed yet.
            AddAssert("the text indicator keeps its own timing", () => display.CellSungHighlightAmount(0) < 1
                && Math.Abs(display.CellSungHighlightAmount(0) - expectedIndicator(JudgementIndicatorMode.FadeIn, 1000)) < 0.0001
                && display.CellVisualScale(0) < 1);
            at("the centre", () => 1000);
            AddUntilStep("the text completes at the centre", () => display.CellSungHighlightAmount(0) == 1 && display.CellVisualScale(0) == 1 && barOnRail(0));

            at("bar release midpoint", () => display.Line.Cells[0].TargetTime + engine.Windows.GreatLate + 50);
            AddUntilStep("the bar still fades after Great closes", () => Math.Abs(display.FloatingBarAlpha(0) - 0.5f) < 0.0001);

            AddStep("move the slider back to the centre", () => config.SetValue(TypeBeatRulesetSetting.ApproachBarAlignment, 100f));
            at("the Great window opens again", () => 1000 - greatEarly);
            AddUntilStep("at 100% the bar is still rising at the opening", () => barBelowRail(0));
            at("the centre again", () => 1000);
            AddUntilStep("at 100% the bar lands on the centre", () => barOnRail(0));
        }

        [Test]
        public void IndicatorAlignmentLeavesTheBarsOnTheCentre()
        {
            load("cat");
            AddStep("indicator at 0%, bars at 100%", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.BottomToTopFill);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, alignment_lead_in);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
                config.SetValue(TypeBeatRulesetSetting.IndicatorAlignment, 0f);
            });
            at("the Great window opens", () => 1000 - greatEarly);
            AddUntilStep("the text completes early", () => display.CellSungHighlightAmount(0) == 1 && display.CellVisualScale(0) == 1);
            AddAssert("the bar is still rising", () => barBelowRail(0));
            at("the centre", () => 1000);
            AddUntilStep("the bar lands on the centre", () => barOnRail(0));
        }

        [Test]
        public void PaceBarRevealFollowsTheBarAlignment()
        {
            load("cat dog", source: new LyricLine
            {
                RawText = "cat dog", StartTime = 0, EndTime = 6000, SingEndTime = 4000,
                Units = new[]
                {
                    new TimedUnit { Text = "cat", StartTime = 1000, EndTime = 1800 },
                    new TimedUnit { Text = "dog", StartTime = 2600, EndTime = 4000 },
                },
            });
            AddStep("reveal with bars at 0%", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.PaceBarReveal, PaceBarRevealMode.Build);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, alignment_lead_in);
                config.SetValue(TypeBeatRulesetSetting.ApproachBarAlignment, 0f);
                var bands = new[] { new PaceBand(0, 4, Color4.Red), new PaceBand(4, 7, Color4.Green) };
                display.SetPaceBands(bands, bands);
            });
            at("just before the early arrival", () => 1000 - greatEarly - 1);
            AddUntilStep("nothing is revealed yet", () => display.RevealedPaceWidth == 0);
            at("the early arrival", () => 1000 - greatEarly);
            AddUntilStep("the arriving span is revealed with its bar", () => display.RevealedPaceWidth > 0 && barOnRail(0));
            AddStep("move the bars back to the centre", () => config.SetValue(TypeBeatRulesetSetting.ApproachBarAlignment, 100f));
            AddUntilStep("the reveal waits for the centre again", () => display.RevealedPaceWidth == 0);
        }
    }
}
