// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets;

namespace typebeat.Game.Online.API.Requests
{
    public class GetCountryRankingsRequest : GetRankingsRequest<GetCountriesResponse>
    {
        public GetCountryRankingsRequest(RulesetInfo ruleset, int page = 1)
            : base(ruleset, page)
        {
        }

        protected override string TargetPostfix() => "country";
    }
}
