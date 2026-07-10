// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using osu.Framework.Allocation;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Graphics;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Skinning;
using osuTK.Graphics;

namespace typebeat.Game.Screens.Play.HUD.HitErrorMeters
{
    public abstract partial class HitErrorMeter : CompositeDrawable, ISerialisableDrawable
    {
        protected HitWindows HitWindows { get; private set; }

        [Resolved]
        private ScoreProcessor processor { get; set; }

        [Resolved]
        private OsuColour colours { get; set; }

        [Resolved(canBeNull: true)]
        private GameplayClockContainer gameplayClockContainer { get; set; }

        public bool UsesFixedAnchor { get; set; }

        [BackgroundDependencyLoader(true)]
        private void load(DrawableRuleset drawableRuleset)
        {
            HitWindows = drawableRuleset?.FirstAvailableHitWindows ?? HitWindows.Empty;

            // This is to allow the visual state to be correct after HUD comes visible after being hidden.
            AlwaysPresent = true;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            if (gameplayClockContainer != null)
                gameplayClockContainer.OnSeek += Clear;

            processor.NewJudgement += processorNewJudgement;
        }

        // Scheduled as meter implementations are likely going to change/add drawables when reacting to this.
        private void processorNewJudgement(JudgementResult j) => Schedule(() => OnNewJudgement(j));

        /// <summary>
        /// Fired when a new judgement arrives.
        /// </summary>
        /// <param name="judgement">The new judgement.</param>
        protected abstract void OnNewJudgement(JudgementResult judgement);

        protected Color4 GetColourForHitResult(HitResult result)
        {
            return colours.ForHitResult(result);
        }

        /// <summary>
        /// Invoked by <see cref="GameplayClockContainer.OnSeek"/>.
        /// Any inheritors of <see cref="HitErrorMeter"/> should have this method clear their container that displays the hit error results.
        /// </summary>
        public abstract void Clear();

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (processor != null)
                processor.NewJudgement -= processorNewJudgement;

            if (gameplayClockContainer != null)
                gameplayClockContainer.OnSeek -= Clear;
        }
    }
}
