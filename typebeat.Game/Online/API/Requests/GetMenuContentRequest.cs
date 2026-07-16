// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API.Requests
{
    public class GetMenuContentRequest : OsuJsonWebRequest<APIMenuContent>
    {
        public GetMenuContentRequest(EndpointConfiguration endpoints)
            : base($@"{endpoints.WebsiteUrl}/menu-content.json")
        {
        }
    }
}
