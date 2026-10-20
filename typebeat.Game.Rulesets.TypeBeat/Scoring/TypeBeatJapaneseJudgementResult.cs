// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>Input grades committed atomically with their completed Japanese character.</summary>
    public class TypeBeatJapaneseJudgementResult(HitObject hitObject, Judgement judgement) : JudgementResult(hitObject, judgement)
    {
        public IReadOnlyList<HitResult>? InputResults { get; set; }
    }
}
