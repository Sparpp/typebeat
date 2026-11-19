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
    /// <summary>
    /// Backlog 499: the "Keyboard hitsounds" checkbox became a three-way choice. A ticked box meant the
    /// per-syllable click, so it migrates to <see cref="KeyboardHitsoundMode.PerSyllable"/>; an unticked
    /// or never-saved one to <see cref="KeyboardHitsoundMode.Off"/>. The new row is the marker, so a later
    /// choice survives an older build flipping the retired checkbox, and an existing row is never rewritten.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class KeyboardHitsoundModeSettingsMigrationTest
    {
        [TestCase(null, KeyboardHitsoundMode.Off)]
        [TestCase("True", KeyboardHitsoundMode.PerSyllable)]
        [TestCase("False", KeyboardHitsoundMode.Off)]
        public void MigratesCheckboxOnceAndPreservesLaterChoices(string? legacy, KeyboardHitsoundMode expected)
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(realm =>
                {
                    if (legacy != null)
                        realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.KeyboardHitsounds, legacy));
                    realm.Add(row(ruleset, 0, TypeBeatRulesetSetting.KeyboardHitsounds, "True"));
                });

                using (var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7))
                {
                    Assert.That(config.Get<KeyboardHitsoundMode>(TypeBeatRulesetSetting.KeyboardHitsoundMode), Is.EqualTo(expected));
                    Assert.That(access.Realm.All<RealmRulesetSetting>().Any(s => s.Variant == 0 && s.Key == nameof(TypeBeatRulesetSetting.KeyboardHitsoundMode)), Is.False,
                        "only the variant being loaded is migrated");
                    config.SetValue(TypeBeatRulesetSetting.KeyboardHitsoundMode, KeyboardHitsoundMode.PerCharacter);
                    config.Save();
                }

                // Running an older build must not undo a later choice.
                access.Write(realm =>
                {
                    var old = realm.All<RealmRulesetSetting>().SingleOrDefault(s => s.Variant == 7 && s.Key == nameof(TypeBeatRulesetSetting.KeyboardHitsounds));
                    if (old == null)
                        realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.KeyboardHitsounds, "False"));
                    else
                        old.Value = "False";
                });

                using var reloaded = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7);
                Assert.That(reloaded.Get<KeyboardHitsoundMode>(TypeBeatRulesetSetting.KeyboardHitsoundMode), Is.EqualTo(KeyboardHitsoundMode.PerCharacter));
            });
        }

        [TestCase(KeyboardHitsoundMode.Off)]
        [TestCase(KeyboardHitsoundMode.PerSyllable)]
        [TestCase(KeyboardHitsoundMode.PerCharacter)]
        public void ExistingChoiceSurvivesMigration(KeyboardHitsoundMode mode)
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(realm =>
                {
                    realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.KeyboardHitsoundMode, mode.ToString()));
                    // A checkbox left saying the opposite of the stored choice must not override it.
                    realm.Add(row(ruleset, 7, TypeBeatRulesetSetting.KeyboardHitsounds, mode == KeyboardHitsoundMode.Off ? "True" : "False"));
                });

                using var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7);
                Assert.That(config.Get<KeyboardHitsoundMode>(TypeBeatRulesetSetting.KeyboardHitsoundMode), Is.EqualTo(mode));
            });
        }

        [Test]
        public void NoStoreDefaultsToOff()
        {
            using var config = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo);
            Assert.That(config.Get<KeyboardHitsoundMode>(TypeBeatRulesetSetting.KeyboardHitsoundMode), Is.EqualTo(KeyboardHitsoundMode.Off));
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
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-hitsound-mode-config-" + Guid.NewGuid().ToString("N"));
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
