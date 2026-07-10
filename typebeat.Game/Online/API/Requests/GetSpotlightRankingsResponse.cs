// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using Newtonsoft.Json;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Users;

namespace typebeat.Game.Online.API.Requests
{
    public class GetSpotlightRankingsResponse
    {
        [JsonProperty("ranking")]
        public List<UserStatistics> Users;

        [JsonProperty("spotlight")]
        public APISpotlight Spotlight;

        [JsonProperty("beatmapsets")]
        public List<APIBeatmapSet> BeatmapSets;
    }
}
