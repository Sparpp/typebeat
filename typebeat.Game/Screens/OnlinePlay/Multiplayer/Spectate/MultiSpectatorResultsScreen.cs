// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play;

namespace typebeat.Game.Screens.OnlinePlay.Multiplayer.Spectate
{
    public partial class MultiSpectatorResultsScreen : SpectatorResultsScreen
    {
        public MultiSpectatorResultsScreen(ScoreInfo score)
            : base(score)
        {
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Scheduler.AddDelayed(() => StatisticsPanel.ToggleVisibility(), 1000);
        }

        protected override Task<ScoreInfo[]> FetchScores() => Task.FromResult<ScoreInfo[]>([]);

        protected override Task<ScoreInfo[]> FetchNextPage(int direction) => Task.FromResult<ScoreInfo[]>([]);
    }
}
