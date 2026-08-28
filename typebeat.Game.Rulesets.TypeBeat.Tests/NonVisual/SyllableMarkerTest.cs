// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The SYLLABLE MARKERS (backlog 225): the tiny triangles drawn in the inter-character gap at
    /// each mid-word syllable boundary of a word the mapper SUBDIVIDED.
    /// <see cref="TypingLine.SyllableMarkerCells"/> is the whole rule, so pinning it pins the
    /// feature; the display does nothing but hang a drawable off each cell it names.
    ///
    /// <para>Two properties run through this fixture and matter more than any individual case:</para>
    /// <list type="number">
    /// <item>A mark can never DISAGREE with judgement, because it is read off the compacted syllable
    /// groups rather than re-derived: every marker cell opens a different group from the cell to its
    /// left. That is the pin that would fail if anyone re-implemented the placement anywhere else.</item>
    /// <item>The gate is "the mapper subtimed this word", not "this word has two syllables". A word
    /// the SYLLABIFIER happily splits gets groups, a highlight and span judgement, and NO marks.
    /// That asymmetry is deliberate: the mark says a human authored a subdivision here.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class SyllableMarkerTest
    {
        #region the derivation

        /// <summary>
        /// The derived (un-authored) arm: "banana" over 1000..1600 with boundaries at 1200 and 1400
        /// is cut evenly in index space, so the marks land on the same cells the existing groups
        /// pin (0,2)(2,4)(4,6) start at. Written out longhand, because a change to the even spread
        /// must fail here rather than quietly move every mark on every subtimed map.
        /// </summary>
        [Test]
        public void DerivedSplitMarksTheEvenCut()
        {
            var line = subtimedLine("banana", 1000, 1600, Array.Empty<int>(), 1200, 1400);

            Assert.That(line.SyllableMarkerCells, Is.EqualTo(new[] { 2, 4 }));
            assertMarksSitOnGroupEdges(line);
        }

        /// <summary>
        /// The authored arm ("ban|a|na"): the mapper's own cut moves the marks with the groups and
        /// the targets, since all three come from the one <c>SplitsFor</c> call.
        /// </summary>
        [Test]
        public void AuthoredSplitMarksTheAuthoredCut()
        {
            var line = subtimedLine("banana", 1000, 1600, new[] { 3, 4 }, 1200, 1400);

            Assert.That(line.SyllableMarkerCells, Is.EqualTo(new[] { 3, 4 }));
            assertMarksSitOnGroupEdges(line);

            // On an AUTHORED word the marked cell is also the one timed AT the boundary, so the
            // triangle sits exactly where the vocal turns over.
            Assert.That(line.Cells[3].TargetTime, Is.EqualTo(1200).Within(1e-9));
            Assert.That(line.Cells[4].TargetTime, Is.EqualTo(1400).Within(1e-9));
        }

        /// <summary>
        /// THE GATE. A word with no <see cref="TimedUnit.SyllableBoundaries"/> gets no marks at all,
        /// even though the syllabifier splits it happily and the engine really does group and light
        /// it. Nothing about that word's rendering changes from before the feature.
        /// </summary>
        [Test]
        public void AWordWithNoBoundariesIsNeverMarked()
        {
            var line = TypingLine.FromLyricLine(
                lineOf("banana", 1000, 2000, unit("banana", 1000, 1600, Array.Empty<int>())),
                TimingGranularity.Syllable);

            Assert.That(Syllabifier.IsSyllabifiable("banana"), Is.True, "the syllabifier would split it");
            Assert.That(line.Syllables.Count, Is.GreaterThan(1), "and the engine does group it");
            Assert.That(line.SyllableMarkerCells, Is.Empty, "but nobody authored those boundaries");
        }

        /// <summary>
        /// One line, one subtimed word and one that is not: the marks appear only inside the word
        /// the mapper subdivided, and the word beside it renders exactly as it always has.
        /// </summary>
        [Test]
        public void OnlyTheSubtimedWordOfALineIsMarked()
        {
            var line = TypingLine.FromLyricLine(
                lineOf("banana orange", 1000, 2200,
                    unit("banana", 1000, 1600, Array.Empty<int>(), 1200, 1400),
                    unit("orange", 1700, 2100, Array.Empty<int>())),
                TimingGranularity.Syllable);

            Assert.That(line.DisplayText, Is.EqualTo("banana orange"));
            Assert.That(line.SyllableMarkerCells, Is.EqualTo(new[] { 2, 4 }), "nothing past the space cell at 6");
            assertMarksSitOnGroupEdges(line);
        }

        /// <summary>
        /// A STYLISED subtimed word is still marked. The syllabifier refuses to analyse "heyyyyy",
        /// so an unsubtimed one gets no groups and no marks, but a mapper who hand-authored the
        /// subdivision overrules that gate (backlog 174's rule) and the marks follow the groups.
        /// </summary>
        [Test]
        public void AStylisedWordIsMarkedOnlyWhenTheMapperSubtimedIt()
        {
            Assert.That(Syllabifier.IsSyllabifiable("heyyyyy"), Is.False, "the fixture must be stylised");

            var plain = TypingLine.FromLyricLine(
                lineOf("heyyyyy", 1000, 2000, unit("heyyyyy", 1000, 1600, Array.Empty<int>())),
                TimingGranularity.Syllable);

            Assert.That(plain.Syllables, Is.Empty, "ungrouped, so there is nothing to mark");
            Assert.That(plain.SyllableMarkerCells, Is.Empty);

            var subtimed = subtimedLine("heyyyyy", 1000, 1600, new[] { 3 }, 1300);

            Assert.That(subtimed.SyllableMarkerCells, Is.EqualTo(new[] { 3 }));
            assertMarksSitOnGroupEdges(subtimed);
        }

        /// <summary>
        /// An OVER-FORCED short word degrades instead of throwing: the syllabifier cannot cut "ab"
        /// into four segments, so fewer groups (and therefore fewer marks) come back than there are
        /// boundaries. The mark count is bounded by the boundary count and is never equal to it by
        /// contract, which is why nothing here asserts equality.
        /// </summary>
        [Test]
        public void AnOverForcedShortWordDegradesToFewerMarks()
        {
            var line = subtimedLine("ab", 1000, 1600, Array.Empty<int>(), 1150, 1300, 1450);

            Assert.That(line.SyllableMarkerCells.Count, Is.LessThan(3), "three boundaries do not fit in two letters");
            Assert.That(line.SyllableMarkerCells, Is.EqualTo(new[] { 1 }));
            assertMarksSitOnGroupEdges(line);
        }

        /// <summary>
        /// A one-syllable subtimed word is a contradiction the data can express (an empty boundary
        /// list) and it produces nothing, as does a word too short to cut at all.
        /// </summary>
        [Test]
        public void NothingToCutMeansNothingToMark()
        {
            var single = subtimedLine("a", 1000, 1600, Array.Empty<int>(), 1300);

            Assert.That(single.SyllableMarkerCells, Is.Empty, "a one-letter word cannot be split");
            Assert.That(TypingLine.FromLyricLine(lineOf("", 1000, 2000), TimingGranularity.Syllable).SyllableMarkerCells, Is.Empty);
        }

        /// <summary>
        /// THE HONESTY PIN, stated over the whole corpus rather than one fixture: every marked cell
        /// opens a different judgement group from the cell to its left. A mark can therefore never
        /// point at a place the engine does not treat as a syllable boundary, which is the entire
        /// reason the cells are derived beside the groups instead of in the renderer.
        /// </summary>
        [Test]
        public void EveryMarkSitsOnAJudgementGroupEdge()
        {
            foreach (var line in corpus())
                assertMarksSitOnGroupEdges(line);
        }

        #endregion

        #region the two projections that could get it wrong

        /// <summary>
        /// The LITERATE mod gives every authored character its own cell, so a mark inside a word
        /// carrying punctuation lands on a DIFFERENT cell index than it does in the default stream,
        /// off the SAME authored boundary. "don'|t" is the case: the default stream drops the
        /// apostrophe and the mark falls on cell 3, literate keeps it and the mark falls on cell 4.
        /// Both are the first cell of the second syllable, which is what a mark means.
        ///
        /// <para>This works only because the derivation runs inside <c>FromLyricLine</c>, after the
        /// stream has been chosen. Anything recomputing cell indices later would mark the wrong
        /// character here.</para>
        /// </summary>
        [Test]
        public void LiterateMarksTheSameBoundaryAtItsOwnCellIndex()
        {
            var source = lineOf("don't", 1000, 2000, unit("don't", 1000, 1600, new[] { 4 }, 1400));

            var normal = TypingLine.FromLyricLine(source, TimingGranularity.Syllable);
            var literate = TypingLine.FromLyricLine(source, TimingGranularity.Syllable, literate: true);

            Assert.That(normal.DisplayText, Is.EqualTo("dont"));
            Assert.That(literate.DisplayText, Is.EqualTo("don't"));

            Assert.That(normal.SyllableMarkerCells, Is.EqualTo(new[] { 3 }));
            Assert.That(literate.SyllableMarkerCells, Is.EqualTo(new[] { 4 }));

            // Same character on both sides of the divide, and the same authored boundary behind it.
            Assert.That(normal.Cells[3].Expected, Is.EqualTo('t'));
            Assert.That(literate.Cells[4].Expected, Is.EqualTo('t'));

            assertMarksSitOnGroupEdges(normal);
            assertMarksSitOnGroupEdges(literate);
        }

        /// <summary>
        /// A HYPHENATED word is the case that makes the derivation's home load-bearing. The default
        /// stream turns the hyphen into a typed SPACE cell, so "well-known song" is two units but
        /// three space-separated runs of cells: a renderer counting gaps to find word 0 would mark
        /// the wrong word from here on. The mark lands inside the one subtimed unit regardless.
        /// </summary>
        [Test]
        public void AHyphenatedWordIsStillOneUnitWithOneMark()
        {
            var line = TypingLine.FromLyricLine(
                lineOf("well-known song", 1000, 2400,
                    unit("well-known", 1000, 1800, new[] { 5 }, 1400),
                    unit("song", 1900, 2300, Array.Empty<int>())),
                TimingGranularity.Syllable);

            Assert.That(line.DisplayText, Is.EqualTo("well known song"), "the hyphen is typed as a space");
            Assert.That(line.SyllableMarkerCells, Is.EqualTo(new[] { 5 }), "the 'k' of known, inside unit 0");
            Assert.That(line.Cells[5].Expected, Is.EqualTo('k'));
            assertMarksSitOnGroupEdges(line);
        }

        /// <summary>
        /// A syllable that owns NO cell is dropped by compaction (here the middle segment of
        /// "a|-|b", whose only character the default stream turns into an untyped-through space),
        /// and the mark that would have opened it goes with it. The syllable AFTER the hole keeps
        /// its mark, because what a mark needs is something of the same word rendered to its left,
        /// not specifically the syllable immediately before it.
        /// </summary>
        [Test]
        public void ASyllableOwningNoCellIsSkippedWithoutLosingTheOnesAroundIt()
        {
            var line = subtimedLine("a-b", 1000, 1600, new[] { 1, 2 }, 1200, 1400);

            Assert.That(line.DisplayText, Is.EqualTo("a b"));
            Assert.That(line.Syllables.Count, Is.EqualTo(2), "the lone-hyphen syllable owns no cell");
            Assert.That(line.SyllableMarkerCells, Is.EqualTo(new[] { 2 }), "one mark, at the 'b'");
            assertMarksSitOnGroupEdges(line);
        }

        /// <summary>A mark is never drawn at a line's leading edge or past its last cell, whatever
        /// the data does; the renderer would have nowhere to put it.</summary>
        [Test]
        public void MarksAlwaysLandInAnInteriorGap()
        {
            foreach (var line in corpus())
                Assert.That(line.SyllableMarkerCells, Is.All.InRange(1, line.Cells.Count - 1));
        }

        #endregion

        #region the marks are a by-product and move nothing

        /// <summary>
        /// ADDITIVE, and pinned as such: adding the marks moved no target, no group and no span. The
        /// numbers here are the ones <c>SyllableSplitTest.UnauthoredWordIsUnchanged</c> already
        /// pins, restated so this fixture fails too if the derivation ever starts writing back.
        /// </summary>
        [Test]
        public void TheMarksChangeNoTargetAndNoGroup()
        {
            var line = subtimedLine("banana", 1000, 1600, Array.Empty<int>(), 1200, 1400);

            Assert.That(line.Cells.Select(c => c.TargetTime), Is.EqualTo(new[] { 1000d, 1100, 1200, 1300, 1400, 1500 }).Within(1e-9));
            Assert.That(line.Syllables, Is.EqualTo(new[]
            {
                new SyllableGroup(0, 2, 1000, 1200),
                new SyllableGroup(2, 4, 1200, 1400),
                new SyllableGroup(4, 6, 1400, 1600),
            }));
        }

        #endregion

        #region the drawn geometry

        /// <summary>
        /// The one geometry rule, pure so it needs no drawable: a mark hangs from the BOTTOM of the
        /// glyph row and clears the sung sweep rail below it. That band is what makes it read as
        /// typography sitting under the word rather than as a widget drawn over the line, and the
        /// clearance is what stops it merging into the rail at large font sizes (the band is
        /// absolute, the mark scales, so the clamp is the binding rule up there).
        /// </summary>
        [TestCase(1f)]
        [TestCase(12f)]
        [TestCase(42f)]
        [TestCase(64f)]
        [TestCase(400f)]
        public void TheMarkHangsFromTheBaselineAndClearsTheRail(float glyphHeight)
        {
            var (top, width, height) = LyricLineDisplay.SyllableMarkerGeometry(glyphHeight);

            Assert.That(top, Is.EqualTo(glyphHeight), "hung from the bottom of the glyph row");
            Assert.That(height, Is.GreaterThanOrEqualTo(1f), "never sub-pixel");
            Assert.That(top + height, Is.LessThanOrEqualTo(glyphHeight + LyricLineDisplay.SWEEP_RAIL_OFFSET - 1f),
                "a clear pixel above the sweep rail");
            Assert.That(width, Is.GreaterThan(height), "wider than tall: a wedge, not an arrowhead");
        }

        /// <summary>Below the clamp the mark tracks the font size, which is what keeps it looking
        /// the same size relative to the lyric at every scale.</summary>
        [Test]
        public void TheMarkScalesWithTheGlyphRowUntilItMeetsTheRail()
        {
            Assert.That(LyricLineDisplay.SyllableMarkerGeometry(30f).Height,
                Is.EqualTo(30f * LyricLineDisplay.SYLLABLE_MARKER_HEIGHT).Within(1e-6));

            Assert.That(LyricLineDisplay.SyllableMarkerGeometry(4000f).Height,
                Is.EqualTo(LyricLineDisplay.SWEEP_RAIL_OFFSET - 1f).Within(1e-6), "clamped, never through the rail");
        }

        #endregion

        #region helpers

        /// <summary>Every marked cell opens a different judgement group from the cell to its left,
        /// and is itself in a group.</summary>
        private static void assertMarksSitOnGroupEdges(TypingLine line)
        {
            foreach (int i in line.SyllableMarkerCells)
            {
                Assert.That(i, Is.InRange(1, line.Cells.Count - 1), "a mark sits in an interior gap");
                Assert.That(line.SyllableIndexOf(i), Is.GreaterThanOrEqualTo(0), $"cell {i} is in a group");
                Assert.That(line.SyllableIndexOf(i), Is.Not.EqualTo(line.SyllableIndexOf(i - 1)),
                    $"cell {i} must open a different syllable from cell {i - 1}");
                Assert.That(line.Syllables[line.SyllableIndexOf(i)].StartCell, Is.EqualTo(i),
                    $"cell {i} must be the FIRST cell of its syllable");
            }

            Assert.That(line.SyllableMarkerCells, Is.Ordered.Ascending);
            Assert.That(line.SyllableMarkerCells.Distinct().Count(), Is.EqualTo(line.SyllableMarkerCells.Count));
        }

        /// <summary>The lines the whole-corpus pins run over: both split arms, both streams, a
        /// stylised word, a degraded one, punctuation and a hyphen.</summary>
        private static IEnumerable<TypingLine> corpus()
        {
            yield return subtimedLine("banana", 1000, 1600, Array.Empty<int>(), 1200, 1400);
            yield return subtimedLine("banana", 1000, 1600, new[] { 3, 4 }, 1200, 1400);
            yield return subtimedLine("beautiful", 1000, 1900, Array.Empty<int>(), 1450);
            yield return subtimedLine("heyyyyy", 1000, 1600, new[] { 3 }, 1300);
            yield return subtimedLine("ab", 1000, 1600, Array.Empty<int>(), 1150, 1300, 1450);
            yield return subtimedLine("a-b", 1000, 1600, new[] { 1, 2 }, 1200, 1400);

            var punctuated = lineOf("don't", 1000, 2000, unit("don't", 1000, 1600, new[] { 4 }, 1400));
            yield return TypingLine.FromLyricLine(punctuated, TimingGranularity.Syllable);
            yield return TypingLine.FromLyricLine(punctuated, TimingGranularity.Syllable, literate: true);

            yield return TypingLine.FromLyricLine(
                lineOf("well-known song", 1000, 2400,
                    unit("well-known", 1000, 1800, new[] { 5 }, 1400),
                    unit("song", 1900, 2300, Array.Empty<int>())),
                TimingGranularity.Syllable);

            yield return TypingLine.FromLyricLine(
                lineOf("banana orange", 1000, 2200,
                    unit("banana", 1000, 1600, Array.Empty<int>(), 1200, 1400),
                    unit("orange", 1700, 2100, Array.Empty<int>())),
                TimingGranularity.Syllable);
        }

        private static TimedUnit unit(string text, double start, double end, IReadOnlyList<int> splits, params double[] boundaries)
            => new TimedUnit
            {
                Text = text,
                StartTime = start,
                EndTime = end,
                Source = TimingSource.Explicit,
                SyllableBoundaries = boundaries,
                SyllableSplits = splits,
            };

        private static LyricLine lineOf(string text, double start, double end, params TimedUnit[] units)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = units.Length > 0 ? units[^1].EndTime : end,
                Units = units,
            };

        private static TypingLine subtimedLine(string word, double start, double end, IReadOnlyList<int> splits, params double[] boundaries)
            => TypingLine.FromLyricLine(lineOf(word, start, end + 400, unit(word, start, end, splits, boundaries)), TimingGranularity.Syllable);

        #endregion
    }
}
