// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Logging;
using osu.Framework.Platform;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    [NonParallelizable]
    public class PaceColourSettingsMigrationTest
    {
        [TestCase(null)]
        [TestCase("PreviousSegment")]
        [TestCase("AccelerationBased")]
        [TestCase("MapRelative")]
        [TestCase("WholeMap")]
        [TestCase("unknown-old-mode")]
        public void ExistingAndNewPlayersGetMapRelativeWithoutWarnings(string? oldMode)
        {
            using var context = new SynchronousRealmTestContext();
            bool wasUpdateThread = ThreadSafety.IsUpdateThread;
            // RulesetConfigManager uses the update-thread Realm context. Mark this synchronous
            // test thread through the framework's internal setter, then restore it below.
            var updateThreadProperty = typeof(ThreadSafety).GetProperty(nameof(ThreadSafety.IsUpdateThread))!;
            updateThreadProperty.SetValue(null, true);
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-pace-config-" + Guid.NewGuid().ToString("N"));
            var warnings = new ConcurrentQueue<string>();
            void log(LogEntry entry)
            {
                if (entry.Message.Contains("PaceColourMode could not be read"))
                    warnings.Enqueue(entry.Message);
            }

            Logger.NewEntry += log;
            try
            {
                using var access = new RealmAccess(new NativeStorage(directory), "client.realm");
                var ruleset = new TypeBeatRuleset();
                var settings = new SettingsStore(access);
                if (oldMode != null)
                {
                    access.Write(r => r.Add(new RealmRulesetSetting
                    {
                        RulesetName = ruleset.ShortName, Variant = 7,
                        Key = nameof(TypeBeatRulesetSetting.PaceColourMode), Value = oldMode,
                    }));
                }

                // The unrelated variant survives; the indicator preset also resets maximum change.
                access.Write(r =>
                {
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 0, Key = nameof(TypeBeatRulesetSetting.PaceColourMode), Value = "PreviousSegment" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.PaceColourMaxChange), Value = "75" });
                });

                using (var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7))
                {
                    Assert.That(config.Get<PaceColourMode>(TypeBeatRulesetSetting.PaceColourMode), Is.EqualTo(PaceColourMode.MapRelative));
                    Assert.That(config.Get<float>(TypeBeatRulesetSetting.PaceColourMaxChange), Is.EqualTo(100f));
                    Assert.That(config.Get<float>(TypeBeatRulesetSetting.PaceColourOpacityCurve), Is.Zero);
                    Assert.That(access.Realm.All<RealmRulesetSetting>().Single(s => s.RulesetName == ruleset.ShortName && s.Variant == 7 && s.Key == nameof(TypeBeatRulesetSetting.PaceColourMode)).Value,
                        Is.EqualTo("MapRelative"), "migration must rewrite the row before parsing it");
                    Assert.That(access.Realm.All<RealmRulesetSetting>().Single(s => s.RulesetName == ruleset.ShortName && s.Variant == 0 && s.Key == nameof(TypeBeatRulesetSetting.PaceColourMode)).Value, Is.EqualTo("PreviousSegment"));
                    config.SetValue(TypeBeatRulesetSetting.PaceColourMode, PaceColourMode.AccelerationBased);
                    config.Save();
                }

                using (var reloaded = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7))
                    Assert.That(reloaded.Get<PaceColourMode>(TypeBeatRulesetSetting.PaceColourMode), Is.EqualTo(PaceColourMode.AccelerationBased), "a later explicit choice must survive restart");

                // A downgrade can write the retired name again after the marker exists.
                access.Write(r => r.All<RealmRulesetSetting>().Single(s => s.RulesetName == ruleset.ShortName && s.Variant == 7 && s.Key == nameof(TypeBeatRulesetSetting.PaceColourMode)).Value = "PreviousSegment");
                using (var healed = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7))
                    Assert.That(healed.Get<PaceColourMode>(TypeBeatRulesetSetting.PaceColourMode), Is.EqualTo(PaceColourMode.MapRelative));
                Logger.Flush();
                Assert.That(warnings, Is.Empty);
            }
            finally
            {
                Logger.NewEntry -= log;
                updateThreadProperty.SetValue(null, wasUpdateThread);
                Directory.Delete(directory, true);
            }
        }
    }
}
