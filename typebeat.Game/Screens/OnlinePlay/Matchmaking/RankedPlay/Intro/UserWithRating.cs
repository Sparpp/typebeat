// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Screens.OnlinePlay.Matchmaking.RankedPlay.Intro
{
    public record UserWithRating(APIUser User, int Rating);
}
