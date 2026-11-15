// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using typebeat.Game.Configuration;
using typebeat.Game.Localisation;

namespace typebeat.Game.Screens.Play.PlayerSettings
{
    public partial class InputSettings : PlayerSettingsGroup
    {
        public InputSettings()
            : base(PlayerSettingsOverlayStrings.InputSettingsTitle)
        {
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            Children = new Drawable[]
            {
                // The mouse switch only: there is no touch counterpart ("Disable taps during gameplay" was
                // removed in backlog 483), and type!beat ships for desktop alone.
                new PlayerCheckbox
                {
                    LabelText = MouseSettingsStrings.DisableClicksDuringGameplay,
                    Current = config.GetBindable<bool>(OsuSetting.MouseDisableButtons)
                }
            };
        }
    }
}
