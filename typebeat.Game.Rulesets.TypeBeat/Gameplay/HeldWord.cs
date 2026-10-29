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
        /// overlapping, between the word's cuts, clear of its rests (<see cref="ClearOfRest"/>), and ending
        /// after its letter is due.
        /// </summary>
        internal static IReadOnlyList<WordHold> UsableHolds(TimedUnit unit, Func<char, bool>? isCell = null)
        {
            var holds = unit.Objects.Holds;

            if (holds.Count == 0)
                return Array.Empty<WordHold>();

            isCell ??= Typeability.IsCell;

            string text = unit.Text;
            var rests = RestsOf(unit, isCell);
            var cuts = cutsOf(unit, rests.Count > 0, isCell).ToArray();
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

                if (!cutsOk || !rests.All(rest => ClearOfRest(hold, rest)) || !EndsAfterItsLetter(hold.EndTime, LetterDueAt(unit, c, isCell)))
                    continue;

                kept.Add(hold);
                lastChar = c;
                lastEnd = hold.EndTime;
            }

            return kept;
        }

        /// <summary>The rests a play honours on <paramref name="unit"/> (<see cref="PausedWord.UsableRests"/>), in time order.</summary>
        internal static IReadOnlyList<WordPause> RestsOf(TimedUnit unit, Func<char, bool>? isCell = null)
            => unit.Pauses.Count == 0
                ? Array.Empty<WordPause>()
                : PausedWord.UsableRests(unit.Text, unit.StartTime, unit.EndTime, unit.Pauses, isCell ?? Typeability.IsCell);

        /// <summary>
        /// A hold sits wholly inside one sung stretch of its word: a letter before the rest's cut is let go by
        /// the rest's start, a letter after it is held from the rest's end. A hold never spans or overlaps a rest.
        /// </summary>
        internal static bool ClearOfRest(WordHold hold, WordPause rest)
            => hold.Char < rest.SplitChar ? hold.EndTime <= rest.StartTime : hold.StartTime >= rest.EndTime;

        /// <summary>
        /// Whether one of the word's rests cuts between letter <paramref name="charIndex"/> and the word's next
        /// cell: that cell opens a new stretch, so it is not handed off from a hold on this letter.
        /// </summary>
        internal static bool RestFollows(TimedUnit unit, int charIndex, Func<char, bool>? isCell = null)
        {
            isCell ??= Typeability.IsCell;
            int next = charIndex + 1;

            while (next < unit.Text.Length && !isCell(unit.Text[next]))
                next++;

            return RestsOf(unit, isCell).Any(rest => rest.SplitChar > charIndex && rest.SplitChar <= next);
        }

        /// <summary>A hold must end strictly after its letter is due, or the on-time press would miss its tail.</summary>
        internal static bool EndsAfterItsLetter(double holdEnd, double letterDue) => holdEnd > letterDue;

        /// <summary>
        /// When character <paramref name="charIndex"/> is due without any hold: the later of the two target
        /// eras, so a hold is honoured in both eras or neither.
        /// </summary>
        internal static double LetterDueAt(TimedUnit unit, int charIndex, Func<char, bool>? isCell = null)
            => TypingLine.HoldFreeCellTarget(unit, charIndex, isCell ?? Typeability.IsCell);

        /// <summary>
        /// The word's cuts as (character, time). On a paused word, each stretch's own cuts against its own
        /// boundaries (<see cref="PausedWord.Of"/>); the rests themselves are <see cref="ClearOfRest"/>'s.
        /// </summary>
        private static IEnumerable<(int Char, double Time)> cutsOf(TimedUnit unit, bool paused, Func<char, bool> isCell)
        {
            if (paused && PausedWord.Of(unit.Text, unit.StartTime, unit.EndTime, unit, isCell) is PausedWord.Cut cut)
            {
                foreach (var piece in cut.Pieces)
                {
                    for (int i = 0; i < piece.Cuts.Count && i < piece.Boundaries.Count; i++)
                        yield return (piece.Cuts[i], piece.Boundaries[i]);
                }

                yield break;
            }

            var splits = SyllableSegments.SplitsFor(unit);

            for (int i = 0; i < splits.Count && i < unit.SyllableBoundaries.Count; i++)
                yield return (splits[i], unit.SyllableBoundaries[i]);
        }
    }
}
