// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using typebeat.Game.Graphics;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Localisation;

namespace typebeat.Game.Overlays.Toolbar
{
    public partial class ToolbarHomeButton : ToolbarButton
    {
        public ToolbarHomeButton()
        {
            ButtonContent.Width *= 1.4f;
            Hotkey = GlobalAction.Home;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            TooltipMain = ToolbarStrings.HomeHeaderTitle;
            TooltipSub = ToolbarStrings.HomeHeaderDescription;
            SetIcon(OsuIcon.Home);
        }
    }
}
