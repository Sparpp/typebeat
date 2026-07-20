// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

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
        /// Routes an engine char judgement to the matching nested cell drawable.
        /// Mapping: Perfect->Great, Good->Ok, Ok->Meh, Premature/Lagging/WrongChar->Miss.
        /// Premature/Lagging accept the char with 0 engine points + combo break; osu Miss also
        /// breaks combo, so the mapping is behaviour-coherent for combo (score weights differ).
        /// </summary>
        public void ApplyCharJudgement(CharJudgement judgement)
        {
            if (!charDrawablesByCell.TryGetValue(judgement.CellIndex, out var charDrawable))
                return;

            charDrawable.ApplyEngineResult(toHitResult(judgement.Type));
        }

        /// <summary>
        /// Called when the engine seals this line: every still-unjudged cell becomes an osu Miss
        /// (matching the engine marking untyped cells Missed), then the line object itself
        /// resolves scoring-inert (IgnoreHit) so osu accuracy tracks only the cells.
        /// </summary>
        public void ApplySealResults()
        {
            foreach (var charDrawable in charDrawablesByCell.Values)
                charDrawable.ApplyEngineResult(HitResult.Miss);

            if (!Judged)
                ApplyResult(HitResult.IgnoreHit);
        }

        private static HitResult toHitResult(JudgementType type)
        {
            switch (type)
            {
                case JudgementType.Perfect:
                    return HitResult.Great;

                case JudgementType.Good:
                    return HitResult.Ok;

                case JudgementType.Ok:
                    return HitResult.Meh;

                default:
                    // Premature, Lagging, WrongChar (and Miss, which never reaches here).
                    return HitResult.Miss;
            }
        }

        protected override void CheckForResult(bool userTriggered, double timeOffset)
        {
            // Results come exclusively from the engine (sealing drives ApplySealResults).
        }
    }
}
