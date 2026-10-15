// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Judgements;

namespace typebeat.Game.Rulesets.TypeBeat.Objects
{
    /// <summary>The release of a held cell, at the hold's end. Its press is the cell's own <see cref="TypeBeatCharObject"/>.</summary>
    public class TypeBeatHoldTailObject : HitObject
    {
        public int LineIndex { get; set; }

        /// <summary>The held cell's index.</summary>
        public int CellIndex { get; set; }

        public override Rulesets.Judgements.Judgement CreateJudgement() => new TypeBeatHoldTailJudgement();

        protected override HitWindows CreateHitWindows() => HitWindows.Empty;
    }
}
