// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Localisation;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.Settings.Sections.Gameplay
{
    public partial class InputSettings : SettingsSubsection
    {
        protected override LocalisableString Header => GameplaySettingsStrings.InputHeader;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            // The gameplay cursor settings osu! kept here are gone (backlog 482): type!beat has no gameplay
            // cursor. Only the Windows key toggle remains, so GameplaySection adds this group on Windows only.
            Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = GameplaySettingsStrings.DisableWinKey,
                Current = config.GetBindable<bool>(OsuSetting.GameplayDisableWinKey)
            }));
        }
    }
}
