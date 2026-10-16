// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Newtonsoft.Json;
using typebeat.Game.Beatmaps;

namespace typebeat.Game.Online.API.Requests.Responses
{
    public class APIBeatmapSetState
    {
        [JsonProperty("metadata_revision")]
        public long MetadataRevision { get; set; }

        [JsonProperty("package_revision")]
        public int PackageRevision { get; set; }

        [JsonProperty("status")]
        public BeatmapOnlineStatus Status { get; set; }

        [JsonProperty("last_updated")]
        public DateTimeOffset? LastUpdated { get; set; }

        [JsonProperty("has_favourited")]
        public bool HasFavourited { get; set; }

        public bool Matches(APIBeatmapSet set) => PackageRevision > 0 && set.MetadataRevision == MetadataRevision
                                                && set.PackageRevision == PackageRevision && set.Status == Status && set.LastUpdated == LastUpdated
                                                && set.HasFavourited == HasFavourited;
    }
}
