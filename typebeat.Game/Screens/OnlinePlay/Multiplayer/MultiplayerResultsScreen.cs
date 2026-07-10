// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Online.Rooms;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.OnlinePlay.Playlists;

namespace typebeat.Game.Screens.OnlinePlay.Multiplayer
{
    public partial class MultiplayerResultsScreen : PlaylistItemScoreResultsScreen
    {
        public MultiplayerResultsScreen(ScoreInfo score, long roomId, PlaylistItem playlistItem)
            : base(score, roomId, playlistItem)
        {
        }
    }
}
