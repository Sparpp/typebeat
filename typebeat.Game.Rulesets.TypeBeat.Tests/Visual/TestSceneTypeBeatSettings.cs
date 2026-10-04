// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatSettings : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        private TypeBeatSettingsSubsection subsection = null!;
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;
        private FormSliderBar<float> gradient => subsection.ChildrenOfType<FormSliderBar<float>>()
            .Single(slider => slider.Caption.ToString() == "Pace colour gradient");

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private void openSettings()
        {
            AddStep("open type!beat settings", () => Child = new OsuScrollContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = subsection = new TypeBeatSettingsSubsection(new TypeBeatRuleset()),
            });
            AddUntilStep("settings controls loaded", () => subsection.IsLoaded
                && subsection.ChildrenOfType<FormSliderBar<float>>().Any()
                && subsection.ChildrenOfType<FormSliderBar<float>>().All(slider => slider.IsLoaded));
        }

        [Test]
        public void ApproachBarDefaultsLoadWithDisabledGradient()
        {
            AddStep("enable approach bars", () => config.SetValue(TypeBeatRulesetSetting.ApproachBars, true));
            openSettings();
            AddAssert("gradient remains disabled at zero", () => gradient.IsDisabled && gradient.Current.Value == 0);

            AddStep("disable approach bars", () => config.SetValue(TypeBeatRulesetSetting.ApproachBars, false));
            AddAssert("gradient becomes editable", () => !gradient.IsDisabled);
            AddStep("change gradient", () => gradient.Current.Value = 65);
            AddAssert("gradient change reaches config", () => config.Get<float>(TypeBeatRulesetSetting.PaceColourGradient) == 65);

            AddStep("enable approach bars again", () => config.SetValue(TypeBeatRulesetSetting.ApproachBars, true));
            AddAssert("gradient resets and becomes disabled", () => gradient.IsDisabled && gradient.Current.Value == 0);
            AddStep("close settings", Clear);
            openSettings();
            AddAssert("disabled gradient survives reopening", () => gradient.IsDisabled && gradient.Current.Value == 0);
        }
    }
}
