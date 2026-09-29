// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Spells lyric text written in a NON-LATIN script out in the ASCII that
    /// <see cref="Typeability.Normalize"/> accepts unchanged (backlog 329). It SITS IN FRONT of
    /// <see cref="Typeability.Normalize"/> and never changes it: a character no table here covers is
    /// handed to <see cref="Typeability.Normalize"/> as it is, so Latin text (diacritics, the
    /// <see cref="Typeability.SPECIAL_LETTERS"/>, curly quotes) comes out exactly as it always has.
    ///
    /// <para>THE CONTRACT. For a result with <see cref="Result.IsComplete"/> set,
    /// <c>Typeability.Normalize(result.Text) == result.Text</c>. A character that no table covers
    /// and that <see cref="Typeability.Normalize"/> would DELETE (a letter of a script this class
    /// does not handle: Thai, Hebrew, a kanji) is NOT deleted: it is kept VERBATIM in
    /// <see cref="Result.Text"/>, listed in <see cref="Result.Unromanised"/>, and its
    /// <see cref="Unit"/> is <see cref="Unit.Flagged"/>, so a caller can flag the word for the
    /// mapper instead of storing it silently shortened. A word in an unknown script therefore never
    /// comes back as an empty string. Symbols and marks outside the tables (a music note, an emoji)
    /// are dropped exactly as <see cref="Typeability.Normalize"/> drops them, without a flag.</para>
    ///
    /// <para>THE LANGUAGE is the map's <c>[Metadata] Language:</c> string (the canonical lowercase
    /// name, <c>BeatmapLanguage.ToCanonicalName()</c> on the game side, <c>beatmapsets.language</c>
    /// on the server) or an ISO 639-1 code, case-insensitive; null, blank and anything
    /// unrecognised read as the default. It is a STRING and not the game's <c>BeatmapLanguage</c>
    /// enum for two reasons: the server mirror of this file cannot see that enum (its vocabulary
    /// is the string list in <c>Packages/BeatmapLanguages.cs</c>), and the enum carries only
    /// Russian among the Cyrillic languages, while the tables below need Ukrainian, Serbian and
    /// the rest. A string lets a future enum member ("ukrainian") select its table the day it is
    /// added, with no change here. The language only matters for CYRILLIC, whose letters are read
    /// differently per language; every other script is romanised the same way whatever it says.</para>
    ///
    /// <para>THE SYSTEMS, all ASCII-folded (no macrons, no apostrophes for aspiration):</para>
    /// <list type="bullet">
    /// <item>Japanese kana: Hepburn. Digraphs (きゃ kya, しゃ sha, ちゃ cha, じゃ ja), the
    /// sokuon doubles the next consonant (っか kka, っちゃ tcha), the long-vowel mark repeats the
    /// vowel before it (コーヒー koohii), ん is n (n' before a vowel or y, so きんえん reads
    /// kin'en), を is o, katakana extensions by the modern table (ファ fa, ティ ti, ヴ vu). Long
    /// vowels written with kana stay as written (おう ou), and は and へ stay ha and he: which one
    /// is a particle is a reading, not a spelling. Kanji are not kana and are flagged.</item>
    /// <item>Korean hangul: Revised Romanisation, by the 0xAC00 arithmetic (initial, medial,
    /// final), each syllable block read ON ITS OWN. There are deliberately NO inter-syllable sound
    /// changes (한국어 is "hangukeo", not the "hangugeo" RR prescribes; 좋아 is "jota"): those are
    /// readings that depend on the word, and the mapper edits the stored text where the sung form
    /// differs.</item>
    /// <item>Cyrillic, per language: Russian (the default) by the practical BGN/PCGN-style system
    /// (ж zh, х kh, ц ts, щ shch, ы y, й y, ё yo, ю yu, я ya); Ukrainian by the official 2010
    /// national system (г h, ґ g, и y, і i, and є ї й ю я as ye yi y yu ya at the start of a word,
    /// ie i i iu ia elsewhere, зг zgh, the apostrophe dropped); Belarusian as Russian with г h, і i,
    /// ў w; Bulgarian by the official streamlined system (ъ a, щ sht, х h, ь y); Serbian as exactly
    /// what <see cref="Typeability.Normalize"/> makes of the official Latin alphabet (ч č c, ш š s,
    /// ж ž z, ћ ć c, ђ đ d, џ dž dz, ц c, х h, љ lj, њ nj, ј j), so a song entered in either of
    /// Serbian's two scripts is typed the same; Macedonian by the official 2008 system (ѓ gj, ќ kj,
    /// ѕ dz, џ dj, ц c, х h); Kazakh as Russian with the 2021 Latin letters ASCII-folded (ә a, ғ g,
    /// қ q, ң ng, ө o, ұ u, ү u, һ h, і i, х h, й i); Mongolian as Russian with MNS 5217 (ж j,
    /// е ye, й i, ө o, ү u). The soft and hard signs are dropped (Bulgarian excepted, as stated).
    /// A letter of another Cyrillic language met under a different language (an і under Russian)
    /// is read by its home language rather than flagged.</item>
    /// <item>Greek, monotonic and polytonic: ELOT 743 simplified. Accents, breathings and iota
    /// subscripts drop; ου ou, αυ au and ευ eu (simplified from av/ev); γ before γ κ ξ χ is n
    /// (άγγελος angelos); θ th, χ ch, ψ ps, η i, υ y, ω o; final sigma is s. A diaeresis on ι or υ
    /// breaks the digraph (προϋπόθεση proypothesi).</item>
    /// <item>Georgian: the 2002 national system without aspiration apostrophes (ქ k, ყ q, წ ts,
    /// ჭ ch, ღ gh, ხ kh); Mtavruli capitals map to capitals.</item>
    /// <item>Armenian: BGN/PCGN, apostrophes dropped (ե ye and ո vo at the start of a word,
    /// ու u, և ev or yev, խ kh, ղ gh, ց ts).</item>
    /// <item>Latin letters Unicode gives no decomposition that <see cref="Typeability.Normalize"/>
    /// does not already spell (<see cref="LATIN_EXTRAS"/>: ħ h, ŧ t, ſ s, ƒ f, ə e, ...). These
    /// live HERE and not in <see cref="Typeability.SPECIAL_LETTERS"/>, which is frozen.</item>
    /// <item>CJK and fullwidth punctuation to the ASCII mark among the supported
    /// <see cref="Typeability.PUNCTUATION"/> (、 and ， comma, 。 period, 「」 quotes, 【】 square
    /// brackets), guillemets to quotes, the katakana middle dot to a space, fullwidth letters and
    /// digits to ASCII, and a decimal digit of any script to its ASCII digit. A CJK mark carries its
    /// own spacing, so a word is broken off with a space after a closing mark (こんにちは、せかい
    /// "konnichiha, sekai") and before an opening one (<see cref="MARKS_OPENING"/>,
    /// <see cref="MARKS_CLOSING"/>). Japanese written without spaces otherwise stays one run: word
    /// segmentation is a reading, and the mapper places the spaces.</item>
    /// </list>
    ///
    /// <para>UNITS, for the importer. <see cref="Result.Units"/> cuts the input into SOURCE UNITS,
    /// each with the run of <see cref="Result.Text"/> it produced: one kana mora (a digraph like きゃ
    /// is one unit, as are っ and ー on their own), one hangul block, one letter or digraph of an
    /// alphabet, one untouched Latin character. A lyric syllable timed against the source text maps
    /// to the concatenation of the text runs of the units inside it, which is the per-syllable
    /// mapping the lyric importer needs; a unit never straddles a source character.</para>
    ///
    /// <para>MIRRORED in the server repo (<c>src/Typebeat.Web/Packages/Lyrics/Romaniser.cs</c>),
    /// identical below the <c>using</c> lines except for the namespace line, and pinned there by
    /// <c>tests/Typebeat.WireCompat/RomaniserParityTest.cs</c> over the shared fixtures in
    /// <c>typebeat.Game.Rulesets.TypeBeat.Tests/NonVisual/fixtures/romaniser</c>. The browser's
    /// <c>typebeat-core.js</c> has NO copy and needs none: romanising happens once, at import, and
    /// what a map stores (and the browser decodes) is already the ASCII this produces.</para>
    ///
    /// <para>Tier 2 scripts (hanzi and kanji by reading, Thai, the Indic scripts, Hebrew, Arabic)
    /// are not covered yet; they come back flagged, and will be added behind this same API.</para>
    /// </summary>
    public static class Romaniser
    {
        /// <summary>
        /// One source unit of a romanised text: the source characters
        /// [<see cref="SourceStart"/>, +<see cref="SourceLength"/>) produced the text
        /// [<see cref="TextStart"/>, +<see cref="TextLength"/>) of <see cref="Result.Text"/>. A unit
        /// may produce no text at all (a soft sign, a dropped symbol, a word-final っ).
        /// <see cref="Flagged"/> marks a unit no table covered, whose source is kept verbatim.
        /// </summary>
        public readonly record struct Unit(int SourceStart, int SourceLength, int TextStart, int TextLength, bool Flagged);

        /// <summary>What <see cref="Romanise"/> returns. See the class summary for the contract.</summary>
        public sealed class Result
        {
            public Result(string text, IReadOnlyList<string> unromanised, IReadOnlyList<Unit> units)
            {
                Text = text;
                Unromanised = unromanised;
                Units = units;
            }

            /// <summary>
            /// The romanised text: Latin ASCII that <see cref="Typeability.Normalize"/> accepts
            /// unchanged, plus, only when <see cref="IsComplete"/> is false, the unromanised
            /// characters kept verbatim where they stood.
            /// </summary>
            public string Text { get; }

            /// <summary>
            /// The source characters no table covered (each with its combining marks), distinct,
            /// in order of first appearance. Empty when <see cref="IsComplete"/>.
            /// </summary>
            public IReadOnlyList<string> Unromanised { get; }

            /// <summary>The source units in source order. See <see cref="Unit"/>.</summary>
            public IReadOnlyList<Unit> Units { get; }

            /// <summary>Every character was romanised or deliberately dropped: nothing was flagged.</summary>
            public bool IsComplete => Unromanised.Count == 0;
        }

        /// <summary>
        /// Romanises <paramref name="text"/> (a word or a whole line; whitespace runs collapse to one
        /// space and the ends are trimmed, as <see cref="Typeability.Normalize"/> does) under the
        /// map's <paramref name="language"/> (see the class summary).
        /// </summary>
        public static Result Romanise(string? text, string? language = null)
        {
            if (string.IsNullOrEmpty(text))
                return new Result(string.Empty, Array.Empty<string>(), Array.Empty<Unit>());

            return new Scanner(text, resolveLanguage(language)).Run();
        }

        /// <summary>
        /// True when <paramref name="text"/> holds a LETTER of a script other than Latin (Cyrillic,
        /// Greek, kana, hangul, a kanji, Thai, ...). Fullwidth Latin letters and the Latin modifier
        /// letters count as Latin; digits, punctuation and symbols are not letters and never count.
        /// </summary>
        public static bool HasNonLatin(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            for (int i = 0; i < text.Length; i++)
            {
                int cp = char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];

                if (isLetter(CharUnicodeInfo.GetUnicodeCategory(text, i)) && !isLatin(cp))
                    return true;

                if (cp > 0xFFFF)
                    i++;
            }

            return false;
        }

        /// <summary>
        /// True when running <paramref name="text"/> through <see cref="Romanise"/> first would store
        /// something different from <see cref="Typeability.Normalize"/> alone: a non-Latin letter,
        /// a <see cref="LATIN_EXTRAS"/> letter, CJK or fullwidth punctuation, a fullwidth or
        /// non-ASCII digit, and so on. Plain Latin text, accented or not, answers false.
        /// </summary>
        public static bool NeedsRomanising(string? text, string? language = null)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            return Romanise(text, language).Text != Typeability.Normalize(text);
        }

        /// <summary>
        /// Latin letters with no Unicode decomposition that <see cref="Typeability.Normalize"/>
        /// would otherwise delete. Case is preserved as in <see cref="Typeability.SPECIAL_LETTERS"/>.
        /// </summary>
        public static readonly IReadOnlyDictionary<char, string> LATIN_EXTRAS = new Dictionary<char, string>
        {
            ['ħ'] = "h", ['Ħ'] = "H",
            ['ŧ'] = "t", ['Ŧ'] = "T",
            ['ſ'] = "s",
            ['ƒ'] = "f", ['Ƒ'] = "F",
            ['ə'] = "e", ['Ə'] = "E",
            ['ĳ'] = "ij", ['Ĳ'] = "IJ",
            ['ŀ'] = "l", ['Ŀ'] = "L",
            ['ǉ'] = "lj", ['ǈ'] = "Lj", ['Ǉ'] = "LJ",
            ['ǌ'] = "nj", ['ǋ'] = "Nj", ['Ǌ'] = "NJ",
            ['ǆ'] = "dz", ['ǅ'] = "Dz", ['Ǆ'] = "DZ",
            ['ʻ'] = "'", ['ʼ'] = "'",
        };

        /// <summary>
        /// CJK, fullwidth-only, guillemet and Armenian punctuation, each to the supported
        /// <see cref="Typeability.PUNCTUATION"/> mark it reads as. The fullwidth forms of the ASCII
        /// marks (！ ？ ： ； （ ） ，) are not listed: every U+FF01..U+FF5E char is shifted onto its
        /// ASCII form arithmetically.
        /// </summary>
        public static readonly IReadOnlyDictionary<char, string> PUNCTUATION_MARKS = new Dictionary<char, string>
        {
            ['、'] = ",", ['､'] = ",", ['﹐'] = ",", ['﹑'] = ",",
            ['。'] = ".", ['｡'] = ".", ['﹒'] = ".",
            ['「'] = "\"", ['」'] = "\"", ['『'] = "\"", ['』'] = "\"",
            ['｢'] = "\"", ['｣'] = "\"", ['﹁'] = "\"", ['﹂'] = "\"", ['﹃'] = "\"", ['﹄'] = "\"",
            ['《'] = "\"", ['》'] = "\"", ['«'] = "\"", ['»'] = "\"",
            ['【'] = "[", ['】'] = "]", ['〔'] = "[", ['〕'] = "]", ['〖'] = "[", ['〗'] = "]",
            ['〘'] = "[", ['〙'] = "]", ['〚'] = "[", ['〛'] = "]",
            ['〈'] = "<", ['〉'] = ">",
            ['〜'] = "~", ['〰'] = "~",
            ['։'] = ".", ['՝'] = ",", ['՚'] = "'",
        };

        /// <summary>The CJK and fullwidth marks that open a word: a word before one is broken off with a space.</summary>
        public const string MARKS_OPENING = "「『【〔〖〘〚〈《｢﹁﹃（［｛«";

        /// <summary>The CJK and fullwidth marks that close a word: a word after one is broken off with a space.</summary>
        public const string MARKS_CLOSING = "、，。．！？：；」』】〕〗〙〛〉》｣﹂﹄､｡﹐﹑﹒）］｝»";

        #region Language

        private enum Lang
        {
            Russian,
            Ukrainian,
            Belarusian,
            Bulgarian,
            Serbian,
            Macedonian,
            Kazakh,
            Mongolian,
        }

        private static Lang resolveLanguage(string? language)
        {
            switch ((language ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "ukrainian":
                case "uk":
                    return Lang.Ukrainian;

                case "belarusian":
                case "be":
                    return Lang.Belarusian;

                case "bulgarian":
                case "bg":
                    return Lang.Bulgarian;

                case "serbian":
                case "sr":
                    return Lang.Serbian;

                case "macedonian":
                case "mk":
                    return Lang.Macedonian;

                case "kazakh":
                case "kk":
                    return Lang.Kazakh;

                case "mongolian":
                case "mn":
                    return Lang.Mongolian;

                default:
                    return Lang.Russian;
            }
        }

        #endregion

        #region Tables

        // Russian, the default, and the base every other Cyrillic language overrides.
        private static readonly Dictionary<char, string> cyrillic_russian = new Dictionary<char, string>
        {
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo",
            ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
            ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
            ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
            ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        };

        // The letters Russian does not have, each read by its home language, consulted after the
        // selected language's own table and the Russian base.
        private static readonly Dictionary<char, string> cyrillic_other_letters = new Dictionary<char, string>
        {
            ['ђ'] = "d", ['ѓ'] = "gj", ['є'] = "ye", ['ѕ'] = "dz", ['і'] = "i", ['ї'] = "yi", ['ј'] = "j",
            ['љ'] = "lj", ['њ'] = "nj", ['ћ'] = "c", ['ќ'] = "kj", ['ў'] = "w", ['џ'] = "dz", ['ґ'] = "g",
            ['ғ'] = "g", ['қ'] = "q", ['ң'] = "ng", ['ү'] = "u", ['ұ'] = "u", ['һ'] = "h", ['ә'] = "a",
            ['ө'] = "o",
        };

        private static readonly Dictionary<char, string> cyrillic_belarusian = new Dictionary<char, string>
        {
            ['г'] = "h", ['ў'] = "w", ['і'] = "i",
        };

        private static readonly Dictionary<char, string> cyrillic_bulgarian = new Dictionary<char, string>
        {
            ['ъ'] = "a", ['щ'] = "sht", ['х'] = "h", ['ь'] = "y",
        };

        private static readonly Dictionary<char, string> cyrillic_serbian = new Dictionary<char, string>
        {
            ['ђ'] = "d", ['ћ'] = "c", ['џ'] = "dz", ['ж'] = "z", ['ч'] = "c", ['ш'] = "s", ['ц'] = "c",
            ['х'] = "h", ['ј'] = "j", ['љ'] = "lj", ['њ'] = "nj",
        };

        private static readonly Dictionary<char, string> cyrillic_macedonian = new Dictionary<char, string>
        {
            ['ѓ'] = "gj", ['ќ'] = "kj", ['ѕ'] = "dz", ['џ'] = "dj", ['ц'] = "c", ['х'] = "h", ['ј'] = "j",
            ['љ'] = "lj", ['њ'] = "nj",
        };

        private static readonly Dictionary<char, string> cyrillic_kazakh = new Dictionary<char, string>
        {
            ['х'] = "h", ['й'] = "i",
        };

        private static readonly Dictionary<char, string> cyrillic_mongolian = new Dictionary<char, string>
        {
            ['ж'] = "j", ['е'] = "ye", ['й'] = "i",
        };

        private static readonly Dictionary<char, string> greek = new Dictionary<char, string>
        {
            ['α'] = "a", ['β'] = "v", ['γ'] = "g", ['δ'] = "d", ['ε'] = "e", ['ζ'] = "z", ['η'] = "i",
            ['θ'] = "th", ['ι'] = "i", ['κ'] = "k", ['λ'] = "l", ['μ'] = "m", ['ν'] = "n", ['ξ'] = "x",
            ['ο'] = "o", ['π'] = "p", ['ρ'] = "r", ['σ'] = "s", ['ς'] = "s", ['τ'] = "t", ['υ'] = "y",
            ['φ'] = "f", ['χ'] = "ch", ['ψ'] = "ps", ['ω'] = "o",
            ['ϐ'] = "v", ['ϑ'] = "th", ['ϕ'] = "f", ['ϖ'] = "p", ['ϲ'] = "s",
        };

        // U+10D0 (ა) onward, the Mkhedruli letters in code point order. Mtavruli capitals
        // (U+1C90 onward) sit at a fixed offset from these.
        private static readonly string[] georgian =
        {
            "a", "b", "g", "d", "e", "v", "z", "t", "i", "k", "l", "m", "n", "o", "p", "zh", "r", "s",
            "t", "u", "p", "k", "gh", "q", "sh", "ch", "ts", "dz", "ts", "ch", "kh", "j", "h",
            "e", "y", "w", "q", "o", "f", "y",
        };

        // U+0561 (ա) to U+0586 (ֆ); the capitals U+0531..U+0556 sit 0x30 below.
        private static readonly string[] armenian =
        {
            "a", "b", "g", "d", "e", "z", "e", "y", "t", "zh", "i", "l", "kh", "ts", "k", "h", "dz", "gh",
            "ch", "m", "y", "n", "sh", "o", "ch", "p", "j", "r", "s", "v", "t", "r", "ts", "v", "p", "k",
            "o", "f",
        };

        // U+3041 (ぁ) to U+3096 (ゖ). っ is the sokuon, handled by the scanner (its entry is unused).
        private static readonly string[] hiragana =
        {
            "a", "a", "i", "i", "u", "u", "e", "e", "o", "o",
            "ka", "ga", "ki", "gi", "ku", "gu", "ke", "ge", "ko", "go",
            "sa", "za", "shi", "ji", "su", "zu", "se", "ze", "so", "zo",
            "ta", "da", "chi", "ji", "", "tsu", "zu", "te", "de", "to", "do",
            "na", "ni", "nu", "ne", "no",
            "ha", "ba", "pa", "hi", "bi", "pi", "fu", "bu", "pu", "he", "be", "pe", "ho", "bo", "po",
            "ma", "mi", "mu", "me", "mo",
            "ya", "ya", "yu", "yu", "yo", "yo",
            "ra", "ri", "ru", "re", "ro",
            "wa", "wa", "i", "e", "o", "n", "vu", "ka", "ke",
        };

        // A kana followed by a SMALL kana that together read as one mora the base table cannot
        // compose (the katakana extensions, mostly). Keyed on the hiragana spelling of the pair.
        private static readonly Dictionary<string, string> kana_pairs = new Dictionary<string, string>
        {
            ["ふぁ"] = "fa", ["ふぃ"] = "fi", ["ふぇ"] = "fe", ["ふぉ"] = "fo", ["ふゅ"] = "fyu",
            ["てぃ"] = "ti", ["でぃ"] = "di", ["てゅ"] = "tyu", ["でゅ"] = "dyu",
            ["とぅ"] = "tu", ["どぅ"] = "du",
            ["しぇ"] = "she", ["ちぇ"] = "che", ["じぇ"] = "je",
            ["うぃ"] = "wi", ["うぇ"] = "we", ["うぉ"] = "wo",
            ["ゔぁ"] = "va", ["ゔぃ"] = "vi", ["ゔぇ"] = "ve", ["ゔぉ"] = "vo", ["ゔゅ"] = "vyu",
            ["つぁ"] = "tsa", ["つぃ"] = "tsi", ["つぇ"] = "tse", ["つぉ"] = "tso",
            ["いぇ"] = "ye",
            ["くぁ"] = "kwa", ["くぃ"] = "kwi", ["くぇ"] = "kwe", ["くぉ"] = "kwo", ["ぐぁ"] = "gwa",
            ["すぃ"] = "si", ["ずぃ"] = "zi",
            ["きぇ"] = "kye", ["ぎぇ"] = "gye", ["にぇ"] = "nye", ["ひぇ"] = "hye", ["びぇ"] = "bye",
            ["ぴぇ"] = "pye", ["みぇ"] = "mye", ["りぇ"] = "rye",
        };

        // Revised Romanisation: 19 initials, 21 medials, 28 finals (index 0 = no final). A final is
        // read as the isolated syllable's coda (ㅅ ㅆ ㅈ ㅊ ㅌ ㅎ all t, a cluster as its sounding
        // consonant), with no look at the next block.
        private static readonly string[] hangul_initials =
        {
            "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h",
        };

        private static readonly string[] hangul_medials =
        {
            "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we",
            "wi", "yu", "eu", "ui", "i",
        };

        private static readonly string[] hangul_finals =
        {
            "", "k", "k", "k", "n", "n", "n", "t", "l", "k", "m", "l", "l", "l", "p", "l", "m", "p", "p",
            "t", "t", "ng", "t", "t", "k", "t", "p", "t",
        };

        // U+3131 (ㄱ) to U+3163 (ㅣ), the compatibility jamo a lone letter is written with (ㅋㅋㅋ):
        // a consonant reads as its initial (ㅇ as ng), a vowel as its medial.
        private static readonly string[] hangul_compatibility =
        {
            "g", "kk", "gs", "n", "nj", "nh", "d", "tt", "r", "lg", "lm", "lb", "ls", "lt", "lp", "lh", "m",
            "b", "pp", "bs", "s", "ss", "ng", "j", "jj", "ch", "k", "t", "p", "h",
            "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we",
            "wi", "yu", "eu", "ui", "i",
        };

        #endregion

        #region Scanner

        private readonly struct Token
        {
            public readonly char C;

            /// <summary>The source cluster (a base char and its combining marks) this token came from.</summary>
            public readonly int Start;

            public readonly int End;

            public Token(char c, int start, int end)
            {
                C = c;
                Start = start;
                End = end;
            }
        }

        private sealed class Scanner
        {
            private readonly string source;
            private readonly Lang lang;
            private readonly List<Token> tokens = new List<Token>();

            private string lastText = string.Empty;
            private char lastKana;

            public Scanner(string source, Lang lang)
            {
                this.source = source;
                this.lang = lang;
                tokenise();
            }

            /// <summary>
            /// Cuts the source into CLUSTERS (a base char or surrogate pair, plus its combining marks
            /// and, for halfwidth katakana, its halfwidth voicing marks) and folds each: NFC so a
            /// decomposed kana or Cyrillic letter recomposes, NFKC for halfwidth katakana, and for Greek
            /// NFD with every mark but the diaeresis dropped. Every folded char remembers its cluster.
            /// </summary>
            private void tokenise()
            {
                int i = 0;

                while (i < source.Length)
                {
                    int start = i;
                    i += char.IsSurrogatePair(source, i) ? 2 : 1;

                    while (i < source.Length && isTrailing(source, i))
                        i += char.IsSurrogatePair(source, i) ? 2 : 1;

                    foreach (char c in fold(source.Substring(start, i - start)))
                        tokens.Add(new Token(c, start, i));
                }
            }

            public Result Run()
            {
                var sb = new StringBuilder(source.Length * 2);
                var units = new List<Unit>();
                var unromanised = new List<string>();
                bool pendingSpace = false;
                bool softSpace = false;
                int t = 0;

                while (t < tokens.Count)
                {
                    char c = tokens[t].C;

                    if (char.IsWhiteSpace(c) || c == '・' || c == '･')
                    {
                        pendingSpace = true;
                        lastText = string.Empty;
                        lastKana = '\0';
                        t++;
                        continue;
                    }

                    // CJK punctuation carries its own spacing, so a text written without spaces
                    // ("こんにちは、せかい") still needs a word break after a closing mark and before an
                    // opening one. That SOFT space is only written when a word follows it, never
                    // between two marks ("」、" is '",').
                    bool opening = MARKS_OPENING.IndexOf(c) >= 0;
                    bool closing = MARKS_CLOSING.IndexOf(c) >= 0;

                    string? text = scan(t, out int next);

                    // A unit ends on a cluster boundary, taking any stray combining marks with it.
                    while (next < tokens.Count && (tokens[next].Start == tokens[next - 1].Start || isMark(tokens[next].C)))
                        next++;

                    int sourceStart = tokens[t].Start;
                    int sourceEnd = tokens[next - 1].End;
                    bool flagged = text == null;

                    if (text == null)
                    {
                        text = source.Substring(sourceStart, sourceEnd - sourceStart);
                        if (!unromanised.Contains(text))
                            unromanised.Add(text);
                    }

                    if (text.Length > 0)
                    {
                        bool soft = opening ? softSpace || char.IsLetterOrDigit(sb.Length > 0 ? sb[^1] : ' ') : softSpace && char.IsLetterOrDigit(text[0]);

                        if ((pendingSpace || soft) && sb.Length > 0)
                            sb.Append(' ');
                        pendingSpace = false;
                        softSpace = closing;
                    }

                    units.Add(new Unit(sourceStart, sourceEnd - sourceStart, sb.Length, text.Length, flagged));
                    sb.Append(text);
                    lastText = text;
                    t = next;
                }

                return new Result(sb.ToString(), unromanised, units);
            }

            /// <summary>
            /// Romanises the unit starting at token <paramref name="t"/>, setting
            /// <paramref name="next"/> past the tokens it consumed. Null means no table covers it.
            /// </summary>
            private string? scan(int t, out int next)
            {
                char c = tokens[t].C;
                next = t + 1;

                if (isKana(c))
                    return scanKana(t, out next);

                lastKana = '\0';

                if (c >= '가' && c <= '힣')
                    return hangulBlock(c - 0xAC00);

                if (c >= 'ᄀ' && c <= 'ᄒ')
                    return scanConjoiningJamo(t, out next);

                if (c >= 'ᅡ' && c <= 'ᅵ')
                    return hangul_medials[c - 0x1161];

                if (c >= 'ᆨ' && c <= 'ᇂ')
                    return hangul_finals[c - 0x11A7];

                if (c >= 'ㄱ' && c <= 'ㅣ')
                    return hangul_compatibility[c - 0x3131];

                if ((c >= 'Ͱ' && c <= 'Ͽ') || (c >= 'ἀ' && c <= '῿'))
                    return scanGreek(t, out next);

                if (c >= 'Ѐ' && c <= 'ԯ')
                    return scanCyrillic(t);

                if ((c >= 'ა' && c <= 'ჷ') || (c >= 'Ა' && c <= 'Ჷ'))
                    return georgianLetter(c);

                if ((c >= 'Ա' && c <= 'Ֆ') || (c >= 'ա' && c <= 'և'))
                    return scanArmenian(t, out next);

                if (isApostrophe(c) && (lang == Lang.Ukrainian || lang == Lang.Belarusian)
                                    && t > 0 && isCyrillic(tokens[t - 1].C) && t + 1 < tokens.Count && isCyrillic(tokens[t + 1].C))
                    return string.Empty;

                if (LATIN_EXTRAS.TryGetValue(c, out string? latin))
                    return latin;

                if (PUNCTUATION_MARKS.TryGetValue(c, out string? mark))
                    return mark;

                if (c >= '！' && c <= '～')
                    return passThrough(((char)(c - 0xFEE0)).ToString(), false);

                if (c > '\u007F' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.DecimalDigitNumber)
                    return CharUnicodeInfo.GetDecimalDigitValue(c).ToString(CultureInfo.InvariantCulture);

                return passThrough(source.Substring(tokens[t].Start, tokens[t].End - tokens[t].Start), true);
            }

            /// <summary>
            /// What <see cref="Typeability.Normalize"/> makes of a character no table here covers.
            /// When it deletes a LETTER outside ASCII, nothing covered it: null (flag it). Anything
            /// else it deletes (a symbol, a mark, unsupported ASCII punctuation) is dropped as always.
            /// </summary>
            private static string? passThrough(string cluster, bool mayFlag)
            {
                string normalized = Typeability.Normalize(cluster);

                if (normalized.Length > 0 || !mayFlag)
                    return normalized;

                return cluster[0] > '\u007F' && isLetter(CharUnicodeInfo.GetUnicodeCategory(cluster, 0)) ? null : string.Empty;
            }

            #region Kana

            private static bool isKana(char c)
                => (c >= 'ぁ' && c <= 'ゖ') || c == 'ゝ' || c == 'ゞ'
                   || (c >= 'ァ' && c <= 'ヺ') || (c >= 'ー' && c <= 'ヾ');

            /// <summary>Katakana to its hiragana, which is what every table is keyed on.</summary>
            private static char toHiragana(char c)
            {
                if (c >= 'ァ' && c <= 'ヶ')
                    return (char)(c - 0x60);
                if (c == 'ヽ' || c == 'ヾ')
                    return (char)(c - 0x60);

                return c;
            }

            private static bool isSmallY(char h) => h == 'ゃ' || h == 'ゅ' || h == 'ょ';

            private static bool isSmallVowel(char h) => h == 'ぁ' || h == 'ぃ' || h == 'ぅ' || h == 'ぇ' || h == 'ぉ';

            private string? scanKana(int t, out int next)
            {
                char h = toHiragana(tokens[t].C);
                next = t + 1;

                if (h == 'ー')
                {
                    lastKana = '\0';
                    return lastVowel(lastText);
                }

                if (h == 'ゝ' || h == 'ゞ')
                {
                    if (lastKana == '\0')
                        return string.Empty;

                    char repeated = h == 'ゞ' ? voiced(lastKana) : lastKana;
                    return kanaCore(repeated, '\0', out _);
                }

                if (h == 'っ')
                {
                    lastKana = '\0';

                    if (t + 1 >= tokens.Count || !isKana(tokens[t + 1].C))
                        return string.Empty;

                    string peek = kanaAt(t + 1, out _);

                    if (peek.StartsWith("ch", StringComparison.Ordinal))
                        return "t";

                    return peek.Length > 0 && isConsonant(peek[0]) ? peek[0].ToString() : string.Empty;
                }

                string roman = kanaAt(t, out next);
                lastKana = h;

                if (h == 'ん' && next < tokens.Count && isKana(tokens[next].C))
                {
                    string following = kanaAt(next, out _);

                    if (following.Length > 0 && "aiueoy".IndexOf(following[0]) >= 0)
                        return "n'";
                }

                return roman;
            }

            /// <summary>The romanisation of the (possibly two-kana) mora at token t, with no sokuon or long mark handling.</summary>
            private string kanaAt(int t, out int next)
            {
                char c = tokens[t].C;
                char h = toHiragana(c);
                char small = '\0';

                if (t + 1 < tokens.Count && isKana(tokens[t + 1].C))
                {
                    char s = toHiragana(tokens[t + 1].C);
                    if (isSmallY(s) || isSmallVowel(s))
                        small = s;
                }

                // ヷ ヸ ヹ ヺ have no hiragana counterpart.
                if (c >= 'ヷ' && c <= 'ヺ')
                {
                    next = t + 1;
                    return c == 'ヷ' ? "va" : c == 'ヸ' ? "vi" : c == 'ヹ' ? "ve" : "vo";
                }

                string roman = kanaCore(h, small, out bool usedSmall);
                next = usedSmall ? t + 2 : t + 1;
                return roman;
            }

            private static string kanaCore(char h, char small, out bool usedSmall)
            {
                usedSmall = false;

                if (h < 'ぁ' || h > 'ゖ' || h == 'っ')
                    return string.Empty;

                string roman = hiragana[h - 0x3041];

                if (small == '\0' || isSmallY(h) || isSmallVowel(h))
                    return roman;

                if (kana_pairs.TryGetValue(new string(new[] { h, small }), out string? pair))
                {
                    usedSmall = true;
                    return pair;
                }

                if (!isSmallY(small) || roman.Length < 2 || roman[^1] != 'i')
                    return roman;

                char vowel = small == 'ゃ' ? 'a' : small == 'ゅ' ? 'u' : 'o';
                usedSmall = true;

                // shi, chi and ji lose the i (sha, cha, ja); every other i-row kana takes a y (kya).
                if (roman == "shi" || roman == "chi" || roman == "ji")
                    return roman.Substring(0, roman.Length - 1) + vowel;

                return roman.Substring(0, roman.Length - 1) + "y" + vowel;
            }

            private static char voiced(char h)
            {
                string composed;

                try
                {
                    composed = (h + "゙").Normalize(NormalizationForm.FormC);
                }
                catch (ArgumentException)
                {
                    return h;
                }

                return composed.Length == 1 ? composed[0] : h;
            }

            private static string lastVowel(string text)
            {
                if (text.Length == 0)
                    return string.Empty;

                char last = char.ToLowerInvariant(text[^1]);
                return "aiueo".IndexOf(last) >= 0 ? last.ToString() : string.Empty;
            }

            private static bool isConsonant(char c) => c >= 'a' && c <= 'z' && "aiueo".IndexOf(c) < 0;

            #endregion

            #region Hangul

            private static string hangulBlock(int s)
                => hangul_initials[s / 588] + hangul_medials[s % 588 / 28] + hangul_finals[s % 28];

            /// <summary>A decomposed syllable (conjoining initial, then a medial and an optional final).</summary>
            private string scanConjoiningJamo(int t, out int next)
            {
                string roman = hangul_initials[tokens[t].C - 0x1100];
                next = t + 1;

                if (next < tokens.Count && tokens[next].C >= 'ᅡ' && tokens[next].C <= 'ᅵ')
                {
                    roman += hangul_medials[tokens[next].C - 0x1161];
                    next++;

                    if (next < tokens.Count && tokens[next].C >= 'ᆨ' && tokens[next].C <= 'ᇂ')
                    {
                        roman += hangul_finals[tokens[next].C - 0x11A7];
                        next++;
                    }
                }

                return roman;
            }

            #endregion

            #region Alphabets

            private string? scanGreek(int t, out int next)
            {
                char c = tokens[t].C;
                char lower = char.ToLowerInvariant(c);
                next = t + 1;

                if (!greek.TryGetValue(lower, out string? roman))
                    return passThrough(source.Substring(tokens[t].Start, tokens[t].End - tokens[t].Start), true);

                // A diaeresis stays in the token stream as its own mark after the letter it sits on.
                bool hasNext = t + 1 < tokens.Count && !hasDiaeresis(t);
                char nextLower = hasNext ? char.ToLowerInvariant(tokens[t + 1].C) : '\0';

                if (nextLower == 'υ' && !hasDiaeresis(t + 1) && (lower == 'ο' || lower == 'α' || lower == 'ε'))
                {
                    next = t + 2;
                    string pair = lower == 'ο' ? "ou" : lower == 'α' ? "au" : "eu";
                    return applyCase(pair, char.IsUpper(c), char.IsUpper(tokens[t + 1].C));
                }

                if (lower == 'γ' && (nextLower == 'γ' || nextLower == 'κ' || nextLower == 'ξ' || nextLower == 'χ'))
                    roman = "n";

                return applyCase(roman, char.IsUpper(c), false);
            }

            private bool hasDiaeresis(int t)
                => t + 1 < tokens.Count && tokens[t + 1].C == '̈' && tokens[t + 1].Start == tokens[t].Start;

            private string? scanCyrillic(int t)
            {
                char c = tokens[t].C;
                char lower = char.ToLowerInvariant(c);
                string? roman = cyrillicLetter(lower, t);

                if (roman == null)
                {
                    // A letter carrying an accent Cyrillic has a precomposed form for (ѐ, ѝ, ӣ): read the base.
                    string decomposed = safeNormalize(lower.ToString(), NormalizationForm.FormD);
                    if (decomposed.Length > 1 && decomposed[0] != lower)
                        roman = cyrillicLetter(decomposed[0], t);
                }

                if (roman == null)
                    return passThrough(source.Substring(tokens[t].Start, tokens[t].End - tokens[t].Start), true);

                return applyCase(roman, c != lower, false);
            }

            private string? cyrillicLetter(char lower, int t)
            {
                if (lang == Lang.Ukrainian)
                {
                    bool initial = isWordInitial(t);

                    switch (lower)
                    {
                        case 'г':
                            return t > 0 && char.ToLowerInvariant(tokens[t - 1].C) == 'з' ? "gh" : "h";

                        case 'и':
                            return "y";

                        case 'є':
                            return initial ? "ye" : "ie";

                        case 'ї':
                            return initial ? "yi" : "i";

                        case 'й':
                            return initial ? "y" : "i";

                        case 'ю':
                            return initial ? "yu" : "iu";

                        case 'я':
                            return initial ? "ya" : "ia";
                    }
                }

                Dictionary<char, string>? own = lang switch
                {
                    Lang.Belarusian => cyrillic_belarusian,
                    Lang.Bulgarian => cyrillic_bulgarian,
                    Lang.Serbian => cyrillic_serbian,
                    Lang.Macedonian => cyrillic_macedonian,
                    Lang.Kazakh => cyrillic_kazakh,
                    Lang.Mongolian => cyrillic_mongolian,
                    _ => null,
                };

                if (own != null && own.TryGetValue(lower, out string? mine))
                    return mine;

                if (cyrillic_russian.TryGetValue(lower, out string? russian))
                    return russian;

                return cyrillic_other_letters.TryGetValue(lower, out string? other) ? other : null;
            }

            private string? scanArmenian(int t, out int next)
            {
                char c = tokens[t].C;
                next = t + 1;

                if (c == 'և')
                    return isWordInitial(t) ? "yev" : "ev";

                bool upper = c <= 'Ֆ';
                char lower = upper ? (char)(c + 0x30) : c;
                bool initial = isWordInitial(t);

                if (lower == 'ո' && t + 1 < tokens.Count && (tokens[t + 1].C == 'ւ' || tokens[t + 1].C == 'Ւ'))
                {
                    next = t + 2;
                    return applyCase("u", upper, false);
                }

                string roman = lower == 'ե' && initial ? "ye"
                    : lower == 'ո' && initial ? "vo"
                    : armenian[lower - 0x0561];

                return applyCase(roman, upper, false);
            }

            private static string georgianLetter(char c)
            {
                bool upper = c >= 'Ა';
                string roman = georgian[(upper ? c - 0xBC0 : c) - 0x10D0];
                return applyCase(roman, upper, false);
            }

            /// <summary>
            /// No letter before this token in the same word (combining marks skipped). For Ukrainian an
            /// apostrophe inside a word counts as a letter, so the я of "м'ясо" is medial.
            /// </summary>
            private bool isWordInitial(int t)
            {
                for (int i = t - 1; i >= 0; i--)
                {
                    char p = tokens[i].C;

                    if (isMark(p))
                        continue;

                    return !(char.IsLetter(p) || (lang == Lang.Ukrainian && isApostrophe(p)));
                }

                return true;
            }

            #endregion
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Capitalises a romanisation for an upper-case source letter: the first letter only (Ж "Zh"),
        /// following <see cref="Typeability.SPECIAL_LETTERS"/>; both letters when a digraph's two
        /// source letters are both capitals (ΟΥ "OU").
        /// </summary>
        private static string applyCase(string roman, bool upperFirst, bool upperSecond)
        {
            if (!upperFirst || roman.Length == 0)
                return roman;

            if (upperSecond)
                return roman.ToUpperInvariant();

            return char.ToUpperInvariant(roman[0]) + roman.Substring(1);
        }

        private static string fold(string cluster)
        {
            char b = cluster[0];

            if (b >= 'ｦ' && b <= 'ﾝ')
                return safeNormalize(cluster, NormalizationForm.FormKC);

            string composed = safeNormalize(cluster, NormalizationForm.FormC);

            if (composed.Length == 0 || !isGreek(composed[0]))
                return composed;

            string decomposed = safeNormalize(composed, NormalizationForm.FormD);
            var sb = new StringBuilder(2);
            sb.Append(decomposed[0]);

            for (int i = 1; i < decomposed.Length; i++)
            {
                if (decomposed[i] == '̈' || !isMark(decomposed[i]))
                    sb.Append(decomposed[i]);
            }

            return sb.ToString();
        }

        private static string safeNormalize(string s, NormalizationForm form)
        {
            try
            {
                return s.Normalize(form);
            }
            catch (ArgumentException)
            {
                return s;
            }
        }

        private static bool isTrailing(string s, int i)
        {
            char c = s[i];
            return c == 'ﾞ' || c == 'ﾟ' || isMark(CharUnicodeInfo.GetUnicodeCategory(s, i));
        }

        private static bool isMark(char c) => isMark(CharUnicodeInfo.GetUnicodeCategory(c));

        private static bool isMark(UnicodeCategory category)
            => category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark || category == UnicodeCategory.EnclosingMark;

        private static bool isLetter(UnicodeCategory category)
            => category == UnicodeCategory.UppercaseLetter || category == UnicodeCategory.LowercaseLetter || category == UnicodeCategory.TitlecaseLetter
               || category == UnicodeCategory.ModifierLetter || category == UnicodeCategory.OtherLetter;

        private static bool isGreek(char c) => (c >= 'Ͱ' && c <= 'Ͽ') || (c >= 'ἀ' && c <= '῿');

        private static bool isCyrillic(char c) => c >= 'Ѐ' && c <= 'ԯ';

        private static bool isApostrophe(char c) => c == '\'' || c == '’' || c == 'ʼ';

        private static bool isLatin(int cp)
            => cp < 0x0370 // ASCII, Latin-1, Latin Extended-A and -B, IPA, spacing modifier letters
               || (cp >= 0x1D00 && cp <= 0x1DBF)
               || (cp >= 0x1E00 && cp <= 0x1EFF)
               || (cp >= 0x2C60 && cp <= 0x2C7F)
               || (cp >= 0xA720 && cp <= 0xA7FF)
               || (cp >= 0xAB30 && cp <= 0xAB6F)
               || (cp >= 0xFB00 && cp <= 0xFB06)
               || (cp >= 0xFF21 && cp <= 0xFF3A)
               || (cp >= 0xFF41 && cp <= 0xFF5A);

        #endregion
    }
}
