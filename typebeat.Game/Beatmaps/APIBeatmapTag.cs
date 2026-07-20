// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Newtonsoft.Json;

namespace typebeat.Game.Beatmaps
{
    public class APIBeatmapTag
    {
        [JsonProperty("tag_id")]
        public long TagId { get; set; }

        [JsonProperty("count")]
        public int VoteCount { get; set; }
    }
}
