// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using osu.Framework.Screens;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Settings.Sections.Maintenance;

namespace typebeat.Game.Overlays.Settings.Sections.General
{
    public partial class InstallationSettings : SettingsSubsection
    {
        protected override LocalisableString Header => GeneralSettingsStrings.InstallationHeader;

        [Resolved]
        private OsuGame? game { get; set; }

        [BackgroundDependencyLoader]
        private void load(Storage storage)
        {
            Add(new SettingsButtonV2
            {
                Text = GeneralSettingsStrings.OpenOsuFolder,
                Keywords = new[] { @"logs", @"files", @"access", "directory" },
                Action = () => storage.PresentExternally(),
            });

            Add(new DangerousSettingsButtonV2
            {
                Text = GeneralSettingsStrings.ChangeFolderLocation,
                Action = () => game?.PerformFromScreen(menu => menu.Push(new MigrationSelectScreen()))
            });
        }
    }
}
