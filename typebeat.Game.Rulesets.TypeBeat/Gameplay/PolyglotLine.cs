// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// THE POLYGLOT DERIVATION (backlog 331): the line a Polyglot play is cut into cells from. Each
    /// word is played in its ORIGINAL (<see cref="TimedUnit.Original"/>) instead of its romanised
    /// text. Japanese words are joined where the original lyric has no space; their timed gap
    /// becomes a pause, or their shared edge becomes a subdivision. The per-cell targets are
    /// re-spread by the ordinary flattening (<see cref="TypingLine"/>), which is handed the
    /// derived line and knows nothing else about the mod.
    ///
    /// <list type="bullet">
    /// <item>A word with no original plays its romanised text exactly as authored (splits, pauses and
    /// all), so a mixed line works. A word the romaniser could not spell
    /// (<see cref="LyricLine.UnromanisedWords"/>, no typed text at all) plays its original at its
    /// own position and span.</item>
    /// <item>JAPANESE WORD TIMING. Each mapper-authored Japanese word has one sung span; its
    /// romanised subdivisions are ignored. Joining words removes untyped spaces but retains word
    /// seams and timed gaps. Older replays can select the previous subdivision-carry rule.</item>
    /// <item>SYLLABLE CUTS (other languages and older Japanese replays). A stored split (<see cref="TimedUnit.SyllableSplits"/>, or the derived one)
    /// indexes the ROMANISED text, so it is carried back onto the original through the romaniser's
    /// own unit mapping run in reverse: the romanised cut lands on the unit whose text starts there,
    /// and the cut in the original is that unit's source start
    /// (<see cref="LyricOriginals.RomanisedWord.SourceIndexOfTextSplit"/>). That only holds while
    /// the stored text is still the romaniser's spelling of the original; a word the mapper
    /// re-spelled, or a cut inside one unit's run, falls back to cuts spread EVENLY over the
    /// original's glyphs (a hangul block counts as one glyph, so a cut never splits a block). A
    /// subdivided word whose original has fewer glyphs than syllables keeps its span and loses its
    /// subdivision (its cells spread evenly over the whole word). An authored PAUSE indexes the
    /// romanised text too and is not carried: a word played in its original sings through its
    /// rests.</item>
    /// <item>A word with NO subdivision is ONE syllable over its span (backlog 363: only the mapper
    /// subdivides, which is what the play does without the mod too). Nothing is carried for it and
    /// nothing is cut per glyph. Japanese runs are cut at their word seams, with internal
    /// authored subdivisions retained only for older replays. A stylised romanised word ("ohhh") keeps no groups, as it has none
    /// without the mod.</item>
    /// <item>LINE-GRANULARITY maps whose originals live on the LINE only
    /// (<see cref="LyricLine.Original"/>, no word carrying one): when the line original has exactly
    /// as many words as the line has units, word i plays over unit i; otherwise the whole original
    /// is spread over the line's sung span by character count.</item>
    /// </list>
    ///
    /// <para>A line with no original anywhere is returned as it is, so its cells are the romanised
    /// ones byte for byte.</para>
    /// </summary>
    public static class PolyglotLine
    {
        /// <summary>
        /// A token's NATURAL split under Polyglot: <see cref="Splits"/> are the cut indices into the
        /// token, or null for a token that has no groups at all. Since backlog 363 a split is always
        /// EMPTY (one syllable over the word's span) or null: an unsubdivided word is never cut.
        /// </summary>
        public readonly record struct NaturalSplit(IReadOnlyList<int>? Splits);

        /// <summary>What <see cref="Derive"/> returns.</summary>
        public sealed class Result
        {
            internal Result(LyricLine line, NaturalSplit?[]? naturalSplits, int[]? rawCluster)
            {
                Line = line;
                NaturalSplits = naturalSplits;
                RawCluster = rawCluster;
            }

            /// <summary>The line the cells are cut from (the input line itself when nothing changed).</summary>
            public LyricLine Line { get; }

            /// <summary>Per token of <see cref="Line"/>, its natural split, or null for the ordinary syllabifier.</summary>
            public NaturalSplit?[]? NaturalSplits { get; }

            /// <summary>Per char of <see cref="Line"/>'s raw text, the id of the hangul block it is a key of, or -1.</summary>
            public int[]? RawCluster { get; }
        }

        private sealed class Piece
        {
            public required TimedUnit Unit;

            /// <summary>The token the piece contributes to the derived raw text.</summary>
            public string Text => TextOverride ?? Unit.Text;

            public string? TextOverride;
            public NaturalSplit? Natural;
            public required int[] Cluster;
        }

        /// <summary>Derives the Polyglot line of <paramref name="line"/> (see the class summary).</summary>
        public static Result Derive(LyricLine line, string? language, bool japaneseWordTiming = true)
        {
            var units = line.Units;
            bool wordLevel = line.UnromanisedWords.Count > 0 || units.Any(u => u.Original != null);

            List<Piece> pieces;

            if (wordLevel)
                pieces = wordPieces(line, language, japaneseWordTiming);
            else if (line.Original != null && units.Count > 0)
                pieces = linePieces(line, language, japaneseWordTiming);
            else
                return new Result(line, null, null);

            pieces.RemoveAll(p => p.Text.Length == 0);

            if (pieces.Count == 0)
                return new Result(line, null, null);

            if (isJapanese(language))
                pieces = joinJapaneseWords(line, splitJapaneseOriginalSpaces(line, pieces));

            var rawCluster = new List<int>();
            int nextBlock = 0;

            for (int i = 0; i < pieces.Count; i++)
            {
                if (i > 0)
                    rawCluster.Add(-1);

                var ids = new Dictionary<int, int>();

                foreach (int c in pieces[i].Cluster)
                {
                    if (c < 0)
                    {
                        rawCluster.Add(-1);
                        continue;
                    }

                    if (!ids.TryGetValue(c, out int id))
                        ids[c] = id = nextBlock++;

                    rawCluster.Add(id);
                }
            }

            var derived = new LyricLine
            {
                RawText = string.Join(' ', pieces.Select(p => p.Text)),
                StartTime = line.StartTime,
                EndTime = line.EndTime,
                SingEndTime = line.SingEndTime,
                Units = pieces.Select(p => p.Unit).ToArray(),
                SealGraceMs = line.SealGraceMs,
                Estimated = line.Estimated,
                Original = line.Original,
            };

            return new Result(derived, pieces.Select(p => p.Natural).ToArray(), rawCluster.ToArray());
        }

        private static List<Piece> wordPieces(LyricLine line, string? language, bool japaneseWordTiming)
        {
            var pieces = new List<Piece>();
            var unromanised = line.UnromanisedWords.OrderBy(w => w.Position).ToList();
            int next = 0;

            // A word with no original keeps its own raw token (the romanised text as the line spells
            // it), exactly as the ordinary flattening reads it.
            string[] tokens = line.RawText.Split(' ');
            bool tokensMatch = tokens.Length == line.Units.Count;

            for (int m = 0; m <= line.Units.Count; m++)
            {
                while (next < unromanised.Count && unromanised[next].Position <= m)
                {
                    var word = unromanised[next++];
                    pieces.Add(originalPiece(word.Original, word.StartTime, word.EndTime, null, language, japaneseWordTiming: japaneseWordTiming));
                }

                if (m < line.Units.Count)
                {
                    var unit = line.Units[m];

                    pieces.Add(unit.Original != null && PolyglotText.CellText(unit.Original).Length > 0
                        ? originalPiece(unit.Original, unit.StartTime, unit.EndTime, unit, language, japaneseWordTiming: japaneseWordTiming)
                        : plainPiece(unit, tokensMatch ? tokens[m] : unit.Text));
                }
            }

            return pieces;
        }

        private static Piece plainPiece(TimedUnit unit, string token)
            => new Piece { Unit = unit, TextOverride = token, Natural = null, Cluster = Enumerable.Repeat(-1, token.Length).ToArray() };

        private static List<Piece> linePieces(LyricLine line, string? language, bool japaneseWordTiming)
        {
            var units = line.Units;
            string[] words = LyricOriginals.CollapseWhitespace(line.Original).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == units.Count)
            {
                // The original tokenises the way the romanised line does: word i over unit i.
                return words.Select((w, i) => originalPiece(w, units[i].StartTime, units[i].EndTime, units[i], language, carryCuts: false, japaneseWordTiming: japaneseWordTiming)).ToList();
            }

            // An older line may only store its original on the line. If Kawazu's words match
            // the romanised units, keep those units' times before removing untyped spaces.
            if (isJapanese(language) && words.Length == 1 && JapaneseReading.Segment(words[0], language) is { } segments
                && segments.Count == units.Count)
                return segments.Select((w, i) => originalPiece(w, units[i].StartTime, units[i].EndTime, units[i], language, carryCuts: false, japaneseWordTiming: japaneseWordTiming)).ToList();

            // It does not: spread the whole original over the line's sung span by character count.
            double start = units[0].StartTime;
            double end = Math.Max(start, units[^1].EndTime);
            int[] counts = words.Select(w => Math.Max(1, PolyglotText.CellText(w).Count(Typeability.IsPolyglotTypeCell))).ToArray();
            double total = counts.Sum();
            var pieces = new List<Piece>(words.Length);
            int before = 0;

            for (int i = 0; i < words.Length; i++)
            {
                double s = start + (end - start) * before / total;
                before += counts[i];
                double e = start + (end - start) * before / total;
                pieces.Add(originalPiece(words[i], s, e, null, language, japaneseWordTiming: japaneseWordTiming));
            }

            return pieces;
        }

        // A word original may contain intentional spaces (the word-script editor allows this).
        // Split its display tokens without changing the timing ramp or losing authored cuts.
        private static List<Piece> splitJapaneseOriginalSpaces(LyricLine line, List<Piece> pieces)
        {
            string[] sources = pieces.Select(p => p.Unit.Original ?? p.Text).ToArray();
            string caption = PolyglotText.ToNfc(line.Original);
            static string withoutSpaces(string text) => new string(PolyglotText.ToNfc(text).Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (caption.Length > 0 && withoutSpaces(caption) == string.Concat(sources.Select(withoutSpaces)))
            {
                // A matching line original also records spaces inside a timed unit. Its whitespace
                // is authoritative even if the word original stores the same characters joined.
                int at = 0;
                for (int i = 0; i < sources.Length; i++)
                {
                    int start = at;
                    int remaining = withoutSpaces(sources[i]).Length;
                    while (at < caption.Length && remaining > 0)
                    {
                        if (!char.IsWhiteSpace(caption[at]))
                            remaining--;
                        at++;
                    }
                    sources[i] = caption.Substring(start, at - start);
                }
            }

            var result = new List<Piece>();
            for (int p = 0; p < pieces.Count; p++)
            {
                Piece piece = pieces[p];
                string[] originals = LyricOriginals.CollapseWhitespace(sources[p]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (originals.Length < 2)
                {
                    result.Add(piece);
                    continue;
                }

                string[] words = originals.Select(w => PolyglotText.CellText(w)).ToArray();
                int[] counts = words.Select(w => w.Count(Typeability.IsPolyglotTypeCell)).ToArray();
                double[] targets = TypingLine.CellTargetsFor(piece.Unit, counts.Sum(), Typeability.IsPolyglotTypeCell);
                IReadOnlyList<int> cuts = SyllableSegments.SplitsFor(piece.Unit);
                int offset = 0;
                int before = 0;
                for (int i = 0; i < words.Length; i++)
                {
                    int after = before + counts[i];
                    double start = i == 0 ? piece.Unit.StartTime : before < targets.Length ? targets[before] : piece.Unit.EndTime;
                    double end = i + 1 == words.Length ? piece.Unit.EndTime : after < targets.Length ? targets[after] : piece.Unit.EndTime;
                    var boundaries = new List<double>();
                    var splits = new List<int>();
                    for (int j = 0; j < cuts.Count && j < piece.Unit.SyllableBoundaries.Count; j++)
                    {
                        double time = piece.Unit.SyllableBoundaries[j];
                        if (cuts[j] > offset && cuts[j] < offset + words[i].Length && time > start && time < end)
                        {
                            boundaries.Add(time);
                            splits.Add(cuts[j] - offset);
                        }
                    }
                    result.Add(new Piece
                    {
                        Unit = new TimedUnit
                        {
                            Text = words[i],
                            Original = (i > 0 || hasOriginalSpace(sources[p], trailing: false) ? " " : string.Empty) + originals[i]
                                       + (i + 1 == words.Length && hasOriginalSpace(sources[p], trailing: true) ? " " : string.Empty),
                            StartTime = start, EndTime = end, Source = piece.Unit.Source, Confidence = piece.Unit.Confidence,
                            SyllableBoundaries = boundaries, SyllableSplits = splits,
                        },
                        Natural = boundaries.Count == 0 ? piece.Natural ?? new NaturalSplit(Array.Empty<int>()) : null,
                        Cluster = piece.Cluster.Skip(offset).Take(words[i].Length).ToArray(),
                    });
                    offset += words[i].Length;
                    before = after;
                }
            }
            return result;
        }

        private static bool isJapanese(string? language) => JapaneseReading.IsJapanese(language);

        /// <summary>
        /// The romanised words of Japanese do not imply typed spaces in the original script.
        /// Only whitespace present between the matching original pieces keeps a space cell.
        /// Without a matching line original, adjacent Japanese words still join unless their
        /// own originals record whitespace at the seam. Mixed Latin words keep their breaks.
        /// </summary>
        private static List<Piece> joinJapaneseWords(LyricLine line, List<Piece> pieces)
        {
            bool[] spaces = originalSpaces(line.Original, pieces) ?? Enumerable.Range(0, pieces.Count - 1)
                .Select(i => hasOriginalSpace(pieces[i].Unit.Original, trailing: true)
                             || hasOriginalSpace(pieces[i + 1].Unit.Original, trailing: false)
                             || !hasJapaneseEdge(pieces[i].Text, trailing: true)
                             || !hasJapaneseEdge(pieces[i + 1].Text, trailing: false)).ToArray();

            var joined = new List<Piece>();
            int from = 0;

            for (int i = 0; i < spaces.Length; i++)
            {
                if (!spaces[i])
                    continue;

                joined.Add(mergeJapaneseRun(pieces.GetRange(from, i - from + 1)));
                from = i + 1;
            }

            joined.Add(mergeJapaneseRun(pieces.GetRange(from, pieces.Count - from)));
            return joined;
        }

        private static bool hasOriginalSpace(string? original, bool trailing)
            => !string.IsNullOrEmpty(original) && char.IsWhiteSpace(trailing ? original[^1] : original[0]);

        private static bool hasJapaneseEdge(string text, bool trailing)
        {
            string kana = JapaneseRomaji.Hiragana(text);
            for (int i = trailing ? kana.Length - 1 : 0; i >= 0 && i < kana.Length; i += trailing ? -1 : 1)
            {
                char c = kana[i];
                if (char.IsPunctuation(c) || char.IsWhiteSpace(c))
                    continue;
                return Kawazu.Utilities.IsKanji(c) || Kawazu.Utilities.IsKana(c) || c is 'ー' or '々' or '〆';
            }
            return false;
        }

        private static bool[]? originalSpaces(string? original, IReadOnlyList<Piece> pieces)
        {
            if (original == null)
                return null;

            string source = LyricOriginals.CollapseWhitespace(PolyglotText.ToNfc(original));
            var spaces = new bool[Math.Max(0, pieces.Count - 1)];
            int at = 0;

            for (int i = 0; i < pieces.Count; i++)
            {
                if (i > 0)
                {
                    while (at < source.Length && char.IsWhiteSpace(source[at]))
                    {
                        spaces[i - 1] = true;
                        at++;
                    }
                }

                string text = LyricOriginals.CollapseWhitespace(PolyglotText.ToNfc(pieces[i].Unit.Original ?? pieces[i].Text));

                if (!source.AsSpan(at).StartsWith(text.AsSpan(), StringComparison.Ordinal))
                    return null;

                at += text.Length;
            }

            return at == source.Length ? spaces : null;
        }

        /// <summary>
        /// Joins original-script words into one typing token. A sung gap is an untyped pause;
        /// adjacent words meet at a subdivision. Their existing syllable cuts keep their times.
        /// </summary>
        private static Piece mergeJapaneseRun(IReadOnlyList<Piece> run)
        {
            if (run.Count == 1)
                return run[0];

            string text = string.Concat(run.Select(p => p.Text));
            double start = run.Min(p => p.Unit.StartTime);
            double end = run.Max(p => p.Unit.EndTime);
            var boundaries = new List<double>();
            var splits = new List<int>();
            var pauses = new List<WordPause>();
            int offset = 0;

            void addBoundary(double time, int split)
            {
                if (time <= start || time >= end || split <= 0 || split >= text.Length
                    || boundaries.Count > 0 && time <= boundaries[^1]
                    || splits.Count > 0 && split <= splits[^1])
                    return;

                boundaries.Add(time);
                splits.Add(split);
            }

            for (int i = 0; i < run.Count; i++)
            {
                Piece piece = run[i];

                if (i > 0)
                {
                    double previousEnd = run[i - 1].Unit.EndTime;

                    if (piece.Unit.StartTime > previousEnd && previousEnd > start && piece.Unit.StartTime < end)
                        pauses.Add(new WordPause(previousEnd, piece.Unit.StartTime, offset));
                    else
                        addBoundary(piece.Unit.StartTime, offset);
                }

                // Only an AUTHORED subdivision cuts inside a word (backlog 363); a word with none
                // is one syllable, so the run is otherwise cut at its word seams alone.
                if (piece.Unit.SyllableBoundaries.Count > 0)
                {
                    IReadOnlyList<int> cuts = SyllableSegments.SplitsFor(piece.Unit);

                    for (int j = 0; j < cuts.Count && j < piece.Unit.SyllableBoundaries.Count; j++)
                        addBoundary(piece.Unit.SyllableBoundaries[j], offset + cuts[j]);
                }

                offset += piece.Text.Length;
            }

            var unit = new TimedUnit
            {
                Text = text,
                StartTime = start,
                EndTime = end,
                Source = run.All(p => p.Unit.Source == TimingSource.Explicit) ? TimingSource.Explicit : TimingSource.Interpolated,
                Confidence = run.Min(p => p.Unit.Confidence),
                SyllableBoundaries = boundaries,
                SyllableSplits = splits,
                Pauses = pauses,
                Original = string.Concat(run.Select(p => p.Unit.Original ?? p.Text)),
            };

            return new Piece { Unit = unit, Natural = null, Cluster = run.SelectMany(p => p.Cluster).ToArray() };
        }

        /// <summary>
        /// One word played in its original over [<paramref name="start"/>, <paramref name="end"/>].
        /// <paramref name="source"/> is the romanised unit it stands in for (its subdivision and its
        /// romanised spelling), or null for a word that has none.
        /// </summary>
        private static Piece originalPiece(string original, double start, double end, TimedUnit? source, string? language, bool carryCuts = true, bool japaneseWordTiming = true)
        {
            var sourceIndex = new List<int>();
            var cluster = new List<int>();
            string text = PolyglotText.CellText(original, sourceIndex, cluster);
            string nfc = PolyglotText.ToNfc(original);
            int[] glyphs = glyphStarts(cluster);

            bool wordTimed = japaneseWordTiming && isJapanese(language)
                             && JapaneseRomaji.Hiragana(text).Any(c => Kawazu.Utilities.IsKanji(c) || Kawazu.Utilities.IsKana(c) || c is 'ー' or '々' or '〆');
            IReadOnlyList<double> boundaries = wordTimed ? Array.Empty<double>() : source?.SyllableBoundaries ?? Array.Empty<double>();
            IReadOnlyList<int> splits = Array.Empty<int>();

            if (boundaries.Count > 0)
            {
                int segments = boundaries.Count + 1;

                int[]? carried = carryCuts && source != null
                    ? carry(SyllableSegments.SplitsFor(source), source.Text, nfc, text, sourceIndex, language)
                    : null;

                if (carried != null && carried.Length == segments - 1)
                    splits = carried;
                else if (glyphs.Length >= segments)
                    splits = evenCuts(glyphs, segments);
                else
                    boundaries = Array.Empty<double>();
            }

            NaturalSplit? natural = null;

            // A word with no subdivision is ONE syllable over its span (backlog 363): the romanised
            // word's automatic split is no longer carried and the per-glyph fallback is gone. A
            // stylised romanised word still has no groups at all, as it has none without the mod.
            if (boundaries.Count == 0)
            {
                natural = !wordTimed && source != null && carryCuts && !Syllabifier.IsSyllabifiable(source.Text)
                    ? new NaturalSplit(null)
                    : new NaturalSplit(Array.Empty<int>());
            }

            var unit = new TimedUnit
            {
                Text = text,
                StartTime = start,
                EndTime = end,
                Source = source?.Source ?? TimingSource.Interpolated,
                Confidence = source?.Confidence ?? 1,
                SyllableBoundaries = boundaries,
                SyllableSplits = boundaries.Count > 0 ? splits : Array.Empty<int>(),
                Original = original,
            };

            return new Piece { Unit = unit, Natural = natural, Cluster = cluster.ToArray() };
        }

        /// <summary>
        /// Carries romanised <paramref name="cuts"/> (indices into <paramref name="romanised"/>) onto
        /// the cell text of the original: each one through the romaniser's units in reverse onto the
        /// NFC original, then onto the first cell that source character produced. Null when any cut
        /// cannot be carried (see the class summary), which sends the caller to its fallback.
        /// </summary>
        internal static int[]? carry(IReadOnlyList<int> cuts, string romanised, string nfcOriginal, string cellText, IReadOnlyList<int> sourceIndex, string? language)
        {
            if (cuts.Count == 0)
                return Array.Empty<int>();

            LyricOriginals.RomanisedWord word;

            try
            {
                word = LyricOriginals.RomaniseWord(nfcOriginal, language);
            }
            catch (Exception)
            {
                return null;
            }

            if (word.Flagged || !string.Equals(word.Text, romanised, StringComparison.Ordinal))
                return null;

            int[] carried = new int[cuts.Count];
            int previous = 0;

            for (int k = 0; k < cuts.Count; k++)
            {
                if (word.SourceIndexOfTextSplit(cuts[k]) is not int source)
                    return null;

                int cell = -1;

                for (int j = 0; j < sourceIndex.Count; j++)
                {
                    if (sourceIndex[j] >= source)
                    {
                        cell = j;
                        break;
                    }
                }

                if (cell <= previous || cell >= cellText.Length)
                    return null;

                carried[k] = previous = cell;
            }

            return carried;
        }

        /// <summary>The cell-text index each GLYPH starts at: every char on its own, a hangul block's keys as one.</summary>
        private static int[] glyphStarts(IReadOnlyList<int> cluster)
        {
            var starts = new List<int>();

            for (int i = 0; i < cluster.Count; i++)
            {
                if (i == 0 || cluster[i] < 0 || cluster[i] != cluster[i - 1])
                    starts.Add(i);
            }

            return starts.ToArray();
        }

        /// <summary><paramref name="segments"/> - 1 cuts spread evenly over the glyphs (requires at least that many glyphs).</summary>
        private static int[] evenCuts(int[] glyphs, int segments)
        {
            int[] cuts = new int[segments - 1];

            for (int s = 1; s < segments; s++)
                cuts[s - 1] = glyphs[(int)Math.Round((double)s * glyphs.Length / segments, MidpointRounding.AwayFromZero)];

            return cuts;
        }
    }
}
