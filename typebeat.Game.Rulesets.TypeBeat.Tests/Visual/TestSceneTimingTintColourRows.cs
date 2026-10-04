// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The timing tint's two colour rows in the Experimental settings, typed into as a player would:
    /// a typed hex lands in the stored setting, the field spells it back without an alpha suffix, and
    /// a second colour can be typed over the first.
    /// </summary>
    public partial class TestSceneTimingTintColourRows : OsuManualInputManagerTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Aquamarine);

        private TypeBeatRulesetConfigManager config = null!;
        private FormColourSwatch[] rows = null!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("build the experimental settings", () =>
            {
                var ruleset = new TypeBeatRuleset();
                config = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo);

                var controls = ((TypeBeatExperimentalSettingsSubsection)ruleset.CreateExperimentalSettings()!).BuildControls(config);
                rows = controls.OfType<Container>().Select(c => c.Children.FirstOrDefault()).OfType<FormColourSwatch>().ToArray();

                Child = new PopoverContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Width = 0.5f,
                        Children = controls,
                    },
                };
            });
        }

        [Test]
        public void TestTypingAnEarlyColour()
        {
            commitHex(0, "#00ff00");

            AddAssert("the setting stores it", () => config.Get<string>(TypeBeatRulesetSetting.TimingTintEarlyColour) == "#00ff00");
            AddAssert("the row holds it opaque", () => rows[0].Current.Value.A == 1);
            AddAssert("the field spells it without a suffix", () => hexIs(0, "#00ff00"));

            commitHex(0, "123456");

            AddAssert("a second colour takes too", () => config.Get<string>(TypeBeatRulesetSetting.TimingTintEarlyColour) == "#123456");
            AddAssert("and is spelled cleanly", () => hexIs(0, "#123456"));
        }

        [Test]
        public void TestTypingALateColour()
        {
            commitHex(1, "#ff00ff");

            AddAssert("the setting stores it", () => config.Get<string>(TypeBeatRulesetSetting.TimingTintLateColour) == "#ff00ff");
            AddAssert("the field spells it without a suffix", () => hexIs(1, "#ff00ff"));
            AddAssert("the early colour is untouched", () => config.Get<string>(TypeBeatRulesetSetting.TimingTintEarlyColour) == "#ff8a3d");
        }

        private void commitHex(int row, string value)
        {
            TextBox box() => rows[row].HexBox.ChildrenOfType<TextBox>().Single();

            AddStep("focus the hex field", () => rows[row].HexBox.TriggerClick());
            AddUntilStep("hex field focused", () => box().HasFocus);
            AddStep($"type '{value}' and press Enter", () =>
            {
                box().Text = value;
                InputManager.Key(Key.Enter);
            });
        }

        private bool hexIs(int row, string expected) => string.Equals(rows[row].HexBox.Current.Value, expected, System.StringComparison.OrdinalIgnoreCase);
    }
}
