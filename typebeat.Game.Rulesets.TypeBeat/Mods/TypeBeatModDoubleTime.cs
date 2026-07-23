// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Speeds up the track. The engine judges in beatmap-time (delta = time - cell.TargetTime),
    /// so the fixed sync windows narrow in real time, exactly osu's intended difficulty effect,
    /// no ruleset-specific code needed.
    /// </summary>
    public class TypeBeatModDoubleTime : ModDoubleTime
    {
    }
}
