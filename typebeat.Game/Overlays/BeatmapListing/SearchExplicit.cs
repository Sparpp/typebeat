// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.BeatmapListing
{
    public enum SearchExplicit
    {
        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.NsfwExclude))]
        Hide,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.NsfwInclude))]
        Show
    }
}
