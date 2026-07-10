// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Screens.OnlinePlay.Matchmaking.Match.BeatmapSelect
{
    public record MatchmakingPlaylistItem(MultiplayerPlaylistItem PlaylistItem, APIBeatmap Beatmap, Mod[] Mods)
    {
        public long ID => PlaylistItem.ID;
    }
}
