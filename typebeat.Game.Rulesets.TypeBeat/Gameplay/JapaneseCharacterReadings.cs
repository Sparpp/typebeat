// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    internal static class JapaneseCharacterReadings
    {
        private static readonly JapaneseTextCache<(string Surface, string Reading), string[]> readings = new JapaneseTextCache<(string, string), string[]>(2048);

        internal static string[] Split(string surface, string reading)
            => readings.GetOrAdd((surface, reading), static key => split(key.Surface, key.Reading));

        private static string[] split(string surface, string reading)
        {
            var memo = new Dictionary<(int, int), (double Cost, string[] Parts)>();
            return solve(0, 0).Parts;

            (double Cost, string[] Parts) solve(int character, int kana)
            {
                if (character == surface.Length)
                    return (kana == reading.Length ? 0 : double.PositiveInfinity, Array.Empty<string>());
                if (memo.TryGetValue((character, kana), out var cached))
                    return cached;
                var best = (Cost: double.PositiveInfinity, Parts: Array.Empty<string>());
                int remaining = surface.Length - character;
                for (int length = 1; length <= reading.Length - kana - remaining + 1; length++)
                {
                    string part = reading.Substring(kana, length);
                    var tail = solve(character + 1, kana + length);
                    if (double.IsPositiveInfinity(tail.Cost))
                        continue;
                    bool known = OriginalLyricAlignment.Matches(surface[character].ToString(), Romaniser.Romanise(part, "japanese").Text, "japanese");
                    // Irregular compound readings still retain their contextual pronunciation.
                    // Choose balanced kana spans only where individual readings cannot resolve it.
                    double cost = tail.Cost + (known ? 0 : 100) + Math.Abs(length - reading.Length / (double)surface.Length);
                    if (cost < best.Cost)
                    {
                        var parts = new string[tail.Parts.Length + 1];
                        parts[0] = part;
                        Array.Copy(tail.Parts, 0, parts, 1, tail.Parts.Length);
                        best = (cost, parts);
                    }
                }
                memo[(character, kana)] = best;
                return best;
            }
        }
    }
}
