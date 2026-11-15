// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using Newtonsoft.Json;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API.Requests
{
    public class SearchBeatmapSetsResponse : ResponseWithCursor
    {
        [JsonProperty("beatmapsets")]
        public IEnumerable<APIBeatmapSet> BeatmapSets;

        /// <summary>
        /// How many sets match the whole search (every filter, not just this page), shown as the listing's
        /// "1,234 results". Nullable so a server that leaves the field out reads as "unknown" (the count is
        /// hidden) rather than as a wrong "0 results".
        /// </summary>
        [JsonProperty("total")]
        public int? Total;
    }
}
