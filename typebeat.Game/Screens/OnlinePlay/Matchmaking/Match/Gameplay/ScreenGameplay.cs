// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Framework.Screens;
using typebeat.Game.Online.Multiplayer;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.OnlinePlay.Multiplayer;

namespace typebeat.Game.Screens.OnlinePlay.Matchmaking.Match.Gameplay
{
    public partial class ScreenGameplay : MultiplayerPlayer
    {
        public ScreenGameplay(Room room, PlaylistItem playlistItem, MultiplayerRoomUser[] users)
            : base(room, playlistItem, users, showFailingOverlay: false)
        {
        }

        protected override async Task PrepareScoreForResultsAsync(Score score)
        {
            await base.PrepareScoreForResultsAsync(score).ConfigureAwait(false);

            Scheduler.Add(() =>
            {
                if (this.IsCurrentScreen())
                    this.Exit();
            });
        }
    }
}
