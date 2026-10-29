// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Judgements;

namespace typebeat.Game.Rulesets.TypeBeat.Objects
{
    /// <summary>One tick of a held cell, at the tick's time.</summary>
    public class TypeBeatHoldTickObject : HitObject
    {
        public int LineIndex { get; set; }

        /// <summary>The held cell's index.</summary>
        public int CellIndex { get; set; }

        /// <summary>This tick's index among its hold's ticks.</summary>
        public int TickIndex { get; set; }

        public override Rulesets.Judgements.Judgement CreateJudgement() => new TypeBeatHoldTickJudgement();

        protected override HitWindows CreateHitWindows() => HitWindows.Empty;
    }
}
