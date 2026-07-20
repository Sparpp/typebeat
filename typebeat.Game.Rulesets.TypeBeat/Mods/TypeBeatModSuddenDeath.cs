// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Fail on the first mistake. The inherited fail-condition already fires on missed / badly
    /// mistimed characters (they reach the health processor as HitResult.Miss); a rejected wrong
    /// key produces no judgement result, so we additionally fail the play the moment the engine
    /// rejects one.
    /// </summary>
    public class TypeBeatModSuddenDeath : ModSuddenDeath, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.WrongKeyRejected += _ => TriggerFailure();
    }
}
