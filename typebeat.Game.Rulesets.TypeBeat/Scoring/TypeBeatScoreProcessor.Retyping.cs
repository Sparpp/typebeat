// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    public partial class TypeBeatScoreProcessor
    {
        private readonly Dictionary<(int Line, int Cell, int Phase), JudgementResult> awardedCells = new Dictionary<(int, int, int), JudgementResult>();

        /// <summary>Amends the cell's existing award without a second combo increment or health event.</summary>
        public void UpdateCellResult(JudgementResult result, HitResult type, double? delta, IReadOnlyList<HitResult>? inputs = null)
        {
            var japanese = result as TypeBeatJapaneseJudgementResult;
            if (result.Type == type && result.JudgedTimeOffset == delta
                && (japanese?.InputResults == inputs || japanese?.InputResults != null && inputs != null && japanese.InputResults.SequenceEqual(inputs)))
                return;
            UpdateScoringResult(result, () =>
            {
                result.Type = type;
                result.JudgedTimeOffset = delta;
                if (japanese != null)
                    japanese.InputResults = inputs;
            });
        }

        // The framework rewinds whole results by their first award time. A best-tier amendment
        // retains that time, so restore the surviving cells' awards from the rebuilt engine too.
        private void resyncCellAwards(TypingEngine engine)
        {
            foreach (var (key, result) in awardedCells.ToArray())
            {
                var cell = engine.Lines[key.Line].Cells[key.Cell];
                var tier = key.Phase == 0 ? cell.JudgedTier : cell.EndJudgedTier;
                if (tier == null)
                    continue;
                var type = TypeBeatResultMapping.CellResult(tier.Value, TypoRule.Deferred, engine.OffTime);
                if (type.HasValue)
                    UpdateCellResult(result, type.Value, key.Phase == 0 ? cell.FirstCorrectDelta : cell.FirstEndDelta,
                        key.Phase == 0 ? cell.InputJudgements?.Select(i => TypeBeatResultMapping.CellResult(i.Type, TypoRule.Deferred, engine.OffTime) ?? HitResult.Miss).ToArray() : null);
            }
        }
    }
}
