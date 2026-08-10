// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Gatekeeper: the line refuses to take a character you did not earn. A wrong key is REJECTED
    /// outright, so nothing is written into the cell and the caret does not move; you stay on the
    /// same character until you hit it. That was type!beat's original model and the default until
    /// backlog 107, when typing wrong characters through (the way every typing site behaves) became
    /// the default and this became opt-in.
    ///
    /// <para>Implemented by clearing one engine flag
    /// (<see cref="Gameplay.TypingEngine.AllowWrongInput"/>) through
    /// <see cref="IApplicableToDrawableRuleset{T}"/>, which is the pattern
    /// <see cref="TypeBeatModMashing"/> and <see cref="TypeBeatModFlashlight"/> use. Deliberately
    /// NOT the <see cref="TypeBeatModLiterate"/> pattern (stamping the hit objects and reading the
    /// mod list back in <c>DrawableTypeBeatRuleset.createEngine</c>): Literate needs that because it
    /// changes the CELL LIST and so must be known before the engine is constructed, whereas this is
    /// a per-keypress judgement flag the engine reads fresh on every key. Nothing else writes the
    /// flag any more (the ruleset setting it used to come from is gone), so there is no ordering
    /// question left for a later playfield load to lose.</para>
    ///
    /// <para>NO multiplier of any kind, score or pp. It is a real difficulty increase, but the two
    /// models already price themselves honestly against each other: rejection keeps the caret on the
    /// character, so a stumble under this mod costs the timing windows of everything behind it
    /// (accuracy) where the default model costs a cell outright (completion). Ranked at 1.0x, exactly
    /// like Sudden Death, which is why it appears in neither
    /// <see cref="Scoring.TypeBeatScoreMultiplierCalculator"/> nor
    /// <c>PerformancePoints.ModMultiplier</c>: both treat an unlisted acronym as 1.0.</para>
    ///
    /// <para>It also restores the 13-consecutive-wrong-keys mash fail, because that streak only ever
    /// accrues on the rejection path (see <c>TypingEngine.ProcessKey</c>). That guard exists to stop
    /// a masher farming a model that will not take a wrong char, so it belongs here.</para>
    /// </summary>
    public class TypeBeatModGatekeeper : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override string Name => "Gatekeeper";

        public override string Acronym => "GK";

        public override ModType Type => ModType.DifficultyIncrease;

        public override LocalisableString Description => "Wrong keys are rejected outright instead of typed through: the caret waits until you get it right.";

        public override bool Ranked => true;

        // Legacy self-report only; the authoritative multiplier lives in the non-obsolete
        // TypeBeatScoreMultiplierCalculator, which leaves this mod unlisted (i.e. 1.0x) on purpose.
        // Both say 1.0x and must move together, which for this mod means neither ever moves.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.0;
#pragma warning restore CS0672

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.AllowWrongInput = false;
    }
}
