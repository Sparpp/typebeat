// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Objects.Drawables
{
    /// <summary>
    /// Invisible scoring carrier for one lyric line. Rendering happens in
    /// <see cref="UI.LyricStage"/> (driven by the engine); this drawable hosts the nested
    /// per-cell scoring objects and resolves itself when the engine seals the line.
    /// </summary>
    public partial class DrawableTypeBeatHitObject : DrawableHitObject<TypeBeatHitObject>
    {
        /// <summary>
        /// The nested cell drawables, keyed by cell index. SORTED rather than a plain
        /// <see cref="Dictionary{TKey,TValue}"/> because <see cref="ApplySealResults"/> iterates it,
        /// and since backlog 124 the seal can hand out two different results (a Miss, which breaks
        /// combo, and an unfixed typo, which is weighted by the combo it finds). The order the seal
        /// walks the cells in therefore reaches the score, so it is pinned to cell order here rather
        /// than left to the insertion order the framework happens to add nested objects in. It is
        /// also the order <c>typebeat-core.js</c> walks, which is the point.
        /// </summary>
        private readonly SortedDictionary<int, DrawableTypeBeatCharObject> charDrawablesByCell = new SortedDictionary<int, DrawableTypeBeatCharObject>();

        public DrawableTypeBeatHitObject(TypeBeatHitObject hitObject)
            : base(hitObject)
        {
        }

        protected override DrawableHitObject CreateNestedHitObject(HitObject hitObject)
        {
            if (hitObject is TypeBeatCharObject charObject)
                return new DrawableTypeBeatCharObject(charObject);

            return base.CreateNestedHitObject(hitObject);
        }

        protected override void AddNestedHitObject(DrawableHitObject hitObject)
        {
            base.AddNestedHitObject(hitObject);

            if (hitObject is DrawableTypeBeatCharObject charDrawable)
            {
                charDrawablesByCell[charDrawable.HitObject.CellIndex] = charDrawable;
                AddInternal(charDrawable);
            }
        }

        protected override void ClearNestedHitObjects()
        {
            base.ClearNestedHitObjects();
            charDrawablesByCell.Clear();
            ClearInternal(false);
        }

        /// <summary>
        /// Routes an engine char judgement to the matching nested cell drawable, through the
        /// shared <see cref="TypeBeatResultMapping.CellResult"/> (which is also what
        /// <see cref="Scoring.TypeBeatReplayScorer"/> re-derives a stored score with, so live play
        /// and recalculation cannot drift). Live play is always <see cref="TypoRule.Deferred"/>;
        /// nothing in gameplay may select the other rule.
        ///
        /// <para>A <see cref="JudgementType.WrongChar"/> maps to NOTHING (backlog 109). A miss is a
        /// character the line ran out of time on; a typo is a typo, and in the default input model
        /// the player can still backspace and type the cell correctly. So a wrong keypress DEFERS
        /// the cell's one osu result instead of spending it: correct it and the retype earns the
        /// cell's real Great/Ok/Meh, leave it and <see cref="ApplySealResults"/> resolves it as
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, a hit, because the player did finish
        /// that character (backlog 124). Applying a Miss used to make the two indistinguishable AND
        /// unrecoverable, because
        /// <see cref="DrawableTypeBeatCharObject.ApplyEngineResult"/> drops every later result.</para>
        ///
        /// <para>The combo break the mistype costs therefore has no result to travel on, and is
        /// mirrored into the score processor by hand on <see cref="TypingEngine.Mistyped"/> instead
        /// (see <c>TypeBeatPlayfield.onMistyped</c>). That is the seam a REJECTED key has always
        /// used, so the two input models now account for a wrong keypress identically. That break is
        /// the cell's whole combo consequence, which is why the seal applies the unfixed typo's hit
        /// combo-neutral (<see cref="Scoring.TypeBeatScoreProcessor.MarkComboNeutral"/>).</para>
        /// </summary>
        public void ApplyCharJudgement(CharJudgement judgement)
        {
            if (TypeBeatResultMapping.CellResult(judgement.Type, TypoRule.Deferred) is not HitResult result)
                return;

            if (!charDrawablesByCell.TryGetValue(judgement.CellIndex, out var charDrawable))
                return;

            charDrawable.ApplyEngineResult(result);
        }

        /// <summary>
        /// Called when the engine seals this line: every still-unjudged cell takes the result
        /// <paramref name="resultForCell"/> gives for its cell index, in ascending cell order, and
        /// then the line object itself resolves scoring-inert (IgnoreHit) so osu accuracy tracks only
        /// the cells.
        ///
        /// <para>"Still unjudged" is exactly the set of cells the play never resolved: one nobody
        /// typed, and one left sitting wrong (see <see cref="ApplyCharJudgement"/>). Since backlog
        /// 124 those two take DIFFERENT results, which is why the caller decides rather than this
        /// loop: only the caller can see the engine's cell states. Already-judged cells are skipped
        /// before the callback runs, so a caller that marks a cell as it answers cannot mark one
        /// whose result is about to be dropped.</para>
        /// </summary>
        public void ApplySealResults(Func<int, HitResult> resultForCell)
        {
            foreach ((int cellIndex, var charDrawable) in charDrawablesByCell)
            {
                if (charDrawable.Judged)
                    continue;

                charDrawable.ApplyEngineResult(resultForCell(cellIndex));
            }

            if (!Judged)
                ApplyResult(TypeBeatResultMapping.LINE_RESULT);
        }

        protected override void CheckForResult(bool userTriggered, double timeOffset)
        {
            // Results come exclusively from the engine (sealing drives ApplySealResults).
        }
    }
}
