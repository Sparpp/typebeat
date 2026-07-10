// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Online.Rooms;
using typebeat.Game.Screens.OnlinePlay.Playlists;
using typebeat.Game.Users;

namespace typebeat.Game.Screens.OnlinePlay.DailyChallenge
{
    public partial class DailyChallengePlayer : PlaylistsPlayer
    {
        protected override UserActivity InitialActivity => new UserActivity.PlayingDailyChallenge(Beatmap.Value.BeatmapInfo, Ruleset.Value);

        public DailyChallengePlayer(Room room, PlaylistItem playlistItem)
            : base(room, playlistItem)
        {
        }
    }
}
