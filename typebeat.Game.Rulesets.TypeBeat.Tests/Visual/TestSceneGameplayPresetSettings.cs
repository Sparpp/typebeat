// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using osu.Framework.Testing;
using typebeat.Game.Configuration;
using typebeat.Game.Configuration.Presets;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Overlays.Settings.Sections;
using typebeat.Game.Overlays.Settings.Sections.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 484: the Gameplay presets control on the real settings overlay, laid out like the Skin
    /// section (dropdown, Rename / Export / Delete, then Import), and the import box with a valid and an
    /// invalid code.
    /// </summary>
    public partial class TestSceneGameplayPresetSettings : OsuTestScene
    {
        private const string sync_metric = "Show sync metric";

        [Resolved]
        private OsuConfigManager gameConfig { get; set; } = null!;

        [Resolved]
        private Clipboard clipboard { get; set; } = null!;

        private readonly List<TemporaryNativeStorage> storages = new List<TemporaryNativeStorage>();

        private GameplayPresetManager? presets;
        private SettingsOverlay settings = null!;

        private TypeBeatRulesetConfigManager rulesetConfig => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("close the previous settings", () =>
            {
                Clear();
                presets?.Dispose();
            });

            AddStep("every setting at its default", () =>
            {
                gameConfig.ResetToDefaults();
                rulesetConfig.ResetToDefaults();
            });

            AddStep("open the settings on a new install", () =>
            {
                var storage = new TemporaryNativeStorage($"{nameof(TestSceneGameplayPresetSettings)}-{Guid.NewGuid():N}");
                storages.Add(storage);

                presets = new GameplayPresetManager(storage, false, schema =>
                    GameplayPresetBindings.Resolve(schema, gameConfig, new Dictionary<string, INamedSettingSource?> { ["typebeat"] = rulesetConfig }));
                presets.EnsureInitialised();

                Child = new DependencyProvidingContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    CachedDependencies = new (Type, object)[] { (typeof(GameplayPresetManager), presets) },
                    Child = settings = new SettingsOverlay
                    {
                        State = { Value = Visibility.Visible },
                    },
                };
            });

            AddUntilStep("presets loaded", () => control<PresetSettings.ImportPresetButton>()?.IsLoaded == true
                                                 && settings.ChildrenOfType<TypeBeatSettingsSubsection>().SingleOrDefault()?.IsLoaded == true);
        }

        [Test]
        public void TestPresetsAreTheFirstThingInGameplay()
        {
            AddAssert("first in the section", () => gameplay.ChildrenOfType<SettingsSubsection>().First() is PresetSettings);
            AddAssert("the dropdown is the section's first row", () => gameplay.ChildrenOfType<SettingsItemV2>().First().Control == dropdown);
        }

        [Test]
        public void TestANewInstallShowsMinimalWithNothingToRenameOrDelete()
        {
            AddAssert("Minimal selected", () => dropdown.Current.Value == presets!.Minimal);
            AddAssert("Minimal and Custom offered", () => dropdown.Items.SequenceEqual(new[] { presets!.Minimal, presets.Custom }));
            AddUntilStep("rename disabled", () => !button<PresetSettings.RenamePresetButton>().Enabled.Value);
            AddUntilStep("delete disabled", () => !button<PresetSettings.DeletePresetButton>().Enabled.Value);
            AddAssert("export enabled", () => button<PresetSettings.ExportPresetButton>().Enabled.Value);
        }

        [Test]
        public void TestChangingASettingSelectsCustom()
        {
            setSyncMetric(true);

            AddUntilStep("the dropdown shows Custom", () => dropdown.Current.Value == presets!.Custom);
            AddUntilStep("rename enabled, to name Custom", () => button<PresetSettings.RenamePresetButton>().Enabled.Value);
            AddUntilStep("delete still disabled", () => !button<PresetSettings.DeletePresetButton>().Enabled.Value);
        }

        [Test]
        public void TestPickingFromTheDropdownAppliesThePreset()
        {
            setSyncMetric(true);
            AddUntilStep("Custom", () => dropdown.Current.Value == presets!.Custom);

            AddStep("pick Minimal", () => dropdown.Current.Value = presets!.Minimal);
            AddAssert("the setting is back on its default", () => !syncMetric.Value);
            AddUntilStep("Minimal stays selected", () => dropdown.Current.Value == presets!.Minimal);

            AddStep("pick Custom", () => dropdown.Current.Value = presets!.Custom);
            AddAssert("the change is back", () => syncMetric.Value);
        }

        [Test]
        public void TestRenameOnCustomSavesANamedPreset()
        {
            setSyncMetric(true);
            AddUntilStep("Custom", () => dropdown.Current.Value == presets!.Custom);

            AddStep("rename", () => button<PresetSettings.RenamePresetButton>().TriggerClick());
            AddUntilStep("the name box is open", () => popover<PresetSettings.RenamePresetPopover>()?.State.Value == Visibility.Visible);
            AddStep("name it", () =>
            {
                var box = popover<PresetSettings.RenamePresetPopover>()!;
                box.TextBox.Text = "Mine";
                box.ChildrenOfType<RoundedButton>().Single().TriggerClick();
            });

            AddUntilStep("the dropdown shows it", () => dropdown.Current.Value.Name == "Mine" && dropdown.Items.Contains(dropdown.Current.Value));
            AddUntilStep("it can be deleted", () => button<PresetSettings.DeletePresetButton>().Enabled.Value);

            AddStep("delete it", () => button<PresetSettings.DeletePresetButton>().TriggerClick());
            AddUntilStep("gone, and Custom selected", () => dropdown.Current.Value == presets!.Custom && dropdown.Items.All(p => p.Name != "Mine"));
            AddAssert("the settings stayed", () => syncMetric.Value);
        }

        [Test]
        public void TestExportCopiesTheShareCode()
        {
            setSyncMetric(true);

            AddStep("export", () => button<PresetSettings.ExportPresetButton>().TriggerClick());
            AddAssert("the clipboard holds Custom's code", () => clipboard.GetText() == presets!.Export(presets.Custom));
        }

        [Test]
        public void TestImportingAValidCode()
        {
            string code = null!;

            AddStep("a code with the sync metric on", () =>
            {
                syncMetric.Value = true;
                code = presets!.Export(presets.Custom);
                presets.Select(presets.Minimal);
            });
            AddAssert("off again", () => !syncMetric.Value);

            importText(() => code);

            AddUntilStep("the box closed", () => popover<PresetSettings.ImportPresetPopover>()?.State.Value != Visibility.Visible);
            AddUntilStep("the dropdown shows Imported 1", () => dropdown.Current.Value.Name == $"{GameplayPresetManager.IMPORTED_NAME} 1" && dropdown.Items.Contains(dropdown.Current.Value));
            AddAssert("applied", () => syncMetric.Value);
        }

        [Test]
        public void TestImportingAnInvalidCode()
        {
            importText(() => "this is not a code");

            AddUntilStep("the box stays open", () => popover<PresetSettings.ImportPresetPopover>()?.State.Value == Visibility.Visible);
            AddAssert("and says so", () => popover<PresetSettings.ImportPresetPopover>()!.Message.Alpha > 0);
            AddAssert("nothing was added", () => dropdown.Items.SequenceEqual(new[] { presets!.Minimal, presets.Custom }));
            AddAssert("Minimal still selected", () => dropdown.Current.Value == presets!.Minimal);
            AddAssert("no setting moved", () => presets!.Settings.All(s => s.CurrentDigit == s.DefaultDigit));
        }

        [Test]
        public void TestImportingACodeFromANewerVersion()
        {
            importText(() =>
            {
                var latest = GameplayPresetSchemas.LATEST;
                var newer = new PresetSchema(latest.Version + 1, latest.Entries.Append(new PresetSchemaEntry("game:SomethingAddedLater", PresetDomain.Bool())));
                return GameplayShareCode.Encode(newer, new int[newer.Entries.Count]);
            });

            AddUntilStep("the box says to update", () => popover<PresetSettings.ImportPresetPopover>()?.MessageText == GameplayPresetManager.MessageFor(ShareCodeError.NewerVersion));
            AddAssert("nothing was added", () => dropdown.Items.Count() == 2);
        }

        private void importText(Func<string> text)
        {
            AddStep("open the import box", () => button<PresetSettings.ImportPresetButton>().TriggerClick());
            AddUntilStep("open", () => popover<PresetSettings.ImportPresetPopover>()?.State.Value == Visibility.Visible);
            AddStep("paste and import", () =>
            {
                var box = popover<PresetSettings.ImportPresetPopover>()!;
                box.TextBox.Text = text();
                box.ChildrenOfType<RoundedButton>().Single().TriggerClick();
            });
        }

        private void setSyncMetric(bool value) => AddStep($"sync metric {(value ? "on" : "off")} through its control", () => syncMetric.Value = value);

        /// <summary>The "Show sync metric" check box's own Current, as a player's click sets it.</summary>
        private Bindable<bool> syncMetric =>
            ((FormCheckBox)settings.ChildrenOfType<SettingsItemV2>().Single(i => i.Control.FilterTerms.Any(t => t.ToString() == sync_metric)).Control).Current;

        private GameplaySection gameplay => settings.SectionsContainer.OfType<GameplaySection>().Single();

        private FormDropdown<GameplayPreset> dropdown => settings.ChildrenOfType<FormDropdown<GameplayPreset>>().Single();

        private T? control<T>() where T : Drawable => settings.ChildrenOfType<T>().SingleOrDefault();

        private T button<T>() where T : SettingsButtonV2 => settings.ChildrenOfType<T>().Single();

        private T? popover<T>() where T : PresetSettings.PresetTextPopover => settings.ChildrenOfType<T>().SingleOrDefault();

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            presets?.Dispose();

            foreach (var storage in storages)
                storage.Dispose();
        }
    }
}
