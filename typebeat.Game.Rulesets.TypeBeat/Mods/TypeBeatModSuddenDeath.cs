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
    ///
    /// <para>Both halves are load-bearing since backlog 107 made typing-through the default. In
    /// DEFAULT play a wrong char lands in its cell and is judged <c>JudgementType.WrongChar</c>,
    /// which <c>DrawableTypeBeatHitObject.toHitResult</c> maps to <c>HitResult.Miss</c>, so the
    /// INHERITED condition fires and the play still fails on the first wrong key. The subscription
    /// below then covers the cases that produce no judgement at all: every wrong key under
    /// <see cref="TypeBeatModGatekeeper"/>, plus the space cases the default path also rejects.
    /// Pinned by <c>TypeBeatModGatekeeperTest</c>, because "Sudden Death silently stopped failing on
    /// wrong keys" is exactly the kind of regression a default flip can hide.</para>
    /// </summary>
    public class TypeBeatModSuddenDeath : ModSuddenDeath, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.WrongKeyRejected += _ => TriggerFailure();
    }
}
