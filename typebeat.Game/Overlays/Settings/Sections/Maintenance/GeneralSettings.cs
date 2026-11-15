// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using osu.Framework.Screens;
using typebeat.Game.Configuration;
using typebeat.Game.Configuration.Presets;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Rulesets;
using typebeat.Game.Screens;
using typebeat.Game.Screens.Import;
using typebeat.Game.Screens.Utility;

namespace typebeat.Game.Overlays.Settings.Sections.Maintenance
{
    public partial class GeneralSettings : SettingsSubsection
    {
        protected override LocalisableString Header => CommonStrings.General;

        private ISystemFileSelector? selector;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        [Resolved]
        private RulesetStore rulesets { get; set; } = null!;

        [Resolved]
        private IRulesetConfigCache rulesetConfigs { get; set; } = null!;

        [Resolved]
        private IDialogOverlay? dialogOverlay { get; set; }

        [Resolved]
        private INotificationOverlay? notifications { get; set; }

        [Resolved]
        private GameplayPresetManager? gameplayPresets { get; set; }

        /// <summary>
        /// Puts the game's settings and every installed ruleset's settings back on their defaults.
        /// What a reset deliberately keeps is listed on <see cref="OsuConfigManager.KeepOnReset"/>.
        /// Gameplay presets are kept: the reset selects Minimal, and the preset that was selected still holds
        /// the Gameplay settings it replaced (Custom remembers them as it does whenever another preset is
        /// selected; see <see cref="GameplayPresetManager.ResetAllSettings"/>).
        /// </summary>
        public void ResetAllSettings()
        {
            void reset()
            {
                config.ResetToDefaults();

                foreach (var info in rulesets.AvailableRulesets)
                    rulesetConfigs.GetConfigFor(info.CreateInstance())?.ResetToDefaults();
            }

            if (gameplayPresets != null)
                gameplayPresets.ResetAllSettings(reset);
            else
                reset();

            notifications?.Post(new SimpleNotification
            {
                Text = "Settings were reset to their defaults. Gameplay presets are kept: select the one you were using to get your Gameplay settings back."
            });
        }

        [BackgroundDependencyLoader]
        private void load(OsuGameBase game, GameHost host, IPerformFromScreenRunner? performer)
        {
            if ((selector = host.CreateSystemFileSelector(game.HandledExtensions.ToArray())) != null)
                selector.Selected += f => Task.Run(() => game.Import(f.FullName));

            AddRange(new Drawable[]
            {
                new SettingsButtonV2
                {
                    Text = DebugSettingsStrings.ImportFiles,
                    Action = () =>
                    {
                        if (selector != null)
                            selector.Present();
                        else
                            performer?.PerformFromScreen(menu => menu.Push(new FileImportScreen()));
                    },
                },
                new SettingsButtonV2
                {
                    Text = DebugSettingsStrings.RunLatencyCertifier,
                    Action = () => performer?.PerformFromScreen(menu => menu.Push(new LatencyCertifierScreen()))
                },
                new DangerousSettingsButtonV2
                {
                    Text = MaintenanceSettingsStrings.ResetAllSettings,
                    Action = () => dialogOverlay?.Push(new ResetSettingsConfirmationDialog(ResetAllSettings)),
                },
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            selector?.Dispose();
        }
    }
}
