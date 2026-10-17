// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// A mod that changes how a <see cref="TypingEngine"/> judges: the ONE place that mod's engine
    /// settings are written. Every engine factory calls it, so a live play
    /// (<c>DrawableTypeBeatRuleset.createEngine</c>), a re-judged replay
    /// (<see cref="Scoring.TypeBeatReplayScorer.CreateEngine"/>) and the Puppeteer co-simulation
    /// (<see cref="Scoring.PuppeteerReplayTransform"/>, which builds its engine through the scorer)
    /// cannot disagree about what a mod does to the rules.
    ///
    /// <para>Called once, on a freshly constructed engine, before the first keypress and before any
    /// replay header is fed. A window scale is therefore MULTIPLIED in (the factors compose in any
    /// order) and every flag is assigned; no two mods assign the same flag, so the result does not
    /// depend on the order of the mod list either. A flag that a replay's CONFIG frame also carries (the
    /// caret pin, Gatekeeper's wrong-input model, Hard Rock's point targets) is written here too:
    /// a stored replay's own header is fed afterwards and wins, so the mod only decides it where no
    /// header does, which is live play.</para>
    ///
    /// <para>The mods that change the CELL LIST (Literate, Polyglot) are not here: they have to be
    /// known before the engine exists, and stay constructor arguments.</para>
    /// </summary>
    public interface IApplicableToTypingEngine : IApplicableMod
    {
        void ApplyToEngine(TypingEngine engine);
    }
}
