// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Bindables;
using osu.Framework.Testing;
using typebeat.Game.Configuration;
using typebeat.Game.Configuration.Presets;
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

        private void start(bool hasRunBefore)
        {
            presets = new GameplayPresetManager(storage, hasRunBefore, schema =>
                GameplayPresetBindings.Resolve(schema, game, new Dictionary<string, INamedSettingSource?> { ["typebeat"] = ruleset }));
            presets.EnsureInitialised();
        }

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
