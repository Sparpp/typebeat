// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Overlays.Profile.Sections.Beatmaps;
using typebeat.Game.Localisation;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Overlays.Profile.Sections
{
    public partial class BeatmapsSection : ProfileSection
    {
        public override LocalisableString Title => UsersStrings.ShowExtraBeatmapsTitle;

        public override string Identifier => @"beatmaps";

        /// <remarks>
        /// The five subsections the server has data for (GET users/{id}/beatmapsets/{type}, counted by the matching
        /// <c>*_beatmapset_count</c> keys). Guest and nominated are not here: a set has one owner,
        /// and there is no nomination step. "Graveyard" is the server's unranked status (sets whose creator
        /// opted out of ranking), the same mapping the listing overlay's graveyard category uses.
        /// </remarks>
        public BeatmapsSection()
        {
            Children = new[]
            {
                new PaginatedBeatmapContainer(BeatmapSetType.Favourite, User, UsersStrings.ShowExtraBeatmapsFavouriteTitle),
                new PaginatedBeatmapContainer(BeatmapSetType.Ranked, User, UsersStrings.ShowExtraBeatmapsRankedTitle),
                new PaginatedBeatmapContainer(BeatmapSetType.Loved, User, RespectedStrings.ProfileBeatmapsTitle),
                new PaginatedBeatmapContainer(BeatmapSetType.Pending, User, UsersStrings.ShowExtraBeatmapsPendingTitle),
                new PaginatedBeatmapContainer(BeatmapSetType.Graveyard, User, UsersStrings.ShowExtraBeatmapsGraveyardTitle),
            };
        }
    }
}
