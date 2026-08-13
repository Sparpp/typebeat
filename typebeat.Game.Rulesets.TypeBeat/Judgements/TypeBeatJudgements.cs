// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Judgements
{
    /// <summary>
    /// Scoring info for one typeable cell. The engine's three quality tiers map onto the osu
    /// results they are named for (Great, Ok, Meh); Premature, Lagging and a seal miss map to Miss,
    /// and an uncorrected typo takes <see cref="TypeBeat.Scoring.TypeBeatResultMapping.UNFIXED_TYPO"/>.
    ///
    /// <para>The MaxResult is what decides which results a cell may legally take at all: the valid
    /// set is the enum interval [MinResult, MaxResult] (see
    /// <see cref="TypeBeat.Scoring.TypeBeatResultMapping.UNFIXED_TYPO"/> for the full argument).
    /// Backlog 133's fourth quality tier raised it from Great to Perfect, and backlog 147 put it
    /// back: with three tiers the interval {Miss, Meh, Ok, Good, Great} is exactly wide enough, and
    /// keeping the ceiling at Perfect would leave <c>maximum_statistics</c> claiming one
    /// <see cref="HitResult.Perfect"/> per cell that no play could ever earn.</para>
    /// </summary>
    public class TypeBeatCharJudgement : Rulesets.Judgements.Judgement
    {
        public override HitResult MaxResult => HitResult.Great;
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
