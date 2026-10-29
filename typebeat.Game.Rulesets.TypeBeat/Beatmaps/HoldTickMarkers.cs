// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Holds in the editor's line box (backlog 447): <see cref="HOLD_MARKER"/> straight after a letter marks
    /// it held, and a run of <see cref="TICK_MARKER"/> straight after that '=' is the hold's tick count
    /// ("lo=**ve" holds the 'o' with two ticks). Every other '*' is punctuation. Like '|', the marks are
    /// editor-only: read and stripped on commit (before the text is normalised), never stored.
    ///
    /// <para>A '=' straight after another mark is a CLOSER: it ends the tick run and marks nothing, so a
    /// held letter followed by a literal star still reads back ("f*ck" held on 'f' shows "f==*ck").</para>
    /// </summary>
    public static class HoldTickMarkers
    {
        public const char HOLD_MARKER = '=';
        public const char TICK_MARKER = '*';

        /// <summary>Whether <paramref name="unit"/> has a usable hold to show.</summary>
        public static bool HasHolds(TimedUnit unit) => unit.Objects.Holds.Count > 0 && HeldWord.UsableHolds(unit).Count > 0;

        /// <summary>
        /// <paramref name="pipeToken"/> with '=' and a star per tick straight after each usable held letter
        /// ("lo=*|ve"), and a closing '=' where a literal star would otherwise read as a tick.
        /// </summary>
        public static string WithMarks(string pipeToken, TimedUnit unit)
        {
            if (!HasHolds(unit))
                return pipeToken;

            var counts = HeldWord.UsableHolds(unit).ToDictionary(h => h.Char, h => HoldTicks.UsableTicks(h).Length);
            var sb = new StringBuilder(pipeToken.Length + 4);
            int c = 0;

            for (int i = 0; i < pipeToken.Length; i++)
            {
                char ch = pipeToken[i];
                sb.Append(ch);

                if (ch == Typeability.SPLIT_MARKER)
                    continue;

                if (counts.TryGetValue(c, out int n))
                {
                    sb.Append(HOLD_MARKER).Append(TICK_MARKER, n);

                    if (i + 1 < pipeToken.Length && pipeToken[i + 1] == TICK_MARKER)
                        sb.Append(HOLD_MARKER);
                }

                c++;
            }

            return sb.ToString();
        }

        /// <summary>Which characters of the box's <paramref name="boxText"/> are hold marks: every '=' and each opener's tick run.</summary>
        public static bool[] MarkMask(string boxText) => scan(boxText, out _);

        /// <summary><paramref name="boxText"/> with its hold marks removed (pipes kept).</summary>
        public static string WithoutMarks(string boxText)
        {
            if (boxText.IndexOf(HOLD_MARKER) < 0)
                return boxText;

            bool[] mask = MarkMask(boxText);
            var sb = new StringBuilder(boxText.Length);

            for (int i = 0; i < boxText.Length; i++)
            {
                if (!mask[i])
                    sb.Append(boxText[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// The mark mask of <paramref name="text"/>, and per character the tick count of an OPENING '=' there
        /// (-1 everywhere else). A '=' straight after a mark closes; any other '=' opens.
        /// </summary>
        private static bool[] scan(string text, out int[] ticksAt)
        {
            var mask = new bool[text.Length];
            ticksAt = new int[text.Length];
            Array.Fill(ticksAt, -1);

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != HOLD_MARKER)
                    continue;

                bool closer = i > 0 && mask[i - 1];
                mask[i] = true;

                if (closer)
                    continue;

                int run = 0;

                while (i + 1 + run < text.Length && text[i + 1 + run] == TICK_MARKER)
                    mask[i + 1 + run++] = true;

                ticksAt[i] = run;
                i += run;
            }

            return mask;
        }

        /// <summary>One whitespace-separated token of a read box text: its text without marks, and its held letters.</summary>
        /// <param name="Token">The token with its hold marks removed (pipes kept).</param>
        /// <param name="Holds">Per held letter (index into the NORMALISED word, pipes removed) its tick count.</param>
        public sealed record MarkedWord(string Token, IReadOnlyDictionary<int, int> Holds);

        /// <summary>
        /// Reads a committed box text BEFORE it is normalised: returns it with its hold marks removed (tokens
        /// joined by single spaces) and every token left non-empty. <paramref name="refused"/> says a '=' marked
        /// no letter (at a word's start, or after a character normalising drops).
        /// </summary>
        public static (string Text, IReadOnlyList<MarkedWord> Words) Read(string raw, out bool refused)
        {
            raw ??= string.Empty;
            refused = false;

            bool[] mask = scan(raw, out int[] ticksAt);
            var words = new List<MarkedWord>();
            var sb = new StringBuilder();
            var holds = new Dictionary<int, int>();

            for (int i = 0; i <= raw.Length; i++)
            {
                if (i == raw.Length || char.IsWhiteSpace(Typeability.FoldTypographic(raw[i])))
                {
                    if (sb.Length > 0)
                        words.Add(new MarkedWord(sb.ToString(), holds));
                    else
                        refused |= holds.Count > 0;

                    sb.Clear();
                    holds = new Dictionary<int, int>();
                    continue;
                }

                if (!mask[i])
                {
                    sb.Append(raw[i]);
                    continue;
                }

                if (ticksAt[i] < 0)
                    continue;

                // The held letter is the last character read; it has to survive normalising.
                string before = sb.ToString();
                int length = normalisedLength(before);

                if (before.Length == 0 || before[^1] == Typeability.SPLIT_MARKER || length == normalisedLength(before[..^1]))
                {
                    refused = true;
                    continue;
                }

                holds[length - 1] = holds.GetValueOrDefault(length - 1) + ticksAt[i];
            }

            return (string.Join(' ', words.Select(w => w.Token)), words);
        }

        /// <summary>
        /// The held letters of the words a NORMALISED commit keeps: a token that normalises to nothing (or to
        /// pipes alone) is dropped there, so it is dropped here too. <paramref name="refused"/> is set when such
        /// a token carried a mark.
        /// </summary>
        public static IReadOnlyList<IReadOnlyDictionary<int, int>> KeptWords(IReadOnlyList<MarkedWord> words, ref bool refused)
        {
            var kept = new List<IReadOnlyDictionary<int, int>>(words.Count);

            foreach (var word in words)
            {
                if (normalisedLength(word.Token) > 0)
                    kept.Add(word.Holds);
                else
                    refused |= word.Holds.Count > 0;
            }

            return kept;
        }

        /// <summary>The length of <paramref name="text"/> as a normalised lyric, pipes not counted.</summary>
        private static int normalisedLength(string text)
        {
            string normalised = Typeability.Normalize(text, keepFreestyleMarkers: true, keepSplitMarkers: true);
            return normalised.Length - normalised.Count(ch => ch == Typeability.SPLIT_MARKER);
        }

        /// <summary>
        /// <paramref name="units"/> with each word's usable holds set to what <paramref name="marks"/> asks: a
        /// marked letter already held keeps its hold (its timing, and its ticks while the count is unchanged,
        /// else re-spaced evenly), a newly marked letter takes <paramref name="newHold"/>'s hold, and a held
        /// letter left unmarked loses its hold. Holds no play would honour are not shown, so they are left alone.
        /// <paramref name="refused"/> says a mark could not be honoured (no usable hold, or fewer ticks than
        /// asked). Applied only when <paramref name="marks"/> pairs one-to-one with <paramref name="units"/>; a
        /// null entry leaves its word as it is.
        /// </summary>
        public static IReadOnlyList<TimedUnit> Apply(IReadOnlyList<TimedUnit> units, IReadOnlyList<IReadOnlyDictionary<int, int>?>? marks,
                                                     Func<TimedUnit, int, WordHold?> newHold, out bool refused)
        {
            refused = false;

            if (marks == null || marks.Count != units.Count)
                return units;

            TimedUnit[]? result = null;

            for (int i = 0; i < units.Count; i++)
            {
                if (marks[i] is not { } wanted)
                    continue;

                var unit = units[i];
                var usable = HeldWord.UsableHolds(unit);

                if (wanted.Count == 0 && usable.Count == 0)
                    continue;

                var holds = unit.Objects.Holds.Where(h => !usable.Contains(h) || wanted.ContainsKey(h.Char)).ToList();

                foreach (int c in wanted.Keys.OrderBy(c => c))
                {
                    if (usable.Any(h => h.Char == c))
                        continue;

                    // A stale hold on the letter gives way to the new one.
                    var others = holds.Where(h => h.Char != c).ToArray();

                    if (newHold(unit.WithObjects(unit.Objects with { Holds = others }), c) is not WordHold added)
                    {
                        refused = true;
                        continue;
                    }

                    holds = others.Append(added).OrderBy(h => h.Char).ToList();
                }

                var shown = HeldWord.UsableHolds(unit.WithObjects(unit.Objects with { Holds = holds.ToArray() }));

                for (int k = 0; k < holds.Count; k++)
                {
                    var hold = holds[k];

                    if (!shown.Contains(hold) || !wanted.TryGetValue(hold.Char, out int n) || n == HoldTicks.UsableTicks(hold).Length)
                        continue;

                    double[] ticks = HoldTicks.Even(hold.StartTime, hold.EndTime, n);
                    refused |= ticks.Length < n;
                    holds[k] = hold with { Ticks = ticks };
                }

                if (holds.SequenceEqual(unit.Objects.Holds))
                    continue;

                result ??= units.ToArray();
                result[i] = unit.WithObjects(unit.Objects with { Holds = holds.ToArray() });
            }

            return result ?? units;
        }
    }
}
