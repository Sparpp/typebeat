// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// THE ONE derivation of how a word's authored pauses (<see cref="TimedUnit.Pauses"/>, the Map
    /// Editor's Insert Pause) cut that word into the stretches it is sung in.
    ///
    /// <para>A rest is a SUBDIVISION that happens to have no characters in it, and every reader of a
    /// subdivision reads it: the ENGINE times each stretch's characters independently, with its own
    /// boundaries and its own char cut, so no cell has a target inside a rest and the characters after
    /// one are timed FROM its end; the judgement groups gain a pair of edges at every rest, so the
    /// characters on the far side are judged against their own sung span exactly as a syllable
    /// subdivider's are, and the gameplay subdivision mark appears at each cut; the editor's strip draws
    /// the stretches either side of every rest, and its line box prints every cut as a pipe so the mapper
    /// can move them with '|' like any other split.</para>
    ///
    /// <para>N rests make N + 1 stretches, and each stretch is a word in its own right: it keeps the
    /// subdivision boundaries that fall inside its span and takes its own char cuts from the syllabifier
    /// (or from the word's authored split, partitioned by which stretch each cut falls in).</para>
    ///
    /// <para>A rest that CANNOT cut its word is IGNORED here, and the stretches close over it: edges that
    /// have left the word's own span, an inverted rest, a split that leaves every typeable cell on one
    /// side of it (a split sitting on punctuation, which would be a rest before the word's first
    /// character or after its last rather than one INSIDE it), and one that overlaps a rest already
    /// counted. That is the same conservative answer the loader gives a rest that no longer fits, and it
    /// is why a word whose every rest is ignored reads exactly as if it had none.</para>
    /// </summary>
    internal static class PausedWord
    {
        /// <summary>
        /// One sung stretch of a word: the token characters it is drawn from, the typeable cells it owns,
        /// the span it is sung over, the subdivision boundaries that fall inside it, the character
        /// positions those divide its text at, and the cell cuts it keeps - null when the word's char
        /// split is not authored, i.e. the derived even spread <see cref="TypingLine.syllableCharTarget"/>
        /// gives a whole word.
        ///
        /// <para><see cref="StartTime"/> and <see cref="EndTime"/> never cover a rest: consecutive
        /// stretches do NOT meet where one sits between them, and that gap belongs to no stretch, exactly
        /// as a space belongs to no syllable.</para>
        /// </summary>
        internal readonly record struct Piece(int FirstChar, int CharCount, int FirstCell, int CellCount,
                                             double StartTime, double EndTime,
                                             IReadOnlyList<double> Boundaries, int[]? CellCuts,
                                             IReadOnlyList<int> Cuts);

        /// <summary>How a word's rests cut it: its sung stretches, and the character cuts between them.</summary>
        internal sealed class Cut
        {
            /// <summary>The sung stretches, in text and in time order.</summary>
            internal IReadOnlyList<Piece> Pieces { get; init; } = Array.Empty<Piece>();

            /// <summary>
            /// The character positions between the RUNS, ascending (one fewer than there are runs): every
            /// rest's own cut, with the word's subdivision cuts among them. This is what a surface printing
            /// the word with a pipe at every division wants.
            /// </summary>
            internal IReadOnlyList<int> Splits { get; init; } = Array.Empty<int>();

            /// <summary>
            /// The span each run of text is sung over, in the same order as <see cref="Splits"/> divides
            /// it - the word's sung stretches with each stretch's own boundaries taken out of it. What the
            /// judgement groups are built from, so the characters on the far side of a rest are judged
            /// against their own sung span exactly as a subdivider's are.
            /// </summary>
            internal IReadOnlyList<(double Start, double End)> RunSpans { get; init; } = Array.Empty<(double, double)>();
        }

        /// <summary>
        /// How <paramref name="unit"/>'s authored rests cut it, or null when it has none this derivation
        /// can honour (see the type's remarks) - in which case every reader keeps the plain word it had
        /// before the feature existed.
        /// </summary>
        internal static Cut? Of(string token, double unitStart, double unitEnd, TimedUnit? unit)
        {
            if (unit == null || unit.Pauses.Count == 0)
                return null;

            var rests = UsableRests(token, unitStart, unitEnd, unit.Pauses);

            if (rests.Count == 0)
                return null;

            // The cuts this word's own subdivision describes: one per boundary, in boundary order.
            bool authored = SyllableSegments.IsAuthoredValid(token, unit.SyllableBoundaries.Count + 1, unit.SyllableSplits);
            IReadOnlyList<int> wordSplits = authored
                ? unit.SyllableSplits
                : SyllableSegments.SplitsFor(token, unit.SyllableBoundaries.Count + 1, null);
            int[]? wordCuts = authored && wordSplits.Count == unit.SyllableBoundaries.Count
                ? SyllableSegments.CellCuts(token, wordSplits)
                : null;

            int stretches = rests.Count + 1;
            var owned = new List<(double Time, int Slot)>[stretches];

            for (int i = 0; i < stretches; i++)
                owned[i] = new List<(double, int)>();

            // Route every boundary to the stretch whose span holds it. One that falls inside a rest goes to
            // the NEARER stretch, keeping its relative position there, exactly as a boundary survives a
            // word being retimed.
            for (int slot = 0; slot < unit.SyllableBoundaries.Count; slot++)
            {
                double time = unit.SyllableBoundaries[slot];
                int stretch = -1;

                for (int i = 0; i < stretches; i++)
                {
                    if (time >= stretchStart(rests, unitStart, i) && time <= stretchEnd(rests, unitEnd, i))
                    {
                        stretch = i;
                        break;
                    }
                }

                if (stretch < 0)
                {
                    int rest = restHolding(rests, time);
                    double fraction = (time - rests[rest].StartTime) / (rests[rest].EndTime - rests[rest].StartTime);
                    stretch = time - rests[rest].StartTime <= rests[rest].EndTime - time ? rest : rest + 1;

                    double lo = stretchStart(rests, unitStart, stretch);
                    double hi = stretchEnd(rests, unitEnd, stretch);
                    time = lo + fraction * (hi - lo);
                }

                owned[stretch].Add((time, slot));
            }

            // The stretches themselves, each with its boundaries in time order, its own char cuts and its
            // own cell cuts.
            var pieces = new List<Piece>(stretches);
            var splits = new List<int>();
            var runSpans = new List<(double Start, double End)>();
            int firstChar = 0;
            int firstCell = 0;

            for (int i = 0; i < stretches; i++)
            {
                int lastChar = i == stretches - 1 ? token.Length : rests[i].SplitChar;
                int charCount = Math.Max(0, lastChar - firstChar);
                int cells = Math.Max(0, cellsBefore(token, lastChar) - firstCell);
                double pieceStart = stretchStart(rests, unitStart, i);
                double pieceEnd = stretchEnd(rests, unitEnd, i);
                var inOrder = owned[i].OrderBy(pair => pair.Time).ToList();
                var slots = inOrder.Select(pair => pair.Slot).ToList();

                var (cuts, cellCuts) = stretchCuts(token, firstChar, charCount, slots, wordSplits, wordCuts, firstCell, cells);

                pieces.Add(new Piece(
                    firstChar, charCount,
                    firstCell, cells,
                    pieceStart, pieceEnd,
                    inOrder.Select(pair => pair.Time).ToArray(),
                    cellCuts,
                    cuts));

                // The runs the stretch's text is drawn in, and the span each is sung over: its boundaries
                // paired positionally with its cuts (the pairing the strip has always used for a degraded
                // word), its LAST run taking whatever the stretch has left, and a clock guard so a stretch
                // the mapper's cuts no longer describe still spreads its characters rather than drawing
                // itself backwards.
                double clock = pieceStart;
                var times = inOrder.Select(pair => pair.Time).ToArray();

                for (int run = 0; run <= cuts.Count; run++)
                {
                    double lo = run == 0 ? pieceStart : boundaryAt(times, run - 1, pieceStart);
                    double hi = run == cuts.Count ? pieceEnd : boundaryAt(times, run, pieceEnd);

                    lo = Math.Max(lo, clock);
                    hi = Math.Max(hi, lo);
                    clock = hi;

                    runSpans.Add((lo, hi));
                }

                // The cuts BETWEEN the stretches are each rest's own character; the stretch's own interior
                // cuts are its boundaries' - together, every division the word has.
                splits.AddRange(cuts);
                firstChar = lastChar;
                firstCell += cells;

                if (i < stretches - 1)
                    splits.Add(rests[i].SplitChar);
            }

            return new Cut { Pieces = pieces, Splits = splits, RunSpans = runSpans };

            // One stretch's cuts: the word's AUTHORED ones for its boundaries, each clamped inside the
            // stretch it is sung in - so a divider the mapper has dragged across a rest takes its character
            // WITH it rather than leaving it behind in the stretch it came from - and forced ascending. When
            // there are none to place, or they cannot all fit inside the stretch, it takes the cut the
            // syllabifier gives for its own text, exactly as a whole word with a stale split re-derives.
            //
            // The CELL cuts come from the resolved CHARACTER cuts when those are the mapper's, and are
            // null (derived) otherwise, so the ramp and the text can never part at different places.
            static (IReadOnlyList<int> Cuts, int[]? Cells) stretchCuts(string token, int firstChar, int charCount, List<int> slots,
                                                                       IReadOnlyList<int> wordSplits, int[]? wordCuts, int firstCell, int cells)
            {
                int lastChar = firstChar + charCount;

                if (charCount >= 2 && slots.Count > 0 && wordCuts != null && slots.Count <= wordSplits.Count)
                {
                    var placed = new List<int>(slots.Count);
                    int previous = firstChar;
                    bool fits = true;

                    foreach (int slot in slots)
                    {
                        int cut = Math.Clamp(wordSplits[slot], firstChar + 1, lastChar - 1);

                        if (cut <= previous)
                        {
                            fits = false;
                            break;
                        }

                        placed.Add(cut);
                        previous = cut;
                    }

                    if (fits)
                    {
                        var splitCuts = new int[placed.Count + 2];
                        splitCuts[0] = 0;

                        for (int i = 0; i < placed.Count; i++)
                            splitCuts[i + 1] = cellsBefore(token, placed[i]) - firstCell;

                        splitCuts[^1] = cells;
                        bool legal = true;

                        for (int i = 1; i < splitCuts.Length; i++)
                        {
                            if (splitCuts[i] <= splitCuts[i - 1] || splitCuts[i] > cells)
                                legal = false;
                        }

                        if (legal)
                            return (placed, splitCuts);
                    }
                }

                var derived = SyllableSegments.SplitsFor(token.Substring(firstChar, charCount), slots.Count + 1, null)
                                              .Select(cut => cut + firstChar).ToList();

                return (derived, null);
            }
        }

        /// <summary>
        /// The stretches a word is laid out and judged in, or null when its rests cannot cut it (in which
        /// case callers keep the plain syllable segmentation they have always had).
        /// </summary>
        internal static IReadOnlyList<Piece>? Pieces(TimedUnit unit, double unitStart, double unitEnd)
            => Of(unit.Text, unitStart, unitEnd, unit)?.Pieces;

        /// <summary>
        /// The character positions a word's text is divided at, for a surface that prints it with a pipe
        /// at every division: the syllable cuts it has always shown, with every rest's own cut among them
        /// when it carries usable ones. Empty for a word with neither, and identical to what the strip
        /// draws its runs from.
        /// </summary>
        internal static IReadOnlyList<int> Cuts(TimedUnit unit, double unitStart, double unitEnd)
        {
            if (Of(unit.Text, unitStart, unitEnd, unit) is Cut cut)
                return cut.Splits;

            return unit.SyllableBoundaries.Count == 0
                ? Array.Empty<int>()
                : SyllableSegments.SplitsFor(unit.Text, unit.SyllableBoundaries.Count + 1, unit.SyllableSplits);
        }

        /// <summary>
        /// The runs of text the editor's word strip draws, with the span each is sung over. A word whose
        /// rests cannot cut it (or one with none) gets exactly the segmentation the strip has always
        /// drawn - its syllable segments bounded by the word's own boundary times - so no existing map's
        /// layout moves because the feature exists.
        ///
        /// <para><paramref name="display"/> is the text as it should be DRAWN (freestyle slots already
        /// substituted); it must be the same length as <see cref="TimedUnit.Text"/>, whose character
        /// positions are what the cuts are expressed in.</para>
        /// </summary>
        internal static IReadOnlyList<Run> DisplayRuns(string display, TimedUnit unit, double unitStart, double unitEnd)
        {
            if (Of(unit.Text, unitStart, unitEnd, unit) is Cut cut)
                return DisplayRuns(display, cut);

            var boundaries = unit.SyllableBoundaries;
            var segments = SyllableSegments.SegmentTexts(display, SyllableSegments.SplitsFor(unit));
            var runs = new List<Run>(segments.Count);

            for (int i = 0; i < segments.Count; i++)
            {
                // The same edge rule gameplay's groups use, including the degraded case where the
                // syllabifier hands back fewer segments than there are boundaries: the last one runs to
                // the word's end.
                double lo = i == 0 ? unitStart : boundaryAt(boundaries, i - 1, unitStart);
                double hi = i == segments.Count - 1 ? unitEnd : boundaryAt(boundaries, i, unitEnd);

                runs.Add(new Run(segments[i], lo, Math.Max(lo, hi)));
            }

            return runs;
        }

        /// <summary>
        /// The rests a word really has, in time order: each one strictly inside the word with a split that
        /// separates typeable characters, none overlapping an earlier one, no two sharing a character, and
        /// every later rest sitting on a later character than the one before it - so the word's dividers
        /// read the same way from left to right in TIME and in TEXT.
        ///
        /// <para>Shared with the LOADER, which keeps exactly this set when it reads a map, so a rest the
        /// play would ignore is never stored in the first place; and with the editor's operations, which
        /// refuse to author one.</para>
        /// </summary>
        internal static List<WordPause> UsableRests(string token, double unitStart, double unitEnd, IEnumerable<WordPause> pauses)
        {
            var rests = new List<WordPause>();
            int totalCells = Typeability.TypeableCount(token);

            foreach (var pause in pauses.OrderBy(p => p.StartTime))
            {
                if (pause.StartTime <= unitStart || pause.EndTime >= unitEnd || pause.StartTime >= pause.EndTime)
                    continue;

                int cut = Math.Clamp(pause.SplitChar, 0, token.Length);
                int cells = cellsBefore(token, cut);

                if (cells <= 0 || cells >= totalCells)
                    continue;

                if (rests.Count > 0 && (pause.StartTime < rests[^1].EndTime || cut <= rests[^1].SplitChar))
                    continue;

                rests.Add(pause);
            }

            return rests;
        }

        /// <summary>
        /// The runs of text a paused word is drawn as: each stretch cut again at its own boundaries, so a
        /// subdivision INSIDE a stretch parts that stretch's characters exactly as it always did, and a
        /// rest's gap falls between two runs rather than over any of them.
        /// </summary>
        private static IReadOnlyList<Run> DisplayRuns(string display, Cut cut)
        {
            var runs = new List<Run>(cut.Splits.Count + 1);
            int span = 0;

            foreach (var piece in cut.Pieces)
            {
                // The stretch's cuts are positions in the TOKEN; the display text is sliced from it.
                var texts = SyllableSegments.SegmentTexts(
                    display.Substring(piece.FirstChar, piece.CharCount),
                    piece.Cuts.Select(position => position - piece.FirstChar).ToArray());

                foreach (var text in texts)
                {
                    var (lo, hi) = cut.RunSpans[span++];
                    runs.Add(new Run(text, lo, hi));
                }
            }

            return runs;
        }

        /// <summary>The start of stretch <paramref name="index"/>: the word's own start, or the end of the
        /// rest before it.</summary>
        private static double stretchStart(IReadOnlyList<WordPause> rests, double unitStart, int index)
            => index == 0 ? unitStart : rests[index - 1].EndTime;

        /// <summary>The end of stretch <paramref name="index"/>: the word's own end, or the start of the
        /// rest after it.</summary>
        private static double stretchEnd(IReadOnlyList<WordPause> rests, double unitEnd, int index)
            => index == rests.Count ? unitEnd : rests[index].StartTime;

        /// <summary>The index of the rest a time no stretch holds sits inside.</summary>
        private static int restHolding(IReadOnlyList<WordPause> rests, double time)
        {
            for (int i = 0; i < rests.Count; i++)
            {
                if (time >= rests[i].StartTime && time <= rests[i].EndTime)
                    return i;
            }

            return rests.Count - 1;
        }

        /// <summary>
        /// How many of a token's typeable cells sit before <paramref name="charIndex"/>.
        /// </summary>
        private static int cellsBefore(string token, int charIndex)
        {
            int cells = 0;

            for (int i = 0; i < charIndex && i < token.Length; i++)
            {
                if (Typeability.IsCell(token[i]))
                    cells++;
            }

            return cells;
        }

        /// <summary>
        /// Boundary <paramref name="index"/> of a word, or a fallback when the word carries fewer
        /// boundaries than the text was cut into (a syllabifier that degraded on an over-forced word
        /// hands back fewer segments than there are boundaries, and the strip tolerates that rather
        /// than refusing to draw the word).
        /// </summary>
        private static double boundaryAt(IReadOnlyList<double> boundaries, int index, double fallback)
            => index >= 0 && index < boundaries.Count ? boundaries[index] : fallback;

        /// <summary>
        /// A stretch of a word's text with the span it is sung over - what one label on the editor's
        /// word strip needs, and nothing else.
        /// </summary>
        internal readonly record struct Run(string Text, double StartTime, double EndTime);
    }
}
