// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using Newtonsoft.Json;
using typebeat.Game.Online.Chat;

namespace typebeat.Game.Online.API.Requests.Responses
{
    [JsonObject(MemberSerialization.OptIn)]
    public class GetChannelResponse
    {
        [JsonProperty(@"channel")]
        public Channel Channel { get; set; } = null!;

        [JsonProperty(@"users")]
        public List<APIUser> Users { get; set; } = null!;
    }
}
