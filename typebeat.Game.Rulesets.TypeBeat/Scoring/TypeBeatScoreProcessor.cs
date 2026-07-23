// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// type!beat scoring. Total score, combo and ACCURACY are the standardised defaults, but the
    /// RANK is derived from <b>completion</b>: the fraction of typeable cells the player actually
    /// typed (any non-miss judgement), instead of accuracy. Typing every character earns an SS
    /// even with wrong-key stumbles and sloppy timing along the way; timing quality still shows in
    /// accuracy, score and combo, it just no longer gates the grade. Cells that scroll past
    /// untyped (miss judgements) are the only thing that costs rank.
    ///
    /// The server mirrors this exactly (typebeat-web ScoringContract.RankFromCompletion); keep
    /// the cutoffs in the two files in sync.
    /// </summary>
    public partial class TypeBeatScoreProcessor : ScoreProcessor
    {
        // Completion → rank cutoffs. Same band shape as the base game's accuracy cutoffs so the
        // grades keep their familiar feel; X strictly requires every cell typed.
        public const double COMPLETION_CUTOFF_X = 1;
        public const double COMPLETION_CUTOFF_S = 0.95;
        public const double COMPLETION_CUTOFF_A = 0.9;
        public const double COMPLETION_CUTOFF_B = 0.8;
        public const double COMPLETION_CUTOFF_C = 0.7;

        public TypeBeatScoreProcessor(TypeBeatRuleset ruleset)
            : base(ruleset)
        {
        }

        public override ScoreRank RankFromScore(double accuracy, IReadOnlyDictionary<HitResult, int> results)
            => RankFromCompletion(ComputeCompletion(results));

        /// <summary>Grade is awarded on completion, so the results-screen gauge fills to completion.</summary>
        public override double GradeProgress(ScoreInfo score) => ComputeCompletion(score);

        /// <summary>
        /// Completion over a set of judgement counts: typed cells / judged cells. Mid-play the
        /// denominator is what has been judged so far (completion sits at 1 until a cell seals as
        /// a miss); at the end of a completed play it is the whole map.
        /// </summary>
        public static double ComputeCompletion(IReadOnlyDictionary<HitResult, int> results)
        {
            int typed = 0, judged = 0;

            foreach ((var result, int count) in results)
            {
                // Line containers judge as IgnoreHit and carry no accuracy weight; the same
                // filter keeps them (and any bonus results) out of completion.
                if (!result.AffectsAccuracy())
                    continue;

                judged += count;

                if (result.IsHit())
                    typed += count;
            }

            return judged > 0 ? (double)typed / judged : 1;
        }

        /// <summary>
        /// Whole-map completion for a finished score: typed cells over the TOTAL cell count (from
        /// <see cref="ScoreInfo.MaximumStatistics"/>), so a failed run reads as "typed 43% of the
        /// map" rather than 100%-of-what-it-saw. Equal to the judged-denominator value for any
        /// completed play.
        /// </summary>
        public static double ComputeCompletion(ScoreInfo score)
        {
            int typed = 0, total = 0;

            foreach ((var result, int count) in score.Statistics)
            {
                if (result.AffectsAccuracy() && result.IsHit())
                    typed += count;
            }

            foreach ((var result, int count) in score.MaximumStatistics)
            {
                if (result.AffectsAccuracy())
                    total += count;
            }

            return total > 0 ? (double)typed / total : 1;
        }

        public static ScoreRank RankFromCompletion(double completion)
        {
            if (completion >= COMPLETION_CUTOFF_X)
                return ScoreRank.X;
            if (completion >= COMPLETION_CUTOFF_S)
                return ScoreRank.S;
            if (completion >= COMPLETION_CUTOFF_A)
                return ScoreRank.A;
            if (completion >= COMPLETION_CUTOFF_B)
                return ScoreRank.B;
            if (completion >= COMPLETION_CUTOFF_C)
                return ScoreRank.C;

            return ScoreRank.D;
        }
    }
}
