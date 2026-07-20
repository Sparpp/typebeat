// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Utils;
using typebeat.Game.Screens.Menu;
using osuTK.Graphics;

namespace typebeat.Game.Seasonal
{
    public partial class SeasonalMenuSideFlashes : MenuSideFlashes
    {
        protected override bool RefreshColoursEveryFlash => true;

        protected override float Intensity => 4;

        protected override Color4 GetBaseColour() => RNG.NextBool() ? SeasonalUIConfig.PRIMARY_COLOUR_1 : SeasonalUIConfig.PRIMARY_COLOUR_2;
    }
}
