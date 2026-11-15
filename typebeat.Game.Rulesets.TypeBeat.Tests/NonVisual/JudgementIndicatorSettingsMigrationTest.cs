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
            (TypeBeatRulesetSetting.JudgementIndicator, "FadeIn"),
            (TypeBeatRulesetSetting.SyllableFadeInDuration, "275"),
            (TypeBeatRulesetSetting.TextPopIn, "False"),
            (TypeBeatRulesetSetting.TextPopInAmount, "12"),
            (TypeBeatRulesetSetting.ShowSyllableMarkers, "False"),
            (TypeBeatRulesetSetting.ShowPaceColours, "True"),
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
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ApproachBars), Is.False);
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.SyllableBrightness), Is.EqualTo(60f));
                Assert.That(config.Get<JudgementIndicatorMode>(TypeBeatRulesetSetting.JudgementIndicator), Is.EqualTo(JudgementIndicatorMode.BottomToTopFill));
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.SyllableFadeInDuration), Is.EqualTo(800f));
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.TextPopIn), Is.True);
                Assert.That(config.Get<float>(TypeBeatRulesetSetting.TextPopInAmount), Is.EqualTo(5f));
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ShowSyllableMarkers), Is.True);
                Assert.That(config.Get<bool>(TypeBeatRulesetSetting.ShowPaceColours), Is.False);
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
                Assert.That(reloaded.Get<JudgementIndicatorMode>(TypeBeatRulesetSetting.JudgementIndicator), Is.EqualTo(JudgementIndicatorMode.FadeIn));
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

        /// <summary>
        /// Backlog 475 revived Indicator Alignment under a NEW key. An install that ran a build between
        /// 2026-10-04 and 2026-10-25 still holds a stored SyllableFadeInEnd row, often at 0 (that era's
        /// shipped default); it must stay orphaned, so the new setting starts on the centre and neither
        /// alignment moves. Choices made on the new keys survive a restart, independently of each other.
        /// </summary>
        [TestCase("0")]
        [TestCase("30")]
        public void AStoredRetiredAlignmentRowDoesNotReachTheNewSettings(string retired)
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(r =>
                {
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.IndicatorDefaultsApplied), Value = "True" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = "SyllableFadeInEnd", Value = retired });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = "IndicatorAlignmentCentreApplied", Value = "True" });
                });

                using (var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7))
                {
                    Assert.That(config.Get<float>(TypeBeatRulesetSetting.IndicatorAlignment), Is.EqualTo(100f));
                    Assert.That(config.Get<float>(TypeBeatRulesetSetting.ApproachBarAlignment), Is.EqualTo(100f));

                    config.SetValue(TypeBeatRulesetSetting.IndicatorAlignment, 40f);
                    config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
                    config.SetValue(TypeBeatRulesetSetting.ApproachBarAlignment, 0f);
                    config.Save();
                }

                // The retired row is untouched (orphaned, never rewritten), and the new rows are separate.
                Assert.That(access.Realm.All<RealmRulesetSetting>().Single(s => s.Variant == 7 && s.Key == "SyllableFadeInEnd").Value, Is.EqualTo(retired));

                using var reloaded = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7);
                Assert.That(reloaded.Get<float>(TypeBeatRulesetSetting.IndicatorAlignment), Is.EqualTo(40f));
                Assert.That(reloaded.Get<float>(TypeBeatRulesetSetting.ApproachBarAlignment), Is.Zero);
            });
        }

        /// <summary>
        /// The alignments are greyed out (never rewritten) while they have nothing to move, following the
        /// pace colour gradient's convention: Approach Bar Alignment while Approach bars are off, and
        /// Indicator Alignment while the Judgement Indicator is None AND Text pop-in is off (it also
        /// times the pop-in). A persisted gate takes effect on load.
        /// </summary>
        [Test]
        public void AlignmentsAreDisabledOnlyWhileTheyHaveNothingToMove()
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(r =>
                {
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.IndicatorDefaultsApplied), Value = "True" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.ApproachBars), Value = "False" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.ApproachBarAlignment), Value = "25" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.JudgementIndicator), Value = "None" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.TextPopIn), Value = "False" });
                    r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.IndicatorAlignment), Value = "60" });
                });

                using var config = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7);
                var bar = config.GetBindable<float>(TypeBeatRulesetSetting.ApproachBarAlignment);
                var indicator = config.GetBindable<float>(TypeBeatRulesetSetting.IndicatorAlignment);

                Assert.Multiple(() =>
                {
                    Assert.That(bar.Disabled, Is.True);
                    Assert.That(bar.Value, Is.EqualTo(25f), "a disabled alignment keeps its stored value");
                    Assert.That(indicator.Disabled, Is.True);
                    Assert.That(indicator.Value, Is.EqualTo(60f), "a disabled alignment keeps its stored value");
                });

                config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
                Assert.That(bar.Disabled, Is.False);
                Assert.That(indicator.Disabled, Is.True, "approach bars do not gate the indicator alignment");

                config.SetValue(TypeBeatRulesetSetting.TextPopIn, true);
                Assert.That(indicator.Disabled, Is.False, "the pop-in alone still follows Indicator Alignment");
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, false);
                Assert.That(indicator.Disabled, Is.True);
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                Assert.That(indicator.Disabled, Is.False);
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.BottomToTopFill);
                Assert.That(indicator.Disabled, Is.False);

                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.None);
                Assert.That(bar.Disabled, Is.False, "the text indicator does not gate the bar alignment");
                Assert.That(config.ResetToDefaults(), Is.GreaterThan(0));
                Assert.That(bar.Value, Is.EqualTo(100f), "reset reaches a gated alignment");
                Assert.That(indicator.Value, Is.EqualTo(100f), "reset reaches a gated alignment");
            });
        }

        /// <summary>
        /// Backlog 465 moved the sung playhead's default from Line to None. A player who has already
        /// booted owns the Line row written then, and keeps it; only an install with no row starts on None.
        /// </summary>
        [Test]
        public void TheSungPlayheadDefaultReachesNewInstallsOnly()
        {
            withStore((access, settings, ruleset) =>
            {
                access.Write(r => r.Add(new RealmRulesetSetting { RulesetName = ruleset.ShortName, Variant = 7, Key = nameof(TypeBeatRulesetSetting.SungCaretStyle), Value = nameof(CaretStyle.Line) }));

                using (var existing = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 7))
                    Assert.That(existing.Get<CaretStyle>(TypeBeatRulesetSetting.SungCaretStyle), Is.EqualTo(CaretStyle.Line));

                using (var fresh = new TypeBeatRulesetConfigManager(settings, ruleset.RulesetInfo, 8))
                    Assert.That(fresh.Get<CaretStyle>(TypeBeatRulesetSetting.SungCaretStyle), Is.EqualTo(CaretStyle.None));
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
