// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Screens.Menu;

namespace typebeat.Game.Seasonal
{
    internal partial class SeasonalMenuLogoVisualisation : MenuLogoVisualisation
    {
        protected override void UpdateColour() => Colour = SeasonalUIConfig.AMBIENT_COLOUR_1;
    }
}
