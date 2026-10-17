// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Net;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API.Requests
{
    public class GetBeatmapSetStateRequest : APIRequest<APIBeatmapSetState>
    {
        private readonly int id;

        public GetBeatmapSetStateRequest(int id) => this.id = id;

        protected override string Target => $"beatmapsets/{id}/state";

        public override bool AllowsAnonymous => true;

        internal HttpStatusCode? ResponseStatusCode => WebRequest?.ResponseStatusCode;
    }
}
