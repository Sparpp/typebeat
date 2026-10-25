// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// One BACKING VOCAL word (backlog 445): a word sung over or around the main line, shown above it
    /// in gameplay and never typed.
    /// </summary>
    /// <param name="Text">The word as it is shown. Never empty.</param>
    /// <param name="StartTime">When it is sung (absolute ms), which is when it pops up in gameplay.</param>
    /// <param name="EndTime">When it stops being sung (absolute ms), never before <paramref name="StartTime"/>.</param>
    /// <param name="Anchor">Where across the line it pops up, as a fraction of the line's width (0 the
    /// lyric's left edge, 1 its right edge), or null for the default place
    /// (<see cref="BackingVocals.DefaultAnchor"/>). Only a mapper's own placement is stored.</param>
    public readonly record struct BackingVocal(string Text, double StartTime, double EndTime, double? Anchor = null);

    /// <summary>
    /// BACKING VOCALS (backlog 445): a purely visual lyric track a line can carry beside the one the
    /// player types. Backing words overlap the main singing freely, so they are timed on their own,
    /// but they belong to a LINE: a word is always inside its line's [StartTime, EndTime] window, so
    /// the two tracks share one set of line divisions.
    ///
    /// <para>NOTHING THAT PLAYS OR RATES A MAP READS THEM. They are not cells, they are not in the
    /// replay, the score, the difficulty or the pace figures, and the server and the browser player
    /// ignore the key. Persisted as the optional <c>"backing"</c> array that ends a <c>[Lyrics]</c>
    /// line object, one flat object per word (<see cref="WRITE_KEY"/>); a line without backing vocals
    /// writes no key at all, so every map written before the feature encodes byte for byte as it did.
    /// The ranked-status comparison and the server's gameplay fingerprint both strip the key
    /// (<see cref="LyricOsuFormat.StripBacking"/>), so adding them to a ranked map never demotes it.</para>
    /// </summary>
    public static class BackingVocals
    {
        /// <summary>The line-object key the words are stored under.</summary>
        public const string WRITE_KEY = "backing";

        /// <summary>Shortest backing word the editor and the importers produce.</summary>
        public const double MIN_SPAN_MS = 30;

        /// <summary>Anchors are stored to this many decimals, so a dragged anchor writes a tidy number.</summary>
        private const int anchor_decimals = 4;

        /// <summary>
        /// The default place of word <paramref name="index"/> of <paramref name="count"/>: evenly spaced
        /// across the line, each in the centre of its own share of the width, so a single word sits in
        /// the middle and four sit at an eighth, three eighths, five eighths and seven eighths.
        /// </summary>
        public static double DefaultAnchor(int index, int count) => count <= 0 ? 0.5 : (Math.Clamp(index, 0, count - 1) + 0.5) / count;

        /// <summary>Where word <paramref name="index"/> pops up: its own anchor, else its default place.</summary>
        public static double AnchorOf(IReadOnlyList<BackingVocal> words, int index)
            => words[index].Anchor is double anchor ? Math.Clamp(anchor, 0, 1) : DefaultAnchor(index, words.Count);

        /// <summary>An anchor as it is stored: clamped into the line and rounded.</summary>
        public static double RoundAnchor(double anchor) => Math.Round(Math.Clamp(anchor, 0, 1), anchor_decimals);

        /// <summary>
        /// The words fitted into a line window: each start clamped into [<paramref name="start"/>,
        /// <paramref name="end"/>], each end into [its start, <paramref name="end"/>], empty texts
        /// dropped, and the list ordered by start (stably, so words sung together keep their order). The
        /// same list comes back untouched when it already fits, so an idle edit allocates nothing.
        /// </summary>
        public static IReadOnlyList<BackingVocal> Fit(IReadOnlyList<BackingVocal>? words, double start, double end)
        {
            if (words == null || words.Count == 0)
                return Array.Empty<BackingVocal>();

            if (end < start)
                end = start;

            bool fits = true;

            for (int i = 0; i < words.Count && fits; i++)
            {
                var word = words[i];
                fits = word.Text.Length > 0 && word.StartTime >= start && word.EndTime <= end && word.EndTime >= word.StartTime
                       && (i == 0 || words[i - 1].StartTime <= word.StartTime);
            }

            if (fits)
                return words;

            return words.Where(w => w.Text.Length > 0)
                        .Select(w =>
                        {
                            double s = Math.Clamp(w.StartTime, start, end);
                            return w with { StartTime = s, EndTime = Math.Clamp(w.EndTime, s, end) };
                        })
                        .OrderBy(w => w.StartTime)
                        .ToArray();
        }

        /// <summary>
        /// The words moved from a line window [<paramref name="oldStart"/>, <paramref name="oldEnd"/>]
        /// into another, for an edit that cuts or joins lines: times are kept and fitted into the new
        /// window, and a PLACED anchor is re-expressed against the new window so the word still pops up
        /// over the same moment of the line (the editor draws an anchor at that moment). Default anchors
        /// stay default.
        /// </summary>
        public static IReadOnlyList<BackingVocal> Rebase(IEnumerable<BackingVocal> words, double oldStart, double oldEnd, double newStart, double newEnd)
        {
            double oldSpan = oldEnd - oldStart;
            double newSpan = newEnd - newStart;

            return Fit(words.Select(w => w.Anchor is double anchor && oldSpan > 0 && newSpan > 0
                ? w with { Anchor = RoundAnchor((oldStart + anchor * oldSpan - newStart) / newSpan) }
                : w).ToArray(), newStart, newEnd);
        }

        /// <summary>
        /// <paramref name="line"/> with its backing vocals replaced by <paramref name="words"/>, fitted
        /// into its window; every other property is carried over unchanged (keep this in step with
        /// <see cref="LyricLine"/>'s properties).
        /// </summary>
        public static LyricLine With(LyricLine line, IReadOnlyList<BackingVocal>? words) => new LyricLine
        {
            RawText = line.RawText,
            StartTime = line.StartTime,
            EndTime = line.EndTime,
            SingEndTime = line.SingEndTime,
            Units = line.Units,
            SealGraceMs = line.SealGraceMs,
            Estimated = line.Estimated,
            Original = line.Original,
            UnromanisedWords = line.UnromanisedWords,
            BackingVocals = Fit(words, line.StartTime, line.EndTime),
        };

        /// <summary>The words shifted by <paramref name="deltaMs"/> (positive = later); anchors are fractions and do not move.</summary>
        public static IReadOnlyList<BackingVocal> Shift(IReadOnlyList<BackingVocal> words, double deltaMs)
            => words.Count == 0 ? words : words.Select(w => w with { StartTime = w.StartTime + deltaMs, EndTime = w.EndTime + deltaMs }).ToArray();

        #region Persistence

        /// <summary>
        /// Writes the <c>"backing"</c> array, or nothing for a line without backing vocals. Called last
        /// for a line object, so the key always ends it (which is what <see cref="LyricOsuFormat.StripBacking"/>
        /// and the server's fingerprint rely on).
        /// </summary>
        public static void Write(Utf8JsonWriter json, IReadOnlyList<BackingVocal> words)
        {
            if (words.Count == 0)
                return;

            json.WriteStartArray(WRITE_KEY);

            foreach (var word in words)
            {
                json.WriteStartObject();
                json.WriteString("text", word.Text);
                json.WriteNumber("start_ms", word.StartTime);
                json.WriteNumber("end_ms", word.EndTime);

                if (word.Anchor is double anchor)
                    json.WriteNumber("anchor", RoundAnchor(anchor));

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        /// <summary>The same array as a <see cref="JsonArray"/>, for writers that build nodes; null for none.</summary>
        public static JsonArray? ToJson(IReadOnlyList<BackingVocal> words)
        {
            if (words.Count == 0)
                return null;

            var array = new JsonArray();

            foreach (var word in words)
            {
                var item = new JsonObject
                {
                    ["text"] = word.Text,
                    ["start_ms"] = word.StartTime,
                    ["end_ms"] = word.EndTime,
                };

                if (word.Anchor is double anchor)
                    item["anchor"] = RoundAnchor(anchor);

                array.Add(item);
            }

            return array;
        }

        /// <summary>
        /// Reads a line object's <c>"backing"</c> array: every object with a non-empty text and a start
        /// becomes a word (a missing end is the start, a missing or malformed anchor is the default).
        /// Anything else in the array is skipped, and a line without the key reads as none.
        /// </summary>
        public static List<BackingVocal>? Read(JsonElement lineElement)
        {
            if (lineElement.ValueKind != JsonValueKind.Object
                || !lineElement.TryGetProperty(WRITE_KEY, out JsonElement array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var words = new List<BackingVocal>();

            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("text", out JsonElement textElement) || textElement.ValueKind != JsonValueKind.String
                    || number(item, "start_ms") is not double start)
                {
                    continue;
                }

                string text = CleanText(textElement.GetString());

                if (text.Length == 0)
                    continue;

                double end = number(item, "end_ms") is double e ? Math.Max(start, e) : start;
                double? anchor = number(item, "anchor") is double a ? Math.Clamp(a, 0, 1) : null;

                words.Add(new BackingVocal(text, start, end, anchor));
            }

            return words.Count > 0 ? words : null;
        }

        /// <summary>
        /// A finite number under <paramref name="key"/>, or null for anything else. The kind is checked
        /// first: <see cref="JsonElement.TryGetDouble"/> throws, rather than failing, on a string.
        /// </summary>
        private static double? number(JsonElement item, string key)
            => item.TryGetProperty(key, out JsonElement element) && element.ValueKind == JsonValueKind.Number
                                                                  && element.TryGetDouble(out double value) && double.IsFinite(value)
                ? value
                : null;

        /// <summary>A word's display text as stored: trimmed, inner whitespace collapsed to single spaces, controls dropped.</summary>
        public static string CleanText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var sb = new StringBuilder(text.Length);
            bool space = false;

            foreach (char c in text.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    space = true;
                    continue;
                }

                if (char.IsControl(c))
                    continue;

                if (space && sb.Length > 0)
                    sb.Append(' ');

                space = false;
                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>The words of a backing text: whitespace-separated, each cleaned; empty for nothing.</summary>
        public static string[] Words(string? text)
            => string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : CleanText(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        #endregion

        #region Import: brackets in a lyrics file

        /// <summary>
        /// One bracketed span of an imported lyric line: the words inside it and where it sat in the line
        /// the player types (<paramref name="MainOffset"/> characters into <see cref="Extraction.Main"/>),
        /// which is what times it when the file gives it no stamp of its own.
        /// </summary>
        public readonly record struct Span(string Text, int MainOffset);

        /// <summary>
        /// An imported line split into the part the player types and its backing vocals. <see cref="Main"/>
        /// is exactly what <see cref="Typeability.StripBackingVocals"/> gives (backlog 255's strip, unchanged).
        /// </summary>
        public readonly record struct Extraction(string Main, IReadOnlyList<Span> Spans);

        /// <summary>
        /// Splits an imported line into its typed part and its bracketed backing vocals. The TYPED part
        /// loses "(...)" and "[...]" alike, nested or not, an unclosed bracket running to the end of the
        /// line and a stray closer simply dropped: the rule <see cref="Typeability.StripBackingVocals"/>
        /// has always applied. Only a ROUND bracket is a backing vocal, though: lyric sources write what
        /// is sung behind the lead in "(...)" and annotate in "[...]" ("[Chorus]", "[x2]", "[Spoken]"),
        /// so a span whose outermost bracket is square is dropped as it always was. Each run of round
        /// bracketed text is one span, brackets inside it dropped, and a span with nothing but
        /// whitespace in it is no span.
        /// </summary>
        public static Extraction Extract(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return new Extraction(string.Empty, Array.Empty<Span>());

            var main = new StringBuilder(raw.Length);
            var spans = new List<Span>();
            var current = new StringBuilder();
            int depth = 0;
            int spanOffset = 0;
            bool sung = false;

            void close()
            {
                string text = CleanText(current.ToString());

                if (sung && text.Length > 0)
                    spans.Add(new Span(text, spanOffset));

                current.Clear();
            }

            foreach (char c in raw)
            {
                if (c == '(' || c == '[')
                {
                    if (depth == 0)
                    {
                        spanOffset = main.Length;
                        sung = IsSungBracket(c);
                    }
                    else
                        current.Append(' ');

                    depth++;
                    continue;
                }

                if (c == ')' || c == ']')
                {
                    if (depth > 0)
                    {
                        depth--;

                        if (depth == 0)
                            close();
                        else
                            current.Append(' ');
                    }

                    continue;
                }

                if (depth == 0)
                    main.Append(c);
                else
                    current.Append(c);
            }

            if (depth > 0)
                close();

            return new Extraction(main.ToString(), spans);
        }

        /// <summary>
        /// Whether an OPENING bracket starts a backing vocal: a round one does, a square one starts an
        /// annotation (see <see cref="Extract"/>). Only the outermost bracket of a run decides.
        /// </summary>
        public static bool IsSungBracket(char opening) => opening == '(';

        /// <summary>
        /// Times words spread across [<paramref name="start"/>, <paramref name="end"/>] by their
        /// letters, the way an untimed lyric line is spread: each word takes a share proportional to its
        /// length. An empty or zero-length span gives every word the minimum span at its start.
        /// </summary>
        public static List<BackingVocal> Spread(IReadOnlyList<string> words, double start, double end)
        {
            var result = new List<BackingVocal>(words.Count);
            int total = words.Sum(w => Math.Max(1, w.Length));

            if (words.Count == 0)
                return result;

            double span = Math.Max(0, end - start);
            double at = start;

            foreach (string word in words)
            {
                double length = total > 0 ? span * Math.Max(1, word.Length) / total : 0;
                result.Add(new BackingVocal(word, at, at + Math.Max(length, 0)));
                at += length;
            }

            return result;
        }

        /// <summary>
        /// Times an imported line's untimed spans by where they sat in it AS IT WAS WRITTEN: every
        /// character of the line, typed or bracketed, takes an equal share of the sung span
        /// [<paramref name="sungStart"/>, <paramref name="sungEnd"/>], so a span is sung where it stood
        /// and for as long as its own letters take (at least <see cref="MIN_SPAN_MS"/>, never past
        /// <paramref name="windowEnd"/>), and a trailing "(down)" lands at the end of the line rather than
        /// after it. The spans come in text order, as <see cref="Extract"/> gives them.
        /// </summary>
        public static List<BackingVocal> Place(IReadOnlyList<Span> spans, int mainLength, double sungStart, double sungEnd, double windowEnd)
        {
            var result = new List<BackingVocal>();

            if (spans.Count == 0)
                return result;

            double sung = Math.Max(0, sungEnd - sungStart);
            int rawLength = Math.Max(0, mainLength) + spans.Sum(s => s.Text.Length);
            int before = 0;

            foreach (var span in spans)
            {
                double at = sungStart + (rawLength > 0 ? sung * (Math.Clamp(span.MainOffset, 0, Math.Max(0, mainLength)) + before) / rawLength : 0);
                double length = Math.Max(MIN_SPAN_MS, rawLength > 0 ? sung * span.Text.Length / rawLength : 0);
                double until = Math.Max(at, Math.Min(windowEnd, at + length));
                result.AddRange(Spread(Words(span.Text), at, until));
                before += span.Text.Length;
            }

            return result;
        }

        #endregion

        /// <summary>A short, stable description for logs and test messages.</summary>
        public static string Describe(IReadOnlyList<BackingVocal> words)
            => string.Join(", ", words.Select(w => FormattableString.Invariant($"{w.Text}@{w.StartTime.ToString("0.#", CultureInfo.InvariantCulture)}")));
    }
}
