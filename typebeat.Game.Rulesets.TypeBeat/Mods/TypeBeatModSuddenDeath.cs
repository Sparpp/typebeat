// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Fail on the first mistake. Two halves, and both are load-bearing:
    ///
    /// <list type="bullet">
    /// <item>The inherited fail condition covers the CELLS: anything reaching the health processor
    /// as <c>HitResult.Miss</c>, i.e. a character the line ran out of time on and a right character
    /// struck outside the Ok window.</item>
    /// <item>The subscription below covers the KEYPRESSES, which raise no judgement result in
    /// EITHER input model. Under <see cref="TypeBeatModGatekeeper"/> a wrong key is rejected and
    /// never had one; in default play (backlog 109) a typed-through wrong char DEFERS its cell's
    /// result, so nothing is a Miss until the line seals on the typo uncorrected.</item>
    /// </list>
    ///
    /// <para><c>TypingEngine.Mistyped</c> is deliberately the hook, not <c>WrongKeyRejected</c>: it
    /// fires exactly once per wrong keypress in BOTH models, where <c>WrongKeyRejected</c> fires
    /// only under Gatekeeper. Sudden Death has to fail on the FIRST wrong key, not on the seal that
    /// eventually notices it, and not twice. Pinned in both models by
    /// <c>TestSceneTypeBeatGatekeeper</c>, because "Sudden Death silently stopped failing on wrong
    /// keys" is exactly the kind of regression a judgement-timing change can hide.</para>
    /// </summary>
    public class TypeBeatModSuddenDeath : ModSuddenDeath, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.Mistyped += TriggerFailure;
    }
}
