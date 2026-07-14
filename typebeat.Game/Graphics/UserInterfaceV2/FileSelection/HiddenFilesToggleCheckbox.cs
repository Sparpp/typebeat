// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Graphics.UserInterfaceV2.FileSelection
{
    internal partial class HiddenFilesToggleCheckbox : OsuCheckbox
    {
        public HiddenFilesToggleCheckbox()
        {
            RelativeSizeAxes = Axes.None;
            AutoSizeAxes = Axes.None;
            Size = new Vector2(140, OsuDirectorySelectorBreadcrumbDisplay.HEIGHT);
            Margin = new MarginPadding { Right = OsuDirectorySelectorBreadcrumbDisplay.HORIZONTAL_PADDING, };
            Anchor = Anchor.CentreLeft;
            Origin = Anchor.CentreLeft;
            LabelTextFlowContainer.Anchor = Anchor.CentreLeft;
            LabelTextFlowContainer.Origin = Anchor.CentreLeft;
            LabelText = UserInterfaceStrings.ShowHidden;

            Scale = new Vector2(0.8f);
        }

        [BackgroundDependencyLoader(true)]
        private void load(OverlayColourProvider? overlayColourProvider, OsuColour colours)
        {
            if (overlayColourProvider != null)
                return;

            Nub.AccentColour = colours.GreySeaFoamLighter;
            Nub.GlowingAccentColour = Color4.White;
            Nub.GlowColour = Color4.White;
        }
    }
}
