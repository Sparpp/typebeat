// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>Only splits a linked original where both romanized halves match its script.</summary>
    internal static class OriginalLyricAlignment
    {
        private static readonly JapaneseTextCache<(string Original, string Text, string? Language), bool> matches = new JapaneseTextCache<(string, string, string?), bool>(8192);
        private static readonly JapaneseTextCache<(string Original, string Text, string? Language), Alignment> alignments = new JapaneseTextCache<(string, string, string?), Alignment>(4096);
        private static readonly char[] readingPunctuation = Typeability.PUNCTUATION.Where(c => c is not '\'' and not '-').ToArray();

        internal static bool Matches(string original, string text, string? language)
            => matches.GetOrAdd((original, text, language), static key => matchesUncached(key.Original, key.Text, key.Language));

        private static bool matchesUncached(string original, string text, string? language)
        {
            string input = new string(text.Where(c => char.IsLetterOrDigit(c) || c == '\'').Select(char.ToLowerInvariant).ToArray());
            var word = LyricOriginals.RomaniseWord(original, language);
            if (!word.Flagged && new string(word.Text.Where(c => char.IsLetterOrDigit(c) || c == '\'').Select(char.ToLowerInvariant).ToArray()) == input)
                return true;
            if (!JapaneseReading.IsJapanese(language))
                return false;
            string surface = new string(original.Where(char.IsLetterOrDigit).ToArray());
            string kana = JapaneseRomaji.Hiragana(string.Concat(JapaneseReading.Read(surface).Select(p => p.Kana)));
            return kana.Length > 0 && (JapaneseRomaji.Matches(kana, input) & JapaneseRomaji.Match.Complete) != 0
                   || JapaneseLyricGenerator.HasDictionaryReading(surface, input);
        }

        internal static int? SourceCut(string original, string text, int cut, string? language)
            => cut <= 0 || cut >= text.Length ? null
                : alignments.GetOrAdd((original, text, language), static key => new Alignment(key.Original, key.Text, key.Language)).SourceCut(cut);

        private sealed class Alignment
        {
            private readonly string original;
            private readonly string text;
            private readonly string? language;
            private readonly LyricOriginals.RomanisedWord word;
            private readonly Lazy<(int[] Indices, string[] Readings)> japanese;
            private readonly Dictionary<int, int?> cuts = new Dictionary<int, int?>();

            internal Alignment(string original, string text, string? language)
            {
                this.original = original;
                this.text = text;
                this.language = language;
                word = LyricOriginals.RomaniseWord(original, language);
                japanese = new Lazy<(int[], string[])>(japaneseReadings);
            }

            internal int? SourceCut(int cut)
            {
                lock (cuts)
                {
                    if (!cuts.TryGetValue(cut, out int? source))
                        cuts.Add(cut, source = sourceCut(cut));
                    return source;
                }
            }

            private (int[], string[]) japaneseReadings()
            {
                // Read and divide the word once, rather than repeating the dictionary search
                // for every candidate cut, display projection and editor text field.
                var characters = original.Select((c, i) => (Character: c, Index: i)).Where(c => char.IsLetterOrDigit(c.Character)).ToArray();
                string surface = new string(characters.Select(c => c.Character).ToArray());
                string spelling = text.ToLowerInvariant().Trim(readingPunctuation);
                string dictionaryKana = JapaneseRomaji.Hiragana(string.Concat(JapaneseReading.Read(surface).Select(p => p.Kana)));
                string? kana = (JapaneseRomaji.Matches(dictionaryKana, spelling) & JapaneseRomaji.Match.Complete) != 0
                    ? dictionaryKana : JapaneseRomaji.ReadAuthored(text);
                return (characters.Select(c => c.Index).ToArray(), kana == null ? Array.Empty<string>() : JapaneseCharacterReadings.Split(surface, kana));
            }

            private int? sourceCut(int cut)
            {
                if (!word.Flagged && string.Equals(word.Text, text, StringComparison.OrdinalIgnoreCase)
                    && word.SourceIndexOfTextSplit(cut) is int mapped && mapped > 0 && mapped < original.Length)
                    return mapped;
                if (JapaneseReading.IsJapanese(language))
                {
                    // The mapper's reading may deliberately differ from the dictionary (国 as goku).
                    // Punctuation must not consume one of the character reading spans.
                    var (indices, readings) = japanese.Value;
                    string left = text[..cut].ToLowerInvariant().Trim(readingPunctuation);
                    string right = text[cut..].ToLowerInvariant().Trim(readingPunctuation);
                    for (int i = 1; i < readings.Length; i++)
                    {
                        string prefix = string.Concat(readings.Take(i)), suffix = string.Concat(readings.Skip(i));
                        if ((JapaneseRomaji.Matches(prefix, left) & JapaneseRomaji.Match.Complete) != 0
                            && (JapaneseRomaji.Matches(suffix, right) & JapaneseRomaji.Match.Complete) != 0)
                            return indices[i];
                    }
                }
                for (int i = 1; i < original.Length; i++)
                {
                    if (char.IsLowSurrogate(original[i]) || char.GetUnicodeCategory(original[i]) == System.Globalization.UnicodeCategory.NonSpacingMark)
                        continue;
                    if (Matches(original[..i], text[..cut], language) && Matches(original[i..], text[cut..], language))
                        return i;
                }
                return null;
            }
        }
    }
}
