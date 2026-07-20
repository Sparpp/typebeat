// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Extensions;
using typebeat.Game.Overlays.Dialog;
using typebeat.Game.Scoring;

namespace typebeat.Game.Screens.Select
{
    public partial class LocalScoreDeleteDialog : DeletionDialog
    {
        private readonly ScoreInfo score;

        public LocalScoreDeleteDialog(ScoreInfo score)
        {
            this.score = score;
        }

        [BackgroundDependencyLoader]
        private void load(ScoreManager scoreManager)
        {
            BodyText = $"{score.User} ({score.DisplayAccuracy}, {score.Rank.GetLocalisableDescription()})";
            DangerousAction = () => scoreManager.Delete(score);
        }
    }
}
