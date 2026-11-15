// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Screens.Play.HUD
{
    public abstract partial class GameplayScoreCounter : ScoreCounter
    {
        private Bindable<long> totalScoreBindable = null!;

        protected GameplayScoreCounter()
            : base(6)
        {
        }

        [BackgroundDependencyLoader]
        private void load(ScoreProcessor scoreProcessor)
        {
            totalScoreBindable = scoreProcessor.TotalScore.GetBoundCopy();
            totalScoreBindable.BindValueChanged(score => Current.Value = score.NewValue, true);
        }
    }
}
