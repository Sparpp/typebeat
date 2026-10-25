// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Platform;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Maintenance, "Reset settings to defaults": game settings and ruleset settings go back to their
    /// defaults and stay there after a restart, while login and bookkeeping survive.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class ResetSettingsToDefaultsTest
    {
        [Test]
        public void GameSettingsResetButLoginAndBookkeepingAreKept()
        {
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-reset-settings-" + Guid.NewGuid().ToString("N"));

            try
            {
                var storage = new NativeStorage(directory);

                using (var config = new OsuConfigManager(storage))
                {
                    config.SetValue(OsuSetting.UIScale, 1.3f);
                    config.SetValue(OsuSetting.ShowFpsDisplay, true);
                    config.SetValue(OsuSetting.MenuMusic, false);
                    config.SetValue(OsuSetting.Token, "token-value");
                    config.SetValue(OsuSetting.Username, "someone");
                    config.SetValue(OsuSetting.Version, "2026.1025.0");
                    config.SetValue(OsuSetting.ShowFirstRunSetup, false);

                    Assert.That(config.ResetToDefaults(), Is.GreaterThan(0));

                    Assert.Multiple(() =>
                    {
                        Assert.That(config.GetBindable<float>(OsuSetting.UIScale).IsDefault, Is.True);
                        Assert.That(config.GetBindable<bool>(OsuSetting.ShowFpsDisplay).IsDefault, Is.True);
                        Assert.That(config.GetBindable<bool>(OsuSetting.MenuMusic).IsDefault, Is.True);
                        Assert.That(config.Get<string>(OsuSetting.Token), Is.EqualTo("token-value"));
                        Assert.That(config.Get<string>(OsuSetting.Username), Is.EqualTo("someone"));
                        Assert.That(config.Get<string>(OsuSetting.Version), Is.EqualTo("2026.1025.0"));
                        Assert.That(config.Get<bool>(OsuSetting.ShowFirstRunSetup), Is.False);
                        Assert.That(config.Get<SeasonalBackgroundMode>(OsuSetting.SeasonalBackgroundMode), Is.EqualTo(SeasonalBackgroundMode.Never));
                    });
                }

                // The reset was saved, not only applied in memory.
                using var reloaded = new OsuConfigManager(storage);
                Assert.Multiple(() =>
                {
                    Assert.That(reloaded.GetBindable<float>(OsuSetting.UIScale).IsDefault, Is.True);
                    Assert.That(reloaded.GetBindable<bool>(OsuSetting.ShowFpsDisplay).IsDefault, Is.True);
                    Assert.That(reloaded.Get<string>(OsuSetting.Username), Is.EqualTo("someone"));
                });
            }
            finally
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
        }

        [Test]
        public void RulesetSettingsResetIncludingOneDisabledByAnotherSetting()
        {
            withStore((settings, ruleset) =>
            {
                using var defaults = new TypeBeatRulesetConfigManager(null, ruleset.RulesetInfo);

                using (var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo))
                {
                    config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 30f);
                    config.SetValue(TypeBeatRulesetSetting.SyllableBrightness, 23f);
                    // Character timing disables the alignment slider, so a single pass could not reset it.
                    config.SetValue(TypeBeatRulesetSetting.CharacterIndicatorTiming, true);
                    Assert.That(config.GetBindable<float>(TypeBeatRulesetSetting.SyllableFadeInEnd).Disabled, Is.True);
                    config.Save();

                    config.ResetToDefaults();

                    assertDefaults(config, defaults);
                }

                using var reloaded = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo);
                assertDefaults(reloaded, defaults);
            });
        }

        private static void assertDefaults(TypeBeatRulesetConfigManager config, TypeBeatRulesetConfigManager defaults) => Assert.Multiple(() =>
        {
            Assert.That(config.Get<bool>(TypeBeatRulesetSetting.CharacterIndicatorTiming), Is.EqualTo(defaults.Get<bool>(TypeBeatRulesetSetting.CharacterIndicatorTiming)));
            Assert.That(config.Get<float>(TypeBeatRulesetSetting.SyllableFadeInEnd), Is.EqualTo(defaults.Get<float>(TypeBeatRulesetSetting.SyllableFadeInEnd)));
            Assert.That(config.Get<float>(TypeBeatRulesetSetting.SyllableBrightness), Is.EqualTo(defaults.Get<float>(TypeBeatRulesetSetting.SyllableBrightness)));
        });

        private static void withStore(Action<SettingsStore, TypeBeatRuleset> action)
        {
            using var context = new SynchronousRealmTestContext();
            bool wasUpdateThread = ThreadSafety.IsUpdateThread;
            var updateThreadProperty = typeof(ThreadSafety).GetProperty(nameof(ThreadSafety.IsUpdateThread))!;
            updateThreadProperty.SetValue(null, true);
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-reset-ruleset-" + Guid.NewGuid().ToString("N"));

            try
            {
                using var access = new RealmAccess(new NativeStorage(directory), "client.realm");
                action(new SettingsStore(access), new TypeBeatRuleset());
            }
            finally
            {
                updateThreadProperty.SetValue(null, wasUpdateThread);
                Directory.Delete(directory, true);
            }
        }
    }
}
