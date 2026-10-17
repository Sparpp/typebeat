// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.Judgements;

namespace typebeat.Game.Rulesets.Scoring
{
    public partial class ScoreProcessor
    {
        /// <summary>Existing scoring results changed without applying another judgement.</summary>
        public event Action? ResultsUpdated;

        /// <summary>
        /// Replaces an existing result's scoring contribution, preserving completion, combo,
        /// health and its original position in the result stream.
        /// </summary>
        protected void UpdateScoringResult(JudgementResult result, Action update)
        {
            if (!result.HasResult || result.FailedAtJudgement && !ApplyNewJudgementsWhenFailed)
                return;

            int combo = Combo.Value, highestCombo = HighestCombo.Value;
            accumulate(-1);
            RemoveScoreChange(result);
            update();
            accumulate(1);
            ApplyScoreChange(result);
            Combo.Value = combo;
            HighestCombo.Value = highestCombo;

            if (TrackHitEvents)
            {
                int index = hitEvents.FindIndex(e => ReferenceEquals(e.HitObject, result.HitObject));
                if (index >= 0)
                {
                    var previous = hitEvents[index];
                    hitEvents[index] = new HitEvent(result.TimeOffset, result.GameplayRate, result.Type,
                        result.HitObject, previous.LastHitObject, previous.Position);
                }
            }
            updateScore();
            updateRank();
            ResultsUpdated?.Invoke();

            void accumulate(int direction)
            {
                var inputs = GetScoringResults(result);
                int count = inputs?.Count ?? 1;
                if (inputs == null)
                    adjust(result.Type);
                else
                    foreach (var input in inputs)
                        adjust(input);
                if (result.Judgement.MaxResult.AffectsAccuracy())
                {
                    currentMaximumBaseScore += direction * count * GetBaseScoreForResult(result.Judgement.MaxResult);
                    currentAccuracyJudgementCount += direction * count;
                }
                if (result.Type.IsBonus())
                    currentBonusPortion += direction * GetBonusScoreChange(result);
                else if (result.Type.IsScorable())
                    currentComboPortion += direction * GetComboScoreChange(result);

                void adjust(HitResult type)
                {
                    ScoreResultCounts[type] = ScoreResultCounts.GetValueOrDefault(type) + direction;
                    if (type.AffectsAccuracy())
                        currentBaseScore += direction * GetBaseScoreForResult(type);
                }
            }
        }
    }
}
