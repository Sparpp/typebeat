// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Resources.Localisation.Web;
using APIUser = typebeat.Game.Online.API.Requests.Responses.APIUser;

namespace typebeat.Game.Overlays.Profile.Sections.Historical
{
    public partial class PaginatedMostPlayedBeatmapContainer : PaginatedProfileSubsection<APIUserMostPlayedBeatmap>
    {
        public PaginatedMostPlayedBeatmapContainer(Bindable<UserProfileData?> user)
            : base(user, UsersStrings.ShowExtraHistoricalMostPlayedTitle)
        {
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            ItemsContainer.Direction = FillDirection.Vertical;
        }

        protected override int GetCount(APIUser user) => user.BeatmapPlayCountsCount;

        protected override APIRequest<List<APIUserMostPlayedBeatmap>> CreateRequest(UserProfileData user, PaginationParameters pagination) =>
            new GetUserMostPlayedBeatmapsRequest(user.User.Id, pagination);

        protected override Drawable CreateDrawableItem(APIUserMostPlayedBeatmap mostPlayed) =>
            new DrawableMostPlayedBeatmap(mostPlayed);
    }
}
