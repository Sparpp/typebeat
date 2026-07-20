// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API.Requests
{
    public class GetUserKudosuHistoryRequest : PaginatedAPIRequest<List<APIKudosuHistory>>
    {
        private readonly long userId;

        public GetUserKudosuHistoryRequest(long userId, PaginationParameters pagination)
            : base(pagination)
        {
            this.userId = userId;
        }

        protected override string Target => $"users/{userId}/kudosu";
    }
}
