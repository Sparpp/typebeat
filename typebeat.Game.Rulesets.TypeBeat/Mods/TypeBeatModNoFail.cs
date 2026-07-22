// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Prevents failing. Both type!beat fail paths (health hitting zero from misses, and the mash
    /// streak — see TypeBeatHealthProcessor) route through HealthProcessor.Failed →
    /// Player.CheckModsAllowFailure; NoFail's PerformFail() returning false suppresses them with no
    /// ruleset-specific code.
    /// </summary>
    public class TypeBeatModNoFail : ModNoFail
    {
    }
}
