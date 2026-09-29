// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// THE ORIGINAL TEXT of a lyric (backlog 330): the song's words as it writes them, in their own
    /// script, kept beside the romanised ASCII the player types. This class holds every rule about
    /// it that is not the romanisation itself (that is <see cref="Romaniser"/>): when a word records
    /// an original at all, how one source word becomes its typed text, where the source's syllable
    /// cuts land in the romanised word, which language a text is in, and whether a map carrying
    /// originals may be submitted.
    ///
    /// <para>THE FORMAT. A [Lyrics] line object and each of its word objects may carry an optional
    /// <c>"original"</c> string, written straight after <c>"text"</c> and ONLY when it differs from
    /// it, so every map without one (every map written before this, and every ASCII import) encodes
    /// byte for byte as it always did. <c>"text"</c> stays the only thing default play, the
    /// difficulty calculator, the pace figures and the server read. A word the romaniser could not
    /// spell is written with an EMPTY <c>"text"</c> and its <c>"original"</c>: both loaders skip it
    /// when pairing words[] with the line's tokens (it has no token), and the game keeps it as an
    /// <see cref="UnromanisedWord"/>.</para>
    /// </summary>
    public static class LyricOriginals
    {
        /// <summary>
        /// Whether <paramref name="source"/> records an ORIGINAL when it becomes lyric text: it
        /// carries a LETTER (or a combining mark) outside ASCII, so the typed text had to spell it
        /// differently ("Привет", "café", "straße", "こんにちは"). Typographic folding alone (a curly
        /// apostrophe, an en dash, a non-breaking space) is not a different spelling and records
        /// nothing, so an English song keeps no originals for its "don’t".
        /// </summary>
        public static bool CarriesOriginal(string? source)
        {
            if (string.IsNullOrEmpty(source))
                return false;

            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] <= '\u007F')
                    continue;

                switch (CharUnicodeInfo.GetUnicodeCategory(source, i))
                {
                    case UnicodeCategory.UppercaseLetter:
                    case UnicodeCategory.LowercaseLetter:
                    case UnicodeCategory.TitlecaseLetter:
                    case UnicodeCategory.ModifierLetter:
                    case UnicodeCategory.OtherLetter:
                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.SpacingCombiningMark:
                    case UnicodeCategory.EnclosingMark:
                        return true;
                }
            }

            return false;
        }

        /// <summary>Trims <paramref name="text"/> and collapses every whitespace run to one space.</summary>
        public static string CollapseWhitespace(string? text)
            => string.IsNullOrEmpty(text) ? string.Empty : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>
        /// The original to STORE beside <paramref name="typed"/>: <paramref name="source"/> with its
        /// whitespace collapsed, or null when it records none (see <see cref="CarriesOriginal"/>) or
        /// would say exactly what the typed text says.
        /// </summary>
        public static string? OriginalFor(string? source, string typed)
        {
            if (!CarriesOriginal(source))
                return null;

            string collapsed = CollapseWhitespace(source);
            return collapsed.Length == 0 || collapsed == typed ? null : collapsed;
        }

        #region Words

        /// <summary>
        /// One SOURCE word made typeable: <see cref="Text"/> is what the map stores as the word's
        /// <c>text</c> (the romanisation, Normalize-clean), or empty when <see cref="Flagged"/>.
        /// </summary>
        public sealed class RomanisedWord
        {
            internal RomanisedWord(string source, string text, bool flagged, Romaniser.Result? result, int[]? textIndex)
            {
                Source = source;
                Text = text;
                Flagged = flagged;
                this.result = result;
                this.textIndex = textIndex;
            }

            private readonly Romaniser.Result? result;

            // Romaniser text index -> index in Text (the spaces a CJK mark inserts are removed).
            private readonly int[]? textIndex;

            /// <summary>The word as the source wrote it.</summary>
            public string Source { get; }

            /// <summary>The word's typed text; empty when <see cref="Flagged"/>.</summary>
            public string Text { get; }

            /// <summary>The romaniser could not spell some letter of it: it imports with no text.</summary>
            public bool Flagged { get; }

            /// <summary>The original to store for it (see <see cref="OriginalFor"/>).</summary>
            public string? Original => Flagged ? CollapseWhitespace(Source) : OriginalFor(Source, Text);

            /// <summary>
            /// THE SYLLABLE MAPPING. Carries the source's own syllable cuts (<paramref name="sourceSplits"/>,
            /// char indices into <see cref="Source"/> at which a new syllable starts, ascending) onto
            /// <see cref="Text"/>, through the per-unit mapping the romaniser returns: a cut at the
            /// start of a source unit lands at the start of the Latin run that unit produced (one kana
            /// mora, one hangul block, one letter of an alphabet each map to a known run).
            ///
            /// <para>A cut is DROPPED, and its syllable merges BACKWARD into the one before it, when:</para>
            /// <list type="bullet">
            /// <item>it falls INSIDE a unit (between き and ゃ of the one mora きゃ): a unit never
            /// straddles a cut.</item>
            /// <item>it opens a lone SOKUON (っ, ッ, ｯ): the geminate closes the syllable before it, the
            /// way romaji hyphenates "kit-te", and its typed consonant is sung in the silence the
            /// previous mora holds, so the next mora keeps its own onset exactly.</item>
            /// <item>it opens a lone LONG-VOWEL MARK (ー, ｰ): the mark repeats the vowel before it, so
            /// "koo" of コー is one syllable, not "ko" and "o".</item>
            /// <item>it would leave a syllable with no text at all (a unit that produced nothing, a soft
            /// sign, two cuts on one Latin index): an empty segment is not a legal split.</item>
            /// </list>
            /// <para>Returns, for each KEPT cut, its index into <paramref name="sourceSplits"/> and its
            /// char index into <see cref="Text"/>; a caller drops the matching time boundaries of the
            /// others. Everything is dropped for a flagged word or a word whose text is not the plain
            /// romanisation.</para>
            /// </summary>
            public IReadOnlyList<(int SourceCut, int TextSplit)> MapSplits(IReadOnlyList<int> sourceSplits)
            {
                var kept = new List<(int, int)>();

                if (Flagged || result == null || textIndex == null || sourceSplits.Count == 0)
                    return kept;

                int previous = 0;

                for (int k = 0; k < sourceSplits.Count; k++)
                {
                    int s = sourceSplits[k];

                    if (s <= 0 || s >= Source.Length)
                        continue;

                    char opener = Source[s];

                    if (opener == 'っ' || opener == 'ッ' || opener == 'ｯ' || opener == 'ー' || opener == 'ｰ')
                        continue;

                    int unit = -1;

                    for (int u = 0; u < result.Units.Count; u++)
                    {
                        if (result.Units[u].SourceStart == s)
                        {
                            unit = u;
                            break;
                        }

                        if (result.Units[u].SourceStart > s)
                            break;
                    }

                    if (unit < 0)
                        continue;

                    int at = textIndex[Math.Min(result.Units[unit].TextStart, textIndex.Length - 1)];

                    if (at <= previous || at >= Text.Length)
                        continue;

                    kept.Add((k, at));
                    previous = at;
                }

                return kept;
            }
        }

        /// <summary>
        /// Romanises ONE source word (no whitespace inside it) under <paramref name="language"/>.
        /// Plain ASCII passes through <see cref="Typeability.Normalize"/> exactly as it always has.
        /// A space the romaniser writes after a CJK mark is removed, because the source made this one
        /// word and a word is one token.
        /// </summary>
        /// <param name="source">The word as written.</param>
        /// <param name="language">The map's language (see <see cref="Romaniser"/>).</param>
        /// <param name="keepMarkers">Keep the editor's authoring marks ('&amp;', '|') in a word that
        /// needs no romanising, as the editor's line box does. A romanised word never keeps them.</param>
        public static RomanisedWord RomaniseWord(string source, string? language, bool keepMarkers = false)
        {
            source ??= string.Empty;

            if (!CarriesOriginal(source) && !Romaniser.NeedsRomanising(source, language))
                return new RomanisedWord(source, Typeability.Normalize(source, keepFreestyleMarkers: keepMarkers, keepSplitMarkers: keepMarkers), false, null, null);

            var result = Romaniser.Romanise(source, language);

            if (!result.IsComplete)
                return new RomanisedWord(source, string.Empty, true, result, null);

            var sb = new StringBuilder(result.Text.Length);
            int[] index = new int[result.Text.Length + 1];

            for (int i = 0; i < result.Text.Length; i++)
            {
                index[i] = sb.Length;

                if (result.Text[i] != ' ')
                    sb.Append(result.Text[i]);
            }

            index[result.Text.Length] = sb.Length;

            string joined = sb.ToString();
            string text = Typeability.Normalize(joined);

            // The contract says the romanisation is Normalize-clean; if some input ever breaks it, the
            // text is still stored clean and the cuts are simply not carried.
            return text == joined
                ? new RomanisedWord(source, text, false, result, index)
                : new RomanisedWord(source, text, false, null, null);
        }

        /// <summary>
        /// A whole source LINE made typeable, word by word (whitespace separates words): the stored
        /// line text is the romanised words joined by single spaces, flagged words left out.
        /// </summary>
        public sealed class RomanisedLine
        {
            internal RomanisedLine(string source, IReadOnlyList<RomanisedWord> words)
            {
                Source = source;
                Words = words;
                Text = string.Join(' ', words.Where(w => !w.Flagged && w.Text.Length > 0).Select(w => w.Text));
            }

            public string Source { get; }

            /// <summary>Every source word, flagged ones included, in order.</summary>
            public IReadOnlyList<RomanisedWord> Words { get; }

            /// <summary>The stored line text: the romanised, non-empty words joined by spaces.</summary>
            public string Text { get; }

            /// <summary>The line original to store (see <see cref="OriginalFor"/>).</summary>
            public string? Original => Words.Any(w => w.Flagged) ? CollapseWhitespace(Source) : OriginalFor(Source, Text);

            public bool AnyFlagged => Words.Any(w => w.Flagged);
        }

        /// <summary>Romanises <paramref name="source"/> as a line (see <see cref="RomanisedLine"/>).</summary>
        public static RomanisedLine RomaniseLine(string? source, string? language, bool keepMarkers = false)
        {
            string collapsed = CollapseWhitespace(source);
            var words = collapsed.Length == 0
                ? Array.Empty<RomanisedWord>()
                : collapsed.Split(' ').Select(w => RomaniseWord(w, language, keepMarkers)).ToArray();

            return new RomanisedLine(collapsed, words);
        }

        #endregion

        #region Language

        /// <summary>
        /// The language a lyric is written in, read off its SCRIPT (backlog 330): the script holding
        /// the majority of the non-Latin letters decides, as the lowercase name the romaniser and the
        /// map's <c>Language:</c> line read. Any kana at all among kana and han makes it Japanese
        /// (Chinese is written without kana). Cyrillic is narrowed by the letters only one language
        /// uses (є ї ґ Ukrainian, ў Belarusian, ђ ћ џ Serbian, ѓ ќ ѕ Macedonian, ә ғ қ ң ө ұ ү һ Kazakh),
        /// Russian otherwise. Null when the text has no non-Latin letter.
        /// </summary>
        public static string? DetectLanguage(IEnumerable<string?> texts)
        {
            var counts = new Dictionary<string, int>();
            var cyrillicHints = new HashSet<string>();

            foreach (string? text in texts)
            {
                if (string.IsNullOrEmpty(text))
                    continue;

                foreach (char c in text)
                {
                    string? script = scriptOf(c);

                    if (script == null)
                        continue;

                    counts[script] = counts.GetValueOrDefault(script) + 1;

                    if (script == "cyrillic" && cyrillicHint(c) is string hint)
                        cyrillicHints.Add(hint);
                }
            }

            if (counts.Count == 0)
                return null;

            if (counts.GetValueOrDefault("kana") > 0)
                counts["kana"] += counts.GetValueOrDefault("han");

            string majority = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;

            switch (majority)
            {
                case "kana":
                    return "japanese";

                case "han":
                    return "chinese";

                case "hangul":
                    return "korean";

                case "cyrillic":
                    foreach (string preferred in new[] { "ukrainian", "belarusian", "serbian", "macedonian", "kazakh" })
                    {
                        if (cyrillicHints.Contains(preferred))
                            return preferred;
                    }

                    return "russian";

                default:
                    return majority;
            }
        }

        /// <summary>
        /// The <see cref="BeatmapLanguage"/> a detected language is OFFERED as: its own member when
        /// the list has one, <see cref="BeatmapLanguage.Other"/> for a real language it lacks, and
        /// <see cref="BeatmapLanguage.Unspecified"/> when nothing was detected.
        /// </summary>
        public static BeatmapLanguage OfferedLanguage(string? detected)
        {
            if (string.IsNullOrEmpty(detected))
                return BeatmapLanguage.Unspecified;

            var language = BeatmapLanguageExtensions.FromCanonicalName(detected);
            return language == BeatmapLanguage.Unspecified ? BeatmapLanguage.Other : language;
        }

        /// <summary>
        /// The language to romanise a map's text under: the map's own <paramref name="mapLanguage"/>
        /// when it names one (not Unspecified, Other or Instrumental), else the one detected from
        /// <paramref name="texts"/>.
        /// </summary>
        public static string? RomanisationLanguage(BeatmapLanguage mapLanguage, IEnumerable<string?> texts)
        {
            if (mapLanguage != BeatmapLanguage.Unspecified && mapLanguage != BeatmapLanguage.Other && mapLanguage != BeatmapLanguage.Instrumental)
                return mapLanguage.ToCanonicalName();

            return DetectLanguage(texts);
        }

        /// <summary>The script a character is a letter of, by Unicode block (code points, so the ranges read as the blocks they are).</summary>
        private static string? scriptOf(char ch)
        {
            int c = ch;

            if (c < 0x0370)
                return null;

            if (c <= 0x03FF || (c >= 0x1F00 && c <= 0x1FFF))
                return "greek";
            if (c >= 0x0400 && c <= 0x052F)
                return "cyrillic";
            if (c >= 0x0530 && c <= 0x058F)
                return "armenian";
            if (c >= 0x0590 && c <= 0x05FF)
                return "hebrew";
            if (c >= 0x0600 && c <= 0x06FF)
                return "arabic";
            if (c >= 0x0900 && c <= 0x097F)
                return "hindi";
            if (c >= 0x0E00 && c <= 0x0E7F)
                return "thai";
            if ((c >= 0x10A0 && c <= 0x10FF) || (c >= 0x1C90 && c <= 0x1CBF))
                return "georgian";
            if ((c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F) || (c >= 0xAC00 && c <= 0xD7AF))
                return "hangul";
            // Hiragana and katakana (the katakana middle dot, 0x30FB, is punctuation), the phonetic
            // extensions and halfwidth katakana.
            if ((c >= 0x3041 && c <= 0x30FF && c != 0x30FB) || (c >= 0x31F0 && c <= 0x31FF) || (c >= 0xFF66 && c <= 0xFF9F))
                return "kana";
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF))
                return "han";

            return null;
        }

        private static string? cyrillicHint(char c)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'є':
                case 'ї':
                case 'ґ':
                    return "ukrainian";

                case 'ў':
                    return "belarusian";

                case 'ђ':
                case 'ћ':
                case 'џ':
                case 'љ':
                case 'њ':
                case 'ј':
                    return "serbian";

                case 'ѓ':
                case 'ќ':
                case 'ѕ':
                    return "macedonian";

                case 'ә':
                case 'ғ':
                case 'қ':
                case 'ң':
                case 'ө':
                case 'ұ':
                case 'ү':
                case 'һ':
                    return "kazakh";

                default:
                    return null;
            }
        }

        #endregion

        #region Map-level facts

        /// <summary>
        /// THE SET-LEVEL FLAG, derived and never stored: whether any word (or line) of these lines
        /// carries an original, an unromanised word included. What the Polyglot mod (backlog 331)
        /// gates its availability on.
        /// </summary>
        public static bool HasOriginals(IEnumerable<LyricLine> lines)
            => lines.Any(l => l.Original != null || l.UnromanisedWords.Count > 0 || l.Units.Any(u => u.Original != null));

        /// <summary><see cref="HasOriginals(IEnumerable{LyricLine})"/> over a decoded beatmap's lines.</summary>
        public static bool HasOriginals(IBeatmap beatmap)
            => HasOriginals(beatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line));

        /// <summary>
        /// Every unromanised word's original across these lines, in order, and for a line with
        /// nothing typed and no unromanised word of its own (an import that romanised a whole line
        /// and could spell none of it), the line's original. Exactly what the server's parse lists
        /// (<c>LyricTiming.Header.Unromanised</c>) and refuses to store.
        /// </summary>
        public static IReadOnlyList<string> UnromanisedWords(IEnumerable<LyricLine> lines)
            => lines.SelectMany(l => l.UnromanisedWords.Count == 0 && l.Units.Count == 0 && l.Original != null
                ? new[] { l.Original }
                : l.UnromanisedWords.Select(w => w.Original)).ToList();

        /// <summary>
        /// Why a map may not be SUBMITTED yet, or null when it may: a map with words the romaniser
        /// could not spell can be saved and played locally, but the server would store (and every
        /// player would be served) a lyric with holes in it, so the editor refuses the upload and
        /// says which words need a romanisation. Names at most five of them.
        /// </summary>
        public static string? SubmissionRefusal(IBeatmap beatmap)
        {
            var flagged = UnromanisedWords(beatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line));

            if (flagged.Count == 0)
                return null;

            string named = string.Join(", ", flagged.Distinct().Take(5));
            string more = flagged.Distinct().Count() > 5 ? ", ..." : string.Empty;

            return flagged.Count == 1
                ? $"This map has a word with no romanisation yet ({named}). Give it one in the lyric editor before submitting."
                : $"This map has {flagged.Count} words with no romanisation yet ({named}{more}). Give each one a romanisation in the lyric editor before submitting.";
        }

        /// <summary>
        /// The import summary line for <paramref name="flagged"/>, or null when there are none.
        /// </summary>
        public static string? ImportSummary(IReadOnlyCollection<string> flagged)
        {
            if (flagged.Count == 0)
                return null;

            string named = string.Join(", ", flagged.Distinct().Take(8));
            string more = flagged.Distinct().Count() > 8 ? ", ..." : string.Empty;

            return $"{flagged.Count} word{(flagged.Count == 1 ? string.Empty : "s")} could not be romanised and "
                   + $"{(flagged.Count == 1 ? "was" : "were")} imported with no typed text: {named}{more}. "
                   + "Romanise them in the editor; the map can be saved but not submitted until then.";
        }

        #endregion
    }
}
