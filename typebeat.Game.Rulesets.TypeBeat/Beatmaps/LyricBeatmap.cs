// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Beatmaps/Beatmap.cs (regression-anchored).
// Renames on entry: Beatmap -> LyricBeatmap, BeatmapMetadata -> LyricBeatmapMetadata
// (collisions with typebeat.Game.Beatmaps types). Logic unchanged.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    public enum TimingGranularity
    {
        Line = 0,
        Word = 1,
        Syllable = 2
    }

    public enum TimingSource
    {
        Interpolated,
        Explicit
    }

    /// <summary>
    /// The single text-normalization and typeability authority for the whole game.
    /// LrcParser and TimingJsonLoader MUST both route text through <see cref="Normalize"/>
    /// so auto-skip classification and fixed-width rendering are deterministic.
    /// </summary>
    public static class Typeability
    {
        /// <summary>
        /// Authoring marker for a FREESTYLE character: a cell the player may satisfy with ANY key
        /// on the typeable surface except space, whose typed char is then displayed for the rest of
        /// the play. The mapper types it straight into the lyric text in the editor; it is never a
        /// literal glyph of the lyric (it is not <see cref="IsTypeable"/>, so <see cref="Normalize"/>
        /// strips it unless the caller explicitly opts in).
        /// </summary>
        public const char FREESTYLE_MARKER = '&';

        /// <summary>
        /// The character an automated player presses on a freestyle cell (any key but space is
        /// accepted, so this is arbitrary; a fixed letter keeps generated replays deterministic and
        /// inside the normal a-z/0-9/space surface the .osr encoding is pinned on). It doubles as
        /// the char the Mashing mod substitutes when a space lands on a freestyle cell.
        /// </summary>
        public const char FREESTYLE_AUTO_CHAR = 'a';

        /// <summary>
        /// Authoring marker for a SYLLABLE SPLIT (backlog 181): where inside a subdivided word the
        /// characters are cut, typed straight into the editor's line text ("ap|ple"). A RESERVED
        /// character of the editing surface only: it is neither typeable nor supported punctuation,
        /// so <see cref="Normalize"/> strips it exactly like any other junk char unless the caller
        /// opts in, and it therefore never reaches a stored lyric, a cell, or the aligner. The
        /// stored form of the split is <see cref="TimedUnit.SyllableSplits"/>, not this glyph.
        /// </summary>
        public const char SPLIT_MARKER = '|';

        // INVARIANT: the accepted set must be a subset of what KeyCharMap can produce
        // (ASCII letters/digits/space after Fold); anything else must classify as
        // non-typeable so it auto-skips instead of stranding the caret on an
        // unreachable cell. Normalize strips Latin diacritics first, so 'é' survives as 'e'.
        // Deliberately excludes FREESTYLE_MARKER: a freestyle cell matches every key rather than
        // this one, and keeping the marker out of here is what makes it invisible to every
        // legacy path (Normalize, LRC import, the aligner's raw text).
        // Deliberately excludes PUNCTUATION too: a mark is only ever typed under the Literate
        // mod, so it must not count as a plain typeable char for the difficulty model, the
        // interpolation weights or the pace statistics (see <see cref="IsPunctuation"/>).
        public static bool IsTypeable(char c)
            => c == ' '
               || (c >= 'a' && c <= 'z')
               || (c >= 'A' && c <= 'Z')
               || (c >= '0' && c <= '9');

        public static bool IsFreestyle(char c) => c == FREESTYLE_MARKER;

        /// <summary>
        /// The punctuation type!beat supports inside an authored lyric line, defined ONCE here,
        /// twenty-two marks: comma, period, apostrophe, hyphen, question mark, exclamation mark,
        /// semicolon, colon, round brackets, square brackets, straight double quote, (added by
        /// backlog 202) dollar, percent, caret, asterisk, angle brackets, forward slash, and (added
        /// by backlog 255) underscore and tilde. Nothing else may carry its own list.
        ///
        /// <para>The round and square brackets are ORDINARY marks here (backlog 255): the strip
        /// that used to delete bracketed backing-vocal spans before this ran now lives at the FILE
        /// IMPORT boundary alone (see <see cref="StripBackingVocals"/>), so the editor and the map
        /// format carry a literal '(' exactly as they carry a comma.</para>
        ///
        /// <para>A map stores the AUTHOR'S form: punctuated and case-sensitive. What the player
        /// actually types (and therefore sees) is derived from it: verbatim under the LITERATE mod,
        /// and through <see cref="ToDefaultStream"/> otherwise. <see cref="Normalize"/> folds the
        /// typographic variants (curly quotes/apostrophes, en/em dashes) into this set on the way
        /// in, so only these ASCII forms ever reach a map.</para>
        ///
        /// <para>The literal is MIRRORED in the server repo
        /// (<c>src/Typebeat.Web/Packages/Lyrics/Typeability.cs</c>) and must stay byte-identical
        /// to it: the two normalizers decide what text a map stores, and a divergence stores
        /// different lyrics on the two sides of an import.</para>
        /// </summary>
        public const string PUNCTUATION = ",.'-?!;:()[]\"$%^*<>/_~";

        /// <summary>
        /// The one supported mark that reads as a WORD BREAK rather than as decoration: without
        /// Literate, "bad-cat" is typed "bad cat", not "badcat". Every other mark simply
        /// disappears from the default stream (see <see cref="DefaultChar"/>).
        /// </summary>
        public const char WORD_BREAK = '-';

        /// <summary>A mark from the supported <see cref="PUNCTUATION"/> set.</summary>
        public static bool IsPunctuation(char c) => PUNCTUATION.IndexOf(c) >= 0;

        /// <summary>
        /// A character that occupies a TYPEABLE CELL: a normal typeable char, or a freestyle slot.
        /// This is what the line flattening (<see cref="Gameplay.TypingLine.FromLyricLine"/>) and
        /// the text statistics count; <see cref="IsTypeable"/> stays the narrower "this exact glyph
        /// must be typed" predicate the normalizer and the key map are built on.
        /// </summary>
        public static bool IsCell(char c) => IsTypeable(c) || IsFreestyle(c);

        /// <summary>
        /// A character the POLYGLOT mod (backlog 331) plays as a typed cell: a letter or a mark of
        /// any script (Unicode categories L and M) or a decimal digit, so "Привет", "γειά", "こんにちは"
        /// and the jamo of a hangul block are all typeable. Used ONLY by the Polyglot flattening
        /// (<see cref="Gameplay.PolyglotLine"/>): <see cref="IsTypeable"/> stays ASCII, the invariant
        /// every legacy path (the normalizer, the key map, the difficulty model, the pace figures and
        /// the server) is built on. A surrogate half is never a cell: a frame stores one UTF-16 unit,
        /// so a character outside the BMP cannot be played (see <see cref="Gameplay.PolyglotText"/>).
        /// </summary>
        public static bool IsPolyglotCell(char c)
        {
            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.DecimalDigitNumber:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The Polyglot twin of <see cref="IsCell"/>: an ASCII cell (freestyle slots included, which a
        /// romanised word with no original still carries) or a <see cref="IsPolyglotCell"/> character.
        /// Identical to <see cref="IsCell"/> on every character a stored romanised lyric can hold.
        /// </summary>
        public static bool IsPolyglotTypeCell(char c) => IsCell(c) || IsPolyglotCell(c);

        /// <summary>
        /// A mark the Polyglot mod types under Literate: a supported <see cref="PUNCTUATION"/> mark or
        /// any Unicode punctuation (、 。 « » ¿ and the rest), since an original is not normalised and
        /// keeps its own script's marks.
        /// </summary>
        public static bool IsPolyglotPunctuation(char c) => IsPunctuation(c) || char.IsPunctuation(c);

        /// <summary>
        /// The Polyglot twin of <see cref="DefaultChar"/>: a hyphen is a word break, a cell folds to
        /// lower case, and EVERY other character (any script's punctuation, a symbol) disappears from
        /// the default stream. On a stored romanised lyric (ASCII letters, digits, spaces, freestyle
        /// markers and the supported marks) it answers exactly what <see cref="DefaultChar"/> answers.
        /// </summary>
        public static char? PolyglotDefaultChar(char c)
        {
            if (c == WORD_BREAK || c == ' ')
                return ' ';

            return IsPolyglotTypeCell(c) ? Fold(c) : null;
        }

        public static char Fold(char c) => char.ToLowerInvariant(c);

        /// <summary>
        /// Removes bracketed backing-vocal spans, "(...)" and "[...]", from lyric text; the
        /// player never types them. A whole-line backing vocal therefore normalizes to empty and
        /// the line is dropped by the import (the previous line extends over its time span).
        /// An unclosed bracket strips to the end of the string. Call BEFORE <see cref="Normalize"/>.
        ///
        /// <para>IMPORT-ONLY since backlog 255. Owner decision: a bracket is a literal lyric mark
        /// in the EDITOR and in the MAP FORMAT (it is one of the supported
        /// <see cref="PUNCTUATION"/> marks and has been Literate-typeable all along), and only the
        /// ingest of a foreign lyrics file still reads "(oh oh)" as a backing vocal to throw away.
        /// The two surviving callers are exactly that boundary: <see cref="LrcParser"/> and
        /// <see cref="TimingJsonLoader.TryParse"/> (the aligner's own timing.json), plus the .osu
        /// writer's import-side sweep in <c>LyricMapImporter.StripBackingVocalLines</c>. The
        /// editor's write paths and the stored-map decode call <see cref="Normalize"/> directly and
        /// keep the brackets.</para>
        /// </summary>
        public static string StripBackingVocals(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            var sb = new StringBuilder(raw.Length);
            int depth = 0;

            foreach (char c in raw)
            {
                if (c == '(' || c == '[')
                {
                    depth++;
                    continue;
                }

                if (c == ')' || c == ']')
                {
                    if (depth > 0)
                        depth--;
                    continue;
                }

                if (depth == 0)
                    sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// The Latin SPECIAL LETTERS <see cref="Normalize"/> spells out in ASCII (backlog 329, step
        /// a): letters that are not a base letter plus a diacritic, so FormD has nothing to
        /// decompose and, without this table, the char was simply DELETED ("straße" stored as
        /// "strae"). Only letters Unicode gives no canonical decomposition belong here; å, ä, ö, é,
        /// ñ and the rest already fold through FormD and are deliberately absent.
        ///
        /// <para>CASE IS PRESERVED, because the Literate mod types the stored line case-sensitively:
        /// a capital maps to a capital. A two-letter spelling of a capital takes ONE fixed form,
        /// capitalised first letter only (Þ "Th", Ŋ "Ng"), so the table stays a pure per-char map
        /// with no look at the neighbours; that reads naturally at the head of a word, which is
        /// where these capitals occur, and an all-caps line reads "ThU" rather than "THU". The
        /// exceptions are ẞ and the ligatures Æ and Œ, which spell out in full capitals ("SS",
        /// "AE", "OE"): capital sharp s exists precisely for all-caps text, and a capital ligature
        /// is two capitals written as one glyph. ı and ĸ have no capital of their own.</para>
        ///
        /// <para>Applied AFTER the FormD decomposition, not before it, so a precomposed letter whose
        /// canonical decomposition is one of these plus a mark (Ǿ = Ø + acute, ǽ = æ + acute, ǣ = æ +
        /// macron) spells out as well instead of being deleted; for every letter in the table itself
        /// the order makes no difference, since none of them decomposes.</para>
        ///
        /// <para>MIRRORED byte for byte in the server repo
        /// (<c>src/Typebeat.Web/Packages/Lyrics/Typeability.cs</c>) and in the browser
        /// (<c>SPECIAL_LETTERS</c> in <c>typebeat-core.js</c>), exactly as <see cref="PUNCTUATION"/>
        /// is: all three decode the same stored [Lyrics] text, and an import stores the raw
        /// aligner line, so a one-sided edit gives the clients different cells on one map.</para>
        /// </summary>
        public static readonly IReadOnlyDictionary<char, string> SPECIAL_LETTERS = new Dictionary<char, string>
        {
            ['ß'] = "ss", ['ẞ'] = "SS",
            ['æ'] = "ae", ['Æ'] = "AE",
            ['œ'] = "oe", ['Œ'] = "OE",
            ['ø'] = "o", ['Ø'] = "O",
            ['ł'] = "l", ['Ł'] = "L",
            ['đ'] = "d", ['Đ'] = "D",
            ['þ'] = "th", ['Þ'] = "Th",
            ['ð'] = "d", ['Ð'] = "D",
            ['ı'] = "i",
            ['ŋ'] = "ng", ['Ŋ'] = "Ng",
            ['ĸ'] = "k",
        };

        /// <summary>
        /// Spells every <see cref="SPECIAL_LETTERS"/> letter in <paramref name="text"/> out in ASCII
        /// and leaves every other char alone. Returns the input itself when it carries none.
        /// </summary>
        public static string SpellSpecialLetters(string text)
        {
            StringBuilder? sb = null;

            for (int i = 0; i < text.Length; i++)
            {
                if (!SPECIAL_LETTERS.TryGetValue(text[i], out string? spelled))
                {
                    sb?.Append(text[i]);
                    continue;
                }

                sb ??= new StringBuilder(text, 0, i, text.Length + 8);
                sb.Append(spelled);
            }

            return sb?.ToString() ?? text;
        }

        /// <summary>
        /// Latin diacritics stripped (FormD, combining marks dropped), the Latin special letters
        /// spelled out (<see cref="SPECIAL_LETTERS"/>: 'ß' -> "ss", 'Ø' -> "O"), curly
        /// quotes/apostrophes -> ASCII, en/em dash -> '-', NBSP -> space, then every char that is
        /// neither typeable nor one of the supported <see cref="PUNCTUATION"/> marks is REMOVED.
        /// Whitespace runs collapse to a single space, trimmed.
        ///
        /// <para>The result is the AUTHOR'S form of the line: original case, supported punctuation
        /// intact. It is what a map stores. It is NOT what the player types without the Literate
        /// mod; that is <see cref="ToDefaultStream"/>, derived from this.</para>
        ///
        /// <para><paramref name="keepFreestyleMarkers"/> additionally preserves
        /// <see cref="FREESTYLE_MARKER"/>s, which are otherwise stripped like any other
        /// untypeable punctuation. Only two callers opt in: the editor's line-text entry (where a
        /// typed '&amp;' IS the authoring gesture) and the decoder for a line the map explicitly
        /// flagged as carrying freestyle cells. Every legacy path keeps the default, so an
        /// ampersand that merely occurs in a song's lyrics ("R&amp;B") still disappears exactly as
        /// it always has.</para>
        ///
        /// <para><paramref name="keepSplitMarkers"/> does the same for
        /// <see cref="SPLIT_MARKER"/>s. Exactly ONE caller opts in, the editor's line-text entry,
        /// where a typed '|' is the authoring gesture for a syllable split; it strips them itself
        /// once it has read their positions, so no stored lyric ever carries one. Every other path
        /// keeps the default and drops them like any other unsupported char.</para>
        /// </summary>
        public static string Normalize(string raw, bool keepFreestyleMarkers = false, bool keepSplitMarkers = false)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            try
            {
                // Decompose so combining marks can be dropped ('é' -> 'e' + U+0301).
                raw = raw.Normalize(NormalizationForm.FormD);
            }
            catch (ArgumentException)
            {
                // Invalid Unicode (broken surrogates); carry on undecomposed.
            }

            // The letters FormD cannot reach ('ß', 'ø', 'ł', ...) are spelled out rather than
            // dropped below as untypeable.
            raw = SpellSpecialLetters(raw);

            var sb = new StringBuilder(raw.Length);
            bool pendingSpace = false;
            bool wroteAny = false;

            foreach (char original in raw)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(original) == UnicodeCategory.NonSpacingMark)
                    continue;

                char c = original switch
                {
                    '‘' or '’' or '‚' or '′' => '\'', // ' ' ‚ ′
                    '“' or '”' or '„' or '″' => '"',  // " " „ ″
                    '–' or '—' or '―' or '−' => '-',  // – — ― −
                    ' ' or ' ' or ' ' => ' ',              // NBSP, figure space, narrow NBSP
                    _ => original
                };

                // Supported punctuation survives into the stored line (the author's form); every
                // other untypeable non-whitespace char is dropped from the game text entirely.
                // Freestyle markers survive only for the callers that asked for them.
                if (!char.IsWhiteSpace(c) && !IsTypeable(c) && !IsPunctuation(c)
                    && !(keepFreestyleMarkers && IsFreestyle(c)) && !(keepSplitMarkers && c == SPLIT_MARKER))
                {
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    // Collapse any run of whitespace into a single space, but only emit
                    // once we know a non-space char follows (avoids leading/trailing spaces).
                    if (wroteAny)
                        pendingSpace = true;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
                wroteAny = true;
            }

            return sb.ToString();
        }

        /// <summary>
        /// The DEFAULT (no-Literate) typed char for one authored char, or null when the default
        /// stream deletes it outright:
        /// <list type="bullet">
        /// <item><see cref="WORD_BREAK"/> ('-') becomes a SPACE: "bad-cat" is typed "bad cat".</item>
        /// <item>every other supported <see cref="PUNCTUATION"/> mark disappears.</item>
        /// <item>everything else (letters, digits, spaces, freestyle markers) folds to lower case,
        /// which is a no-op for all but letters.</item>
        /// </list>
        /// </summary>
        public static char? DefaultChar(char c)
        {
            if (c == WORD_BREAK)
                return ' ';

            if (IsPunctuation(c))
                return null;

            return Fold(c);
        }

        /// <summary>
        /// THE derivation: projects an authored line onto the DEFAULT (no-Literate) typed stream,
        /// appending each surviving char to <paramref name="text"/> and, in lockstep, the index in
        /// <paramref name="raw"/> it came from to <paramref name="sourceIndices"/>. Every char goes
        /// through <see cref="DefaultChar"/>, so marks disappear, a hyphen turns into a space and
        /// everything else folds to lower case.
        ///
        /// <para>Spaces are handled a RUN at a time (a run being consecutive space-producing chars,
        /// authored spaces and hyphens alike, with deleted marks skipped over). A run that contains
        /// NO hyphen is emitted verbatim, space for space; that is what makes the projection
        /// provably byte-identical to the authored text for every hyphen-free line, which is every
        /// line any map written before punctuation existed holds, so the default path cannot have
        /// moved under them. A run that DOES contain a hyphen collapses to exactly one space
        /// ("a - b" is "a b", not "a  b"), and to none at all at either end of the line ("-a-" is
        /// "a"), where it would separate nothing.</para>
        ///
        /// <para>A collapsed run reports the index of its FIRST space-producing char, so the space
        /// cell inherits the earliest of the candidate timing slots: for a bare "bad-cat" that is
        /// the hyphen's own slot between the two letters it separated.</para>
        ///
        /// <para>The engine's cell flattening (<see cref="Gameplay.TypingLine.FromLyricLine"/>),
        /// the on-screen lyric (which renders those same cells) and the string form
        /// (<see cref="ToDefaultStream"/>) all go through this one function: what you see is
        /// exactly what you type, by construction rather than by agreement.</para>
        /// </summary>
        public static void ProjectDefault(string raw, StringBuilder text, List<int>? sourceIndices = null)
            => ProjectDefault(raw, text, sourceIndices, DefaultChar);

        /// <summary>
        /// <see cref="ProjectDefault(string, StringBuilder, List{int})"/> through a caller's own
        /// per-char rule (the Polyglot mod's <see cref="PolyglotDefaultChar"/>); the run handling is
        /// the same one, so the two streams cannot disagree about spaces.
        /// </summary>
        public static void ProjectDefault(string raw, StringBuilder text, List<int>? sourceIndices, Func<char, char?> defaultChar)
        {
            if (string.IsNullOrEmpty(raw))
                return;

            bool wroteAny = false;
            int i = 0;

            while (i < raw.Length)
            {
                if (defaultChar(raw[i]) is not char c)
                {
                    i++;
                    continue;
                }

                if (c != ' ')
                {
                    text.Append(c);
                    sourceIndices?.Add(i);
                    wroteAny = true;
                    i++;
                    continue;
                }

                // Measure the space run: where it ends, whether a hyphen is in it, and the index of
                // its first space-producing char.
                bool hasBreak = false;
                int firstSpace = -1;
                int end = i;

                while (end < raw.Length)
                {
                    if (defaultChar(raw[end]) is not char d)
                    {
                        end++; // a deleted mark inside the run does not end it
                        continue;
                    }

                    if (d != ' ')
                        break;

                    if (raw[end] == WORD_BREAK)
                        hasBreak = true;

                    if (firstSpace < 0)
                        firstSpace = end;

                    end++;
                }

                if (!hasBreak)
                {
                    // Authored spaces only: emitted exactly as authored, one cell each.
                    for (int k = i; k < end; k++)
                    {
                        if (defaultChar(raw[k]) is not ' ')
                            continue;

                        text.Append(' ');
                        sourceIndices?.Add(k);
                        wroteAny = true;
                    }
                }
                else if (wroteAny && end < raw.Length)
                {
                    // One word break, unless the run leads or trails the line and so separates
                    // nothing at all.
                    text.Append(' ');
                    sourceIndices?.Add(firstSpace);
                }

                i = end;
            }
        }

        /// <summary>
        /// The DEFAULT (no-Literate) typed stream of an authored line: lower-cased, hyphens turned
        /// into word breaks, every other supported mark deleted.
        /// "The bad-cat sat." becomes "the bad cat sat".
        ///
        /// <para>IDEMPOTENT, and stronger: the result equals <c>ToLowerInvariant</c> of the input
        /// for ANY line without hyphens or marks, which is every line of every map authored before
        /// punctuation existed (their text was stripped on the way in). Those maps therefore play,
        /// score and count exactly as they always have.</para>
        /// </summary>
        public static string ToDefaultStream(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            var sb = new StringBuilder(raw.Length);
            ProjectDefault(raw, sb);
            return sb.ToString();
        }

        /// <summary>
        /// Cells the player must type in <paramref name="text"/>: typeable chars plus freestyle
        /// slots. Punctuation is deliberately NOT counted: it is only a cell under the Literate
        /// mod, and every weight this feeds (interpolation, pace, difficulty) is measured on the
        /// default stream. Identical to the historical typeable-only count for every text that has
        /// been through a default <see cref="Normalize"/> (which has no markers to count).
        /// </summary>
        public static int TypeableCount(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int count = 0;

            foreach (char c in text)
            {
                if (IsCell(c))
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// AN AUTHORED PAUSE INSIDE ONE WORD - the Map Editor's <b>Insert Pause</b>.
    ///
    /// <para>A rest the singer takes between two of the word's characters, for the multisyllabic words
    /// where the breath falls mid-word: nothing is typed while it lasts, the caret waits where it is,
    /// and the characters after it are timed from its END. It is deliberately NOT a rest for the pace
    /// figures - the word still reads as uninterrupted singing, so the WPM denominators keep counting
    /// it as silence inside the word instead of resetting on a breath.</para>
    ///
    /// <para><see cref="SplitChar"/> is the character index the pause sits AFTER (the character before
    /// it is the last one typed before the wait), so it is strictly inside the token, like the
    /// syllable splits it sits beside. The times are absolute milliseconds, strictly inside the unit's
    /// own span, with <see cref="StartTime"/> before <see cref="EndTime"/>. An old map carries none of
    /// this at all.</para>
    ///
    /// <para>A word may take SEVERAL rests (see <see cref="TimedUnit.Pauses"/>), one per breath: each is
    /// a divider of its own, so the word is sung in as many stretches as it has rests plus one, and every
    /// pair must agree with the text - a rest later in TIME sits on a later character than the ones
    /// before it, and no two rests share a character or a moment.</para>
    /// </summary>
    public readonly record struct WordPause(double StartTime, double EndTime, int SplitChar);

    public sealed class TimedUnit
    {
        public required string Text { get; init; }
        public required double StartTime { get; init; }
        public required double EndTime { get; init; }
        public TimingSource Source { get; init; } = TimingSource.Interpolated;

        /// <summary>
        /// Aligner confidence (acoustic margin, 0..1); 1 when trusted or unknown. Units below
        /// SyncWindows.LowConfidenceScore are judged at Line-granularity windows.
        /// </summary>
        public double Confidence { get; init; } = 1;

        /// <summary>
        /// Optional syllable-subdivision boundaries (absolute ms), each strictly inside
        /// (<see cref="StartTime"/>, <see cref="EndTime"/>) and sorted ascending. N boundaries split
        /// the word into N+1 syllable segments for finer sub-word timing; empty for an undivided
        /// word. Editor-authored (the draggable dotted lines) and round-tripped through the
        /// timing.json <c>words[].syllables[]</c>.
        /// </summary>
        public IReadOnlyList<double> SyllableBoundaries { get; init; } = Array.Empty<double>();

        /// <summary>
        /// The authored pauses inside this word, in TIME order: empty for every word in every map written
        /// before the feature existed, and for every word that never needed one. See
        /// <see cref="WordPause"/>.
        ///
        /// <para>N rests cut the word into N + 1 sung stretches, each timed in its own right by
        /// <see cref="Gameplay.PausedWord"/> - the ONE derivation the engine's targets and judgement
        /// groups, the editor's word strip and its line box all read. The rests never overlap each other
        /// and never share a character, and each one's cut stands in the same order as its time, so a
        /// word's dividers always read the same way from left to right in both senses.</para>
        /// </summary>
        public IReadOnlyList<WordPause> Pauses { get; init; } = Array.Empty<WordPause>();

        /// <summary>
        /// Optional AUTHORED character split of <see cref="Text"/> into its syllable segments
        /// (backlog 181): the char indices at which a new segment STARTS, strictly ascending, each
        /// strictly inside (0, Text.Length), and exactly <see cref="SyllableBoundaries"/>.Count of
        /// them ("ap|ple" on a one-boundary word is <c>[2]</c>).
        ///
        /// <para>EMPTY means DERIVED: the <see cref="Gameplay.Syllabifier"/> picks the split, which
        /// is what every map written before this field carries and therefore the case that must
        /// stay byte-identical. Never read this property directly; go through
        /// <see cref="Gameplay.SyllableSegments.SplitsFor(TimedUnit)"/>, which falls back to the
        /// derived split whenever an authored one has gone stale (a retyped word, a boundary
        /// added or removed).</para>
        ///
        /// <para>Persisted ADDITIVELY beside the existing per-syllable objects as the word-level
        /// <c>split_chars</c> array; the per-syllable <c>text</c> fields stay cosmetic and are
        /// never read back.</para>
        /// </summary>
        public IReadOnlyList<int> SyllableSplits { get; init; } = Array.Empty<int>();

        /// <summary>
        /// The word as the song WRITES it, in its own script (backlog 330): "Привет" over the
        /// <see cref="Text"/> "Privet" the player types. Null for a word that has no other spelling,
        /// which is every word of every map written before the field existed and every word whose
        /// source text was plain ASCII. See <see cref="LyricOriginals"/> for when one is recorded.
        ///
        /// <para>DISPLAY AND AUTHORING DATA ONLY. Default play, the difficulty calculator, the pace
        /// figures and the server all read <see cref="Text"/> alone, so a word gaining or losing an
        /// original changes no cell, no target time and no rating. The Polyglot mod (backlog 331) is
        /// the one reader that plays it. Persisted as the word object's optional <c>original</c>
        /// key, written only when it differs from <see cref="Text"/>.</para>
        /// </summary>
        public string? Original { get; init; }
    }

    /// <summary>
    /// A word the romaniser could not spell (backlog 330): a kanji with no reading, a Tier 2 script
    /// (hanzi, Thai, Hebrew, ...). It keeps its ORIGINAL text and its sung span, and has NO typed
    /// text yet, so it is not a <see cref="TimedUnit"/> (a unit is one whitespace token of
    /// <see cref="LyricLine.RawText"/>, and this word has none): it contributes no cell, no target
    /// time and nothing to any rating until the mapper romanises it in the editor, which turns it
    /// into an ordinary unit at <see cref="Position"/>.
    ///
    /// <para>On the wire it is a word object with an EMPTY <c>text</c> and an <c>original</c>, which
    /// both loaders skip when pairing words[] with the line's tokens. A map carrying one can be
    /// saved but not submitted (<see cref="LyricOriginals.SubmissionRefusal"/>).</para>
    /// </summary>
    /// <param name="Position">How many of the line's <see cref="LyricLine.Units"/> come before it,
    /// 0..Units.Count: the slot the word takes once it has a romanisation.</param>
    /// <param name="Original">The word's text in its own script, never empty.</param>
    /// <param name="StartTime">The word's sung start (absolute ms).</param>
    /// <param name="EndTime">The word's sung end (absolute ms).</param>
    public readonly record struct UnromanisedWord(int Position, string Original, double StartTime, double EndTime);

    public sealed class LyricLine
    {
        /// <summary>
        /// The AUTHOR'S form of the line, already normalized via <see cref="Typeability.Normalize"/>:
        /// original case, supported punctuation intact. What the player types (and sees) is derived
        /// from it: verbatim under the Literate mod, through
        /// <see cref="Typeability.ToDefaultStream"/> otherwise.
        /// </summary>
        public required string RawText { get; init; }

        public required double StartTime { get; init; }

        /// <summary>Hard seal deadline == next line's StartTime (last line: vocal end + tail).</summary>
        public required double EndTime { get; init; }

        /// <summary>Vocal end estimate; StartTime &lt;= SingEndTime &lt;= EndTime.</summary>
        public required double SingEndTime { get; init; }

        /// <summary>One per whitespace token of <see cref="RawText"/>, in order.</summary>
        public required IReadOnlyList<TimedUnit> Units { get; init; }

        /// <summary>
        /// Extra time past <see cref="EndTime"/> during which the line stays typeable before
        /// sealing, granted when the source vocals genuinely overrun the line boundary
        /// (backing vocals overlapping the next line). 0 for normal lines.
        /// </summary>
        public double SealGraceMs { get; init; }

        /// <summary>
        /// The aligner had no acoustic evidence for this line; its timing was paced from hand
        /// stamps. Judged at the wider Line-granularity windows regardless of beatmap granularity.
        /// </summary>
        public bool Estimated { get; init; }

        /// <summary>
        /// The whole line as the song WRITES it, in its own script and spacing (backlog 330), or
        /// null when it has no other spelling. The editor shows it as a caption over the romanised
        /// line; nothing that plays or rates the map reads it. Persisted as the line object's
        /// optional <c>original</c> key, written only when it differs from <see cref="RawText"/>.
        /// </summary>
        public string? Original { get; init; }

        /// <summary>
        /// The words of this line the romaniser could not spell, in order (see
        /// <see cref="UnromanisedWord"/>). Empty for every line of every map that has none, which is
        /// every map written before backlog 330.
        ///
        /// <para>A line made of NOTHING but such words has an empty <see cref="RawText"/> and no
        /// <see cref="Units"/>: it has no cell to type, and it exists only so an import of a script
        /// the romaniser cannot read yet keeps the lyric for the mapper to romanise instead of
        /// dropping it (a line without an original that yields no cell is still dropped, exactly as
        /// before). Such a line types nothing and judges nothing.</para>
        /// </summary>
        public IReadOnlyList<UnromanisedWord> UnromanisedWords { get; init; } = Array.Empty<UnromanisedWord>();
    }

    public sealed class LyricBeatmapMetadata
    {
        public required string Artist { get; init; }
        public required string Title { get; init; }

        /// <summary>Absolute path to the map folder.</summary>
        public required string FolderPath { get; init; }

        public required string AudioFileName { get; init; }

        /// <summary>timing.json present (cheap existence check at discovery time).</summary>
        public bool HasWordTiming { get; init; }

        public string DisplayName => $"{Artist} - {Title}";
    }

    public sealed class LyricBeatmap
    {
        public required LyricBeatmapMetadata Metadata { get; init; }
        public required IReadOnlyList<LyricLine> Lines { get; init; }
        public required TimingGranularity Granularity { get; init; }

        public double FirstLineStart => Lines.Count > 0 ? Lines[0].StartTime : 0;
        public double LastLineEnd => Lines.Count > 0 ? Lines[^1].EndTime : 0;
    }
}
