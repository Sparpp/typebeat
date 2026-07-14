// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Overlays.Settings;

namespace typebeat.Game.Screens.Play.PlayerSettings
{
    public partial class PlayerCheckbox : SettingsCheckbox
    {
        protected override Drawable CreateControl() => new PlayerCheckboxControl();

        public partial class PlayerCheckboxControl : OsuCheckbox
        {
            [BackgroundDependencyLoader]
            private void load(OsuColour colours)
            {
                Nub.AccentColour = colours.Yellow;
                Nub.GlowingAccentColour = colours.YellowLighter;
                Nub.GlowColour = colours.YellowDark;
            }
        }
    }
}
