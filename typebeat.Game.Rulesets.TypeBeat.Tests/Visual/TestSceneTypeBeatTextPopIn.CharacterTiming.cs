// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatTextPopIn
    {
        [TestCase(JudgementIndicatorMode.FadeIn, false, false)]
        [TestCase(JudgementIndicatorMode.BottomToTopFill, false, false)]
        [TestCase(JudgementIndicatorMode.FadeIn, true, false)]
        [TestCase(JudgementIndicatorMode.BottomToTopFill, true, false)]
        [TestCase(JudgementIndicatorMode.FadeIn, false, true)]
        [TestCase(JudgementIndicatorMode.BottomToTopFill, false, true)]
        public void CharacterIndicatorsArriveAtAutoplayPresses(JudgementIndicatorMode mode, bool hardRock, bool subdivided)
        {
            double[] presses = Array.Empty<double>();
            load(subdivided ? "catdog" : "cat", hardRock, splitStretch: subdivided);
            AddStep("enable character indicator timing", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, mode);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 0f);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 200f);
                config.SetValue(TypeBeatRulesetSetting.CharacterIndicatorTiming, true);
                Mod[] mods = hardRock ? new Mod[] { new TypeBeatModHardRock() } : Array.Empty<Mod>();
                presses = new TypeBeatModAutoplay().CreateReplayData(drawableRuleset.Beatmap, mods).Replay.Frames
                    .Cast<TypeBeatReplayFrame>().Where(f => !f.IsConfig && !f.IsConfigExtended).Select(f => f.Time).ToArray();
            });
            at("first Autoplay press", () => presses[0]);
            AddUntilStep("first character finishes", () => colourIs(0, TypeBeatStyle.SungCharForBrightness(50)) && display.CellVisualScale(0) == 1);
            AddAssert("next character waits instead of sharing the syllable indicator", () => colourIs(1, TypeBeatStyle.UntypedChar) && display.CellColourFillAlpha(1) == 0 && display.CellVisualScale(1) == 0.95f);
            at("halfway through second character lead-in", () => presses[1] - 100);
            AddUntilStep("second character is halfway through its own indicator", () => mode == JudgementIndicatorMode.FadeIn
                ? colourIs(1, TypeBeatStyle.SungCharForBrightness(50 * 31f / 1023f))
                : Math.Abs(display.CellColourFillFraction(1) - 0.5f) < 0.0001);
            AddAssert("alignment is disabled without changing its saved value", () => config.GetBindable<float>(TypeBeatRulesetSetting.SyllableFadeInEnd).Disabled && config.Get<float>(TypeBeatRulesetSetting.SyllableFadeInEnd) == 0);
            at("second Autoplay press", () => presses[1]);
            AddUntilStep("second character completes at its press", () => colourIs(1, TypeBeatStyle.SungCharForBrightness(50)) && display.CellVisualScale(1) == 1);
            at("rewind before the second approach", () => presses[1] - 201);
            AddUntilStep("seeking reconstructs the unfilled character", () => colourIs(1, TypeBeatStyle.UntypedChar) && display.CellColourFillAlpha(1) == 0);
            at("return to the middle of the approach", () => presses[1] - 100);
            AddUntilStep("approach resumes at the same fraction", () => mode == JudgementIndicatorMode.FadeIn
                ? colourIs(1, TypeBeatStyle.SungCharForBrightness(50 * 31f / 1023f))
                : Math.Abs(display.CellColourFillFraction(1) - 0.5f) < 0.0001);
            AddStep("disable character timing", () => config.SetValue(TypeBeatRulesetSetting.CharacterIndicatorTiming, false));
            AddUntilStep("normal window-based highlighting returns", () => colourIs(1, TypeBeatStyle.SungCharForBrightness(50)) && display.CellVisualScale(1) == 1);
            AddAssert("alignment becomes adjustable again", () => !config.GetBindable<float>(TypeBeatRulesetSetting.SyllableFadeInEnd).Disabled);
        }
    }
}
