// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>Retyped cells may improve their award without earning another combo increment.</summary>
        public bool BestRetypeJudgement { get; set; } = true;

        private static bool betterTier(JudgementType candidate, JudgementType previous)
            => SyncWindows.BasePoints(candidate) > SyncWindows.BasePoints(previous);

        private int inputAccuracyPoints(JudgementType tier) => tier switch
        {
            JudgementType.Great => 300,
            JudgementType.Ok => 100,
            JudgementType.Meh => 50,
            JudgementType.Premature or JudgementType.Lagging when OffTime == OffTimeRule.MehHit => 50,
            _ => 0,
        };

        private JudgementType awardedRetypeTier(TypingCell cell, double delta, bool pastRushCap)
        {
            var tier = TypeBeatResultMapping.AwardedTier(WindowsFor(cell).Classify(delta), cell.HeldWrongBeforeJudged, CorrectionCredit);
            return pastRushCap ? TypeBeatResultMapping.RushCapTier(tier) : tier;
        }

        private int improveRetypedAward(TypingCell cell, JudgementType tier, double delta, double time, bool finish = false)
        {
            var previous = finish ? cell.EndJudgedTier!.Value : cell.JudgedTier!.Value;
            counts[previous]--;
            counts[tier]++;
            int award = (int)Math.Round(SyncWindows.BasePoints(tier) * (finish ? cell.EndAwardMultiplier : cell.AwardMultiplier));
            int points = award - (finish ? cell.EndAwardedPoints : cell.AwardedPoints);
            score += points;
            int sample = finish ? cell.EndSyncSampleIndex : cell.SyncSampleStart;
            if (sample >= 0)
                syncTimeline[sample] = new SyncSample(time, delta);
            if (finish)
            {
                cell.FirstEndDelta = delta;
                cell.EndJudgedTier = tier;
                cell.EndAwardedPoints = award;
            }
            else
            {
                cell.FirstCorrectDelta = delta;
                cell.JudgedTier = tier;
                cell.AwardedPoints = award;
            }
            return points;
        }

        private (int Points, bool Upgraded) improveJapaneseRetype(TypingLine line, TypingCell cell, int cellIndex, double time)
        {
            var previous = cell.InputJudgements!;
            double end = line.JapaneseCharacterEnd(cellIndex);
            double[] times = japaneseCommitInputTimes is { Length: > 0 } ? japaneseCommitInputTimes : new[] { time };
            var candidate = times.Select((t, i) =>
            {
                double delta = i == 0 || t < cell.TargetTime ? t - cell.TargetTime : Math.Max(0, t - end);
                return new JapaneseInputJudgement(awardedRetypeTier(cell, delta, false), delta);
            }).ToArray();

            if (candidate.Length == previous.Count)
            {
                bool changed = false;
                for (int i = 0; i < candidate.Length; i++)
                {
                    if (betterTier(candidate[i].Type, previous[i].Type))
                        changed = true;
                    else
                    {
                        candidate[i] = previous[i];
                        times[i] = syncTimeline[cell.SyncSampleStart + i]!.Value.Time;
                    }
                }
                if (!changed)
                    return (0, false);
            }
            // Different valid spellings have different input counts. Keep the complete attempt
            // with better accuracy rather than inventing a correspondence between its keystrokes.
            else if (candidate.Average(i => inputAccuracyPoints(i.Type)) <= previous.Average(i => inputAccuracyPoints(i.Type)))
                return (0, false);

            foreach (var input in previous)
                counts[input.Type]--;
            foreach (var input in candidate)
                counts[input.Type]++;
            int difference = candidate.Length - previous.Count;
            totalTimedCells += difference;
            totalKeypresses += difference;
            correctKeypresses += difference;
            for (int i = 0; i < cell.SyncSampleCount; i++)
                syncTimeline[cell.SyncSampleStart + i] = null;
            cell.SyncSampleStart = syncTimeline.Count;
            cell.SyncSampleCount = candidate.Length;
            for (int i = 0; i < candidate.Length; i++)
                syncTimeline.Add(new SyncSample(times[i], candidate[i].Delta));
            cell.InputJudgements = candidate;
            cell.FirstCorrectDelta = candidate[0].Delta;
            cell.JudgedTier = candidate[0].Type;
            int award = candidate.Sum(i => (int)Math.Round(SyncWindows.BasePoints(i.Type) * cell.AwardMultiplier));
            int points = award - cell.AwardedPoints;
            score += points;
            cell.AwardedPoints = award;
            return (points, true);
        }
    }
}
