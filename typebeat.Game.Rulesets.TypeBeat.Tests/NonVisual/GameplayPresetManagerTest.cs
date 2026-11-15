// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Bindables;
using osu.Framework.Development;
using osu.Framework.Testing;
using typebeat.Game.Configuration;
using typebeat.Game.Configuration.Presets;
using typebeat.Game.Database;
using typebeat.Game.Rulesets.TypeBeat.Configuration;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 484: what selecting, editing, saving, deleting, importing and resetting do to Gameplay
    /// presets and to the settings they cover, against the real game and ruleset configs.
    /// </summary>
    [TestFixture]
    public class GameplayPresetManagerTest
    {
        private TemporaryNativeStorage storage = null!;
        private OsuConfigManager game = null!;
        private TypeBeatRulesetConfigManager ruleset = null!;
        private GameplayPresetManager presets = null!;

        [SetUp]
        public void SetUp()
        {
            storage = new TemporaryNativeStorage($"gameplay-presets-{Guid.NewGuid():N}");
            game = new OsuConfigManager(storage);
            ruleset = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo);
        }

        [TearDown]
        public void TearDown()
        {
            presets?.Dispose();
            ruleset.Dispose();
            game.Dispose();
            storage.Dispose();
        }

        [Test]
        public void ANewInstallStartsOnMinimal()
        {
            Assert.That(GameplayPresetManager.HasRunBefore(game), Is.False);

            start(GameplayPresetManager.HasRunBefore(game));

            Assert.That(presets.Current.Value, Is.SameAs(presets.Minimal));
            Assert.That(presets.Entries, Is.EqualTo(new[] { presets.Minimal, presets.Custom }));
        }

        [Test]
        public void AnExistingInstallShowsCustomWithItsSettingsUnchanged()
        {
            game.SetValue(OsuSetting.Version, "2026.1101.0");
            game.SetValue(OsuSetting.DimLevel, 0.7);
            ruleset.SetValue(TypeBeatRulesetSetting.CaretStyle, CaretStyle.Block);
            ruleset.SetValue(TypeBeatRulesetSetting.TimingTintEarlyColour, "#123456");
            ruleset.SetValue(TypeBeatRulesetSetting.LyricFont, "Some Installed Font");

            var before = snapshot();

            start(GameplayPresetManager.HasRunBefore(game));

            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));
            Assert.That(snapshot(), Is.EqualTo(before));
        }

        [Test]
        public void AnExistingInstallStillAtTheDefaultsAlsoShowsCustom()
        {
            game.SetValue(OsuSetting.Version, "2026.1101.0");

            start(GameplayPresetManager.HasRunBefore(game));

            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));
        }

        [Test]
        public void ANewLookingInstallWhoseSettingsAreNotDefaultsShowsCustomAndNothingMoves()
        {
            // game.ini (which holds the run-before stamp) was deleted; the ruleset settings in client.realm stayed.
            ruleset.SetValue(TypeBeatRulesetSetting.ShowSyncMetric, true);
            ruleset.SetValue(TypeBeatRulesetSetting.LineSpacing, 150f);
            var before = snapshot();

            start(GameplayPresetManager.HasRunBefore(game));

            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));
            Assert.That(snapshot(), Is.EqualTo(before));
        }

        [Test]
        public void ChangingACoveredSettingSwitchesToCustom()
        {
            start(false);

            game.SetValue(OsuSetting.DimLevel, 0.55);
            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom), "from Minimal");

            var named = presets.SaveCustomAs("Mine")!;
            Assert.That(presets.Current.Value, Is.SameAs(named));

            ruleset.SetValue(TypeBeatRulesetSetting.ShowSyncMetric, true);
            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom), "from a named preset");
        }

        [Test]
        public void ChangesAreStillSeenAfterAGarbageCollection()
        {
            start(false);

            // Configs link to their bound copies weakly; the manager must hold what it listens through.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            ruleset.SetValue(TypeBeatRulesetSetting.ShowSyncMetric, true);
            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            presets.Select(presets.Minimal);
            Assert.That(ruleset.Get<bool>(TypeBeatRulesetSetting.ShowSyncMetric), Is.False);
        }

        [Test]
        public void ChangingAnExcludedSettingKeepsTheSelection()
        {
            start(false);

            ruleset.SetValue(TypeBeatRulesetSetting.LyricFont, "Some Installed Font");
            game.SetValue(OsuSetting.GameplayDisableWinKey, false);

            Assert.That(presets.Current.Value, Is.SameAs(presets.Minimal));
        }

        [Test]
        public void CustomIsRestoredAfterSelectingMinimal()
        {
            start(false);

            game.SetValue(OsuSetting.DimLevel, 0.7);
            ruleset.SetValue(TypeBeatRulesetSetting.CaretStyle, CaretStyle.Block);
            ruleset.SetValue(TypeBeatRulesetSetting.TimingTintLateColour, "#abcdef");
            var custom = snapshot();

            presets.Select(presets.Minimal);

            Assert.That(game.Get<double>(OsuSetting.DimLevel), Is.EqualTo(0.4));
            Assert.That(coveredAreDefault(), Is.True);

            presets.Select(presets.Custom);

            Assert.That(snapshot(), Is.EqualTo(custom));
        }

        [Test]
        public void RenameOnCustomSavesItAsANamedPresetAndTheNextChangeStartsAFreshCustom()
        {
            start(false);

            ruleset.SetValue(TypeBeatRulesetSetting.LineSpacing, 150f);
            var saved = snapshot();

            var mine = presets.SaveCustomAs("Mine")!;

            Assert.That(mine.Kind, Is.EqualTo(GameplayPresetKind.Saved));
            Assert.That(presets.Current.Value, Is.SameAs(mine));
            Assert.That(presets.Entries, Is.EqualTo(new[] { presets.Minimal, mine, presets.Custom }));

            ruleset.SetValue(TypeBeatRulesetSetting.LineSpacing, 60f);
            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));

            presets.Select(mine);
            Assert.That(snapshot(), Is.EqualTo(saved), "the named preset kept what Custom held when it was named");

            presets.Select(presets.Custom);
            Assert.That(ruleset.Get<float>(TypeBeatRulesetSetting.LineSpacing), Is.EqualTo(60f), "the fresh Custom has the later change");
        }

        [Test]
        public void SavingOnlyWorksFromCustomAndNeedsAName()
        {
            start(false);

            Assert.That(presets.SaveCustomAs("Mine"), Is.Null, "Minimal is selected");

            game.SetValue(OsuSetting.BlurLevel, 0.5);

            Assert.That(presets.SaveCustomAs("   "), Is.Null);
            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));
        }

        [Test]
        public void NamesStayUnique()
        {
            start(false);

            game.SetValue(OsuSetting.BlurLevel, 0.5);
            var first = presets.SaveCustomAs("Mine")!;
            game.SetValue(OsuSetting.BlurLevel, 0.6);
            var second = presets.SaveCustomAs("mine")!;
            game.SetValue(OsuSetting.BlurLevel, 0.7);
            var reserved = presets.SaveCustomAs("Custom")!;

            Assert.That(new[] { first.Name, second.Name, reserved.Name }, Is.EqualTo(new[] { "Mine", "mine (2)", "Custom (2)" }));

            var renamed = presets.Rename(second, "Mine")!;
            Assert.That(renamed.Name, Is.EqualTo("Mine (2)"));
            Assert.That(presets.Entries, Does.Contain(renamed).And.Not.Contain(second));
        }

        [Test]
        public void RenamingTheSelectedPresetKeepsItSelected()
        {
            start(false);

            game.SetValue(OsuSetting.BlurLevel, 0.5);
            var mine = presets.SaveCustomAs("Mine")!;

            var renamed = presets.Rename(mine, "Ours")!;

            Assert.That(presets.Current.Value, Is.SameAs(renamed));
            Assert.That(renamed.Name, Is.EqualTo("Ours"));
            Assert.That(presets.Export(renamed), Is.EqualTo(presets.Export(presets.Custom)));
        }

        [Test]
        public void BuiltInEntriesCannotBeDeletedAndMinimalCannotBeRenamed()
        {
            start(false);

            Assert.Multiple(() =>
            {
                Assert.That(presets.CanDelete(presets.Minimal), Is.False);
                Assert.That(presets.CanDelete(presets.Custom), Is.False);
                Assert.That(presets.CanRename(presets.Minimal), Is.False);
                Assert.That(presets.CanRename(presets.Custom), Is.True, "Rename on Custom names it");

                Assert.That(presets.Delete(presets.Minimal), Is.False);
                Assert.That(presets.Delete(presets.Custom), Is.False);
                Assert.That(presets.Rename(presets.Minimal, "Other"), Is.Null);
                Assert.That(presets.Entries, Is.EqualTo(new[] { presets.Minimal, presets.Custom }));
            });
        }

        [Test]
        public void DeletingTheSelectedPresetLeavesTheSettingsAsCustom()
        {
            start(false);

            game.SetValue(OsuSetting.DimLevel, 0.9);
            var mine = presets.SaveCustomAs("Mine")!;
            Assert.That(presets.CanDelete(mine), Is.True);

            var before = snapshot();

            Assert.That(presets.Delete(mine), Is.True);
            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));
            Assert.That(presets.Entries, Does.Not.Contain(mine));
            Assert.That(snapshot(), Is.EqualTo(before));
        }

        [Test]
        public void ApplyingAPresetNeverWritesAnExcludedSetting()
        {
            start(false);

            game.SetValue(OsuSetting.DimLevel, 0.9);
            string code = presets.Export(presets.Custom);

            ruleset.SetValue(TypeBeatRulesetSetting.LyricFont, "Some Installed Font");
            game.SetValue(OsuSetting.GameplayDisableWinKey, false);

            presets.Select(presets.Minimal);
            presets.Import(code);
            presets.Select(presets.Custom);

            Assert.That(ruleset.Get<string>(TypeBeatRulesetSetting.LyricFont), Is.EqualTo("Some Installed Font"));
            Assert.That(game.Get<bool>(OsuSetting.GameplayDisableWinKey), Is.False);
        }

        [Test]
        public void SettingsPresetsDoNotCoverAreLeftAlone()
        {
            start(false);

            game.SetValue(OsuSetting.UIScale, 1.3f);
            ruleset.SetValue(TypeBeatRulesetSetting.LyricOffsetMs, 25.0);

            presets.Select(presets.Custom);
            presets.Select(presets.Minimal);

            Assert.That(game.Get<float>(OsuSetting.UIScale), Is.EqualTo(1.3f));
            Assert.That(ruleset.Get<double>(TypeBeatRulesetSetting.LyricOffsetMs), Is.EqualTo(25.0));
        }

        [Test]
        public void ImportingAppliesTheCodeAndNumbersTheImports()
        {
            start(false);

            game.SetValue(OsuSetting.DimLevel, 0.25);
            ruleset.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
            ruleset.SetValue(TypeBeatRulesetSetting.TimingTintEarlyColour, "#00ff7f");
            var exported = snapshot();
            string code = presets.Export(presets.Custom);

            presets.Select(presets.Minimal);

            var first = presets.Import(code);

            Assert.That(first.Succeeded, Is.True);
            Assert.That(first.Preset!.Name, Is.EqualTo("Imported 1"));
            Assert.That(presets.Current.Value, Is.SameAs(first.Preset));
            Assert.That(snapshot(), Is.EqualTo(exported));

            var second = presets.Import($"  `{code}`  ");
            Assert.That(second.Preset!.Name, Is.EqualTo("Imported 2"));
            Assert.That(presets.Export(second.Preset), Is.EqualTo(code), "an import exports as the code it came from");
        }

        [Test]
        public void AnInvalidCodeChangesNothing()
        {
            start(false);

            game.SetValue(OsuSetting.DimLevel, 0.25);
            var before = snapshot();
            var entries = presets.Entries.ToArray();

            string valid = presets.Export(presets.Custom);
            string typo = valid[..5] + (valid[5] == 'a' ? 'b' : 'a') + valid[6..];

            foreach (string text in new[] { "", "not a code!", typo, valid[..^3] })
            {
                var result = presets.Import(text);

                Assert.That(result.Succeeded, Is.False, text);
                Assert.That(result.Message, Is.Not.Empty);
            }

            Assert.That(presets.Entries, Is.EqualTo(entries));
            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));
            Assert.That(snapshot(), Is.EqualTo(before));
        }

        [Test]
        public void ACodeFromANewerVersionChangesNothingAndSaysSo()
        {
            start(false);

            var latest = GameplayPresetSchemas.LATEST;
            var newer = new PresetSchema(latest.Version + 1, latest.Entries.Append(new PresetSchemaEntry("game:SomethingAddedLater", PresetDomain.Bool())));
            string code = GameplayShareCode.Encode(newer, newer.Entries.Select(e => e.Domain.Radix - 1).ToArray());

            var result = presets.Import(code);

            Assert.That(result.Error, Is.EqualTo(ShareCodeError.NewerVersion));
            Assert.That(presets.Entries, Is.EqualTo(new[] { presets.Minimal, presets.Custom }));
            Assert.That(coveredAreDefault(), Is.True);
        }

        [Test]
        public void PresetsSelectionAndCustomSurviveARestart()
        {
            start(false);

            game.SetValue(OsuSetting.DimLevel, 0.8);
            var mine = presets.SaveCustomAs("Mine")!;
            string mineCode = presets.Export(mine);

            ruleset.SetValue(TypeBeatRulesetSetting.CaretSmoothing, 5f);
            var custom = snapshot();

            presets.Select(presets.Minimal);

            restart();

            Assert.That(presets.Current.Value, Is.SameAs(presets.Minimal));
            Assert.That(presets.Entries.Select(e => e.Name), Is.EqualTo(new[] { "Minimal", "Mine", "Custom" }));
            Assert.That(presets.Export(presets.Entries[1]), Is.EqualTo(mineCode));

            presets.Select(presets.Custom);
            Assert.That(snapshot(), Is.EqualTo(custom));

            presets.Select(presets.Entries[1]);
            restart();
            Assert.That(presets.Current.Value.Name, Is.EqualTo("Mine"));
        }

        [Test]
        public void SettingsThatMovedWhileNothingWatchedAreRelabelledCustomNotMoved()
        {
            start(false);
            presets.Dispose();
            presets = null!;

            // As a changed default, an older build or a hand-edited game.ini would leave them.
            game.SetValue(OsuSetting.DimLevel, 0.6);
            var before = snapshot();

            start(true);

            Assert.That(presets.Current.Value, Is.SameAs(presets.Custom));
            Assert.That(snapshot(), Is.EqualTo(before));
        }

        [Test]
        public void ResetAllSettingsKeepsPresetsSelectsMinimalAndCustomCanUndoIt()
        {
            start(false);

            game.SetValue(OsuSetting.BlurLevel, 0.3);
            var mine = presets.SaveCustomAs("Mine")!;
            game.SetValue(OsuSetting.DimLevel, 0.9);
            ruleset.SetValue(TypeBeatRulesetSetting.ShowPaceColours, true);
            var custom = snapshot();

            presets.ResetAllSettings(() =>
            {
                game.ResetToDefaults();
                ruleset.ResetToDefaults();
            });

            Assert.That(presets.Current.Value, Is.SameAs(presets.Minimal));
            Assert.That(presets.Entries, Does.Contain(mine));
            Assert.That(coveredAreDefault(), Is.True);

            presets.Select(presets.Custom);
            Assert.That(coveredSnapshot(), Is.EqualTo(covered(custom)));
        }

        [Test]
        public void AGreyedOutSettingIsRestoredToo()
        {
            start(false);

            // Approach Bar Alignment is greyed out while approach bars are off, which they are by default.
            ruleset.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
            ruleset.SetValue(TypeBeatRulesetSetting.ApproachBarAlignment, 30f);
            ruleset.SetValue(TypeBeatRulesetSetting.ApproachBars, false);
            string code = presets.Export(presets.Custom);

            presets.Select(presets.Minimal);
            Assert.That(ruleset.Get<float>(TypeBeatRulesetSetting.ApproachBarAlignment), Is.EqualTo(100f));

            presets.Import(code);

            Assert.That(ruleset.Get<float>(TypeBeatRulesetSetting.ApproachBarAlignment), Is.EqualTo(30f));
            Assert.That(ruleset.GetBindable<float>(TypeBeatRulesetSetting.ApproachBarAlignment).Disabled, Is.True, "and stays greyed out");
        }

        [Test]
        public void ACodeCannotUnpinThePaceColourGradient()
        {
            start(false);

            // A crafted code: approach bars on, which pins the gradient at 0, and a gradient of 50 anyway.
            var latest = GameplayPresetSchemas.LATEST;
            var keys = latest.Entries.Select(e => e.Key).ToList();
            int[] digits = keys.Select(k => presets.Settings.Single(s => s.Key == k).DefaultDigit).ToArray();
            digits[keys.IndexOf("typebeat:ApproachBars")] = 1;
            digits[keys.IndexOf("typebeat:PaceColourGradient")] = 50;

            foreach (bool barsAlreadyOn in new[] { false, true })
            {
                presets.Select(presets.Minimal);

                if (barsAlreadyOn)
                    ruleset.SetValue(TypeBeatRulesetSetting.ApproachBars, true);

                var imported = presets.Import(GameplayShareCode.Encode(latest, digits));

                Assert.That(imported.Succeeded, Is.True);
                Assert.That(ruleset.Get<bool>(TypeBeatRulesetSetting.ApproachBars), Is.True);
                Assert.That(ruleset.Get<float>(TypeBeatRulesetSetting.PaceColourGradient), Is.Zero, $"approach bars already on: {barsAlreadyOn}");
                Assert.That(presets.Export(imported.Preset!), Is.EqualTo(liveCode()), "the import holds what it applied");
            }
        }

        [Test]
        public void ACodeLeavesTheSameSettingsWhateverTheyWereBefore()
        {
            // Settings gate each other (one greys out or pins another), so where the settings start from
            // matters to a careless implementation. Applying a code must not depend on it, and the imported
            // preset must hold exactly what applying it left.
            start(false);

            var random = new Random(484);

            for (int i = 0; i < 300; i++)
            {
                string code = randomCode(random);

                var first = presets.Import(code).Preset!;
                var settled = coveredSnapshot();
                Assert.That(presets.Export(first), Is.EqualTo(liveCode()), $"import of {code} holds what it applied");

                var scramble = presets.Import(randomCode(random)).Preset!;
                var second = presets.Import(code).Preset!;

                Assert.That(coveredSnapshot(), Is.EqualTo(settled), $"importing {code} again after {presets.Export(scramble)}");
                Assert.That(presets.Export(second), Is.EqualTo(presets.Export(first)));

                presets.Delete(first);
                presets.Delete(scramble);
                presets.Delete(second);
            }
        }

        [Test]
        public void WhatACodeAppliesComesBackUnchangedAfterARestart()
        {
            // A config re-imposes its rules when it loads (a gate that pins a value pins it again), so a code
            // that left a combination the config does not allow would come back different after a restart,
            // and the preset would be relabelled Custom. A pin that does not hold against a write made with
            // its gate lifted fails this, whichever setting it is on.
            using var context = new SynchronousRealmTestContext();
            var updateThread = typeof(ThreadSafety).GetProperty(nameof(ThreadSafety.IsUpdateThread))!;
            bool wasUpdateThread = ThreadSafety.IsUpdateThread;
            updateThread.SetValue(null, true);

            try
            {
                var info = new TypeBeatRuleset().RulesetInfo;
                using var realm = new RealmAccess(storage, "client.realm");
                var store = new SettingsStore(realm);
                using var persisted = new TypeBeatRulesetConfigManager(store, info);

                start(false, persisted);

                var random = new Random(4841);

                for (int i = 0; i < 40; i++)
                {
                    var imported = presets.Import(randomCode(random)).Preset!;
                    var applied = covered(game, persisted);

                    game.Save();
                    persisted.Save();

                    using (var reloadedGame = new OsuConfigManager(storage))
                    using (var reloadedRuleset = new TypeBeatRulesetConfigManager(store, info))
                        Assert.That(covered(reloadedGame, reloadedRuleset), Is.EqualTo(applied), $"after importing {presets.Export(imported)}");

                    presets.Delete(imported);
                }

                presets.Dispose();
                presets = null!;
            }
            finally
            {
                updateThread.SetValue(null, wasUpdateThread);
            }
        }

        [Test]
        public void TheSkinEditorsLeaseIsNotAnEdit()
        {
            start(false);

            game.SetValue(OsuSetting.HUDVisibilityMode, HUDVisibilityMode.Never);
            var never = presets.SaveCustomAs("No HUD")!;

            using (new SkinEditorLikeLease(game))
            {
                Assert.That(game.Get<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode), Is.EqualTo(HUDVisibilityMode.Always));
                Assert.That(presets.Current.Value, Is.SameAs(never), "while the lease holds it");
            }

            Assert.That(game.Get<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode), Is.EqualTo(HUDVisibilityMode.Never));
            Assert.That(presets.Current.Value, Is.SameAs(never), "after it is returned");
        }

        [Test]
        public void SelectingDuringALeaseAppliesTheRestAtOnceAndTheLeasedSettingWhenTheLeaseEnds()
        {
            start(false);

            game.SetValue(OsuSetting.HUDVisibilityMode, HUDVisibilityMode.Never);
            game.SetValue(OsuSetting.DimLevel, 0.9);
            var never = presets.SaveCustomAs("No HUD")!;

            game.SetValue(OsuSetting.HUDVisibilityMode, HUDVisibilityMode.HideDuringGameplay);
            game.SetValue(OsuSetting.BlurLevel, 0.5);
            var custom = coveredSnapshot();

            using (new SkinEditorLikeLease(game))
            {
                Assert.DoesNotThrow(() => presets.Select(never));

                Assert.That(presets.Current.Value, Is.SameAs(never));
                Assert.That(game.Get<double>(OsuSetting.BlurLevel), Is.Zero, "everything else applied at once");
                Assert.That(game.Get<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode), Is.EqualTo(HUDVisibilityMode.Always), "the lease still holds HUD");
            }

            Assert.That(game.Get<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode), Is.EqualTo(HUDVisibilityMode.Never), "written when the lease ended");
            Assert.That(presets.Current.Value, Is.SameAs(never));
            Assert.That(liveCode(), Is.EqualTo(presets.Export(never)));

            presets.Select(presets.Custom);
            Assert.That(coveredSnapshot(), Is.EqualTo(custom), "Custom remembered the player's HUD, not the lease's");
        }

        [Test]
        public void SavingDuringALeaseSavesThePlayersValueNotTheLeases()
        {
            start(false);

            game.SetValue(OsuSetting.HUDVisibilityMode, HUDVisibilityMode.HideDuringGameplay);

            GameplayPreset saved;

            using (new SkinEditorLikeLease(game))
            {
                ruleset.SetValue(TypeBeatRulesetSetting.ShowSyncMetric, true);
                saved = presets.SaveCustomAs("Saved meanwhile")!;
            }

            presets.Select(presets.Minimal);
            presets.Select(saved);

            Assert.That(game.Get<HUDVisibilityMode>(OsuSetting.HUDVisibilityMode), Is.EqualTo(HUDVisibilityMode.HideDuringGameplay));
            Assert.That(ruleset.Get<bool>(TypeBeatRulesetSetting.ShowSyncMetric), Is.True);
        }

        [Test]
        public void ResetAllSettingsDuringALeaseLandsOnMinimalWhenItEnds()
        {
            start(false);

            game.SetValue(OsuSetting.HUDVisibilityMode, HUDVisibilityMode.Never);
            game.SetValue(OsuSetting.DimLevel, 0.9);

            using (new SkinEditorLikeLease(game))
            {
                presets.ResetAllSettings(() =>
                {
                    game.ResetToDefaults();
                    ruleset.ResetToDefaults();
                });
            }

            Assert.That(presets.Current.Value, Is.SameAs(presets.Minimal));
            Assert.That(coveredAreDefault(), Is.True);
        }

        private void start(bool hasRunBefore, INamedSettingSource? rulesetSource = null)
        {
            presets = new GameplayPresetManager(storage, hasRunBefore, schema =>
                GameplayPresetBindings.Resolve(schema, game, new Dictionary<string, INamedSettingSource?> { ["typebeat"] = rulesetSource ?? ruleset }));
            presets.EnsureInitialised();
        }

        /// <summary>The covered settings of these two configs, by key, as text.</summary>
        private static Dictionary<string, string> covered(INamedSettingSource gameSource, INamedSettingSource rulesetSource) =>
            GameplayPresetSchemas.LATEST.Entries.ToDictionary(e => e.Key, e =>
            {
                int colon = e.Key.IndexOf(':');
                var source = e.Key[..colon] == GameplayPresetSchemas.GAME_SOURCE ? gameSource : rulesetSource;
                return string.Format(CultureInfo.InvariantCulture, "{0}", source.FindSetting(e.Key[(colon + 1)..]));
            });

        private void restart()
        {
            presets.Dispose();
            start(true);
        }

        /// <summary>Every game and ruleset setting, as text.</summary>
        private Dictionary<string, string> snapshot()
        {
            var values = new Dictionary<string, string>();

            foreach (var (prefix, source) in new (string, INamedSettingSource)[] { ("game", game), ("typebeat", ruleset) })
            {
                foreach (string name in source.SettingNames)
                    values[$"{prefix}:{name}"] = string.Format(CultureInfo.InvariantCulture, "{0}", source.FindSetting(name));
            }

            return values;
        }

        private Dictionary<string, string> coveredSnapshot() => covered(snapshot());

        private static Dictionary<string, string> covered(Dictionary<string, string> all)
        {
            var keys = GameplayPresetSchemas.LATEST.Entries.Select(e => e.Key).ToHashSet();
            return all.Where(p => keys.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        }

        private bool coveredAreDefault() => presets.Settings.All(s => s.CurrentDigit == s.DefaultDigit);

        /// <summary>The stored values of the covered settings right now, as a code.</summary>
        private string liveCode()
        {
            var latest = GameplayPresetSchemas.LATEST;
            return GameplayShareCode.Encode(latest, latest.Entries.Select(e => presets.Settings.Single(s => s.Key == e.Key).CurrentDigit).ToArray());
        }

        private static string randomCode(Random random)
        {
            var latest = GameplayPresetSchemas.LATEST;
            return GameplayShareCode.Encode(latest, latest.Entries.Select(e => random.Next(e.Domain.Radix)).ToArray());
        }

        /// <summary>
        /// What SkinEditorOverlay does to HUD visibility while it is open: binds its own copy, leases it
        /// (reverting on return) and holds it at Always. Disposing returns the lease.
        /// </summary>
        private sealed class SkinEditorLikeLease : IDisposable
        {
            private readonly Bindable<HUDVisibilityMode> copy = new Bindable<HUDVisibilityMode>();
            private readonly LeasedBindable<HUDVisibilityMode> lease;

            public SkinEditorLikeLease(OsuConfigManager config)
            {
                config.BindWith(OsuSetting.HUDVisibilityMode, copy);
                lease = copy.BeginLease(true);
                lease.Value = HUDVisibilityMode.Always;
            }

            public void Dispose()
            {
                lease.Return();
                copy.UnbindAll();
            }
        }

        [Test]
        public void AnOldCodeIsReadByItsOwnSchema()
        {
            // Between the two versions one setting went, one arrived, a slider's range and step changed
            // and an enum lost a member.
            var version1 = new PresetSchema(1, "t:Removed bool", "t:Slider range 0 10 1", "t:Letter enum X Y Z", "t:Tint colour");
            var version2 = new PresetSchema(2, "t:Slider range 0 8 2", "t:Letter enum X Z W", "t:Tint colour", "t:Added bool");

            var source = new FakeSource
            {
                ["Slider"] = new BindableFloat(),
                ["Letter"] = new Bindable<Letter>(Letter.W),
                ["Tint"] = new Bindable<string>("#000000"),
                ["Added"] = new Bindable<bool>(true) { Value = false },
            };

            presets = new GameplayPresetManager(storage, true,
                schema => GameplayPresetBindings.Resolve(schema, new FakeSource(), new Dictionary<string, INamedSettingSource?> { ["t"] = source }),
                new[] { version1, version2 });
            presets.EnsureInitialised();

            Assert.That(presets.Settings.Select(s => s.Key), Is.EqualTo(new[] { "t:Slider", "t:Letter", "t:Tint", "t:Added" }));

            // 3 is between two of the new steps (2 and 4) and snaps; Y no longer exists.
            Assert.That(presets.Import(GameplayShareCode.Encode(version1, new[] { 1, 3, 1, 0x123456 })).Succeeded, Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(source.Get<float>("Slider"), Is.EqualTo(4f));
                Assert.That(source.Get<Letter>("Letter"), Is.EqualTo(Letter.W), "a member that is gone takes the default");
                Assert.That(source.Get<string>("Tint"), Is.EqualTo("#123456"));
                Assert.That(source.Get<bool>("Added"), Is.True, "a setting added since takes its default");
            });

            // 10 is past the new maximum and clamps; Z still exists.
            presets.Import(GameplayShareCode.Encode(version1, new[] { 0, 10, 2, 0xabcdef }));

            Assert.That(source.Get<float>("Slider"), Is.EqualTo(8f));
            Assert.That(source.Get<Letter>("Letter"), Is.EqualTo(Letter.Z));
            Assert.That(source.Get<string>("Tint"), Is.EqualTo("#abcdef"));
        }

        private enum Letter
        {
            X,
            Z,
            W,
        }

        private class FakeSource : Dictionary<string, IBindable>, INamedSettingSource
        {
            public IEnumerable<string> SettingNames => Keys;

            public IBindable? FindSetting(string name) => TryGetValue(name, out var bindable) ? bindable.GetBoundCopy() : null;

            public T Get<T>(string name) => ((Bindable<T>)this[name]).Value;
        }
    }
}
