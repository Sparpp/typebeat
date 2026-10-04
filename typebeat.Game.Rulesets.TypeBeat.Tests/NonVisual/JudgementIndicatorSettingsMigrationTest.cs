// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
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
    public class JudgementIndicatorSettingsMigrationTest
    {
        private static readonly (TypeBeatRulesetSetting Setting, string Value)[] customSettings =
        {
            (TypeBeatRulesetSetting.UseSpaceErrorDot, "False"),
            (TypeBeatRulesetSetting.ApproachBars, "False"),
            (TypeBeatRulesetSetting.SyllableBrightness, "23"),
            (TypeBeatRulesetSetting.JudgementIndicator, "BottomToTopFill"),
            (TypeBeatRulesetSetting.SyllableFadeInEnd, "70"),
            (TypeBeatRulesetSetting.SyllableFadeInDuration, "275"),
            (TypeBeatRulesetSetting.TextPopIn, "False"),
            (TypeBeatRulesetSetting.TextPopInAmount, "12"),
            (TypeBeatRulesetSetting.ShowSyllableMarkers, "False"),
            (TypeBeatRulesetSetting.ShowPaceColours, "False"),
            (TypeBeatRulesetSetting.PaceColourMode, "AccelerationBased"),
            (TypeBeatRulesetSetting.PaceColourMaxChange, "75"),
            (TypeBeatRulesetSetting.PaceColourOpacityCurve, "65"),
            (TypeBeatRulesetSetting.PaceColourGradient, "42"),
            (TypeBeatRulesetSetting.CaretSmoothing, "45"),
        };

        private static void assertPreset(TypeBeatRulesetConfigManager config)
        {
            Assert.Multiple(() =>
            {
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.UseSpaceErrorDot), Is.True);
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ApproachBars), Is.True);
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.SyllableBrightness), Is.EqualTo(60f));
                Assert.That(config.Get<JudgementIndicatorMode>(TypeBeatRulesetSetting.JudgementIndicator), Is.EqualTo(JudgementIndicatorMode.FadeIn));
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.SyllableFadeInEnd), Is.Zero);
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.SyllableFadeInDuration), Is.EqualTo(800f));
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.TextPopIn), Is.True);
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.TextPopInAmount), Is.EqualTo(5f));
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ShowSyllableMarkers), Is.True);
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ShowPaceColours), Is.True);
                Assert.That(config.Get<PaceColourMode>(TypeBeatRulesetSetting.PaceColourMode), Is.EqualTo(PaceColourMode.MapRelative));
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.PaceColourMaxChange), Is.EqualTo(100f));
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.PaceColourOpacityCurve), Is.Zero);
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.PaceColourGradient), Is.Zero);
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.CaretSmoothing), Is.EqualTo(20f));
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.IndicatorDefaultsApplied), Is.True);
            });
        }

        [Test]
        public void NewPlayersReceiveTheCompletePreset()
        {
            using var config = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo);
            assertPreset(config);
        }

        [TestCase(null)]
        [TestCase("None")]
        [TestCase("BottomToTopFill")]
        [TestCase("FloatingBars")]
        [TestCase("RightToLeftFill")]
        [TestCase("CentreOutHorizontal")]
        [TestCase("CentreOutVertical")]
        public void ExistingChoicesResetOnceAndEveryLaterChoiceSurvivesRestart(string? oldIndicator)
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(r =>
                {
                    foreach (var (setting, value) in customSettings)
                    {
                        if (setting == TypeBeatRulesetSetting.JudgementIndicator && oldIndicator == null)
                            continue;
                        r.Add(new RealmRulesetSetting
                        {
                            RulesetName = ruleset.ShortName, Variant = 7, Key = setting.ToString(),
                            Value = setting == TypeBeatRulesetSetting.JudgementIndicator ? oldIndicator! : value,
                        });
                    }
                    // Even users who already completed the older pace migration receive this preset.
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.PaceColourMapRelativeDefaultApplied), Value = "True" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.SyllableFadeIn), Value = "False" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.LyricFont), Value = "OpenDyslexic" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 0, Key = nameof(TypeBeatRulesetSetting.SyllableBrightness), Value = "11" });
                });

                using (var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7))
                {
                    assertPreset(config);
                    Assert.That(config.Get<string>(TypeBeatRulesetSetting.LyricFont), Is.EqualTo("OpenDyslexic"));
                    Assert.That(access.Realm.All<RealmRulesetSetting>().Single(s => s.Variant == 0 && s.Key == nameof(TypeBeatRulesetSetting.SyllableBrightness)).Value, Is.EqualTo("11"));
                    Assert.That(access.Realm.All<RealmRulesetSetting>().Any(s => s.Variant == 0 && s.Key == nameof(TypeBeatRulesetSetting.IndicatorDefaultsApplied)), Is.False);

                    // Disable bars before changing the gradient, then customise every migrated setting.
                    foreach (var (setting, value) in customSettings)
                    {
                        if (bool.TryParse(value, out bool flag))
                            config.SetValue(setting, flag);
                        else if (setting == TypeBeatRulesetSetting.JudgementIndicator)
                            config.SetValue(setting, Enum.Parse<JudgementIndicatorMode>(value));
                        else if (setting == TypeBeatRulesetSetting.PaceColourMode)
                            config.SetValue(setting, Enum.Parse<PaceColourMode>(value));
                        else
                            config.SetValue(setting, float.Parse(value, CultureInfo.InvariantCulture));
                    }
                    config.Save();
                }

                using var reloaded = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7);
                foreach (var (setting, value) in customSettings)
                {
                    string stored = access.Realm.All<RealmRulesetSetting>().Single(s => s.Variant == 7 && s.Key == setting.ToString()).Value;
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float expected))
                        Assert.That(float.Parse(stored, CultureInfo.InvariantCulture), Is.EqualTo(expected), setting.ToString());
                    else
                        Assert.That(stored, Is.EqualTo(value), setting.ToString());
                }
                Assert.That(reloaded.Get<JudgementIndicatorMode>(TypeBeatRulesetSetting.JudgementIndicator), Is.EqualTo(JudgementIndicatorMode.BottomToTopFill));
                Assert.That(reloaded.Get<bool>(TypeBeatRulesetSetting.ApproachBars), Is.False);
                Assert.That(reloaded.Get<float>(TypeBeatRulesetSetting.PaceColourGradient), Is.EqualTo(42f));
            });
        }

        [Test]
        public void PersistedApproachBarsDisableGradientWithoutReplacingTheTextIndicator()
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(r =>
                {
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.IndicatorDefaultsApplied), Value = "True" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.ApproachBars), Value = "True" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.JudgementIndicator), Value = "BottomToTopFill" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.PaceColourGradient), Value = "73" });
                });
                using var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7);
                Assert.That(config.Get<JudgementIndicatorMode>(TypeBeatRulesetSetting.JudgementIndicator), Is.EqualTo(JudgementIndicatorMode.BottomToTopFill));
                Assert.That(config.GetBindable<float>(TypeBeatRulesetSetting.PaceColourGradient).Value, Is.Zero);
                Assert.That(config.GetBindable<float>(TypeBeatRulesetSetting.PaceColourGradient).Disabled, Is.True);
            });
        }

        private static void withStore(Action<RealmAccess, SettingsStore, TypeBeatRuleset> action)
        {
            using var context = new SynchronousRealmTestContext();
            bool wasUpdateThread = ThreadSafety.IsUpdateThread;
            var updateThreadProperty = typeof(ThreadSafety).GetProperty(nameof(ThreadSafety.IsUpdateThread))!;
            updateThreadProperty.SetValue(null, true);
            string directory = Path.Combine(Path.GetTempPath(), "typebeat-indicator-config-" + Guid.NewGuid().ToString("N"));
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
