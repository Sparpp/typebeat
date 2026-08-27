// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Screens.Play.HUD.JudgementCounter
{
    /// <summary>
    /// Keeps track of judgements for a current play session, exposing bindable counts which can
    /// be used for display purposes.
    /// </summary>
    public partial class JudgementCountController : Component
    {
        [Resolved]
        private ScoreProcessor scoreProcessor { get; set; } = null!;

        private readonly Dictionary<HitResult, JudgementCount> results = new Dictionary<HitResult, JudgementCount>();

        public IEnumerable<JudgementCount> Counters => counters;

        private readonly List<JudgementCount> counters = new List<JudgementCount>();

        private Ruleset rulesetInstance = null!;

        [BackgroundDependencyLoader]
        private void load(IBindable<RulesetInfo> ruleset)
        {
            rulesetInstance = ruleset.Value.CreateInstance();

            // Due to weirdness in judgements, some results have the same name and should be aggregated for display purposes.
            // There's only one case of this right now ("slider end").
            foreach (var group in rulesetInstance.GetHitResultsForDisplay().GroupBy(r => r.displayName))
            {
                var judgementCount = new JudgementCount
                {
                    DisplayName = group.Key,
                    Types = group.Select(r => r.result).ToArray(),
                    ResultCount = new BindableInt()
                };

                counters.Add(judgementCount);

                foreach (var r in group)
                    results[r.result] = judgementCount;
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            scoreProcessor.OnResetFromReplayFrame += updateAllCountsFromReplayFrame;
            scoreProcessor.NewJudgement += judgement => updateCount(judgement, false);
            scoreProcessor.JudgementReverted += judgement => updateCount(judgement, true);
        }

        private bool hasUpdatedCountsFromReplayFrame;

        private void updateAllCountsFromReplayFrame()
        {
            if (hasUpdatedCountsFromReplayFrame)
                return;

            // Accumulated, not assigned, because a ruleset may fold several stored results into one
            // displayed counter (Ruleset.GetDisplayResultFor). Zeroing first keeps this the absolute
            // set it has always been: it runs once, at a seek, and must not add to what is showing.
            foreach (var counter in counters)
                counter.ResultCount.Value = 0;

            foreach (var kvp in scoreProcessor.Statistics)
            {
                if (!results.TryGetValue(rulesetInstance.GetDisplayResultFor(kvp.Key), out var count))
                    continue;

                count.ResultCount.Value += kvp.Value;
            }

            hasUpdatedCountsFromReplayFrame = true;
        }

        private void updateCount(JudgementResult judgement, bool revert)
        {
            if (!results.TryGetValue(rulesetInstance.GetDisplayResultFor(judgement.Type), out var count))
                return;

            if (revert)
                count.ResultCount.Value--;
            else
                count.ResultCount.Value++;
        }
    }
}
