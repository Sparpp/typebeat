// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Objects.Drawables;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Objects.Drawables
{
    /// <summary>
    /// Invisible scoring carrier for one typeable cell. The visible cell is rendered by
    /// <see cref="UI.LyricLineDisplay"/> from engine state; this drawable only exists so the
    /// engine's per-char judgements reach osu's <see cref="ScoreProcessor"/> via ApplyResult.
    /// </summary>
    public partial class DrawableTypeBeatCharObject : DrawableHitObject<TypeBeatCharObject>
    {
        public DrawableTypeBeatCharObject(TypeBeatCharObject hitObject)
            : base(hitObject)
        {
        }

        /// <summary>
        /// Forwards an engine judgement as this cell's one-and-only osu result. Later engine
        /// re-judgements of the same cell (backspace + retype) are ignored: the first osu result
        /// stands, mirroring the engine's own scoring-inert retype rule for ever-correct cells.
        /// </summary>
        public void ApplyEngineResult(HitResult result)
        {
            if (Judged)
                return;

            ApplyResult(result);
        }

        protected override void CheckForResult(bool userTriggered, double timeOffset)
        {
            // Results come exclusively from the engine (via ApplyEngineResult); the engine's
            // line sealing guarantees every cell resolves. No time-based auto-judgement here.
        }
    }
}
