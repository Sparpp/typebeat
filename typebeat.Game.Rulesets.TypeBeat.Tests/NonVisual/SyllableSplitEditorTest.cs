// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// AUTHORING a syllable split in the editor (backlog 181). Two surfaces do it and both read the
    /// same derivation:
    ///
    /// <list type="bullet">
    /// <item>the LINE BOX, where a subdivided word shows as "ap|ple" and the mapper moves the pipe.
    /// The pipe is a reserved character of that surface only: it is stripped on commit, it never
    /// reaches a stored lyric, and <see cref="Typeability.Normalize"/> drops it on every other
    /// path.</item>
    /// <item>the TIMELINE strip, where the same cut decides which characters print on either side
    /// of each dotted line.</item>
    /// </list>
    ///
    /// <para>The other half of the fixture is the op sweep: every operation that changes a word's
    /// TEXT or its BOUNDARY COUNT has to keep the split valid or drop it to derived, because a
    /// stale char index would silently re-cut the word rather than fail.</para>
    /// </summary>
    [TestFixture]
    public class SyllableSplitEditorTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        #region The line box: display and the pipe matrix

        [Test]
        public void PipeDisplayShowsTheEffectiveSplit()
        {
            var beatmap = createBeatmap();

            // Word 0 is subdivided (derived), word 1 is not subdivided at all, so only the first
            // carries a pipe.
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("ap|ple orange"),
                "a derived split is shown as readily as an authored one: it is what gameplay groups on");

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, lineAt(beatmap, 0), 0, 0, 3);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("app|le orange"));

            // Two boundaries, two pipes.
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 1).Line), Is.EqualTo("ba|na|na"));

            // A line with no subdivision anywhere is its own stored text, character for character.
            var plain = new LyricLine
            {
                RawText = "apple orange",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 2600,
                Units = new[] { newUnit("apple", 1000, 1400), newUnit("orange", 1500, 2600) },
            };

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(plain), Is.EqualTo("apple orange"));
        }

        [Test]
        public void PipeDisplayIsTheStoredTextWhenTokensAndUnitsDisagree()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            line.Line = new LyricLine
            {
                RawText = "apple orange extra",
                StartTime = line.Line.StartTime,
                EndTime = line.Line.EndTime,
                SingEndTime = line.Line.SingEndTime,
                Units = line.Line.Units,
            };

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("apple orange extra"));
        }

        [Test]
        public void PipeAuthorsTheSplit()
        {
            var beatmap = createBeatmap();

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "app|le orange"), Is.True);

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.Text, Is.EqualTo("apple"), "the pipe never reaches the stored lyric");
            Assert.That(lineAt(beatmap, 0).Line.RawText, Is.EqualTo("apple orange"));
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1300d }), "the TIMES are untouched by a text commit");
        }

        [Test]
        public void CommittingTheDisplayedTextChangesNothing()
        {
            var beatmap = createBeatmap();
            var before = lineAt(beatmap, 0).Line;

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), TypeBeatEditorOperations.PipeDisplayText(before)), Is.True);

            // Identity, not merely equality: a no-op commit must not rebuild the line, and above all
            // must not PIN the derived split into split_chars, which would retime the word.
            Assert.That(lineAt(beatmap, 0).Line, Is.SameAs(before));
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableSplits, Is.Empty);
        }

        [Test]
        public void MovingAPipeBackOntoTheDerivedSplitStaysDerived()
        {
            var beatmap = createBeatmap();

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "app|le orange"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 3 }));

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "ap|ple orange"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableSplits, Is.Empty, "back on the derived cut, so nothing is stored");
        }

        [Test]
        public void PipesOnAWordWithNoSubdivisionsAuthorThem()
        {
            var beatmap = createBeatmap();

            // "orange" (1500..2600) has no subdivision at all; two pipes ask for three segments,
            // and there is nothing else to say where they sit, so the word's span is cut evenly.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "ap|ple or|an|ge"), Is.True);

            var units = lineAt(beatmap, 0).Line.Units;
            Assert.That(lineAt(beatmap, 0).Line.RawText, Is.EqualTo("apple orange"), "the pipes never reach the stored lyric");

            Assert.That(units[1].SyllableSplits, Is.EqualTo(new[] { 2, 4 }));
            Assert.That(units[1].SyllableBoundaries.Count, Is.EqualTo(2));
            Assert.That(units[1].SyllableBoundaries[0], Is.EqualTo(1500 + 1100 / 3.0).Within(1e-6));
            Assert.That(units[1].SyllableBoundaries[1], Is.EqualTo(1500 + 2 * 1100 / 3.0).Within(1e-6));
            Assert.That(units[1].Source, Is.EqualTo(TimingSource.Explicit), "a hand-placed cut is hand timing");

            Assert.That(segmentTexts(units[1]), Is.EqualTo(new[] { "or", "an", "ge" }));

            // The word that already had a boundary is untouched by the same commit.
            Assert.That(units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300d }));
        }

        [Test]
        public void AnAuthoringPipePromotesALineGranularityMap()
        {
            // A Line map persists no word data, so the encoder would drop a subdivision that did
            // not come with a granularity promotion. It also re-derives its units from the text,
            // which is why the commit cannot early-out on "the stripped text did not change".
            var beatmap = lineGranularityBeatmap();
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "fri|ed rice"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("fried rice"));
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Syllable));

            var unit = line.Line.Units[0];
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(unit.SyllableBoundaries.Count, Is.EqualTo(1));
            Assert.That(unit.SyllableBoundaries[0], Is.EqualTo((unit.StartTime + unit.EndTime) / 2).Within(1e-6));

            // And it survives the save it would otherwise have been dropped by.
            var reopened = SyllableSplitTest.DecodeOsu(encode(beatmap));
            Assert.That(reopened[0].Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(reopened[0].Line), Is.EqualTo("fri|ed rice"));
        }

        [Test]
        public void AnIllegalAuthoringPipeStillAuthorsNothing()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            // A pipe at the very end of "orange" would leave an empty last segment.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "apple orange|"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("apple orange"));
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.Empty);
            Assert.That(line.Line.Units[1].SyllableSplits, Is.Empty);
        }

        [Test]
        public void AnOrdinaryTextCommitDoesNotMoveGranularity()
        {
            // The promotion is keyed on a pipe having AUTHORED something, so retyping a line on a
            // Line map (the common case) leaves the map exactly where it was.
            var beatmap = lineGranularityBeatmap();
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "boiled rice"), Is.True);
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Line));
        }

        [Test]
        public void SurplusPipesAreDropped()
        {
            var beatmap = createBeatmap();

            // One boundary wants ONE split; the second pipe has no segment to open.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "a|pp|le orange"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1 }));
        }

        /// <summary>
        /// The forgiving rule, narrowed by backlog 204 to a NONZERO shortfall: some pipe is still
        /// there, so the commit is read as "these cuts moved", not as "this word is not subdivided".
        /// Deleting them ALL is the one case that removes (see the region below).
        /// </summary>
        [Test]
        public void FewerButNonzeroPipesThanBoundariesKeepsTheRestWhereItWas()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            // Two boundaries on "banana" (line 1): derived "ba|na|na".
            var second = lineAt(beatmap, 1);
            TypeBeatEditorOperations.SetSyllableSplit(beatmap, second, 0, 1, 5);
            Assert.That(second.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 2, 5 }));

            // One pipe given: it takes the FIRST split, the second keeps the value it showed, and
            // BOTH boundaries stay: a text commit never changes a boundary count downwards here.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, second, "b|anana"), Is.True);
            Assert.That(second.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 5 }));
            Assert.That(second.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3200d, 3400 }));

            Assert.That(line.Line.RawText, Is.EqualTo("apple orange"), "the other line is untouched");
        }

        [TestCase("|apple orange", TestName = "PipeRefused_LeadingEmptiesFirstSegment")]
        [TestCase("apple| orange", TestName = "PipeRefused_TrailingEmptiesLastSegment")]
        public void APipeThatEmptiesASegmentKeepsThePreviousSplit(string typed)
        {
            var beatmap = createBeatmap();

            // Author a split first, so "keeps the previous" is visible rather than vacuous.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "app|le orange"), Is.True);
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), typed), Is.True);

            Assert.That(lineAt(beatmap, 0).Line.RawText, Is.EqualTo("apple orange"));
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void ATokenOfNothingButPipesDisappears()
        {
            var beatmap = createBeatmap();

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "apple | orange"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.RawText, Is.EqualTo("apple orange"), "no empty word is created");
        }

        [Test]
        public void ALineOfNothingButPipesIsRefused()
        {
            var beatmap = createBeatmap();
            var before = lineAt(beatmap, 0).Line;

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "| ||"), Is.False);
            Assert.That(lineAt(beatmap, 0).Line, Is.SameAs(before));
        }

        [Test]
        public void RetypingAWordDropsASplitItNoLongerFits()
        {
            var beatmap = createBeatmap();

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "app|le orange"), Is.True);

            // Same token count, shorter word: index 3 is now past the end, so the word derives again.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "ape orange"), Is.True);

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.Text, Is.EqualTo("ape"));
            Assert.That(unit.SyllableSplits, Is.Empty);
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1300d }), "the subdivision TIME survives a rename");
        }

        /// <summary>
        /// Inverted by backlog 204: a word count change used to throw every subdivision away, on the
        /// grounds that no per-word mapping existed. One does now, the conservative one: a word that
        /// came back spelled EXACTLY as it was is the same word, and only the changed words re-derive.
        /// </summary>
        [Test]
        public void ChangingTheWordCountDropsOnlyTheChangedWords()
        {
            var beatmap = createBeatmap();

            // "apple" [1000, 1400] carries its boundary at 1300, three quarters of the way through.
            // Appending "plum" redistributes every span, but "apple" is still "apple".
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "app|le orange plum"), Is.True);

            var units = lineAt(beatmap, 0).Line.Units;
            Assert.That(units.Count, Is.EqualTo(3));

            Assert.That(units[0].SyllableBoundaries.Count, Is.EqualTo(1));
            Assert.That(units[0].SyllableBoundaries[0],
                Is.EqualTo(units[0].StartTime + (units[0].EndTime - units[0].StartTime) * 0.75).Within(1e-6),
                "the boundary keeps its RELATIVE position in the word it belongs to");
            Assert.That(units[0].SyllableSplits, Is.EqualTo(new[] { 3 }), "and this commit's own pipe is read over it");

            Assert.That(units[1].SyllableBoundaries, Is.Empty, "\"orange\" never had one");
            Assert.That(units[2].SyllableBoundaries, Is.Empty, "\"plum\" is a word that did not exist");
        }

        [Test]
        public void ChangingTheWordCountDropsAReWordedWordsSubdivision()
        {
            var beatmap = createBeatmap();

            // "apple" is gone AND the word count moved, so nothing anchors the subdivision: there
            // is no honest place to put the syllables of a word that no longer exists.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "pear orange plum"), Is.True);

            var units = lineAt(beatmap, 0).Line.Units;
            Assert.That(units.Count, Is.EqualTo(3));
            Assert.That(units.All(u => u.SyllableBoundaries.Count == 0 && u.SyllableSplits.Count == 0), Is.True);
        }

        [Test]
        public void DeletingAWordFromTheTextKeepsTheOtherWordsSubdivisions()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "ap|ple or|ange"), Is.True);

            // "orange" is typed away; "apple" is the same word and keeps its cut through the
            // redistribution that follows.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "ap|ple"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("apple"));
            Assert.That(line.Line.Units, Has.Count.EqualTo(1));
            Assert.That(line.Line.Units[0].SyllableBoundaries.Count, Is.EqualTo(1));

            // The kept boundary is the RESCALED old one (three quarters through the word), not the
            // even halving a fresh authoring pipe would have produced.
            var kept = line.Line.Units[0];
            Assert.That(kept.SyllableBoundaries[0],
                Is.EqualTo(kept.StartTime + (kept.EndTime - kept.StartTime) * 0.75).Within(1e-6));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("ap|ple"));
        }

        [Test]
        public void ThePipeIsAnAuthoringMarkAndNeverALyricChar()
        {
            // It is not a character of the game's text surface, so nothing can ever type one, and
            // the normalizer only lets it through for the callers that read it as a mark: the
            // editor's line box, the LRC importer and the aligner's display text (backlog 202).
            Assert.That(Typeability.Normalize("ap|ple"), Is.EqualTo("apple"));
            Assert.That(Typeability.Normalize("ap|ple", keepFreestyleMarkers: true), Is.EqualTo("apple"));
            Assert.That(Typeability.Normalize("ap|ple", keepSplitMarkers: true), Is.EqualTo("ap|ple"));
            Assert.That(Typeability.IsTypeable(Typeability.SPLIT_MARKER), Is.False);
            Assert.That(Typeability.IsPunctuation(Typeability.SPLIT_MARKER), Is.False);

            // Every one of those readers strips it once the position is read, so no stored lyric
            // carries one, from the editor...
            var beatmap = createBeatmap();
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "ap|ple or|ange"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.RawText, Is.EqualTo("apple orange"));

            // ...or from LRC import, which authors the subdivision the pipe asked for.
            var imported = LrcParser.Parse("[00:01.00]ap|ple\n[00:03.00]\n");
            Assert.That(imported[0].RawText, Is.EqualTo("apple"));
            Assert.That(imported[0].Units[0].SyllableSplits, Is.EqualTo(new[] { 2 }));

            // The OTHER text-authoring op does not read pipes at all: a word is one token, and
            // there is no timing to divide until it exists.
            Assert.That(TypeBeatEditorOperations.AddWord(beatmap, lineAt(beatmap, 0), 0, "wo|rd"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.RawText, Is.EqualTo("apple word orange"));
        }

        #endregion

        #region Deleting the pipe removes the subdivision (backlog 204)

        [Test]
        public void DeletingAWordsLastPipeRemovesItsSubdivision()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            // The box shows "ap|ple orange"; the mapper deletes the pipe and commits. Before 204
            // the fewer-pipes rule put the boundary (and therefore the pipe) straight back, so a
            // subdivision could not be undone from the line box at all.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "apple orange"), Is.True);

            var unit = line.Line.Units[0];
            Assert.That(unit.SyllableBoundaries, Is.Empty);
            Assert.That(unit.SyllableSplits, Is.Empty);
            Assert.That(unit.StartTime, Is.EqualTo(1000), "the word keeps its own span");
            Assert.That(unit.EndTime, Is.EqualTo(1400));
            Assert.That(unit.Source, Is.EqualTo(TimingSource.Explicit), "un-subdividing is hand timing too");
            Assert.That(unit.Confidence, Is.EqualTo(1));

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("apple orange"),
                "and the box does not put the pipe back on the next frame");
        }

        [Test]
        public void APipeOnlyDeletionIsNotSwallowedByTheNoOpEarlyOut()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            var before = line.Line;

            // The STRIPPED text is "apple orange" either way, so the shrunken pipe set is the whole
            // edit. Identity, not equality: the early-out has to let this one through.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "apple orange"), Is.True);

            Assert.That(line.Line, Is.Not.SameAs(before), "the line was rebuilt");
            Assert.That(line.Line.RawText, Is.EqualTo(before.RawText));
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.Empty);
        }

        [Test]
        public void RemovingOnlyOneWordsPipesLeavesEveryOtherWordSubdivided()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            // Both words of line 0 subdivided: "ap|ple or|ange".
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "ap|ple or|ange"), Is.True);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("ap|ple or|ange"));

            double[] orangeBoundaries = line.Line.Units[1].SyllableBoundaries.ToArray();

            // Only the FIRST word's pipe is deleted.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "apple or|ange"), Is.True);

            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.Empty);
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.EqualTo(orangeBoundaries), "the neighbour is untouched");
            Assert.That(line.Line.Units[1].SyllableSplits, Is.EqualTo(new[] { 2 }));
            Assert.That(lineAt(beatmap, 1).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3200d, 3400 }),
                "and so is the rest of the map");

            // The map is left at Syllable granularity (words[] is still written), and the removal
            // survives the save it has to survive.
            var reopened = SyllableSplitTest.DecodeOsu(encode(beatmap));

            Assert.That(reopened[0].Line.Units[0].SyllableBoundaries, Is.Empty);
            Assert.That(reopened[0].Line.Units[1].SyllableBoundaries, Is.EqualTo(orangeBoundaries).Within(1e-6));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(reopened[0].Line), Is.EqualTo("apple or|ange"));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(reopened[1].Line), Is.EqualTo("ba|na|na"));
        }

        [Test]
        public void DeletingEveryPipeOfALineUnsubdividesEveryWordOfIt()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "ap|ple or|ange"), Is.True);
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "apple orange"), Is.True);

            Assert.That(line.Line.Units.All(u => u.SyllableBoundaries.Count == 0 && u.SyllableSplits.Count == 0), Is.True);

            // No demotion is required: the encoder writes no syllables[] for a boundary-free unit,
            // and demoting could cost the map its words[] instead.
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Syllable));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(SyllableSplitTest.DecodeOsu(encode(beatmap))[0].Line),
                Is.EqualTo("apple orange"));
        }

        [Test]
        public void ARetypedWordKeepsItsSubdivisionTimesEvenWithNoPipe()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            // The removal rule reads a DELETED pipe, which is only unambiguous on a word the mapper
            // left spelled as it was. "ape" is a different word, so the older forgiving rule stands:
            // the char split goes (it no longer fits), the boundary TIME survives.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "ape orange"), Is.True);

            Assert.That(line.Line.Units[0].Text, Is.EqualTo("ape"));
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300d }));
        }

        #endregion

        #region Unsubdivide: the inverse of the subdivide press

        [Test]
        public void UnsubdivideMergesTheNarrowestAdjacentPair()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana" [3000, 3600], boundaries 3200 / 3400

            // Segments 200 / 350 / 50. Removing the SECOND boundary merges 350 + 50 = 400, the
            // narrowest pair; removing the first would merge 200 + 350 = 550.
            TypeBeatEditorOperations.SetSyllableBoundary(beatmap, line, 0, 1, 3550);

            Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, 0), Is.True);
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3200d }));
        }

        [Test]
        public void SubdivideThenUnsubdivideGivesTheWordBack()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0); // word 1 is "orange" [1500, 2600], not subdivided

            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 1), Is.EqualTo(2050));
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 1), Is.EqualTo(1775));

            // Each press bisected the widest segment, so each un-press merges the narrowest pair,
            // which is the pair the last press created: exact inverses, in order.
            Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, 1), Is.True);
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 2050d }));

            Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, 1), Is.True);
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.Empty, "a one-boundary word returns to plain");
            Assert.That(line.Line.Units[1].SyllableSplits, Is.Empty);
        }

        #region The caret decides where a fresh subdivision lands

        [Test]
        public void SubdivideLandsOnTheCaretWhenItIsInsideTheWord()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0); // "orange" is [1500, 2600]

            // Without a caret this word bisects (2050). With one inside it, the divider goes exactly
            // where the mapper is pointing instead.
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 1, 2100), Is.EqualTo(2100));
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 2100d }));

            // And again on the next press, wherever the song has moved to by then: a mapper cutting
            // as they listen never has to drag the line afterwards.
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 1, 2400), Is.EqualTo(2400));
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 2100d, 2400 }));
        }

        [Test]
        public void SubdivideBisectsWhenTheCaretIsOutsideTheWord()
        {
            // Before the word, after it, and nowhere near the map at all: every one of them is
            // "not inside the word", so the widest segment is bisected exactly as it always was.
            foreach (double caret in new[] { 900d, 1499d, 2601d, 9999d })
            {
                var beatmap = createBeatmap();
                var line = lineAt(beatmap, 0);

                Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 1, caret), Is.EqualTo(2050),
                    $"a caret at {caret} is outside \"orange\" [1500, 2600]");
            }

            // A caller with no caret to offer (the three-argument call, and anything without a clock)
            // behaves the same way.
            var noCaret = createBeatmap();
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(noCaret, lineAt(noCaret, 0), 1), Is.EqualTo(2050));

            var nullCaret = createBeatmap();
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(nullCaret, lineAt(nullCaret, 0), 1, null), Is.EqualTo(2050));
        }

        [Test]
        public void SubdivideBisectsWhenTheCaretCannotCutALegalSegment()
        {
            // Inside the word but within MIN_SYLLABLE_MS of one of its edges: two legal segments do
            // not fit, so the press falls back rather than refusing.
            foreach (double caret in new[] { 1500d, 1519d, 2581d, 2600d })
            {
                var beatmap = createBeatmap();

                Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, lineAt(beatmap, 0), 1, caret), Is.EqualTo(2050),
                    $"a caret at {caret} sits too close to an edge of \"orange\" [1500, 2600]");
            }

            // A caret exactly ON an existing boundary is inside no segment at all ("apple" carries
            // one at 1300), so that press bisects the widest one as before.
            var onBoundary = createBeatmap();
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(onBoundary, lineAt(onBoundary, 0), 0, 1300), Is.EqualTo(1150));
        }

        [Test]
        public void SubdivideOnTheCaretCutsTheSegmentTheCaretLandedIn()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0); // "apple" [1000, 1400], divided once at 1300

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 3); // "app|le"

            // A caret in the SECOND segment ("le", characters 3..5) cuts there: "app|l|e". The old
            // bisect cuts the WIDEST segment instead, which is segment 0 here ("a|pp|le"), so the
            // characters follow the caret just as the time does.
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0, 1320), Is.EqualTo(1320));
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300d, 1320 }));
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 3, 4 }));
            Assert.That(segmentTexts(line.Line.Units[0]), Is.EqualTo(new[] { "app", "l", "e" }));
        }

        [Test]
        public void OnlyTheWordUnderTheCaretTakesTheCaret()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0); // "apple" [1000, 1400] (divided at 1300), "orange" [1500, 2600]

            // A multi-selection runs the op word by word with ONE caret, so the words the caret is
            // not over keep their old behaviour: "apple" bisects, "orange" takes the caret.
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0, 2100), Is.EqualTo(1150));
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 1, 2100), Is.EqualTo(2100));
        }

        #endregion

        [Test]
        public void UnsubdivideIsANoOpOnAWordWithNoSubdivision()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            var before = line.Line;

            Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, 1), Is.False,
                "\"orange\" is not subdivided");
            Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, 9), Is.False, "out of range");
            Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, -1), Is.False);

            Assert.That(line.Line, Is.SameAs(before));
        }

        [Test]
        public void UnsubdividingAMultiWordSelectionIsOneUndoStep()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "ap|ple or|ange"), Is.True);

            // What the panel's button does: one outer transaction over the selection, each op's own
            // nested transaction ref-counted inside it, so the whole press is a single undo.
            beatmap.BeginChange();

            foreach (int i in new[] { 0, 1 })
                Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, i), Is.True);

            beatmap.EndChange();

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("apple orange"));
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.Empty);
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.Empty);
        }

        [Test]
        public void UnsubdivideDropsTheSplitThatCutTheMergedPair()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana", two boundaries

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 1);
            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 1, 5);
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 5 }));

            // Even segments, so the tie goes leftmost: boundary 0 merges "b" and "anan".
            Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, 0), Is.True);

            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3400d }));
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 5 }));
        }

        #endregion

        #region The timeline strip

        /// <summary>
        /// What <c>LyricTimeline.WordBlock</c> prints between the dotted lines is exactly the shared
        /// derivation, so a mapper reads the real judgement grouping off the strip.
        /// </summary>
        [Test]
        public void TimelineSegmentTextsFollowTheEffectiveSplit()
        {
            var beatmap = createBeatmap();

            Assert.That(segmentTexts(lineAt(beatmap, 0).Line.Units[0]), Is.EqualTo(new[] { "ap", "ple" }));
            Assert.That(segmentTexts(lineAt(beatmap, 0).Line.Units[1]), Is.EqualTo(new[] { "orange" }), "no boundary, one block of text");

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, lineAt(beatmap, 0), 0, 0, 4);
            Assert.That(segmentTexts(lineAt(beatmap, 0).Line.Units[0]), Is.EqualTo(new[] { "appl", "e" }));

            Assert.That(segmentTexts(lineAt(beatmap, 1).Line.Units[0]), Is.EqualTo(new[] { "ba", "na", "na" }));
        }

        #endregion

        #region SetSyllableSplit

        [Test]
        public void SetSyllableSplitMaterialisesTheDerivedSplitAroundTheOneItMoves()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana", boundaries at 1200/1400, derived [2,4]

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 1);

            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 4 }),
                "the untouched split keeps the value the mapper could see");
        }

        [TestCase(-5, 1)]
        [TestCase(0, 1)]
        [TestCase(99, 3)]
        public void SetSyllableSplitClampsInsideItsNeighbours(int requested, int expected)
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana", derived [2,4]

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, requested);

            Assert.That(line.Line.Units[0].SyllableSplits[0], Is.EqualTo(expected));
            Assert.That(line.Line.Units[0].SyllableSplits[1], Is.EqualTo(4));
        }

        [Test]
        public void SetSyllableSplitIgnoresAWordWithNoBoundaries()
        {
            var beatmap = createBeatmap();
            var before = lineAt(beatmap, 0).Line;

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, lineAt(beatmap, 0), 1, 0, 2);

            Assert.That(lineAt(beatmap, 0).Line, Is.SameAs(before));
        }

        [Test]
        public void SetSyllableSplitIsOneUndoStep()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 4);
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 4 }));
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300d }), "no time moved");
        }

        #endregion

        #region The op sweep: everything that can invalidate a char index

        [Test]
        public void SplitWordDividesTheWordAtItsSubdivision()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana" [3000, 3600], boundaries 3200 / 3400

            // The cut is the FIRST subdivision: the characters left of it become "ba" and the rest
            // "nana", each owning the old word's span on its own side of the boundary.
            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 0, 0), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("ba nana"), "a space at the cut is what makes two tokens");
            Assert.That(line.Line.Units.Count, Is.EqualTo(2));
            Assert.That(line.Line.Units[0].Text, Is.EqualTo("ba"));
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(3000));
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(3200));
            Assert.That(line.Line.Units[1].Text, Is.EqualTo("nana"));
            Assert.That(line.Line.Units[1].StartTime, Is.EqualTo(3200));
            Assert.That(line.Line.Units[1].EndTime, Is.EqualTo(3600));

            // The cut subdivision is CONSUMED (it is the gap between the two words now), while the
            // word's other one rides into the half that owns it, rebased onto the shorter token.
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.Empty);
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 3400d }));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("ba na|na"));
        }

        [Test]
        public void SplitWordAtTheLastSubdivisionLeavesTheFirstHalfItsOwn()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana", boundaries 3200 / 3400

            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 0, 1), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("bana na"));
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3200d }));
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.Empty);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("ba|na na"));
        }

        [Test]
        public void SplitWordKeepsAnAuthoredSplitOnBothSides()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana", boundaries 3200 / 3400

            // "ban|a|na" authored (NOT the syllabifier's answer, which is "ba|na|na"), then cut at
            // the FIRST boundary: the first word keeps nothing, and the second keeps its cut rebased
            // onto the shorter token it now is.
            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 3);
            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 1, 4);
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 3, 4 }));

            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 0, 0), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("ban ana"));
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.Empty, "the first half has no subdivision left");
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.EqualTo(new[] { 3400d }));
            Assert.That(segmentTexts(line.Line.Units[1]), Is.EqualTo(new[] { "a", "na" }),
                "the surviving cut is rebased onto \"ana\": still after its first character");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("ban a|na"));
        }

        [Test]
        public void SplitWordRefusesWhatItCannotExpress()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0); // "apple" [1000, 1400] (boundary 1300), "orange" [1500, 2600]
            var before = line.Line;

            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 0, -1), Is.False, "no such subdivision");
            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 0, 1), Is.False, "the word has one boundary");
            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 9, 0), Is.False, "no such word");
            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 1, 0), Is.False, "\"orange\" is not subdivided at all");
            Assert.That(line.Line, Is.SameAs(before), "a refused split changes nothing");

            // A word the syllabifier cannot name a character cut for (an over-forced short word) has
            // no place to put a word break either.
            var overForced = createBeatmap();
            var stub = lineAt(overForced, 0);
            stub.Line = new LyricLine
            {
                RawText = "ab orange",
                StartTime = stub.Line.StartTime,
                EndTime = stub.Line.EndTime,
                SingEndTime = stub.Line.SingEndTime,
                Units = new[]
                {
                    // Three boundaries cannot fit in two letters: the syllabifier answers with fewer
                    // cuts than segments, so one of them has no character to live between.
                    newUnit("ab", 1000, 1400, 1100, 1150, 1200),
                    newUnit("orange", 1500, 2600),
                },
            };

            Assert.That(stub.Line.Units[0].SyllableBoundaries.Count, Is.EqualTo(3));
            Assert.That(SyllableSegments.SplitsFor(stub.Line.Units[0]).Count, Is.LessThan(3), "the fixture must be over-forced");
            Assert.That(TypeBeatEditorOperations.SplitWord(overForced, stub, 0, 2), Is.False,
                "a cut the syllabifier cannot place between two characters cannot become a word break");
        }

        /// <summary>
        /// The granularity follows the data down: a word whose LAST subdivision is consumed by the
        /// split leaves the line with no subdivisions at all, and the line drops from Syllable to Word
        /// (it never falls to Line, since both halves are hand timing now).
        /// </summary>
        [Test]
        public void SplitWordDemotesGranularityWhenTheLastSubdivisionGoes()
        {
            var beatmap = createBeatmap();

            // The map's only subdivision is "apple"'s, so consuming it is what the granularity has to
            // follow: "banana" is left plain here for exactly that reason.
            var plain = lineAt(beatmap, 1);
            plain.Line = new LyricLine
            {
                RawText = plain.Line.RawText,
                StartTime = plain.Line.StartTime,
                EndTime = plain.Line.EndTime,
                SingEndTime = plain.Line.SingEndTime,
                Units = new[] { newUnit("banana", 3000, 3600) },
            };

            var line = lineAt(beatmap, 0); // "apple" [1000, 1400] with one boundary at 1300
            var unit = line.Line.Units[0];
            int cut = SyllableSegments.SplitsFor(unit)[0];

            Assert.That(cut, Is.GreaterThan(0).And.LessThan(unit.Text.Length), "the fixture must have a real cut");
            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 0, 0), Is.True);

            Assert.That(line.Line.RawText,
                Is.EqualTo(unit.Text.Substring(0, cut) + " " + unit.Text.Substring(cut) + " orange"),
                "the cut word becomes two tokens; the line's other word is untouched");
            Assert.That(line.Line.Units.All(u => u.SyllableBoundaries.Count == 0), Is.True);
            Assert.That(line.Line.Units.All(u => u.Source == TimingSource.Explicit), Is.True, "both halves are hand timing now");
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Word), "no boundary is left, but the hand timing is");
        }

        [Test]
        public void DraggingABoundaryKeepsTheSplit()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 4);
            TypeBeatEditorOperations.SetSyllableBoundary(beatmap, line, 0, 0, 1250);

            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1250d }));
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 4 }), "the boundary COUNT did not change");
        }

        [Test]
        public void AddingABoundaryBisectsTheAuthoredSegment()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            // "app|le": the widest segment in TIME is the first (1000..1300 vs 1300..1400), so its
            // characters are the ones bisected: "app" becomes "a" + "pp".
            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 3);
            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0), Is.Not.Null);

            Assert.That(line.Line.Units[0].SyllableBoundaries.Count, Is.EqualTo(2));
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 3 }));
            Assert.That(segmentTexts(line.Line.Units[0]), Is.EqualTo(new[] { "a", "pp", "le" }));
        }

        [Test]
        public void AddingABoundaryToADerivedWordKeepsItDerived()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0), Is.Not.Null);

            Assert.That(line.Line.Units[0].SyllableBoundaries.Count, Is.EqualTo(2));
            Assert.That(line.Line.Units[0].SyllableSplits, Is.Empty, "the syllabifier simply re-answers for the higher count");
            Assert.That(segmentTexts(line.Line.Units[0]), Is.EqualTo(SyllableSegments.SegmentTexts("apple", SyllableSegments.Derived("apple", 3))));
        }

        [Test]
        public void RemovingABoundaryDropsTheSplitThatCutIt()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana", two boundaries

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 1);
            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 1, 5);
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 5 }));

            TypeBeatEditorOperations.RemoveSyllableBoundary(beatmap, line, 0, 0);

            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3400d }));
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 5 }), "the surviving split still cuts the same characters");
        }

        [Test]
        public void RemovingTheLastBoundaryLeavesNoSplit()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 4);
            TypeBeatEditorOperations.RemoveSyllableBoundary(beatmap, line, 0, 0);

            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.Empty);
            Assert.That(line.Line.Units[0].SyllableSplits, Is.Empty);
        }

        [Test]
        public void ShrinkingAWordPastABoundaryDropsTheSplitWithIt()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1); // "banana" 3000..3600, boundaries 3200/3400

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 1);
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 4 }));

            // Resize the word so only one boundary is left inside it.
            TypeBeatEditorOperations.SetUnitTiming(beatmap, line, 0, 3000, 3300);

            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3200d }));
            Assert.That(line.Line.Units[0].SyllableSplits, Is.Empty, "a split written for two segments cannot describe one");
        }

        [Test]
        public void MovingAWordKeepsItsSplit()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1);

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 1);
            TypeBeatEditorOperations.MoveUnit(beatmap, line, 0, 3050);

            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 4 }));
        }

        [Test]
        public void ShiftingEveryTimeKeepsEverySplit()
        {
            var beatmap = createBeatmap();

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, lineAt(beatmap, 0), 0, 0, 4);
            TypeBeatEditorOperations.ShiftAllTimes(beatmap, 250);

            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1550d }));
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 4 }), "a split is an index, not a time");
        }

        [Test]
        public void MergingAndSplittingLinesCarryTheSplitWithItsWord()
        {
            var beatmap = createBeatmap();

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, lineAt(beatmap, 0), 0, 0, 4);
            TypeBeatEditorOperations.MergeWithNext(beatmap, lineAt(beatmap, 0));

            var merged = lineAt(beatmap, 0);
            Assert.That(merged.Line.RawText, Is.EqualTo("apple orange banana"));
            Assert.That(merged.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 4 }));

            TypeBeatEditorOperations.SplitLine(beatmap, merged, 2);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 4 }));
            Assert.That(lineAt(beatmap, 1).Line.RawText, Is.EqualTo("banana"));
        }

        [Test]
        public void RemovingAWordLeavesItsNeighboursSplitsAlone()
        {
            var beatmap = createBeatmap();

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, lineAt(beatmap, 0), 0, 0, 4);
            Assert.That(TypeBeatEditorOperations.RemoveWord(beatmap, lineAt(beatmap, 0), 1), Is.True);

            Assert.That(lineAt(beatmap, 0).Line.RawText, Is.EqualTo("apple"));
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 4 }));
        }

        /// <summary>
        /// The word op is unit-wise: it drops ONE unit and leaves the rest as objects, so the
        /// surviving words keep their boundary TIMES as well as their splits, and so does every
        /// other line. Pinned because "remove word" reads like a whole-line rebuild.
        /// </summary>
        [Test]
        public void RemovingAWordLeavesItsNeighboursSubdivisionsAlone()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            // Both words subdivided, the second with an authored split, so there is something to lose.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "ap|ple or|ange"), Is.True);
            double[] orangeBoundaries = line.Line.Units[1].SyllableBoundaries.ToArray();

            Assert.That(TypeBeatEditorOperations.RemoveWord(beatmap, line, 0), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("orange"));
            Assert.That(line.Line.Units, Has.Count.EqualTo(1));
            Assert.That(line.Line.Units[0].SyllableBoundaries, Is.EqualTo(orangeBoundaries), "the survivor's dotted line did not move");
            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 2 }));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("or|ange"));

            Assert.That(lineAt(beatmap, 1).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3200d, 3400 }),
                "and neither did another line's");
        }

        /// <summary>
        /// The timing clipboard carries word SPANS and nothing else, so a split never travels with a
        /// paste: the target word keeps its own, dropped only if the pasted span cost it a boundary.
        /// </summary>
        [Test]
        public void PastingUnitTimingsDoesNotMoveSplitsBetweenWords()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 4);

            var payload = TypeBeatEditorOperations.CopyUnitTimings(line, new[] { 0 });
            Assert.That(payload, Is.Not.Null);

            TypeBeatEditorOperations.PasteUnitTimings(beatmap, line, 1, payload!);

            Assert.That(line.Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 4 }), "the source word is untouched");
            Assert.That(line.Line.Units[1].SyllableSplits, Is.Empty, "nothing was carried onto the target");
            Assert.That(line.Line.Units[1].SyllableBoundaries, Is.Empty);
        }

        #endregion

        #region Reload stability

        [Test]
        public void AnAuthoredSplitSurvivesSaveAndReopen()
        {
            var beatmap = createBeatmap();

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, lineAt(beatmap, 0), 0, 0, 4);
            TypeBeatEditorOperations.SetSyllableSplit(beatmap, lineAt(beatmap, 1), 0, 0, 1);

            var reopened = SyllableSplitTest.DecodeOsu(encode(beatmap));

            Assert.That(reopened[0].Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 4 }));
            Assert.That(reopened[0].Line.Units[1].SyllableSplits, Is.Empty);
            Assert.That(reopened[1].Line.Units[0].SyllableSplits, Is.EqualTo(new[] { 1, 4 }));

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(reopened[0].Line), Is.EqualTo("appl|e orange"));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(reopened[1].Line), Is.EqualTo("b|ana|na"));
        }

        #endregion

        #region fixture

        /// <summary>
        /// Line 0: "apple orange", the apple subdivided once (derived "ap|ple"), the orange not at
        /// all. Line 1: "banana" subdivided twice (derived "ba|na|na").
        /// </summary>
        private static EditorBeatmap createBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Split";
            beatmap.Metadata.AudioFile = "audio.mp3";

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = "apple orange",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 2600,
                    Units = new[]
                    {
                        newUnit("apple", 1000, 1400, 1300),
                        newUnit("orange", 1500, 2600),
                    },
                },
                Granularity = TimingGranularity.Syllable,
            });

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 3000,
                LineIndex = 1,
                Line = new LyricLine
                {
                    RawText = "banana",
                    StartTime = 3000,
                    EndTime = 5000,
                    SingEndTime = 3600,
                    Units = new[] { newUnit("banana", 3000, 3600, 3200, 3400) },
                },
                Granularity = TimingGranularity.Syllable,
            });

            return new EditorBeatmap(beatmap);
        }

        /// <summary>
        /// A one-line LINE-granularity map: no persisted word data at all, units re-derived from
        /// the text on every load, which is what an LRC-only import produces.
        /// </summary>
        private static EditorBeatmap lineGranularityBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Line";
            beatmap.Metadata.AudioFile = "audio.mp3";

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = "fried rice",
                    StartTime = 1000,
                    EndTime = 5000,
                    SingEndTime = 4000,
                    // Char-weighted, exactly as the loader re-derives them: "fried"(6) / "rice"(5).
                    Units = new[]
                    {
                        interpolatedUnit("fried", 1000, 1000 + 3000 * 6 / 11.0),
                        interpolatedUnit("rice", 1000 + 3000 * 6 / 11.0, 4000),
                    },
                },
                Granularity = TimingGranularity.Line,
            });

            return new EditorBeatmap(beatmap);
        }

        private static TimedUnit interpolatedUnit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, Source = TimingSource.Interpolated };

        private static TimedUnit newUnit(string text, double start, double end, params double[] boundaries)
            => new TimedUnit
            {
                Text = text,
                StartTime = start,
                EndTime = end,
                Source = TimingSource.Explicit,
                SyllableBoundaries = boundaries,
            };

        private static TypeBeatHitObject lineAt(EditorBeatmap editorBeatmap, int index)
            => TypeBeatEditorOperations.OrderedLines(editorBeatmap)[index];

        private static IReadOnlyList<string> segmentTexts(TimedUnit unit)
            => SyllableSegments.SegmentTexts(unit.Text, SyllableSegments.SplitsFor(unit));

        private static string encode(EditorBeatmap editorBeatmap)
        {
            var sb = new System.Text.StringBuilder();

            using (var writer = new System.IO.StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(editorBeatmap, writer);

            return sb.ToString();
        }

        #endregion
    }
}
