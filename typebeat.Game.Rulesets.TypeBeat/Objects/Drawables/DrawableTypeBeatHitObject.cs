// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
        private readonly Dictionary<int, DrawableTypeBeatCharObject> charDrawablesByCell = new Dictionary<int, DrawableTypeBeatCharObject>();

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
        /// cell's real Great/Ok/Meh, leave it and <see cref="ApplySealResults"/> misses it exactly
        /// like a cell nobody ever touched. Applying a Miss used to make the two indistinguishable
        /// AND unrecoverable, because
        /// <see cref="DrawableTypeBeatCharObject.ApplyEngineResult"/> drops every later result.</para>
        ///
        /// <para>The combo break the mistype costs therefore has no result to travel on, and is
        /// mirrored into the score processor by hand on <see cref="TypingEngine.Mistyped"/> instead
        /// (see <c>TypeBeatPlayfield.onMistyped</c>). That is the seam a REJECTED key has always
        /// used, so the two input models now account for a wrong keypress identically. The cell is
        /// PREPAID at the same moment (backlog 122) so that the deferred Miss, when it lands, misses
        /// the cell without breaking combo a second time.</para>
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
        /// Called when the engine seals this line: every still-unjudged cell becomes an osu Miss,
        /// then the line object itself resolves scoring-inert (IgnoreHit) so osu accuracy tracks
        /// only the cells. "Still unjudged" is exactly the engine's own seal-miss set: a cell that
        /// was never typed, and a cell left sitting wrong (see <see cref="ApplyCharJudgement"/>).
        /// </summary>
        public void ApplySealResults()
        {
            foreach (var charDrawable in charDrawablesByCell.Values)
                charDrawable.ApplyEngineResult(TypeBeatResultMapping.SEAL_MISS);

            if (!Judged)
                ApplyResult(TypeBeatResultMapping.LINE_RESULT);
        }

        protected override void CheckForResult(bool userTriggered, double timeOffset)
        {
            // Results come exclusively from the engine (sealing drives ApplySealResults).
        }
    }
}
