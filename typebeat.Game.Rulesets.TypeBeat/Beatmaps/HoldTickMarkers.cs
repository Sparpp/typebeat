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
    /// Hold ticks in the editor's line box: a run of <see cref="TICK_MARKER"/> straight after a held letter
    /// is that hold's tick count; anywhere else '*' is punctuation. Like '|', tick marks are stripped on commit.
    ///
    /// <para>Run-length rule (for words with literal stars, e.g. "f*ck" held on 'f'): of a run of L stars
    /// after a held letter, the first L - m are ticks, where m is the word's own stars there.</para>
    /// </summary>
    public static class HoldTickMarkers
    {
        public const char TICK_MARKER = '*';

        /// <summary>Whether any usable hold of <paramref name="unit"/> has a tick.</summary>
        public static bool HasTicks(TimedUnit unit)
            => unit.Objects.Holds.Count > 0 && HeldWord.UsableHolds(unit).Any(h => HoldTicks.UsableTicks(h).Length > 0);

        /// <summary><paramref name="pipeToken"/> with a star per tick straight after each held letter ("hel*|lo").</summary>
        public static string WithTicks(string pipeToken, TimedUnit unit)
        {
            if (!HasTicks(unit))
                return pipeToken;

            var counts = HeldWord.UsableHolds(unit).ToDictionary(h => h.Char, h => HoldTicks.UsableTicks(h).Length);
            var sb = new StringBuilder(pipeToken.Length + 4);
            int c = 0;

            foreach (char ch in pipeToken)
            {
                sb.Append(ch);

                if (ch == Typeability.SPLIT_MARKER)
                    continue;

                if (counts.TryGetValue(c, out int n) && n > 0)
                    sb.Append(TICK_MARKER, n);

                c++;
            }

            return sb.ToString();
        }

        /// <summary>Which characters of the box's <paramref name="boxText"/> are tick marks of <paramref name="units"/>.</summary>
        public static bool[] TickStarMask(string boxText, IReadOnlyList<TimedUnit> units)
        {
            var mask = new bool[boxText.Length];
            int word = 0, start = 0;

            for (int i = 0; i <= boxText.Length; i++)
            {
                if (i < boxText.Length && boxText[i] != ' ')
                    continue;

                if (i > start && word < units.Count && units[word].Objects.Holds.Count > 0)
                {
                    var unit = units[word];
                    var held = HeldWord.UsableHolds(unit).Select(h => h.Char).ToHashSet();
                    bool[] stars = tickStarsOf(boxText.Substring(start, i - start), held, unit.Text);
                    Array.Copy(stars, 0, mask, start, stars.Length);
                }

                word++;
                start = i + 1;
            }

            return mask;
        }

        /// <summary>
        /// The run-length rule: which characters of <paramref name="token"/> (one word, pipes kept) are tick
        /// marks, given its <paramref name="held"/> letter indices into its stored <paramref name="text"/>.
        /// </summary>
        private static bool[] tickStarsOf(string token, IReadOnlySet<int> held, string text)
        {
            var mask = new bool[token.Length];

            if (held.Count == 0)
                return mask;

            int c = 0;

            for (int i = 0; i < token.Length; i++)
            {
                if (token[i] == Typeability.SPLIT_MARKER)
                    continue;

                bool isHeld = held.Contains(c);
                c++;

                if (!isHeld)
                    continue;

                // The word's own stars after the held letter.
                int literal = 0;

                while (c + literal < text.Length && text[c + literal] == TICK_MARKER)
                    literal++;

                // The star run straight after the letter.
                int run = 0;

                while (i + 1 + run < token.Length && token[i + 1 + run] == TICK_MARKER)
                    run++;

                // Every star up to the next letter, pipes skipped.
                int span = run;

                for (int k = i + 1 + run; k < token.Length && (token[k] == TICK_MARKER || token[k] == Typeability.SPLIT_MARKER); k++)
                {
                    if (token[k] == TICK_MARKER)
                        span++;
                }

                int ticks = Math.Min(run, Math.Max(0, span - literal));

                for (int k = 0; k < ticks; k++)
                    mask[i + 1 + k] = true;

                // Tick marks are not characters of the word.
                i += ticks;
            }

            return mask;
        }

        /// <summary>The box's words as stored text: pipes and tick marks removed.</summary>
        public static string[] PlainWords(string boxText, bool[] tickStars)
        {
            var sb = new StringBuilder(boxText.Length);

            for (int i = 0; i < boxText.Length; i++)
            {
                if (boxText[i] != Typeability.SPLIT_MARKER && !(i < tickStars.Length && tickStars[i]))
                    sb.Append(boxText[i]);
            }

            return sb.ToString().Split(' ');
        }

        /// <summary>
        /// Reads a committed box text (pipes kept): returns it with tick marks removed, and per kept token the
        /// tick count per held character, or null for a token that is not an unchanged held word. A token is
        /// paired with the word at the same index when the word count is unchanged, otherwise with the next
        /// word it spells.
        /// </summary>
        public static (string Text, IReadOnlyList<IReadOnlyDictionary<int, int>?> Counts) Read(string withMarkers, IReadOnlyList<TimedUnit> units)
        {
            string[] tokens = (withMarkers ?? string.Empty).Split(' ');
            int keptCount = tokens.Count(isWord);
            var counts = new List<IReadOnlyDictionary<int, int>?>(keptCount);
            bool sameCount = keptCount == units.Count;
            int cursor = 0, keptIndex = 0;

            for (int t = 0; t < tokens.Length; t++)
            {
                if (!isWord(tokens[t]))
                    continue;

                (string Token, Dictionary<int, int> Counts)? match = null;

                if (sameCount)
                    match = tryStrip(tokens[t], units[keptIndex]);
                else
                {
                    string plainToken = tokens[t].Replace(Typeability.SPLIT_MARKER.ToString(), string.Empty);

                    for (int u = cursor; u < units.Count && match == null; u++)
                    {
                        match = tryStrip(tokens[t], units[u]);

                        // An unheld word the token spells takes its place, so a repeated word never skips to a later held twin.
                        if (match == null && plainToken == units[u].Text && HeldWord.UsableHolds(units[u]).Count == 0)
                        {
                            cursor = u + 1;
                            break;
                        }

                        if (match != null)
                            cursor = u + 1;
                    }
                }

                if (match is { } m)
                {
                    tokens[t] = m.Token;
                    counts.Add(m.Counts);
                }
                else
                    counts.Add(null);

                keptIndex++;
            }

            return (string.Join(' ', tokens), counts);

            static bool isWord(string token) => token.Any(ch => ch != Typeability.SPLIT_MARKER);
        }

        /// <summary>
        /// <paramref name="token"/> with the tick marks after <paramref name="unit"/>'s held letters removed and
        /// their counts, when what is left spells <paramref name="unit"/>; else null.
        /// </summary>
        private static (string Token, Dictionary<int, int> Counts)? tryStrip(string token, TimedUnit unit)
        {
            if (unit.Objects.Holds.Count == 0)
                return null;

            var held = HeldWord.UsableHolds(unit).Select(h => h.Char).ToHashSet();

            if (held.Count == 0)
                return null;

            bool[] ticks = tickStarsOf(token, held, unit.Text);
            var output = new StringBuilder(token.Length);
            var plain = new StringBuilder(token.Length);
            var counts = new Dictionary<int, int>();
            int c = 0;

            for (int i = 0; i < token.Length; i++)
            {
                char ch = token[i];

                // A tick mark belongs to the last character read.
                if (ticks[i])
                    counts[c - 1] = counts.GetValueOrDefault(c - 1) + 1;
                else
                {
                    output.Append(ch);

                    if (ch != Typeability.SPLIT_MARKER)
                    {
                        plain.Append(ch);
                        c++;
                    }
                }
            }

            return plain.ToString() == unit.Text ? (output.ToString(), counts) : null;
        }

        /// <summary>
        /// <paramref name="units"/> with each held word's ticks set to its read count: kept when the count is
        /// unchanged, else re-spaced evenly. <paramref name="trimmed"/> says a hold got fewer ticks than asked.
        /// Applied only when <paramref name="counts"/> pairs one-to-one with <paramref name="units"/>.
        /// </summary>
        public static IReadOnlyList<TimedUnit> Apply(IReadOnlyList<TimedUnit> units, IReadOnlyList<IReadOnlyDictionary<int, int>?>? counts, out bool trimmed)
        {
            trimmed = false;

            if (counts == null || counts.Count != units.Count)
                return units;

            TimedUnit[]? result = null;

            for (int i = 0; i < units.Count; i++)
            {
                if (counts[i] is not { } wanted || units[i].Objects.Holds.Count == 0)
                    continue;

                var unit = units[i];
                var usable = HeldWord.UsableHolds(unit);
                WordHold[]? holds = null;

                for (int k = 0; k < unit.Objects.Holds.Count; k++)
                {
                    var hold = unit.Objects.Holds[k];

                    if (!usable.Contains(hold))
                        continue;

                    int n = wanted.GetValueOrDefault(hold.Char);

                    if (n == HoldTicks.UsableTicks(hold).Length)
                        continue;

                    double[] ticks = HoldTicks.Even(hold.StartTime, hold.EndTime, n);
                    trimmed |= ticks.Length < n;

                    holds ??= unit.Objects.Holds.ToArray();
                    holds[k] = hold with { Ticks = ticks };
                }

                if (holds == null)
                    continue;

                result ??= units.ToArray();
                result[i] = unit.WithObjects(unit.Objects with { Holds = holds });
            }

            return result ?? units;
        }
    }
}
