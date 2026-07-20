// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Newtonsoft.Json;

namespace typebeat.Game.Online.Rooms
{
    public class APIScoreToken
    {
        [JsonProperty("id")]
        public long ID { get; set; }
    }
}
