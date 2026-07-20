// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using Newtonsoft.Json;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API.Requests
{
    public class GetSpotlightsRequest : APIRequest<SpotlightsCollection>
    {
        protected override string Target => "spotlights";
    }

    public class SpotlightsCollection
    {
        [JsonProperty("spotlights")]
        public List<APISpotlight> Spotlights;
    }
}
