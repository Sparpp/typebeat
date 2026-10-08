// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using typebeat.Game.Overlays.Mods;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Screens.Select;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneCustomRatePitch : OsuTestScene
    {
        private UserModSelectOverlay overlay = null!;
        private ModRateAdjust rateMod = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private PitchSettingsSlider pitchSlider => overlay.ChildrenOfType<PitchSettingsSlider>().Single();
        private SettingsCheckbox automatic => overlay.ChildrenOfType<SettingsCheckbox>()
            .Single(c => c.LabelText.ToString() == "Adjust pitch automatically");

        [TestCase("DT")]
        [TestCase("HT")]
        public void TestSelectingOtherModsAndChangingSpeedWithAutomaticPitch(string acronym)
        {
            AddStep("open mod menu", () =>
            {
                SelectedMods.Value = Array.Empty<Mod>();
                Ruleset.Value = new TypeBeatRuleset().RulesetInfo;
                Beatmap.Value = CreateWorkingBeatmap(Ruleset.Value);
                Child = overlay = new UserModSelectOverlay
                {
                    RelativeSizeAxes = Axes.Both,
                    State = { Value = Visibility.Visible },
                    Beatmap = { Value = Beatmap.Value },
                    SelectedMods = { BindTarget = SelectedMods },
                };
            });
            AddUntilStep("rate panel loaded", () => overlay.ChildrenOfType<ModPanel>().Any(p => p.Mod.Acronym == acronym && p.IsLoaded));
            AddStep("select rate mod", () => overlay.ChildrenOfType<ModPanel>().Single(p => p.Mod.Acronym == acronym).TriggerClick());
            AddUntilStep("rate mod selected", () => SelectedMods.Value.Any(m => m.Acronym == acronym));
            AddStep("open customisation", () => overlay.ChildrenOfType<ModCustomisationPanel>().Single().ExpandedState.Value
                = ModCustomisationPanel.ModCustomisationPanelState.ExpandedByMod);
            AddUntilStep("pitch controls loaded", () => overlay.ChildrenOfType<PitchSettingsSlider>().Any(s => s.IsLoaded));
            AddStep("enable automatic pitch", () =>
            {
                rateMod = SelectedMods.Value.OfType<ModRateAdjust>().Single();
                automatic.Current.Value = true;
                rateMod.SpeedChange.Value = acronym == "DT" ? 1.7 : 0.7;
            });
            AddAssert("pitch slider is disabled and dimmed", () => pitchSlider.Current.Disabled
                && pitchSlider.ChildrenOfType<PitchSettingsSlider.PitchRoundedSliderBar>().Single().Alpha == 0.3f);

            // This selection rebuilds ModSettingChangeTracker and the visible customisation controls.
            AddStep("select another mod", () => overlay.ChildrenOfType<ModPanel>().Single(p => p.Mod is TypeBeatModLiterate).TriggerClick());
            AddStep("reopen customisation", () => overlay.ChildrenOfType<ModCustomisationPanel>().Single().ExpandedState.Value
                = ModCustomisationPanel.ModCustomisationPanelState.ExpandedByMod);
            AddUntilStep("new pitch controls loaded", () => overlay.ChildrenOfType<PitchSettingsSlider>().Any(s => s.IsLoaded));
            AddAssert("automatic pitch survives selecting another mod", () => automatic.Current.Value
                && pitchSlider.Current.Disabled
                && Math.Abs(pitchSlider.Current.Value - 12 * Math.Log2(rateMod.SpeedChange.Value)) < 1e-9);

            AddStep("change speed with hotkey handler", () =>
            {
                var handler = overlay.ChildrenOfType<ModSpeedHotkeyHandler>().Single();
                Assert.That(handler.ChangeSpeed(acronym == "DT" ? 0.05 : -0.05,
                    overlay.AllAvailableMods.Where(m => m.ValidForSelection.Value).Select(m => m.Mod)), Is.True);
            });
            AddUntilStep("pitch slider follows updated speed", () => overlay.ChildrenOfType<PitchSettingsSlider>().Any(s => s.IsLoaded)
                && Math.Abs(pitchSlider.Current.Value - 12 * Math.Log2(rateMod.SpeedChange.Value)) < 1e-9);
            AddAssert("automatic pitch remains locked", () => pitchSlider.Current.Disabled);
            AddStep("disable automatic pitch", () => automatic.Current.Value = false);
            AddAssert("manual adjustment becomes available", () => !pitchSlider.Current.Disabled);
            AddStep("adjust pitch manually", () => pitchSlider.Current.Value = -2);
            AddAssert("manual value applied", () => pitchSlider.Current.Value == -2);
        }
    }
}
