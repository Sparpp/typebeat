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
        /// on the typeable surface, whose typed char is then displayed for the rest of the play.
        /// The mapper types it straight into the lyric text in the editor; it is never a literal
        /// glyph of the lyric (it is not <see cref="IsTypeable"/>, so <see cref="Normalize"/>
        /// strips it unless the caller explicitly opts in).
        /// </summary>
        public const char FREESTYLE_MARKER = '&';

        /// <summary>
        /// The character an automated player presses on a freestyle cell (any key is accepted, so
        /// this is arbitrary; a fixed letter keeps generated replays deterministic and inside the
        /// normal a-z/0-9/space surface the .osr encoding is pinned on).
        /// </summary>
        public const char FREESTYLE_AUTO_CHAR = 'a';

        // INVARIANT: the accepted set must be a subset of what KeyCharMap can produce
        // (ASCII letters/digits/space after Fold); anything else must classify as
        // non-typeable so it auto-skips instead of stranding the caret on an
        // unreachable cell. Normalize strips Latin diacritics first, so 'é' survives as 'e'.
        // Deliberately excludes FREESTYLE_MARKER: a freestyle cell matches every key rather than
        // this one, and keeping the marker out of here is what makes it invisible to every
        // legacy path (Normalize, LRC import, the aligner's raw text).
        public static bool IsTypeable(char c)
            => c == ' '
               || (c >= 'a' && c <= 'z')
               || (c >= 'A' && c <= 'Z')
               || (c >= '0' && c <= '9');

        public static bool IsFreestyle(char c) => c == FREESTYLE_MARKER;

        /// <summary>
        /// A character that occupies a TYPEABLE CELL: a normal typeable char, or a freestyle slot.
        /// This is what the line flattening (<see cref="Gameplay.TypingLine.FromLyricLine"/>) and
        /// the text statistics count; <see cref="IsTypeable"/> stays the narrower "this exact glyph
        /// must be typed" predicate the normalizer and the key map are built on.
        /// </summary>
        public static bool IsCell(char c) => IsTypeable(c) || IsFreestyle(c);

        public static char Fold(char c) => char.ToLowerInvariant(c);

        /// <summary>
        /// Removes bracketed backing-vocal spans, "(...)" and "[...]", from lyric text; the
        /// player never types them. A whole-line backing vocal therefore normalizes to empty and
        /// the line is dropped by both loaders (the previous line extends over its time span).
        /// An unclosed bracket strips to the end of the string. Call BEFORE <see cref="Normalize"/>.
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
        /// Latin diacritics stripped (FormD, combining marks dropped), curly
        /// quotes/apostrophes -> ASCII, en/em dash -> '-', NBSP -> space,
        /// then every char the player cannot type (apostrophes, commas, any other
        /// punctuation) is REMOVED, never displayed, never a cell. Whitespace runs
        /// collapse to a single space, trimmed.
        ///
        /// <para><paramref name="keepFreestyleMarkers"/> additionally preserves
        /// <see cref="FREESTYLE_MARKER"/>s, which are otherwise stripped like any other
        /// untypeable punctuation. Only two callers opt in: the editor's line-text entry (where a
        /// typed '&amp;' IS the authoring gesture) and the decoder for a line the map explicitly
        /// flagged as carrying freestyle cells. Every legacy path keeps the default, so an
        /// ampersand that merely occurs in a song's lyrics ("R&amp;B") still disappears exactly as
        /// it always has.</para>
        /// </summary>
        public static string Normalize(string raw, bool keepFreestyleMarkers = false)
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

                // "You can't type it, so don't display it": untypeable non-whitespace chars
                // (apostrophes, commas, all other punctuation) are dropped from the game text
                // entirely; monkeytype-style bare words. Freestyle markers survive only for the
                // callers that asked for them.
                if (!char.IsWhiteSpace(c) && !IsTypeable(c) && !(keepFreestyleMarkers && IsFreestyle(c)))
                    continue;

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
        /// Cells the player must type in <paramref name="text"/>: typeable chars plus freestyle
        /// slots. Identical to the historical typeable-only count for every text that has been
        /// through a default <see cref="Normalize"/> (which has no markers to count).
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
    }

    public sealed class LyricLine
    {
        /// <summary>Already normalized via <see cref="Typeability.Normalize"/>.</summary>
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
