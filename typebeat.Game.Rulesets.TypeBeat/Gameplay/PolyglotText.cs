// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// THE CHARACTER RULES of the Polyglot mod (backlog 331): how an original becomes cells, how a
    /// committed piece of OS text becomes presses, and when a press matches a cell. Pure, so every
    /// rule is pinned by tests rather than by the input stack it rides on.
    ///
    /// <list type="bullet">
    /// <item>NFC. Both sides are compared in Normalization Form C: an original is normalised before it
    /// is cut into cells, a commit is normalised before it is cut into presses, and a single press is
    /// normalised against a single cell (<see cref="Matches"/>), so a dead-key "e" + U+0301 lands as
    /// the one "é" the lyric holds, and a CJK compatibility ideograph matches its canonical twin.</item>
    /// <item>HANGUL is played in KEYS. A precomposed syllable block is cut into the compatibility jamo
    /// a 2-set keyboard types for it, one key per cell: compound vowels and double finals split into
    /// the two keys that build them (와 is ㅇ ㅗ ㅏ, 닭 is ㄷ ㅏ ㄹ ㄱ), and a tense initial stays one
    /// (shifted) key (까 is ㄲ ㅏ). A commit arriving as a composed block (an IME that composes) is
    /// cut the same way and fed key by key at its one timestamp, so both kinds of Korean input land
    /// on the same cells. The display recomposes the block as it is typed (<see cref="Compose"/>).</item>
    /// <item>ONE UTF-16 UNIT PER PRESS. A replay frame stores one <see cref="char"/> (a float in the
    /// legacy frame, exact across the BMP), so a character outside the BMP is out of scope: it is
    /// never a cell (<see cref="Typeability.IsPolyglotCell"/> refuses a surrogate half) and a commit
    /// carrying one drops it rather than feeding half of it.</item>
    /// </list>
    /// </summary>
    public static class PolyglotText
    {
        private const int hangul_base = 0xAC00;
        private const int hangul_last = 0xD7A3;
        private const int vowel_count = 21;
        private const int final_count = 28;

        /// <summary>The 19 initials, as the compatibility jamo (one key each; the tense ones are shifted keys).</summary>
        private const string initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";

        /// <summary>The 21 medials, each as the KEYS that type it (a compound vowel is two).</summary>
        private static readonly string[] medial_keys =
        {
            "ㅏ", "ㅐ", "ㅑ", "ㅒ", "ㅓ", "ㅔ", "ㅕ", "ㅖ", "ㅗ", "ㅗㅏ", "ㅗㅐ", "ㅗㅣ", "ㅛ", "ㅜ", "ㅜㅓ", "ㅜㅔ", "ㅜㅣ", "ㅠ", "ㅡ", "ㅡㅣ", "ㅣ",
        };

        /// <summary>The 28 finals (index 0 = none), each as the KEYS that type it (a double final is two).</summary>
        private static readonly string[] final_keys =
        {
            "", "ㄱ", "ㄲ", "ㄱㅅ", "ㄴ", "ㄴㅈ", "ㄴㅎ", "ㄷ", "ㄹ", "ㄹㄱ", "ㄹㅁ", "ㄹㅂ", "ㄹㅅ", "ㄹㅌ", "ㄹㅍ", "ㄹㅎ", "ㅁ", "ㅂ", "ㅂㅅ", "ㅅ", "ㅆ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ",
        };

        /// <summary>Whether <paramref name="c"/> is a precomposed hangul syllable block.</summary>
        public static bool IsHangulSyllable(char c) => c >= hangul_base && c <= hangul_last;

        /// <summary>The keys (compatibility jamo) a 2-set keyboard types for one syllable block.</summary>
        public static string HangulKeys(char syllable)
        {
            if (!IsHangulSyllable(syllable))
                return syllable.ToString();

            int index = syllable - hangul_base;
            int initial = index / (vowel_count * final_count);
            int medial = index % (vowel_count * final_count) / final_count;
            int final = index % final_count;

            return initials[initial] + medial_keys[medial] + final_keys[final];
        }

        /// <summary>
        /// Recomposes a run of hangul KEYS typed for ONE syllable block into what the block looks like
        /// so far: "ㅇ" alone, "아" once the vowel lands, "안" with the final. A run that does not
        /// spell a block (a wrong key typed into it, a vowel with no initial) comes back as the keys
        /// themselves, so the display never invents a block the player did not type.
        /// </summary>
        public static string Compose(IReadOnlyList<char> keys)
        {
            if (keys.Count == 0)
                return string.Empty;

            string joined = string.Concat(keys);
            int initial = initials.IndexOf(keys[0]);

            if (initial < 0 || keys.Count == 1)
                return joined;

            // The longest medial the keys after the initial spell, then the longest final after it.
            for (int medialLength = Math.Min(2, keys.Count - 1); medialLength >= 1; medialLength--)
            {
                int medial = Array.IndexOf(medial_keys, string.Concat(keySlice(keys, 1, medialLength)));

                if (medial < 0)
                    continue;

                int rest = keys.Count - 1 - medialLength;

                if (rest > 2)
                    return joined;

                int final = rest == 0 ? 0 : Array.IndexOf(final_keys, string.Concat(keySlice(keys, 1 + medialLength, rest)));

                if (final <= 0 && rest > 0)
                    return joined;

                return ((char)(hangul_base + (initial * vowel_count + medial) * final_count + final)).ToString();
            }

            return joined;
        }

        private static IEnumerable<char> keySlice(IReadOnlyList<char> keys, int start, int count)
        {
            for (int i = start; i < start + count; i++)
                yield return keys[i];
        }

        /// <summary>
        /// The CELL TEXT of an original: normalised to NFC, every hangul block cut into its keys, and
        /// whitespace removed (an original is one word; a line original is split on spaces before it
        /// gets here). <paramref name="sourceIndex"/>, when given, receives for each cell-text char
        /// the index in the NFC original it came from; <paramref name="cluster"/> receives, for each,
        /// the index in the NFC original of the hangul block it belongs to, or -1 for a char that is
        /// its own glyph.
        /// </summary>
        public static string CellText(string original, List<int>? sourceIndex = null, List<int>? cluster = null)
        {
            string nfc = ToNfc(original);
            var sb = new StringBuilder(nfc.Length);

            for (int i = 0; i < nfc.Length; i++)
            {
                char c = nfc[i];

                if (char.IsWhiteSpace(c))
                    continue;

                if (IsHangulSyllable(c))
                {
                    foreach (char key in HangulKeys(c))
                    {
                        sb.Append(key);
                        sourceIndex?.Add(i);
                        cluster?.Add(i);
                    }

                    continue;
                }

                sb.Append(c);
                sourceIndex?.Add(i);
                cluster?.Add(-1);
            }

            return sb.ToString();
        }

        /// <summary><paramref name="text"/> in NFC, or unchanged when it is not valid Unicode.</summary>
        public static string ToNfc(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            try
            {
                return text.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return text;
            }
        }

        /// <summary>
        /// The PRESSES a piece of committed OS text makes, in order: the text in NFC, each hangul block
        /// cut into its keys (a stray CONJOINING jamo mapped to the compatibility key that types it),
        /// with control characters and anything outside the BMP dropped. A space is kept: the caller
        /// decides whether a space from text input means anything (under Polyglot the spacebar is a
        /// gesture that arrives through the key bindings instead).
        /// </summary>
        public static IEnumerable<char> InputCharacters(string? committed)
        {
            string nfc = ToNfc(committed);

            for (int i = 0; i < nfc.Length; i++)
            {
                char c = nfc[i];

                if (char.IsSurrogate(c) || char.IsControl(c))
                    continue;

                if (IsHangulSyllable(c))
                {
                    foreach (char key in HangulKeys(c))
                        yield return key;

                    continue;
                }

                yield return conjoiningToKey(c);
            }
        }

        /// <summary>
        /// Whether a press of <paramref name="typed"/> satisfies a cell expecting
        /// <paramref name="expected"/>: equal in NFC, and folded to lower case unless
        /// <paramref name="caseSensitive"/> (the Literate mod) says otherwise.
        /// </summary>
        public static bool Matches(char typed, char expected, bool caseSensitive)
        {
            if (typed == expected)
                return true;

            char t = nfcChar(typed);
            char e = nfcChar(expected);

            return caseSensitive ? t == e : Typeability.Fold(t) == Typeability.Fold(e);
        }

        /// <summary>
        /// A single character in NFC when that is still a single character (a CJK compatibility
        /// ideograph, the Angstrom and Ohm signs, a Greek tonos variant), else the character itself.
        /// </summary>
        private static char nfcChar(char c)
        {
            if (c < 0x80 || char.IsSurrogate(c))
                return c;

            string n = ToNfc(c.ToString());
            return n.Length == 1 ? n[0] : c;
        }

        /// <summary>A conjoining jamo (U+1100 block) as the compatibility key that types it; anything else unchanged.</summary>
        private static char conjoiningToKey(char c)
        {
            if (c >= 0x1100 && c < 0x1100 + initials.Length)
                return initials[c - 0x1100];

            if (c >= 0x1161 && c < 0x1161 + vowel_count && medial_keys[c - 0x1161].Length == 1)
                return medial_keys[c - 0x1161][0];

            if (c >= 0x11A8 && c < 0x11A8 + final_count - 1 && final_keys[c - 0x11A8 + 1].Length == 1)
                return final_keys[c - 0x11A8 + 1][0];

            return c;
        }
    }
}
