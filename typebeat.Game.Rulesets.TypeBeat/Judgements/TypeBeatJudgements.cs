// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Judgements
{
    /// <summary>
    /// Scoring info for one typeable cell. The engine's four quality tiers map onto the osu results
    /// they are named for (Perfect, Great, Ok, Meh); Premature, Lagging and a seal miss map to Miss,
    /// and an uncorrected typo takes <see cref="TypeBeat.Scoring.TypeBeatResultMapping.UNFIXED_TYPO"/>.
    ///
    /// <para>The MaxResult is what decides which results a cell may legally take at all: the valid
    /// set is the enum interval [MinResult, MaxResult] (see
    /// <see cref="TypeBeat.Scoring.TypeBeatResultMapping.UNFIXED_TYPO"/> for the full argument), so
    /// backlog 133's fourth quality tier is exactly what raised it from Great to Perfect. It costs
    /// the accuracy DENOMINATOR nothing: the base game gives Perfect the same base score of 300 as a
    /// Great, deliberately, so the per-cell maximum is unmoved.</para>
    /// </summary>
    public class TypeBeatCharJudgement : Rulesets.Judgements.Judgement
    {
        public override HitResult MaxResult => HitResult.Perfect;
    }

    /// <summary>
    /// Scoring info for the line container object itself: scoring-inert (the nested cells carry
    /// all score/accuracy weight); applied on seal so osu sees the line resolve.
    /// </summary>
    public class TypeBeatLineJudgement : Rulesets.Judgements.Judgement
    {
        public override HitResult MaxResult => HitResult.IgnoreHit;
    }
}
