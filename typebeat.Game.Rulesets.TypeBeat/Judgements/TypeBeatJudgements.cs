// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Judgements
{
    /// <summary>
    /// Scoring info for one typeable cell. Engine judgements map onto osu results as
    /// Perfect->Great, Good->Ok, Ok->Meh, Premature/Lagging/WrongChar/seal-miss->Miss.
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
