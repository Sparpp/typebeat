// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Prevents failing. type!beat's only fail is the mash streak (TypeBeatHealthProcessor), which
    /// routes through HealthProcessor.Failed → Player.CheckModsAllowFailure; NoFail's PerformFail()
    /// returning false suppresses it with no ruleset-specific code.
    /// </summary>
    public class TypeBeatModNoFail : ModNoFail
    {
    }
}
