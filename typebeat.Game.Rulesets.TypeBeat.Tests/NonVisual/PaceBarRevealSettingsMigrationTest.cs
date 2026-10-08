// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Platform;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    [NonParallelizable]
    public class PaceBarRevealSettingsMigrationTest
    {
        [TestCase(null, PaceBarRevealMode.Off)]
        [TestCase("True", PaceBarRevealMode.Build)]
        [TestCase("False", PaceBarRevealMode.Off)]
        public void MigratesCheckboxOnceAndPreservesLaterChoices(string? legacy, PaceBarRevealMode expected)
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(realm =>
                {
                    if (legacy != null)
                        realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.BuildPaceBarWithApproach, legacy));
                    realm.Add(row(ruleset, 0, TypeBeatRulesetSetting.BuildPaceBarWithApproach, "False"));
                    realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.IndicatorDefaultsApplied, "True"));
                    realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.ApproachBars, "False"));
                });
                using (var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7))
                {
                    Assert.That(config.Get<PaceBarRevealMode>(TypeBeatRulesetSetting.PaceBarReveal), Is.EqualTo(expected));
                    Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ApproachBars), Is.False);
                    Assert.That(access.Realm.All<RealmRulesetSetting>().Any(s => s.Variant == 0 && s.Key == nameof(TypeBeatRulesetSetting.PaceBarReveal)), Is.False);
                    config.SetValue(TypeBeatRulesetSetting.PaceBarReveal, PaceBarRevealMode.ColourIn);
                    config.Save();
                }
                // Running an older build must not undo a later dropdown choice.
                access.Write(realm => realm.All<RealmRulesetSetting>().Single(s => s.Variant == 7 && s.Key == nameof(TypeBeatRulesetSetting.BuildPaceBarWithApproach)).Value = "False");
                using var reloaded = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7);
                Assert.That(reloaded.Get<PaceBarRevealMode>(TypeBeatRulesetSetting.PaceBarReveal), Is.EqualTo(PaceBarRevealMode.ColourIn));
            });
        }

        [TestCase(PaceBarRevealMode.Off)]
        [TestCase(PaceBarRevealMode.Build)]
        [TestCase(PaceBarRevealMode.ColourIn)]
        public void ExistingDropdownChoiceSurvivesMigration(PaceBarRevealMode mode)
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(realm =>
                {
                    realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.PaceBarReveal, mode.ToString()));
                    realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.BuildPaceBarWithApproach, "False"));
                });
                using var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7);
                Assert.That(config.Get<PaceBarRevealMode>(TypeBeatRulesetSetting.PaceBarReveal), Is.EqualTo(mode));
            });
        }

        private static RealmRulesetSetting row(TypeBeatRuleset ruleset, int variant, TypeBeatRulesetSetting key, string value) => new RealmRulesetSetting
        {
            RulesetName = ruleset.ShortName, Variant = variant, Key = key.ToString(), Value = value,
        };

        private static void withStore(Action<RealmAccess, SettingsStore, TypeBeatRuleset> action)
        {
            using var context = new SynchronousRealmTestContext();
            bool wasUpdateThread = ThreadSafety.IsUpdateThread;
            var updateThreadProperty = typeof(ThreadSafety).GetProperty(nameof(ThreadSafety.IsUpdateThread))!;
            updateThreadProperty.SetValue(null, true);
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-pace-reveal-config-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var access = new RealmAccess(new NativeStorage(directory), "client.realm");
                action(access, new SettingsStore(access), new TypeBeatRuleset());
            }
            finally
            {
                updateThreadProperty.SetValue(null, wasUpdateThread);
                Directory.Delete(directory, true);
            }
        }
    }
}
