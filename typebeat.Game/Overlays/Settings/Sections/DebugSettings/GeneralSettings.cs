// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.UserInterfaceV2;

namespace typebeat.Game.Overlays.Settings.Sections.DebugSettings
{
    public partial class GeneralSettings : SettingsSubsection
    {
        // Named "Debug" rather than "General" because this subsection now sits inside the
        // Maintenance section, which already has a General subsection of its own.
        protected override LocalisableString Header => @"Debug";

        [BackgroundDependencyLoader]
        private void load(FrameworkDebugConfigManager config, FrameworkConfigManager frameworkConfig)
        {
            Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = @"Show log overlay",
                Current = frameworkConfig.GetBindable<bool>(FrameworkSetting.ShowLogOverlay)
            }));

            Add(new SettingsItemV2(new FormCheckBox
            {
                Caption = @"Bypass front-to-back render pass",
                Current = config.GetBindable<bool>(DebugSetting.BypassFrontToBackPass)
            }));
        }
    }
}
