// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Settings.Sections.General;
using typebeat.Game.Updater;

namespace typebeat.Game.Overlays.Settings.Sections
{
    public partial class GeneralSection : SettingsSection
    {
        public override LocalisableString Header => CommonStrings.General;

        public override Drawable CreateIcon() => new SpriteIcon
        {
            Icon = OsuIcon.Settings
        };

        [BackgroundDependencyLoader]
        private void load(UpdateManager? updateManager)
        {
            Add(new QuickActionSettings());
            Add(new LanguageSettings());
            if (updateManager?.CanCheckForUpdate == true)
                Add(new UpdateSettings());
            if (RuntimeInfo.IsDesktop)
                Add(new InstallationSettings());
        }
    }
}
