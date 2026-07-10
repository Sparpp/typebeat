// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using typebeat.Game.Graphics;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.BeatmapSet
{
    public partial class FeaturedArtistBeatmapBadge : BeatmapBadge
    {
        [BackgroundDependencyLoader]
        private void load(OsuColour colours)
        {
            BadgeText = BeatmapsetsStrings.FeaturedArtistBadgeLabel;
            BadgeColour = colours.FeaturedArtistColour;
            // todo: add linking support to allow redirecting featured artist badge to corresponding track.
        }
    }
}
