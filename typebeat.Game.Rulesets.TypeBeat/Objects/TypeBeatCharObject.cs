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
        public int Phase { get; set; }
        public int MinimumRomajiInputs { get; set; }
        public bool JapaneseInputScoring { get; set; }
        internal int FullComboAfter { get; set; }

        public bool ConfigureJapaneseScoring(bool characterTiming, bool inputScoring)
        {
            bool scoring = Phase == 0 || characterTiming && !inputScoring;
            bool inputs = Phase == 0 && MinimumRomajiInputs > 0 && characterTiming && inputScoring;
            bool changed = ScoringEnabled != scoring || JapaneseInputScoring != inputs;
            ScoringEnabled = scoring;
            JapaneseInputScoring = inputs;
            return changed;
        }

        public bool ScoringEnabled { get; set; } = true;

        public override Rulesets.Judgements.Judgement CreateJudgement() => new CharacterPhaseJudgement(this);

        private sealed class CharacterPhaseJudgement(TypeBeatCharObject owner) : TypeBeatCharJudgement
        {
            public override HitResult MaxResult => owner.ScoringEnabled ? HitResult.Great : HitResult.IgnoreHit;
        }

        protected override HitWindows CreateHitWindows() => new TypeBeatHitWindows(Typeability.IsFreestyle(Expected));
    }
}
