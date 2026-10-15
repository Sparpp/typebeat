// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>Which typed-letter picks of an auto-fill word a play honours.</summary>
    public static class TypedLetters
    {
        /// <summary>The usable picks, ascending and distinct: only on an auto-fill word, inside the text, on a letter or digit cell.</summary>
        public static IReadOnlyList<int> UsablePicks(TimedUnit unit, Func<char, bool> isCell)
        {
            var picks = unit.Objects.TypedLetters;

            if (!unit.Objects.AutoFill || picks.Count == 0)
                return Array.Empty<int>();

            string text = unit.Text;

            return picks.Where(i => i >= 0 && i < text.Length && isCell(text[i]) && char.IsLetterOrDigit(text[i])).Distinct().OrderBy(i => i).ToArray();
        }

        /// <summary>Whether the editor lets a mapper pick <paramref name="c"/> (the default-rules test of <see cref="UsablePicks"/>).</summary>
        public static bool IsPickable(char c) => Typeability.IsCell(c) && char.IsLetterOrDigit(c);
    }
}
