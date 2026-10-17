// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Fixed reference scales from the 119 installed dev maps on 2026-10-06 at 1.0x.
    /// Future maps may exceed 10; zero is the only numeric clamp. This only changes display.
    /// </summary>
    internal static class DifficultyMetricScale
    {
        // The Hanging Tree [Insane], no mods (online map 58).
        internal const double MostTypableZ = 0.6651010887772172;
        // Burn, Baby, Burn [easy], Literate (online map 296).
        internal const double LeastTypableLiterateZ = -0.821946054506459;
        // Looping the Rooms [Muzukashii], no mods (online map 90).
        internal const double LeastRhythmLoad = 1.6334230042489838e-05;
        // Kingslayer [Extreme], Hard Rock (online map 84).
        internal const double MostHardRockRhythmLoad = 0.013568227458937066;

        internal static double Complexity(double z)
            => Math.Max(0, 1 + 9 * (MostTypableZ - z) / (MostTypableZ - LeastTypableLiterateZ));

        internal static double Rhythm(double loadPerSecond)
            => Math.Max(0, 1 + 9 * (loadPerSecond - LeastRhythmLoad) / (MostHardRockRhythmLoad - LeastRhythmLoad));

        internal static float BarLength(double value) => (float)Math.Clamp(value / 10, 0, 1);
    }
}
