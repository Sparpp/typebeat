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
    /// carries spaces splits into its own words. For Japanese without a supplied transliteration,
    /// Kawazu's dictionary boundaries also separate words before the remaining pieces become
    /// syllables. A <c>&lt;span ttm:role="x-bg"&gt;</c> is a BACKING
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
        /// <param name="language">The language to romanise non-Latin words under (backlog 330; see
        /// <see cref="Romaniser"/>) when the document ships no transliteration of its own.</param>
        public static bool TryParse(string? ttml, out IReadOnlyList<LyricLine> lines, out TtmlMetadata metadata, double offsetMs = 0, string? language = null)
        {
            lines = Array.Empty<LyricLine>();

            if (!TryParseRaw(ttml, out IReadOnlyList<LyricLine> raw, out metadata, offsetMs, language))
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
        public static bool TryParseRaw(string? ttml, out IReadOnlyList<LyricLine> lines, out TtmlMetadata metadata, double offsetMs = 0, string? language = null)
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

            var transliterations = readTransliterations(root);
            var built = new List<LyricLine>();

            // Descendants() is document order, so lines come out in song order whatever the
            // div/p nesting looks like.
            foreach (XElement paragraph in root.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                if (tryBuildLine(paragraph, transliterations, language, out LyricLine line))
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
            /// <summary>Each fragment NORMALIZED (<see cref="Text"/>) beside its source text (<c>Raw</c>).</summary>
            public readonly List<(string Text, double? Start, double? End, string Raw)> Fragments = new List<(string, double?, double?, string)>();

            public string Text => string.Concat(Fragments.Select(f => f.Text));

            /// <summary>The word as the document writes it (backlog 330).</summary>
            public string Raw => string.Concat(Fragments.Select(f => f.Raw));
        }

        /// <summary>
        /// One word of a line as the map will store it (backlog 330): its typed text and original, or
        /// an unromanised word (<see cref="Flagged"/>, no text), with the fragments that time it and,
        /// for a romanised word, where its syllable cuts land in the typed text.
        /// </summary>
        private sealed class ResolvedWord
        {
            public required string Text { get; init; }
            public string? Original { get; init; }
            public bool Flagged { get; init; }

            /// <summary>The fragments that time the word (each fragment's Text is only used for its length when <see cref="Cuts"/> is null).</summary>
            public required IReadOnlyList<(string Text, double? Start, double? End, string Raw)> Fragments { get; init; }

            /// <summary>
            /// For each fragment after the first, the char index in <see cref="Text"/> it starts at,
            /// or null where the cut is not carried (see <see cref="LyricOriginals.RomanisedWord.MapSplits"/>).
            /// Null as a whole for a word whose fragments ARE its text (a Latin word, a transliteration).
            /// </summary>
            public IReadOnlyList<int?>? Cuts { get; init; }
        }

        private static bool tryBuildLine(XElement paragraph, IReadOnlyDictionary<string, XElement> transliterations, string? language, out LyricLine line)
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

            // THE ORIGINAL TEXT (backlog 330): the source's own transliteration when it ships one for
            // this line, else the romaniser word by word. A line of plain ASCII resolves to exactly
            // the words it always had.
            string? key = readAttribute(paragraph, "key");
            List<ResolvedWord>? resolved = key != null && transliterations.TryGetValue(key, out XElement? transliteration)
                ? pairTransliteration(words, transliteration)
                : null;

            // Japanese lyrics commonly omit spaces between timed spans. Those spans may cross
            // dictionary word boundaries, so split the source words before assigning syllables.
            resolved ??= splitJapaneseWords(words, language).Select(w => resolve(w, language)).ToList();

            var typed = resolved.Where(r => !r.Flagged).ToList();
            string text = string.Join(' ', typed.Select(w => w.Text));
            bool anyFlagged = resolved.Count > typed.Count;

            if (TimingJsonLoader.YieldsNoCells(text) && !anyFlagged)
                return false;

            string? lineOriginal = resolved.Any(r => r.Original != null) || anyFlagged
                ? LyricOriginals.CollapseWhitespace(string.Concat(pieces.Select(p => p.Text)))
                : null;

            if (lineOriginal == text)
                lineOriginal = null;

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
            IReadOnlyList<TimedUnit> units;
            var pending = new List<UnromanisedWord>();

            if (pieces.Any(p => p.Start.HasValue || p.End.HasValue))
            {
                var built = new List<TimedUnit>();

                foreach (var word in resolved)
                {
                    TimedUnit unit = buildUnit(word, start, singEnd);

                    if (word.Flagged)
                        pending.Add(new UnromanisedWord(built.Count, word.Original!, unit.StartTime, unit.EndTime));
                    else
                        built.Add(unit);
                }

                units = built;
            }
            else if (anyFlagged)
            {
                var interpolated = LrcParser.InterpolateWords(resolved.Select(r => (r.Text, r.Flagged, r.Original ?? r.Text, r.Original)).ToList(), start, singEnd);
                units = interpolated.Units;
                pending.AddRange(interpolated.Pending);
            }
            else
            {
                units = LrcParser.InterpolateUnits(text, start, singEnd)
                                 .Select((u, i) => i < typed.Count && typed[i].Original != null ? LrcParser.WithOriginal(u, typed[i].Original) : u)
                                 .ToArray();
            }

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
                Original = lineOriginal,
                UnromanisedWords = pending,
            };

            return true;
        }

        /// <summary>
        /// One source word resolved by the ROMANISER (backlog 330). A word with nothing to romanise
        /// is its normalized fragments exactly as before; a romanised word's fragment cuts are mapped
        /// into its typed text through the romaniser's units.
        /// </summary>
        private static ResolvedWord resolve(WordBuffer word, string? language)
        {
            string raw = word.Raw;

            if (!LyricOriginals.CarriesOriginal(raw) && !Romaniser.NeedsRomanising(raw, language))
                return new ResolvedWord { Text = word.Text, Fragments = word.Fragments };

            var romanised = LyricOriginals.RomaniseWord(raw, language);

            if (romanised.Flagged)
                return new ResolvedWord { Text = string.Empty, Original = romanised.Original, Flagged = true, Fragments = word.Fragments };

            // The source cut at the start of each fragment after the first, in raw chars.
            var sourceCuts = new List<int>();
            int at = 0;

            for (int i = 0; i < word.Fragments.Count; i++)
            {
                if (i > 0)
                    sourceCuts.Add(at);

                at += word.Fragments[i].Raw.Length;
            }

            var cuts = new int?[sourceCuts.Count];

            foreach (var (sourceCut, textSplit) in romanised.MapSplits(sourceCuts))
                cuts[sourceCut] = textSplit;

            return new ResolvedWord { Text = romanised.Text, Original = romanised.Original, Fragments = word.Fragments, Cuts = cuts };
        }

        /// <summary>
        /// Pairs a line's words with the document's own TRANSLITERATION of it (Apple's
        /// <c>&lt;transliteration&gt;&lt;text for="key"&gt;</c>, backlog 330), which is preferred over
        /// the automatic romanisation because it is the reading the song actually has (kanji
        /// included). The transliteration's words become the typed words, timed by their own spans
        /// and cut into syllables by their own pieces, and each takes as its original the source
        /// text it transliterates:
        /// <list type="bullet">
        /// <item>the same number of words on both sides pairs them one for one;</item>
        /// <item>otherwise (Japanese writes no spaces, its transliteration does) every source
        /// fragment goes to the transliterated word whose span holds its start, which needs both
        /// sides timed and every word to receive at least one fragment.</item>
        /// </list>
        /// Anything else returns null and the romaniser is used instead.
        /// </summary>
        private static List<ResolvedWord>? pairTransliteration(List<WordBuffer> words, XElement transliteration)
        {
            var pieces = new List<Piece>();
            collectPieces(transliteration, null, null, pieces);

            List<WordBuffer> latin = buildWords(pieces);

            if (latin.Count == 0 || latin.Any(w => Romaniser.HasNonLatin(w.Raw) || w.Text.Length == 0))
                return null;

            if (latin.Count == words.Count)
            {
                return latin.Select((w, i) => new ResolvedWord
                {
                    Text = w.Text,
                    Original = LyricOriginals.CarriesOriginal(words[i].Raw) && words[i].Raw != w.Text ? words[i].Raw : null,
                    Fragments = w.Fragments.Any(f => f.Start.HasValue || f.End.HasValue) ? w.Fragments : words[i].Fragments,
                    Cuts = w.Fragments.Any(f => f.Start.HasValue || f.End.HasValue) ? null : new int?[Math.Max(0, words[i].Fragments.Count - 1)],
                }).ToList();
            }

            var sourceFragments = words.SelectMany(w => w.Fragments).ToList();

            if (sourceFragments.Any(f => f.Start == null) || latin.Any(w => w.Fragments.Any(f => f.Start == null || f.End == null)))
                return null;

            var assigned = new StringBuilder[latin.Count];

            foreach (var fragment in sourceFragments)
            {
                int owner = -1;

                for (int i = 0; i < latin.Count; i++)
                {
                    double from = latin[i].Fragments.Min(f => f.Start!.Value);
                    double to = latin[i].Fragments.Max(f => f.End!.Value);

                    if (fragment.Start!.Value >= from && fragment.Start.Value < to)
                    {
                        owner = i;
                        break;
                    }
                }

                if (owner < 0)
                    return null;

                (assigned[owner] ??= new StringBuilder()).Append(fragment.Raw);
            }

            if (assigned.Any(a => a == null))
                return null;

            return latin.Select((w, i) => new ResolvedWord
            {
                Text = w.Text,
                Original = assigned[i].ToString() is string source && LyricOriginals.CarriesOriginal(source) && source != w.Text ? source : null,
                Fragments = w.Fragments,
            }).ToList();
        }

        /// <summary>
        /// The document's TRANSLITERATIONS by line key: Apple writes them under
        /// <c>&lt;iTunesMetadata&gt;&lt;transliterations&gt;&lt;transliteration&gt;&lt;text for="L1"&gt;</c>
        /// beside the lines they spell, keyed by each <c>&lt;p itunes:key&gt;</c>. The first one per key wins.
        /// </summary>
        private static Dictionary<string, XElement> readTransliterations(XElement root)
        {
            var byKey = new Dictionary<string, XElement>(StringComparer.Ordinal);

            foreach (XElement transliteration in root.Descendants().Where(e => e.Name.LocalName == "transliteration"))
            {
                foreach (XElement text in transliteration.Elements().Where(e => e.Name.LocalName == "text"))
                {
                    if (readAttribute(text, "for") is string key && !byKey.ContainsKey(key))
                        byKey[key] = text;
                }
            }

            return byKey;
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

                    string raw = text.Substring(from, i - from);
                    string normalized = Typeability.Normalize(raw);

                    // A fragment that normalizes away entirely (a bare "(", an unsupported glyph) is
                    // neither a word nor a word BREAK: it adds nothing and closes nothing, so the
                    // pieces around it stay the one word they were written as. A fragment written in
                    // a script Normalize deletes is NOT such a fragment (backlog 330): it is a word
                    // the romaniser will spell, and is kept with its source text.
                    if (normalized.Length == 0 && !LyricOriginals.CarriesOriginal(raw))
                        continue;

                    if (current == null)
                    {
                        current = new WordBuffer();
                        words.Add(current);
                    }

                    current.Fragments.Add((normalized, piece.Start, piece.End, raw));
                }
            }

            return words;
        }

        /// <summary>
        /// Turns Kawazu's Japanese word boundaries into actual words, retaining each TTML
        /// fragment's timing. A boundary inside one timed fragment divides its span in proportion
        /// to the source characters on either side.
        /// </summary>
        private static List<WordBuffer> splitJapaneseWords(List<WordBuffer> words, string? language)
        {
            var result = new List<WordBuffer>();

            foreach (WordBuffer word in words)
            {
                IReadOnlyList<string>? parts = JapaneseReading.Segment(word.Raw, language);

                if (parts == null)
                {
                    result.Add(word);
                    continue;
                }

                int partIndex = 0;
                int partEnd = parts[0].Length;
                int sourceAt = 0;
                var split = new WordBuffer();

                foreach (var fragment in word.Fragments)
                {
                    int offset = 0;

                    while (offset < fragment.Raw.Length)
                    {
                        int length = Math.Min(fragment.Raw.Length - offset, partEnd - sourceAt);
                        string raw = fragment.Raw.Substring(offset, length);
                        double? start = fragment.Start;
                        double? end = fragment.End;

                        if (start.HasValue && end.HasValue && end > start)
                        {
                            double duration = end.Value - start.Value;
                            end = start.Value + duration * (offset + length) / fragment.Raw.Length;
                            start += duration * offset / fragment.Raw.Length;
                        }
                        else
                        {
                            if (offset > 0)
                                start = null;

                            if (offset + length < fragment.Raw.Length)
                                end = null;
                        }

                        split.Fragments.Add((Typeability.Normalize(raw), start, end, raw));
                        offset += length;
                        sourceAt += length;

                        if (sourceAt == partEnd)
                        {
                            result.Add(split);
                            split = new WordBuffer();
                            partIndex++;

                            if (partIndex < parts.Count)
                                partEnd += parts[partIndex].Length;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// One word as a unit: its span is the union of its fragments' stamps, and every fragment
        /// after the first that starts strictly inside that span is a SYLLABLE boundary with the
        /// character split that goes with it. The split is only ever emitted when it is a valid cut
        /// of this exact word (strictly ascending, inside the token), which is the same contract the
        /// loader validates against.
        /// </summary>
        private static TimedUnit buildUnit(ResolvedWord word, double fallbackStart, double fallbackEnd)
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

                // A ROMANISED word's cuts are the source's, carried through the romaniser's units
                // (backlog 330): a cut the mapping does not carry takes its time boundary with it,
                // so the syllable merges into the one before it.
                int? cut = word.Cuts == null ? chars : i > 0 && i - 1 < word.Cuts.Count ? word.Cuts[i - 1] : null;

                if (i > 0 && cut is int split && fragment.Start is double s && s > start && s < end)
                {
                    boundaries.Add(s);
                    splits.Add(split);
                }

                chars += fragment.Text.Length;
            }

            // The cuts must stay a legal split of the typed word; a romanised word whose cuts do not
            // is left undivided rather than cut wrongly.
            if (word.Cuts != null && !Gameplay.SyllableSegments.IsAuthoredValid(word.Text, splits.Count + 1, splits))
            {
                boundaries.Clear();
                splits.Clear();
            }

            return new TimedUnit
            {
                Text = word.Text,
                StartTime = start,
                EndTime = end,
                Source = TimingSource.Explicit,
                SyllableBoundaries = boundaries,
                SyllableSplits = splits,
                Original = word.Original,
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
                    Original = line.Original,
                    UnromanisedWords = line.UnromanisedWords
                                           .Select(w => w with { StartTime = w.StartTime + deltaMs, EndTime = w.EndTime + deltaMs })
                                           .ToArray(),
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
                        Original = u.Original,
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
