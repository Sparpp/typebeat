// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Objects.Drawables
{
    /// <summary>
    /// Invisible scoring carrier for a hold's tail or tick: the engine judges it, and this only forwards
    /// the result to the score processor.
    /// </summary>
    public partial class DrawableTypeBeatHoldPart<TObject> : DrawableHitObject<TObject>
        where TObject : HitObject
    {
        public DrawableTypeBeatHoldPart(TObject hitObject)
            : base(hitObject)
        {
        }

        /// <summary>Applies the engine's result; a later one is ignored.</summary>
        public void ApplyEngineResult(HitResult result)
        {
            if (Judged)
                return;

            ApplyResult(static (r, type) => r.Type = type, result);
        }

        protected override void CheckForResult(bool userTriggered, double timeOffset)
        {
            // Results come only from the engine.
        }
    }
}
