// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Development;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Settings.Sections.Maintenance;

namespace typebeat.Game.Overlays.Settings.Sections
{
    public partial class MaintenanceSection : SettingsSection
    {
        public override LocalisableString Header => MaintenanceSettingsStrings.MaintenanceSectionHeader;

        public override Drawable CreateIcon() => new SpriteIcon
        {
            Icon = OsuIcon.Maintenance
        };

        public MaintenanceSection()
        {
            Children = new Drawable[]
            {
                new GeneralSettings(),
                new BeatmapSettings(),
                new SkinSettings(),
                new CollectionsSettings(),
                new ScoreSettings(),
                new ModPresetSettings()
            };

            // The former Debug section lives here now, so its subsections are appended rather
            // than carrying a section of their own.
            if (DebugUtils.IsDebugBuild)
            {
                Add(new DebugSettings.GeneralSettings());
                Add(new DebugSettings.BatchImportSettings());
            }

            Add(new DebugSettings.MemorySettings());
        }
    }
}
