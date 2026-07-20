// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Judgements;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Objects
{
    /// <summary>
    /// One nested scoring object per typeable display cell of a lyric line.
    /// StartTime is the cell's interpolated target time; the engine (not osu's hit windows)
    /// decides the result, forwarded via the drawable when the engine judges the cell.
    /// </summary>
    public class TypeBeatCharObject : HitObject
    {
        public int LineIndex { get; set; }

        /// <summary>Display-cell index within the line (indexes ALL cells, typeable or not).</summary>
        public int CellIndex { get; set; }

        /// <summary>The normalized display char this cell expects.</summary>
        public char Expected { get; set; }

        /// <summary>Window tier the engine judges this cell at (widened for unreliable timing).</summary>
        public TimingGranularity JudgeGranularity { get; set; }

        public override Rulesets.Judgements.Judgement CreateJudgement() => new TypeBeatCharJudgement();

        protected override HitWindows CreateHitWindows() => new TypeBeatHitWindows(JudgeGranularity);
    }
}
