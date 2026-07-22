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
    /// Literate: restores case sensitivity to gameplay. Normally typing is case-insensitive (the
    /// caret folds both the key and the target to lower-case before matching); with this mod on, a
    /// letter must be typed in the target's EXACT case — a right letter in the wrong case is judged
    /// wrong, just like any other wrong char. Implemented by flipping a single engine flag
    /// (<see cref="Gameplay.TypingEngine.CaseSensitive"/>); the key handler already forwards Shift so
    /// held-Shift keys produce the capitals the target demands.
    /// </summary>
    public class TypeBeatModLiterate : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override string Name => "Literate";

        public override string Acronym => "LT";

        public override LocalisableString Description => "Case matters — type every letter in its exact case.";

        public override ModType Type => ModType.DifficultyIncrease;

        // The real multiplier is defined in TypeBeatScoreMultiplierCalculator (the authoritative,
        // non-obsolete path osu now uses). This obsolete override is kept only so the mod also
        // self-reports 1.05x for any legacy reader.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.05;
#pragma warning restore CS0672

        public override bool Ranked => true;

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.CaseSensitive = true;
    }
}
