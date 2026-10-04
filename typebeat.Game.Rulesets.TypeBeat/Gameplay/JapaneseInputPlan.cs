// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kawazu;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>Readings shared by Japanese Polyglot input and the furigana display.</summary>
    internal sealed class JapaneseInputPlan
    {
        internal sealed record Group(int StartCell, int EndCellExclusive, string Reading, bool Furigana);
        internal IReadOnlyList<Group> Groups { get; }
        private readonly Group?[] cells;
        private readonly string text;

        private JapaneseInputPlan(string text, List<Group> groups)
        {
            this.text = text;
            Groups = groups;
            cells = new Group?[text.Length];
            for (int at = 0; at < groups.Count; at++)
            {
                Group group = groups[at];
                // Shared n and doubled consonants can cross dictionary word boundaries (本に,
                // 真っ赤). Keep ruby spans separate while the grammar reads both parts at once.
                while (at + 1 < groups.Count && group.EndCellExclusive == groups[at + 1].StartCell
                       && (group.Reading.EndsWith('っ')
                           || group.Reading.EndsWith('ん') && "なにぬねの".Contains(groups[at + 1].Reading[0])))
                {
                    var next = groups[++at];
                    group = new Group(group.StartCell, next.EndCellExclusive, group.Reading + next.Reading, false);
                }
                for (int i = group.StartCell; i < group.EndCellExclusive; i++)
                    cells[i] = group;
            }
        }

        internal Group? At(int cell) => cell >= 0 && cell < cells.Length ? cells[cell] : null;

        private readonly Dictionary<int, Group?> remainingGroups = new Dictionary<int, Group?>();

        /// <summary>The remaining reading after a direct input or skip inside a conversion group.</summary>
        internal Group? AtForInput(int cell)
        {
            var group = At(cell);
            if (group == null || group.StartCell == cell)
                return group;
            if (remainingGroups.TryGetValue(cell, out var remaining))
                return remaining;

            string prefix = string.Concat(JapaneseReading.Read(text.Substring(group.StartCell, cell - group.StartCell))
                .Select(p => JapaneseRomaji.Hiragana(p.Kana)));
            if (prefix.Length > 0 && prefix.Length < group.Reading.Length && group.Reading.StartsWith(prefix, StringComparison.Ordinal))
                remaining = new Group(cell, group.EndCellExclusive, group.Reading[prefix.Length..], false);
            else
            {
                var suffix = Create(text.Substring(cell, group.EndCellExclusive - cell)).At(0);
                remaining = suffix == null ? null : new Group(cell, cell + suffix.EndCellExclusive, suffix.Reading, false);
            }
            remainingGroups[cell] = remaining;
            return remaining;
        }

        internal bool IsJapaneseCell(int cell)
        {
            if (cell < 0 || cell >= text.Length || char.IsSurrogate(text[cell]))
                return false;
            char c = JapaneseRomaji.Hiragana(text[cell].ToString())[0];
            return Utilities.IsKanji(c) || Utilities.IsKana(c) || c is 'ー' or '々' or '〆';
        }

        internal bool IsRomanCell(int cell)
        {
            if (cell < 0 || cell >= text.Length || char.IsSurrogate(text[cell]))
                return false;
            // Compatibility forms include fullwidth Roman letters/digits. Latin extensions remain
            // literal Roman text too; the romaji used to type Japanese is not the lyric's script.
            char c = JapaneseRomaji.Hiragana(text[cell].ToString())[0];
            return c is >= '0' and <= '9' || char.IsLetter(c)
                && (c <= '\u024f' || c is >= '\u1e00' and <= '\u1eff' or >= '\u2c60' and <= '\u2c7f'
                    or >= '\ua720' and <= '\ua7ff' or >= '\uab30' and <= '\uab6f');
        }

        internal string Following(Group group)
        {
            int next = group.EndCellExclusive;
            return next < cells.Length ? cells[next]?.Reading ?? text[next].ToString() : string.Empty;
        }

        internal static JapaneseInputPlan Create(string text, LyricLine? authored = null, bool respectWordBoundaries = true)
        {
            var groups = new List<Group>();
            var parts = JapaneseReading.Read(text).ToList();
            int nextOriginal = 0;
            foreach (var unit in authored?.Units ?? Array.Empty<TimedUnit>())
            {
                string original = PolyglotText.ToNfc(unit.Original);
                if (original.Length == 0 && authored?.Units.Count == 1)
                    original = PolyglotText.ToNfc(authored.Original);
                if (original.Length == 0)
                    continue;
                // Punctuation may have been removed by the non-Literate cell stream.
                string surface = new string(original.Where(c => Typeability.IsPolyglotTypeCell(c) || c == ' ').ToArray());
                int start = text.IndexOf(surface, nextOriginal, StringComparison.Ordinal);
                if (start < 0 || surface.Length == 0)
                    continue;
                nextOriginal = start + surface.Length;
                if (!surface.Any(Utilities.IsKanji))
                    continue;

                // A dictionary compound may straddle two mapper-authored timing windows.
                // Read those pieces separately so committing one cannot judge the next word.
                if (respectWordBoundaries && parts.Any(p => p.Start < nextOriginal && p.Start + p.Length > start
                                                          && (p.Start < start || p.Start + p.Length > nextOriginal)))
                    replaceParts(start, nextOriginal, JapaneseReading.Read(surface)
                        .Select(p => new JapaneseReading.Reading(start + p.Start, p.Length, p.Kana)));

                string spelling = unit.Text.ToLowerInvariant();
                var dictionary = JapaneseReading.Romanise(original, "japanese");
                if (dictionary?.Text == spelling)
                    continue;
                var inside = parts.Where(p => p.Start >= start && p.Start + p.Length <= nextOriginal).ToArray();
                string existing = string.Concat(inside.Select(p => JapaneseRomaji.Hiragana(p.Kana)));
                if (existing.Length > 0 && (JapaneseRomaji.Matches(existing, spelling) & JapaneseRomaji.Match.Complete) != 0)
                    continue;
                string? reading = JapaneseRomaji.ReadAuthored(spelling);
                if (reading == null)
                    continue;
                replaceParts(start, nextOriginal, new[] { new JapaneseReading.Reading(start, surface.Length, reading) });
            }

            void replaceParts(int start, int end, IEnumerable<JapaneseReading.Reading> replacements)
            {
                var overlapping = parts.Where(p => p.Start < end && p.Start + p.Length > start).ToArray();
                parts.RemoveAll(p => p.Start < end && p.Start + p.Length > start);
                foreach (var part in overlapping)
                {
                    if (part.Start < start)
                        addRemainder(part.Start, start);
                    if (part.Start + part.Length > end)
                        addRemainder(end, part.Start + part.Length);
                }
                parts.AddRange(replacements);
            }

            void addRemainder(int start, int end)
            {
                foreach (var part in JapaneseReading.Read(text.Substring(start, end - start)))
                    parts.Add(new JapaneseReading.Reading(start + part.Start, part.Length, part.Kana));
            }

            foreach (JapaneseReading.Reading part in parts.OrderBy(p => p.Start))
            {
                string surface = text.Substring(part.Start, part.Length);
                string reading = JapaneseRomaji.Hiragana(part.Kana);
                string normal = normalise(surface, out var starts, out var ends);
                if (!surface.Any(Utilities.IsKanji))
                    continue;

                // Okurigana retains its own cells. Only the kanji run wears ruby, and only that
                // run must finish a dictionary reading before it can be converted.
                int prefix = 0;
                while (prefix < normal.Length && prefix < reading.Length && !Utilities.IsKanji(normal[prefix]) && normal[prefix] == reading[prefix])
                    prefix++;
                int suffix = 0;
                while (suffix < normal.Length - prefix && suffix < reading.Length - prefix
                       && !Utilities.IsKanji(normal[normal.Length - suffix - 1]) && normal[normal.Length - suffix - 1] == reading[reading.Length - suffix - 1])
                    suffix++;
                int originalPrefix = prefix == 0 ? 0 : ends[prefix - 1];
                int originalSuffix = suffix == 0 ? surface.Length : starts[normal.Length - suffix];
                string coreReading = reading.Substring(prefix, reading.Length - prefix - suffix);
                if (coreReading.Length > 0 && originalSuffix > originalPrefix)
                    groups.Add(new Group(part.Start + originalPrefix, part.Start + originalSuffix, coreReading, true));
            }

            // Kana supplies its own reading. Build its grammar directly from the displayed text,
            // independently of dictionary success and word segmentation. This also keeps small
            // kana and doubled consonants together when authored units divide a kana run.
            int freeStart = 0;
            foreach (Group group in groups.OrderBy(g => g.StartCell).ToArray())
            {
                addKana(freeStart, text.Substring(freeStart, group.StartCell - freeStart));
                freeStart = group.EndCellExclusive;
            }
            addKana(freeStart, text[freeStart..]);
            groups.Sort((a, b) => a.StartCell.CompareTo(b.StartCell));
            return new JapaneseInputPlan(text, groups);

            void addKana(int start, string source)
            {
                string kana = normalise(source, out var starts, out var ends);
                for (int at = 0; at < kana.Length;)
                {
                    int length = JapaneseRomaji.MoraLength(kana, at);
                    string mora = kana.Substring(at, length);
                    if (Utilities.IsKana(kana[at]) || kana[at] == 'ー')
                        groups.Add(new Group(start + starts[at], start + ends[at + length - 1], mora, false));
                    at += length;
                }
            }
        }
        private static string normalise(string source, out List<int> starts, out List<int> ends)
        {
            starts = new List<int>();
            ends = new List<int>();
            var kana = new StringBuilder();
            for (int at = 0; at < source.Length;)
            {
                int length = at + 1 < source.Length
                             && (char.IsSurrogatePair(source, at) || source[at + 1] is '\uff9e' or '\uff9f' or '\u3099' or '\u309a') ? 2 : 1;
                string normal = JapaneseRomaji.Hiragana(source.Substring(at, length));
                kana.Append(normal);
                foreach (char _ in normal)
                {
                    starts.Add(at);
                    ends.Add(at + length);
                }
                at += length;
            }
            return kana.ToString();
        }
    }
}
