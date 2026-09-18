// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Apple Music TTML -> type!beat lyric timing. Pure and static, like <see cref="LrcParser"/>:
    /// it turns a TTML document into <see cref="LyricLine"/>s carrying the file's own word (and
    /// syllable) timing, which <c>LyricMapImporter.SynthesizeTimingJsonFromTtml</c> then serializes
    /// as the same version-2 timing.json the aligner writes. Nothing downstream of the timing.json
    /// needs to know TTML exists.
    ///
    /// <para><b>What a TTML already knows.</b> Apple's files are word- and syllable-timed, so
    /// unlike the LRC path there is nothing to align and no audio is needed: the timings here ARE
    /// the song's own. A &lt;p&gt; is a LINE; its &lt;span&gt; children are timed PIECES. Pieces
    /// with no whitespace between them are SYLLABLES of one word ("Ab" "ra" "ca" "dab" "ra," -&gt;
    /// "Abracadabra,"), pieces separated by whitespace are separate words, and a piece whose text
    /// carries spaces splits into its own words. A <c>&lt;span ttm:role="x-bg"&gt;</c> is a BACKING
    /// VOCAL and is dropped with its whole subtree, which is the same reading the import boundary
    /// already gives a bracketed backing vocal: the player never types it.</para>
    ///
    /// <para><b>A line with no timed spans is left LINE-LEVEL.</b> Files are published in
    /// <c>itunes:timing="Line"</c> as well as word timing, and inventing per-word stamps out of the
    /// line's own span would be worse than useless: the loader would see a run of identical spans
    /// and collapse every word after the first into a zero-length point. Such a line therefore
    /// carries no <c>words[]</c> at all, and the loader's interpolation spreads its words across the
    /// line - the shape a .lrc import has always produced. A line that no stamp anywhere can place
    /// (no span time, no line time, no part time) is dropped outright.</para>
    ///
    /// <para><b>Times.</b> <c>begin</c>/<c>end</c> are accepted as "SS.mmm", "MM:SS.mmm" or
    /// "HH:MM:SS.mmm" and are used VERBATIM (plus the optional offset), because a TTML's own
    /// timeline is the song's. The document's <c>leadingSilence</c> and <c>lyricOffset</c> metadata
    /// are reported through <see cref="TtmlMetadata"/> and deliberately NOT baked in, so a mapper
    /// whose rip needs nudging can judge it with the editor's own "shift all timings" tool instead
    /// of inheriting a guess. Nothing is re-derived either: a missing piece stamp falls back to its
    /// enclosing line's span, a missing line stamp to its part's, and a line with no usable text is
    /// dropped outright so the previous line's window runs over its span - exactly what the
    /// timing.json loader does with the same case.</para>
    /// </summary>
    public static class TtmlParser
    {
        /// <summary>File extensions <see cref="IsTtmlFile"/> accepts.</summary>
        public static readonly string[] EXTENSIONS = { ".ttml", ".xml" };

        /// <summary>
        /// The document-level facts a TTML states about itself. All of them are REPORTED, never
        /// applied: see the class remarks for why the timeline is taken verbatim.
        /// </summary>
        public sealed class TtmlMetadata
        {
            /// <summary>The <c>itunes:timing</c> mode ("Word", "Syllable", "Line", "None"); empty when unstated.</summary>
            public string Timing { get; init; } = string.Empty;

            /// <summary>
            /// Milliseconds of silence ahead of the first word, per <c>&lt;iTunesMetadata leadingSilence&gt;</c>
            /// (which states SECONDS). 0 when the document does not say.
            /// </summary>
            public double LeadingSilenceMs { get; init; }

            /// <summary>Milliseconds of <c>&lt;audio lyricOffset&gt;</c> lead-in (also stated in seconds). 0 when unstated.</summary>
            public double LyricOffsetMs { get; init; }

            /// <summary>The document's own end (<c>&lt;body dur&gt;</c>), when it states one.</summary>
            public double? SongEndMs { get; init; }
        }

        /// <summary>
        /// Parses <paramref name="ttml"/> into the lyric lines a map will actually hold: the file is
        /// read (<see cref="TryParseRaw"/>) and then taken through the same
        /// <see cref="SynthesizedTimingJson"/> write and <see cref="TimingJsonLoader"/> read that an
        /// import performs, so a line's window, its word clamping and its seal grace are the
        /// loader's, not a second opinion. False for anything that is not a &lt;tt&gt; document, is
        /// not well-formed XML, or yields no line with a cell to type. Never throws.
        /// </summary>
        /// <param name="ttml">The TTML document text (a leading BOM is tolerated).</param>
        /// <param name="lines">The lines, in document order, exactly as decoding this document would
        /// produce them.</param>
        /// <param name="metadata">The document's own timing facts; see <see cref="TtmlMetadata"/>.</param>
        /// <param name="offsetMs">Added to every produced time, line and word alike. 0 keeps the
        /// document's own timeline.</param>
        public static bool TryParse(string? ttml, out IReadOnlyList<LyricLine> lines, out TtmlMetadata metadata, double offsetMs = 0)
        {
            lines = Array.Empty<LyricLine>();

            if (!TryParseRaw(ttml, out IReadOnlyList<LyricLine> raw, out metadata, offsetMs))
                return false;

            string timing = SynthesizedTimingJson.Write(raw, wordTiming: true, songEndMs: metadata.SongEndMs ?? raw[^1].SingEndTime);

            lines = TimingJsonLoader.TryParse(timing, out IReadOnlyList<LyricLine> loaded) ? loaded : raw;
            return lines.Count > 0;
        }

        /// <summary>
        /// Parses <paramref name="ttml"/> into the document's OWN reading: word and line spans as
        /// written, with no window applied (every line's <c>EndTime</c> is its sung end). This is
        /// the shape an import stores, because the alignment document is a record of where the words
        /// ARE - a word that overruns its line boundary is what earns the line its seal grace, and
        /// clamping it here would destroy that evidence before the loader ever sees it. Use
        /// <see cref="TryParse"/> for lines to play or edit.
        /// </summary>
        public static bool TryParseRaw(string? ttml, out IReadOnlyList<LyricLine> lines, out TtmlMetadata metadata, double offsetMs = 0)
        {
            lines = Array.Empty<LyricLine>();
            metadata = new TtmlMetadata();

            if (string.IsNullOrWhiteSpace(ttml))
                return false;

            if (ttml[0] == '\uFEFF')
                ttml = ttml.Substring(1);

            XDocument document;

            try
            {
                // PreserveWhitespace: the space BETWEEN two spans is what separates two words, and
                // dropping insignificant whitespace would fuse a whole line into one word.
                document = XDocument.Parse(ttml, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return false;
            }

            XElement? root = document.Root;

            if (root == null || root.Name.LocalName != "tt")
                return false;

            metadata = readMetadata(root);

            var built = new List<LyricLine>();

            // Descendants() is document order, so lines come out in song order whatever the
            // div/p nesting looks like.
            foreach (XElement paragraph in root.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                if (tryBuildLine(paragraph, out LyricLine line))
                    built.Add(line);
            }

            if (built.Count == 0)
                return false;

            if (offsetMs != 0)
                built = shift(built, offsetMs);

            lines = built;
            return true;
        }

        /// <summary>
        /// Parses <paramref name="ttml"/> into lyric lines, or an empty list when it is not a usable
        /// TTML. The convenience overload for callers that do not need the document's metadata.
        /// </summary>
        public static IReadOnlyList<LyricLine> Parse(string? ttml, double offsetMs = 0)
            => TryParse(ttml, out IReadOnlyList<LyricLine> lines, out _, offsetMs) ? lines : Array.Empty<LyricLine>();

        /// <summary>Whether <paramref name="path"/>'s extension is one a TTML is delivered under.</summary>
        public static bool IsTtmlFile(string? path)
            => !string.IsNullOrEmpty(path) && EXTENSIONS.Contains(Path.GetExtension(path).ToLowerInvariant());

        /// <summary>
        /// Whether <paramref name="content"/> opens as a TTML document (an optional XML declaration
        /// followed by a &lt;tt&gt; root), which is how a dropped file is recognised on its CONTENT
        /// rather than on its name. Cheap enough to run on every import.
        /// </summary>
        public static bool LooksLikeTtml(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return false;

            int i = 0;

            while (i < content.Length && char.IsWhiteSpace(content[i]))
                i++;

            if (i >= content.Length || content[i] != '<')
                return false;

            // "<tt ...>" or "<?xml ...?>\n<tt ...>"; the trailing test keeps a "<ttm:agent" inside
            // some other document from counting as the root.
            for (int at = content.IndexOf("<tt", i, StringComparison.Ordinal); at >= 0; at = content.IndexOf("<tt", at + 3, StringComparison.Ordinal))
            {
                int after = at + 3;

                if (after >= content.Length || content[after] == '>' || content[after] == '/' || char.IsWhiteSpace(content[after]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// "SS.mmm", "MM:SS.mmm" or "HH:MM:SS.mmm" -> milliseconds. Every segment is a plain
        /// invariant-culture number, so a fractional seconds field needs no special case.
        /// </summary>
        public static bool TryParseTimestamp(string? token, out double milliseconds)
        {
            milliseconds = 0;

            if (string.IsNullOrWhiteSpace(token))
                return false;

            string[] parts = token.Trim().Split(':');

            if (parts.Length == 0 || parts.Length > 3)
                return false;

            double total = 0;

            foreach (string part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
                    return false;

                total = total * 60 + value;
            }

            milliseconds = total * 1000;
            return true;
        }

        #region Parsing

        /// <summary>One timed or untimed run of text inside a line, in document order.</summary>
        private readonly record struct Piece(string Text, double? Start, double? End);

        /// <summary>
        /// A word under construction: the pieces that make it up, in order. One piece is an
        /// undivided word; two or more are its syllables.
        /// </summary>
        private sealed class WordBuffer
        {
            public readonly List<(string Text, double? Start, double? End)> Fragments = new List<(string, double?, double?)>();

            public string Text
            {
                get
                {
                    var sb = new StringBuilder();

                    foreach (var fragment in Fragments)
                        sb.Append(fragment.Text);

                    return sb.ToString();
                }
            }
        }

        private static bool tryBuildLine(XElement paragraph, out LyricLine line)
        {
            line = null!;

            // A <p> may sit inside a <div> (a song part) that carries its own span; that is the
            // fallback when the line itself states no time.
            XElement? part = paragraph.Parent;
            double? partStart = part != null && part.Name.LocalName == "div" ? readTime(part, "begin") : null;
            double? partEnd = part != null && part.Name.LocalName == "div" ? readTime(part, "end") : null;

            var pieces = new List<Piece>();
            collectPieces(paragraph, null, null, pieces);

            List<WordBuffer> words = buildWords(pieces);

            if (words.Count == 0)
                return false;

            string text = string.Join(' ', words.Select(w => w.Text));

            if (TimingJsonLoader.YieldsNoCells(text))
                return false;

            // A line states its own span; the part's span covers a line that does not, and a word's
            // own stamp covers one the document left bare. With none of the three there is nothing
            // saying WHEN this line happens, and a line nobody can place is dropped rather than
            // parked at time zero.
            double? statedStart = readTime(paragraph, "begin") ?? partStart ?? firstTime(words, useStart: true);

            if (statedStart == null)
                return false;

            double start = statedStart.Value;
            double singEnd = readTime(paragraph, "end") ?? partEnd ?? firstTime(words, useStart: false) ?? start;

            if (singEnd < start)
                singEnd = start;

            // A line whose words state no times of their own ("itunes:timing=Line", and any line the
            // stripper left spanless) has NO word reading to record: stamping every word with the
            // line's whole span would not just be noise, it would hand the loader a run of identical
            // spans and collapse every word after the first into a zero-length point. Such a line is
            // left LINE-LEVEL, and the loader's own interpolation spreads its words across the line -
            // which is exactly the shape a .lrc import has always produced.
            IReadOnlyList<TimedUnit> units = pieces.Any(p => p.Start.HasValue || p.End.HasValue)
                ? words.Select(w => buildUnit(w, start, singEnd)).ToArray()
                : LrcParser.InterpolateUnits(text, start, singEnd);

            line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                // The RAW reading: the sung end, not a seal. A line's hard seal is the next line's
                // start, which is the loader's rule to apply when it rebuilds these lines from the
                // synthesized timing.json (see TryParse).
                EndTime = singEnd,
                SingEndTime = singEnd,
                Units = units,
            };

            return true;
        }

        /// <summary>
        /// Flattens a line into timed pieces. Text nodes inherit the span above them; a &lt;span&gt;
        /// carries its own begin/end and recurses into its children when it has any, so a word group
        /// whose syllables are themselves spans keeps the syllables' own stamps while a plain span
        /// is one piece. A backing-vocal span is skipped with everything under it.
        /// </summary>
        private static void collectPieces(XElement element, double? inheritedStart, double? inheritedEnd, List<Piece> pieces)
        {
            foreach (XNode node in element.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        if (text.Value.Length > 0)
                            pieces.Add(new Piece(text.Value, inheritedStart, inheritedEnd));
                        break;

                    case XElement span when span.Name.LocalName == "span":
                        if (isBackingVocal(span))
                            break;

                        double? start = readTime(span, "begin") ?? inheritedStart;
                        double? end = readTime(span, "end") ?? inheritedEnd;

                        if (span.Elements().Any(e => e.Name.LocalName == "span"))
                            collectPieces(span, start, end, pieces);
                        else
                            pieces.Add(new Piece(span.Value, start, end));

                        break;

                    // Any other element (<br/>, an unknown wrapper) is transparent: carry the
                    // ambient stamps through it rather than losing the text beneath.
                    case XElement other:
                        collectPieces(other, inheritedStart, inheritedEnd, pieces);
                        break;
                }
            }
        }

        /// <summary>
        /// Groups pieces into words: whitespace closes a word, and consecutive non-space runs in
        /// adjacent pieces are syllables of one word. Each fragment is normalized as it is taken, so
        /// the word text is exactly what the map will store and the syllable char positions count
        /// the same characters the rest of the game counts.
        /// </summary>
        private static List<WordBuffer> buildWords(List<Piece> pieces)
        {
            var words = new List<WordBuffer>();
            WordBuffer? current = null;

            foreach (Piece piece in pieces)
            {
                string text = piece.Text;
                int i = 0;

                while (i < text.Length)
                {
                    if (char.IsWhiteSpace(text[i]))
                    {
                        current = null;
                        i++;
                        continue;
                    }

                    int from = i;

                    while (i < text.Length && !char.IsWhiteSpace(text[i]))
                        i++;

                    string normalized = Typeability.Normalize(text.Substring(from, i - from));

                    // A fragment that normalizes away entirely (a bare "(", an unsupported glyph) is
                    // neither a word nor a word BREAK: it adds nothing and closes nothing, so the
                    // pieces around it stay the one word they were written as.
                    if (normalized.Length == 0)
                        continue;

                    if (current == null)
                    {
                        current = new WordBuffer();
                        words.Add(current);
                    }

                    current.Fragments.Add((normalized, piece.Start, piece.End));
                }
            }

            return words;
        }

        /// <summary>
        /// One word as a unit: its span is the union of its fragments' stamps, and every fragment
        /// after the first that starts strictly inside that span is a SYLLABLE boundary with the
        /// character split that goes with it. The split is only ever emitted when it is a valid cut
        /// of this exact word (strictly ascending, inside the token), which is the same contract the
        /// loader validates against.
        /// </summary>
        private static TimedUnit buildUnit(WordBuffer word, double fallbackStart, double fallbackEnd)
        {
            double start = fallbackStart;
            double end = fallbackEnd;
            bool anyStart = false;
            bool anyEnd = false;

            foreach (var fragment in word.Fragments)
            {
                if (fragment.Start is double s)
                {
                    start = anyStart ? Math.Min(start, s) : s;
                    anyStart = true;
                }

                if (fragment.End is double e)
                {
                    end = anyEnd ? Math.Max(end, e) : e;
                    anyEnd = true;
                }
            }

            if (end < start)
                end = start;

            var boundaries = new List<double>();
            var splits = new List<int>();
            int chars = 0;

            for (int i = 0; i < word.Fragments.Count; i++)
            {
                var fragment = word.Fragments[i];

                if (i > 0 && fragment.Start is double s && s > start && s < end)
                {
                    boundaries.Add(s);
                    splits.Add(chars);
                }

                chars += fragment.Text.Length;
            }

            return new TimedUnit
            {
                Text = word.Text,
                StartTime = start,
                EndTime = end,
                Source = TimingSource.Explicit,
                SyllableBoundaries = boundaries,
                SyllableSplits = splits,
            };
        }

        /// <summary>
        /// Every produced time moved by <paramref name="deltaMs"/>: lines, words and syllable
        /// boundaries together, so nothing can drift out of alignment with anything else. Char
        /// splits are indices and ride through untouched.
        /// </summary>
        private static List<LyricLine> shift(IReadOnlyList<LyricLine> lines, double deltaMs)
        {
            var shifted = new List<LyricLine>(lines.Count);

            foreach (LyricLine line in lines)
            {
                shifted.Add(new LyricLine
                {
                    RawText = line.RawText,
                    StartTime = line.StartTime + deltaMs,
                    EndTime = line.EndTime + deltaMs,
                    SingEndTime = line.SingEndTime + deltaMs,
                    SealGraceMs = line.SealGraceMs,
                    Estimated = line.Estimated,
                    Units = line.Units.Select(u => new TimedUnit
                    {
                        Text = u.Text,
                        StartTime = u.StartTime + deltaMs,
                        EndTime = u.EndTime + deltaMs,
                        Source = u.Source,
                        Confidence = u.Confidence,
                        SyllableBoundaries = u.SyllableBoundaries.Count == 0
                            ? u.SyllableBoundaries
                            : u.SyllableBoundaries.Select(b => b + deltaMs).ToArray(),
                        SyllableSplits = u.SyllableSplits,
                    }).ToArray(),
                });
            }

            return shifted;
        }

        private static TtmlMetadata readMetadata(XElement root)
        {
            double leadingSilence = readSeconds(root.Descendants().FirstOrDefault(e => e.Name.LocalName == "iTunesMetadata"), "leadingSilence");
            double lyricOffset = readSeconds(root.Descendants().FirstOrDefault(e => e.Name.LocalName == "audio"), "lyricOffset");

            XElement? body = root.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
            double? songEnd = readTime(body, "dur") ?? readTime(root, "dur");

            return new TtmlMetadata
            {
                Timing = readAttribute(root, "timing") ?? string.Empty,
                LeadingSilenceMs = leadingSilence,
                LyricOffsetMs = lyricOffset,
                SongEndMs = songEnd,
            };
        }

        private static bool isBackingVocal(XElement element)
        {
            string? role = readAttribute(element, "role");
            return role != null && role.StartsWith("x-bg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The first stated start (or end) among a word's fragments, for a line that states no span of its own.</summary>
        private static double? firstTime(List<WordBuffer> words, bool useStart)
        {
            foreach (WordBuffer word in words)
            {
                foreach (var fragment in word.Fragments)
                {
                    double? value = useStart ? fragment.Start : fragment.End;

                    if (value.HasValue)
                        return value;
                }
            }

            return null;
        }

        /// <summary>
        /// An attribute's time value, matched on LOCAL name so the TTML namespace prefix (or its
        /// absence) makes no difference.
        /// </summary>
        private static double? readTime(XElement? element, string attribute)
        {
            if (element == null)
                return null;

            foreach (XAttribute candidate in element.Attributes())
            {
                if (!candidate.Name.LocalName.Equals(attribute, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TryParseTimestamp(candidate.Value, out double milliseconds))
                    return milliseconds;
            }

            return null;
        }

        /// <summary>A plain attribute value, matched on local name like <see cref="readTime"/>.</summary>
        private static string? readAttribute(XElement element, string attribute)
        {
            foreach (XAttribute candidate in element.Attributes())
            {
                if (candidate.Name.LocalName.Equals(attribute, StringComparison.OrdinalIgnoreCase))
                    return candidate.Value;
            }

            return null;
        }

        /// <summary>An attribute stated in SECONDS (Apple's metadata fields) -> milliseconds.</summary>
        private static double readSeconds(XElement? element, string attribute)
        {
            string? raw = element == null ? null : readAttribute(element, attribute);

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
                ? seconds * 1000
                : 0;
        }

        #endregion
    }
}
