// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// type!beat's take on Relax: every keypress is accepted as the correct character, so the play
    /// is purely about keeping up with the timing. Implemented by flipping a single engine flag
    /// (TypingEngine.MashingEnabled) that short-circuits the per-key match check.
    /// </summary>
    public class TypeBeatModMashing : ModRelax, IApplicableToTypingEngine
    {
        public override string Name => "Mashing";

        public override LocalisableString Description => "Any key is the right key, just keep the rhythm.";

        public void ApplyToEngine(TypingEngine engine) => engine.MashingEnabled = true;
    }
}
