// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    [NonParallelizable]
    public class CharacterIndicatorTimingSettingsTest
    {
        [Test]
        public void CharacterTimingDisablesAlignmentAndPreservesItsValueAcrossReloads()
        {
            using var context = new SynchronousRealmTestContext();
            bool wasUpdateThread = ThreadSafety.IsUpdateThread;
            var updateThreadProperty = typeof(ThreadSafety).GetProperty(nameof(ThreadSafety.IsUpdateThread))!;
            updateThreadProperty.SetValue(null, true);
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-character-indicator-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var access = new RealmAccess(new NativeStorage(directory), "client.realm");
                var settings = new SettingsStore(access);
                var ruleset = new TypeBeatRuleset();
                using (var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo))
                {
                    Assert.That(config.Get<bool>(TypeBeatRulesetSetting.CharacterIndicatorTiming), Is.False);
                    var controls = buildControls(config);
                    var alignment = controls.OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Indicator Alignment");
                    var characterTiming = controls.OfType<FormCheckBox>().Single(c => c.Caption.ToString() == "Character timing for indicators");
                    alignment.Current.Value = 73f;
                    characterTiming.Current.Value = true;
                    Assert.That(alignment.IsDisabled, Is.True);
                    Assert.That(alignment.Current.Value, Is.EqualTo(73f));
                    Assert.Throws<InvalidOperationException>(() => alignment.Current.Value = 0f);
                    // A disabled binding must also be safe when the settings menu is recreated.
                    var recreated = buildControls(config);
                    Assert.That(recreated.OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Indicator Alignment").IsDisabled, Is.True);
                    foreach (var control in recreated)
                        control.Dispose();
                    config.Save();
                    foreach (var control in controls)
                        control.Dispose();
                }
                using var reloaded = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo);
                Assert.That(reloaded.Get<bool>(TypeBeatRulesetSetting.CharacterIndicatorTiming), Is.True);
                var rebuilt = buildControls(reloaded);
                var restored = rebuilt.OfType<FormSliderBar<float>>().Single(c => c.Caption.ToString() == "Indicator Alignment");
                Assert.That(restored.IsDisabled, Is.True);
                reloaded.SetValue(TypeBeatRulesetSetting.CharacterIndicatorTiming, false);
                Assert.That(restored.IsDisabled, Is.False);
                Assert.That(restored.Current.Value, Is.EqualTo(73f));
                restored.Current.Value = 30f;
                Assert.That(reloaded.Get<float>(TypeBeatRulesetSetting.SyllableFadeInEnd), Is.EqualTo(30f));
                foreach (var control in rebuilt)
                    control.Dispose();
            }
            finally
            {
                updateThreadProperty.SetValue(null, wasUpdateThread);
                Directory.Delete(directory, true);
            }
        }

        private static Drawable[] buildControls(TypeBeatRulesetConfigManager config) =>
            new TypeBeatSettingsSubsection(new TypeBeatRuleset()).BuildControls(config).OfType<SettingsItemV2>().Select(c => (Drawable)c.Control).ToArray();
    }
}
