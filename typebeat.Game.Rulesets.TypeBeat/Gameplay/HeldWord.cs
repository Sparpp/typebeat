// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// Which holds a word honours. A hold adds no syllable cut: the word is grouped and judged as without
    /// it, the held cell carries the span its release is graded on, and the letters after it are due no
    /// earlier than the hold's end (see <see cref="TypingCell.HandsOffFrom"/>). Stale holds are ignored, not repaired.
    /// </summary>
    internal static class HeldWord
    {
        internal const double MIN_HOLD_MS = 100;

        /// <summary>
        /// The usable holds of <paramref name="unit"/>, in character order: on a cell, at least
        /// <see cref="MIN_HOLD_MS"/> long, inside the word (a first-letter hold starts with it), not
        /// overlapping, between the word's cuts, and ending after its letter is due. None on a word with pauses.
        /// </summary>
        internal static IReadOnlyList<WordHold> UsableHolds(TimedUnit unit, Func<char, bool>? isCell = null)
        {
            var holds = unit.Objects.Holds;

            if (holds.Count == 0 || unit.Pauses.Count > 0)
                return Array.Empty<WordHold>();

            isCell ??= Typeability.IsCell;

            string text = unit.Text;
            var cuts = cutsOf(unit).ToArray();
            var kept = new List<WordHold>();
            int lastChar = -1;
            double lastEnd = unit.StartTime;

            foreach (var hold in holds.OrderBy(h => h.Char))
            {
                int c = hold.Char;
                bool hasNext = c + 1 < text.Length;

                if (c <= lastChar || c < 0 || c >= text.Length || !isCell(text[c]))
                    continue;

                if (hold.EndTime - hold.StartTime < MIN_HOLD_MS || hold.StartTime < lastEnd)
                    continue;

                bool startOk = c == 0
                    ? Math.Abs(hold.StartTime - unit.StartTime) < 0.5
                    : hold.StartTime > unit.StartTime && hold.StartTime < unit.EndTime;

                bool endOk = hasNext ? hold.EndTime < unit.EndTime : hold.EndTime <= unit.EndTime;

                if (!startOk || !endOk)
                    continue;

                bool cutsOk = cuts.All(cut => cut.Char < c ? cut.Time < hold.StartTime
                    : cut.Char == c || (cut.Char == c + 1 ? cut.Time >= hold.EndTime : cut.Time > hold.EndTime));

                if (!cutsOk || !EndsAfterItsLetter(hold.EndTime, LetterDueAt(unit, c, isCell)))
                    continue;

                kept.Add(hold);
                lastChar = c;
                lastEnd = hold.EndTime;
            }

            return kept;
        }

        /// <summary>A hold must end strictly after its letter is due, or the on-time press would miss its tail.</summary>
        internal static bool EndsAfterItsLetter(double holdEnd, double letterDue) => holdEnd > letterDue;

        /// <summary>
        /// When character <paramref name="charIndex"/> is due without any hold: the later of the two target
        /// eras, so a hold is honoured in both eras or neither.
        /// </summary>
        internal static double LetterDueAt(TimedUnit unit, int charIndex, Func<char, bool>? isCell = null)
            => TypingLine.HoldFreeCellTarget(unit, charIndex, isCell ?? Typeability.IsCell);

        /// <summary>The word's cuts as (character, time).</summary>
        private static IEnumerable<(int Char, double Time)> cutsOf(TimedUnit unit)
        {
            var splits = SyllableSegments.SplitsFor(unit);

            for (int i = 0; i < splits.Count && i < unit.SyllableBoundaries.Count; i++)
                yield return (splits[i], unit.SyllableBoundaries[i]);
        }
    }
}
