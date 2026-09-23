// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// AUTHORING an authored pause in the editor (the Map Editor's Insert Pause) and everything that has
// to CARRY it afterwards.
//
// The gesture is one op, <see cref="TypeBeatEditorOperations.InsertWordPause"/>, and the rest of this
// fixture is the sweep every word-shaped edit has to survive: a move, a resize, a subdivision, a text
// commit, a word split, and a save/reopen. All of them rebuild the word's TimedUnit, so all of them
// are places a rest can silently vanish - and a rest that vanishes only on SAVE is worse than one that
// refuses to be authored, because the mapper has no way to tell until the map is reopened.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class WordPauseEditorTest
    {
        private const double tolerance = 1e-9;

        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        #region The gesture

        /// <summary>
        /// The whole gesture on a plain six-character word, with the playhead parked in the middle:
        /// the rest starts AT the playhead, its split snaps to the first character due at or after it
        /// ("please" flat targets 1000, 1166.67, 1333.33, 1500, ... - so a playhead at 1450 takes the
        /// 'a', giving "ple|ase"), and it initially runs to that character's own time. The word is
        /// stamped Explicit hand timing and the map is lifted to at least Word granularity, without
        /// which the encoder would omit words[] and drop the rest on save.
        /// </summary>
        [Test]
        public void InsertPauseTakesThePlayheadAsTheStartAndSnapsTheSplitForward()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            var pause = TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            Assert.That(pause, Is.Not.Null);
            Assert.That(pause!.Value.SplitChar, Is.EqualTo(3), "the first character due at/after 1450 is the 'a' of \"please\"");
            Assert.That(pause.Value.StartTime, Is.EqualTo(1450).Within(tolerance), "the playhead IS the start");
            Assert.That(pause.Value.EndTime, Is.EqualTo(1500).Within(tolerance), "and the rest runs to the moment that character was due");

            var unit = line.Line.Units[0];
            Assert.That(unit.Pauses, Is.EqualTo(new[] { pause }));
            Assert.That(unit.Source, Is.EqualTo(TimingSource.Explicit), "a rest is hand timing");
            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Word), "words[] must be persisted or the rest is dropped on save");
        }

        /// <summary>
        /// A playhead past every character boundary takes the LAST split the word can hold, since a
        /// rest still needs a character on each side of it; and the rest is still the minimum length
        /// rather than an inverted one when the start has been clamped past its end.
        /// </summary>
        [Test]
        public void APlayheadPastTheLastBoundaryTakesTheLastHoldableSplit()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            var pause = TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1900);

            Assert.That(pause, Is.Not.Null);
            Assert.That(pause!.Value.SplitChar, Is.EqualTo(5), "\"please\" keeps its last character after the rest");
            Assert.That(pause.Value.StartTime, Is.EqualTo(1900).Within(tolerance));
            Assert.That(pause.Value.EndTime, Is.GreaterThan(pause.Value.StartTime), "the rest never inverts");
        }

        /// <summary>
        /// Words the gesture cannot serve: too few typeable characters to breathe between, or too
        /// short a span to hold a rest with room either side of it. Both are no-ops, so the button
        /// can stay permanently enabled.
        /// </summary>
        [Test]
        public void InsertPauseIsANoOpWhereAWordCannotHoldOne()
        {
            var beatmap = createMap(("a", 1000, 2000), ("hi", 1000, 1040));

            Assert.That(TypeBeatEditorOperations.InsertWordPause(beatmap, lineAt(beatmap, 0), 0, 1500), Is.Null,
                "one character has no pair to sit between");
            Assert.That(TypeBeatEditorOperations.InsertWordPause(beatmap, lineAt(beatmap, 0), 1, 1020), Is.Null,
                "a 40 ms word cannot hold a rest with room either side");
            Assert.That(lineAt(beatmap, 0).Line.Units.All(u => u.Pauses.Count == 0));
        }

        /// <summary>
        /// Dragging the two edges: each is clamped to stay a minimum span inside the word and away
        /// from the other edge, so neither can be pulled past the word's own bounds or through its
        /// partner. Setting an edge to where it already is changes nothing.
        /// </summary>
        [Test]
        public void TheTwoEdgesAreClampedInsideTheWordAndApart()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            TypeBeatEditorOperations.SetWordPauseStart(beatmap, line, 0, 0, 0);
            Assert.That(line.Line.Units[0].Pauses[0].StartTime, Is.EqualTo(1020).Within(tolerance), "20 ms inside the word's own start");

            TypeBeatEditorOperations.SetWordPauseStart(beatmap, line, 0, 0, 1999);
            Assert.That(line.Line.Units[0].Pauses[0].StartTime, Is.EqualTo(1480).Within(tolerance), "20 ms before the rest's end");

            TypeBeatEditorOperations.SetWordPauseEnd(beatmap, line, 0, 0, 9999);
            Assert.That(line.Line.Units[0].Pauses[0].EndTime, Is.EqualTo(1980).Within(tolerance), "20 ms inside the word's own end");

            TypeBeatEditorOperations.SetWordPauseEnd(beatmap, line, 0, 0, 0);
            Assert.That(line.Line.Units[0].Pauses[0].EndTime, Is.EqualTo(1500).Within(tolerance), "20 ms after the rest's start");
        }

        /// <summary>Double-clicking the rest takes it back out; a word that never had one is untouched.</summary>
        [Test]
        public void TheRestCanBeTakenBackOut()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);
            TypeBeatEditorOperations.RemoveWordPause(beatmap, line, 0, 0);

            Assert.That(line.Line.Units[0].Pauses, Is.Empty);

            TypeBeatEditorOperations.RemoveWordPause(beatmap, line, 1, 0); // never had one
            Assert.That(line.Line.Units[1].Pauses, Is.Empty);
        }

        #endregion

        #region Everything that has to carry it

        /// <summary>
        /// A rest is LOCKED IN PLACE through a re-time, exactly as a subdivision boundary is: it sits at
        /// the time the mapper put it, and stretching or squeezing the word re-times the word around it.
        /// A move that still leaves it inside the word therefore leaves it exactly where it was, and one
        /// the new span can no longer hold goes - the same clamp-then-drop rule the boundaries take.
        /// </summary>
        [Test]
        public void RetimingAWordLeavesItsRestWhereItIs()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            // MOVED: the word shifts 400 ms later, and the rest does not follow it.
            TypeBeatEditorOperations.MoveUnit(beatmap, line, 0, 1400);
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1400).Within(tolerance), "the move itself landed");
            Assert.That(line.Line.Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1450, 1500, 3) }), "and the rest stayed put");

            // SHRUNK around it: still where it was.
            var resized = createBeatmap();
            var resizedLine = lineAt(resized, 0);
            TypeBeatEditorOperations.InsertWordPause(resized, resizedLine, 0, 1450);
            TypeBeatEditorOperations.SetUnitTiming(resized, resizedLine, 0, 1200, 1800);

            Assert.That(resizedLine.Line.Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1450, 1500, 3) }),
                "shrinking the word does not drag the breath into a shorter one");

            // And a re-time that leaves the rest behind drops it, rather than squashing it onto an edge.
            TypeBeatEditorOperations.SetUnitTiming(resized, resizedLine, 0, 1200, 1400);

            Assert.That(resizedLine.Line.Units[0].Pauses, Is.Empty, "a rest the new span cannot hold goes");
        }

        /// <summary>
        /// Subdividing, un-subdividing and moving a dotted line all leave the rest alone: they are
        /// decisions about the word's cuts, not about its breath.
        /// </summary>
        [Test]
        public void TheSubdivisionOpsLeaveTheRestAlone()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);
            var pauses = line.Line.Units[0].Pauses;

            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0), Is.Not.Null);
            Assert.That(line.Line.Units[0].Pauses, Is.EqualTo(pauses), "subdividing keeps the rest");

            TypeBeatEditorOperations.SetSyllableSplit(beatmap, line, 0, 0, 2);
            Assert.That(line.Line.Units[0].Pauses, Is.EqualTo(pauses), "moving a cut keeps the rest");

            Assert.That(TypeBeatEditorOperations.RemoveNarrowestSyllableBoundary(beatmap, line, 0), Is.True);
            Assert.That(line.Line.Units[0].Pauses, Is.EqualTo(pauses), "un-subdividing keeps the rest");
        }

        /// <summary>
        /// A TEXT commit carries the rest only while the word came back spelled exactly as it was. The
        /// split is an index into that spelling, so a retyped word invalidates it the same way it
        /// invalidates an authored char split.
        /// </summary>
        [Test]
        public void ATextCommitKeepsTheRestOnlyForAnUnchangedWord()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);
            var pauses = line.Line.Units[0].Pauses;

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "please go"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.EqualTo(pauses), "the same spelling keeps the rest");

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "pleases go"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.Empty, "a retyped word cannot keep an index into a spelling it no longer has");
        }

        /// <summary>
        /// Splitting a word at one of its own subdivisions sends the rest to whichever half its split
        /// character lands in, rebased onto that half's spelling. A rest sitting exactly ON the cut is
        /// dropped, because the gap between the two new words is already a break there.
        /// </summary>
        [Test]
        public void SplittingAWordSendsTheRestToTheHalfThatOwnsItsSplit()
        {
            // "remember" cut at 1300 into "re|member", with the rest after its fifth character.
            var beatmap = createSubdividedMap("remember", 1000, 2000, 1300, 2, new WordPause(1650, 1800, 5));
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.SplitWord(beatmap, line, 0, 0), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("re member"));
            Assert.That(line.Line.Units[0].Pauses, Is.Empty, "the rest is past the cut, so the first half has none");
            Assert.That(line.Line.Units[1].Pauses, Is.EqualTo(new[] { new WordPause(1650, 1800, 3) }),
                "and the second half keeps it, rebased onto \"member\"");
        }

        #endregion

        #region The line box authors the rest's cut with a pipe

        /// <summary>
        /// The line box prints a rest as a PIPE, exactly where its cut falls, and committing the box
        /// moves that cut: retyping "ple|ase" as "pl|ease" moves the breath one character earlier while
        /// the rest's own times stay put. The rest is a divider like any other on that surface.
        /// </summary>
        [Test]
        public void TheLineBoxAuthorsTheRestsCut()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("ple|ase go"));

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "pl|ease go"), Is.True);

            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1450, 1500, 2) }),
                "the cut moved; the rest's times did not");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("pl|ease go"));
        }

        /// <summary>
        /// A word carrying BOTH prints both cuts, in text order - "re|mem|ber", one pipe for the
        /// subdivision and one for the rest - and moving the second moves the REST while the
        /// subdivision's own cut and time stay where they were.
        /// </summary>
        [Test]
        public void AWordWithASubdivisionAndARestShowsBothCuts()
        {
            var beatmap = createSubdividedMap("remember", 1000, 2000, 1300, 2, new WordPause(1600, 1800, 5));
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("re|mem|ber"));

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "re|memb|er"), Is.True);

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.Pauses, Is.EqualTo(new[] { new WordPause(1600, 1800, 6) }), "the rest's cut moved");
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1300d }), "the subdivision's time did not");
            Assert.That(SyllableSegments.SplitsFor(unit), Is.EqualTo(new[] { 2 }), "nor its cut");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("re|memb|er"));
        }

        /// <summary>
        /// Deleting a rest's pipe does NOT delete the rest: a rest with no cut has nowhere to sit, and
        /// it has its own gesture for going away. The word keeps exactly what it had (so the commit is
        /// not even an undo step), while a subdivision's pipe going still removes the subdivision.
        /// </summary>
        [Test]
        public void DeletingARestsPipeLeavesTheRestWhereItWas()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "please go"), Is.True);

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.Pauses, Is.EqualTo(new[] { new WordPause(1450, 1500, 3) }), "the rest and its cut both stayed");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("ple|ase go"),
                "and the box still shows the cut it kept");
        }

        /// <summary>
        /// A pipe set that would not describe legal cuts leaves the word exactly as it was, on the same
        /// terms every other split commit refuses: here a pipe on top of the rest's own position, which
        /// would leave the second half with no characters at all.
        /// </summary>
        [Test]
        public void AnImpossiblePipeSetLeavesTheRestAlone()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            // "please" with a single pipe at the very END of the word: the far side would be empty.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, lineAt(beatmap, 0), "please| go"), Is.True);

            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1450, 1500, 3) }),
                "an illegal cut is refused rather than half-applied");
        }

        #endregion

        #region SHIFT+drag: promoting a subdivision into a rest

        /// <summary>
        /// A word can take MORE THAN ONE breath: the playhead's gesture ADDS a rest where it points, so a
        /// mapper who parks it in two different breaths gets two of them - and each one prints its own pipe,
        /// so the line box shows the word divided three ways.
        /// </summary>
        [Test]
        public void AWordCanTakeMoreThanOneBreath()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            var first = TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1150);
            var second = TypeBeatEditorOperations.InsertWordPause(beatmap, lineAt(beatmap, 0), 0, 1750);

            Assert.That(first, Is.Not.Null, "the first breath was authored");
            Assert.That(second, Is.Not.Null, "and so was the second");

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.Pauses, Is.EqualTo(new[] { first!.Value, second!.Value }), "both are kept, in time order");
            Assert.That(unit.Pauses[0].SplitChar, Is.LessThan(unit.Pauses[1].SplitChar), "and in character order too");

            string pipes = TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line);
            Assert.That(pipes.Replace("|", string.Empty), Is.EqualTo("please go"));
            Assert.That(pipes.Count(c => c == '|'), Is.EqualTo(2), "a pipe for each breath");
        }

        /// <summary>
        /// A breath that would not fit among the ones the word already has - here one starting over the
        /// first - is refused rather than stored, and the word keeps exactly what it had.
        /// </summary>
        [Test]
        public void ABreathThatWouldNotFitIsRefused()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            var first = TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1150);
            Assert.That(first, Is.Not.Null);

            // The playhead one character earlier puts the new rest over the one already there.
            Assert.That(TypeBeatEditorOperations.InsertWordPause(beatmap, lineAt(beatmap, 0), 0, 1100), Is.Null,
                "a rest cannot be authored over one the word already has");

            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.EqualTo(new[] { first!.Value }), "and nothing moved");
        }

        /// <summary>
        /// Both breaths survive a save and reopen, in order.
        /// </summary>
        [Test]
        public void SeveralBreathsSurviveSaveAndReopen()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1150);
            TypeBeatEditorOperations.InsertWordPause(beatmap, lineAt(beatmap, 0), 0, 1750);

            var expected = lineAt(beatmap, 0).Line.Units[0].Pauses.ToArray();
            var reopened = SyllableSplitTest.DecodeOsu(encode(beatmap));

            Assert.That(reopened[0].Line.Units[0].Pauses, Is.EqualTo(expected));
        }

        [Test]
        public void ASubdivisionAddedToAPausedWordDividesAroundBoth()
        {
            // A rest across the middle of the word's widest span, with the caret parked INSIDE it - which
            // is where the mapper's playhead is, having just put the breath there.
            var beatmap = createMap(("remember", 1000, 2000, new WordPause(1200, 1800, 5)));
            var line = lineAt(beatmap, 0);

            double? added = TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0, 1500);

            Assert.That(added, Is.EqualTo(1100),
                "the widest SUNG span is bisected: a caret in the rest is inside no span, and no divider belongs in one");

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1100d }));
            Assert.That(unit.Pauses, Is.EqualTo(new[] { new WordPause(1200, 1800, 5) }), "and the rest is untouched");

            // The characters DO move around both dividers: the half before the rest is split at the new
            // divider's own time, and the rest opens the last run from its far edge.
            var halves = runs(unit);
            Assert.That(string.Concat(halves.Select(r => r.Text)), Is.EqualTo("remember"), "every character is still drawn once");
            Assert.That(halves.Length, Is.EqualTo(3), "one run either side of the new divider, and one past the rest");
            Assert.That(halves[0].Start, Is.EqualTo(1000));
            Assert.That(halves[0].End, Is.EqualTo(1100));
            Assert.That(halves[1].Start, Is.EqualTo(1100), "the new divider's time");
            Assert.That(halves[1].End, Is.EqualTo(1200), "up to the rest");
            Assert.That(halves[2].Start, Is.EqualTo(1800), "and the rest's far edge opens the last one");
            Assert.That(halves[2].End, Is.EqualTo(2000));

            // Which is what the line box shows too: a pipe for each of them.
            string pipes = TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line);
            Assert.That(pipes.Replace("|", string.Empty), Is.EqualTo("remember"));
            Assert.That(pipes.Count(c => c == '|'), Is.EqualTo(2), "one for the divider, one for the rest");
        }

        /// <summary>
        /// A word already divided by a rest can still be subdivided: the new divider lands in the word
        /// (the widest span, or the caret when it is inside one), the rest is left exactly where it was,
        /// and the characters move around both of them.
        /// </summary>
        [Test]
        public void APausedWordCanStillBeSubdivided()
        {
            var beatmap = createMap(("remember", 1000, 2000, new WordPause(1600, 1800, 5)));
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0), Is.EqualTo(1300),
                "the widest SUNG span is bisected: [1000, 1600] here, never the rest");

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1300d }));
            Assert.That(unit.Pauses, Is.EqualTo(new[] { new WordPause(1600, 1800, 5) }), "and the rest is untouched");

            string pipes = TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line);
            Assert.That(pipes.Replace("|", string.Empty), Is.EqualTo("remember"));
            Assert.That(pipes.Count(c => c == '|'), Is.EqualTo(2), "two dividers, drawn in text order");
        }

        /// <summary>
        /// And its dividers can be MOVED: dragging one re-times it, the rest stays put, and the characters
        /// move with it.
        /// </summary>
        [Test]
        public void ADividerCanBeMovedOnAPausedWord()
        {
            var beatmap = createMap(("remember", 1000, 2000, new WordPause(1600, 1800, 5)));
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0);
            TypeBeatEditorOperations.SetSyllableBoundary(beatmap, line, 0, 0, 1300);

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1300d }));
            Assert.That(unit.Pauses, Is.EqualTo(new[] { new WordPause(1600, 1800, 5) }));
            Assert.That(runs(unit)[0].End, Is.EqualTo(1300), "and the characters move with it");
        }

        /// <summary>
        /// A divider cannot be PARKED inside the rest: one there would separate characters the rest
        /// already separates, and the word's authored cuts would stop describing its halves, so no later
        /// divider could be moved at all. A drag that lands in the rest therefore holds the divider
        /// against the rest's own edge on the side it came from.
        /// </summary>
        [Test]
        public void ADividerCannotBeParkedInsideTheRest()
        {
            var beatmap = createMap(("remember", 1000, 2000, new WordPause(1400, 1700, 5)));
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0);   // lands at 1200, before the rest

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1200d }));

            TypeBeatEditorOperations.SetSyllableBoundary(beatmap, line, 0, 0, 1550);

            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1380d }),
                "held 20 ms short of the rest, on the side it came from");

            // And a divider on the FAR side is held against that edge in mirror image.
            TypeBeatEditorOperations.SetSyllableBoundary(beatmap, line, 0, 0, 1800);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1800d }),
                "while a sweep clean ACROSS the rest is followed, so crossing a breath is not a wall");
        }

        /// <summary>
        /// The gesture's edit: "reme|mber" (one divider at 1300, cut after four characters) dragged
        /// across 400 ms becomes a REST from 1300 to 1700, the divider is consumed, and the word's line
        /// box reads exactly as it did before - the rest stands where the pipe stood.
        /// </summary>
        [Test]
        public void PromotingASubdivisionTurnsTheSweptSpanIntoARest()
        {
            var beatmap = createSubdividedMap("remember", 1000, 2000, 1300, 4, pause: null);
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("reme|mber"));

            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(beatmap, line, 0, 0, 1300, 1700), Is.True);

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.Pauses, Is.EqualTo(new[] { new WordPause(1300, 1700, 4) }), "the rest spans the sweep, after the same character");
            Assert.That(unit.SyllableBoundaries, Is.Empty, "and the divider that made it is consumed");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("reme|mber"),
                "the rest prints its own pipe, so the box reads the same");
        }

        /// <summary>
        /// Dragged the other way the span is the same, whichever end the mapper pulled from: the rest
        /// always spans the distance swept, so its near edge stays where the characters before it ran out
        /// (the divider's own time) and its far edge follows the cursor.
        /// </summary>
        [Test]
        public void DraggingTheDividerBackwardsOpensTheSameSpan()
        {
            var beatmap = createSubdividedMap("remember", 1000, 2000, 1300, 4, pause: null);
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(beatmap, line, 0, 0, 1300, 1100), Is.True);

            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1100, 1300, 4) }));
        }

        /// <summary>
        /// A word with OTHER dividers keeps them: only the one that was promoted goes, and the rest of
        /// the split still describes the word it is left on. "ba|na|na" promoted at its first divider
        /// reads "ba|na|na" still - one pipe for the rest, one for the divider that stayed.
        /// </summary>
        [Test]
        public void PromotingOneDividerLeavesTheOthersWhereTheyWere()
        {
            var beatmap = createSubdividedMap("banana", 1000, 2000, 1300, 2, pause: null, secondBoundary: 1600, secondSplit: 4);
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("ba|na|na"));

            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(beatmap, line, 0, 0, 1300, 1450), Is.True);

            var unit = lineAt(beatmap, 0).Line.Units[0];
            Assert.That(unit.Pauses, Is.EqualTo(new[] { new WordPause(1300, 1450, 2) }), "the promoted divider became the rest");
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1600d }), "and the other one stayed at its time");
            Assert.That(SyllableSegments.SplitsFor(unit), Is.EqualTo(new[] { 4 }), "still cutting after \"nana\"'s first pair");
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("ba|na|na"));
        }

        /// <summary>
        /// The gesture refuses what it cannot express, leaving the word exactly as the drag left it: a
        /// word that already carries a rest (one per word, and it has its own gesture to take it out), a
        /// sweep too short to be a drag, a sweep that would put an edge outside the word, and a divider
        /// with no character cut to sit after.
        /// </summary>
        /// <summary>
        /// The gesture refuses what it cannot express, leaving the word exactly as the drag left it: a
        /// sweep too short to be a drag, a divider that is not there, a word that is not there, a sweep
        /// that would land ON a breath the word already has, and a divider with no character cut to sit
        /// after.
        /// </summary>
        [Test]
        public void PromotionRefusesWhatItCannotExpress()
        {
            var beatmap = createSubdividedMap("remember", 1000, 2000, 1300, 4, pause: null);
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(beatmap, line, 0, 0, 1300, 1305), Is.False, "a 5 ms sweep is not a drag");
            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(beatmap, line, 0, 9, 1300, 1700), Is.False, "no such divider");
            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(beatmap, line, 9, 0, 1300, 1700), Is.False, "no such word");

            // A word that already takes a breath: the sweep must not swallow it. The divider sits at 1300
            // and the rest covers [1600, 1800], so a sweep that lands inside it cannot become another one.
            var breathed = createSubdividedMap("remember", 1000, 2000, 1300, 4, new WordPause(1600, 1800, 5));
            var breathedLine = lineAt(breathed, 0);

            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(breathed, breathedLine, 0, 0, 1300, 1700), Is.False,
                "a rest cannot be authored over one the word already has");
            Assert.That(lineAt(breathed, 0).Line.Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1600, 1800, 5) }), "which is untouched");
            Assert.That(lineAt(breathed, 0).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300d }), "nor is its divider consumed");

            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.Empty, "nothing was authored");
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300d }), "and the divider is still there");
        }

        /// <summary>
        /// A sweep dragged past the word's own edge is CLAMPED to it, exactly as dragging the rest's own
        /// edges is: the mapper gets the widest rest the word can hold rather than nothing.
        /// </summary>
        [Test]
        public void ASweepPastTheWordsEdgeIsClampedToIt()
        {
            var beatmap = createSubdividedMap("remember", 1000, 2000, 1300, 4, pause: null);
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(beatmap, line, 0, 0, 1300, 9000), Is.True);

            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1300, 1980, 4) }),
                "the far edge comes to rest 20 ms inside the word's own end");
        }

        /// <summary>
        /// And a promoted rest is a rest the ENGINE can honour: the same test the play applies to an
        /// inert one is asked before it is authored, so a divider whose cut leaves every typeable cell on
        /// one side of it cannot become a rest that does nothing. Here "ab-" has its cut before the dash,
        /// a legal split of the TEXT that leaves no character at all on the far side.
        /// </summary>
        [Test]
        public void PromotionRefusesARestTheEngineWouldIgnore()
        {
            var beatmap = createSubdividedMap("ab-", 1000, 2000, 1300, 2, pause: null);
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.ExtendSubdivisionIntoPause(beatmap, line, 0, 0, 1300, 1700), Is.False);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].Pauses, Is.Empty);
            Assert.That(lineAt(beatmap, 0).Line.Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1300d }), "and the divider stays");
        }

        #endregion

        #region Reload stability

        /// <summary>
        /// The rest survives a save and reopen, on a word the mapper never subdivided - which is the
        /// common case and the one the writer used to nest inside syllables[] and drop.
        /// </summary>
        [Test]
        public void AnAuthoredPauseSurvivesSaveAndReopen()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            var reopened = SyllableSplitTest.DecodeOsu(encode(beatmap));

            Assert.That(reopened[0].Granularity, Is.EqualTo(TimingGranularity.Word));
            Assert.That(reopened[0].Line.Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1450, 1500, 3) }));
            Assert.That(reopened[0].Line.Units[1].Pauses, Is.Empty);
        }

        /// <summary>
        /// A rest on a LINE-granularity map has to lift the map to Word on the way in. Without that
        /// promotion the encoder writes no words[] at all, so the mapper's rest would be gone the next
        /// time the map was opened - the trap the op exists to close.
        /// </summary>
        [Test]
        public void InsertingARestLiftsALineMapToWordSoItCanBeSaved()
        {
            var beatmap = createMap(granularity: TimingGranularity.Line, ("please", 1000, 2000), ("go", 2200, 2600));
            var line = lineAt(beatmap, 0);

            Assert.That(line.Granularity, Is.EqualTo(TimingGranularity.Line));

            var pause = TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);
            Assert.That(pause, Is.Not.Null);

            var reopened = SyllableSplitTest.DecodeOsu(encode(beatmap));
            Assert.That(reopened[0].Line.Units[0].Pauses, Is.EqualTo(new[] { pause }));
        }

        /// <summary>
        /// The IMPORT path's writer (<see cref="SynthesizedTimingJson"/>) writes the rest beside the
        /// syllable data rather than inside it, so a rest on an un-subdivided word survives its round
        /// trip too. This is the pin the writer's nesting bug used to fail.
        /// </summary>
        [Test]
        public void TheSynthesizedWriterKeepsARestOnAnUnsubdividedWord()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            string json = SynthesizedTimingJson.Write(new[] { lineAt(beatmap, 0).Line }, wordTiming: true, songEndMs: 3000);
            string path = writeTemp(json);

            try
            {
                Assert.That(TimingJsonLoader.TryLoad(path, out var lines), Is.True);
                Assert.That(lines![0].Units[0].Pauses, Is.EqualTo(new[] { new WordPause(1450, 1500, 3) }));
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        #endregion

        #region fixture

        /// <summary>
        /// One WORD-granularity line, "please go": the six-character word the gesture is exercised on
        /// over [1000, 2000], and a second word far enough right that the first can be moved by 400 ms
        /// without touching it.
        /// </summary>
        private static EditorBeatmap createBeatmap() => createMap(("please", 1000, 2000), ("go", 2600, 3000));

        private static EditorBeatmap createMap(params (string Text, double Start, double End)[] words)
            => createMap(TimingGranularity.Word, words);


        private static EditorBeatmap createMap((string Text, double Start, double End, WordPause? Pause) word)
            => build(TimingGranularity.Word, word.Text, new[]
            {
                new TimedUnit
                {
                    Text = word.Text,
                    StartTime = word.Start,
                    EndTime = word.End,
                    Source = TimingSource.Explicit,
                    Pauses = word.Pause is WordPause only ? new[] { only } : Array.Empty<WordPause>(),
                },
            });

        private static EditorBeatmap createSubdividedMap(string text, double start, double end, double boundary, int split, WordPause? pause,
                                                          double? secondBoundary = null, int? secondSplit = null)
            => createSubdividedMap(TimingGranularity.Word, text, start, end, boundary, split, pause, secondBoundary, secondSplit);

        private static EditorBeatmap createMap(TimingGranularity granularity, params (string Text, double Start, double End)[] words)
        {
            var units = words.Select(w => new TimedUnit
            {
                Text = w.Text,
                StartTime = w.Start,
                EndTime = w.End,
                Source = granularity == TimingGranularity.Line ? TimingSource.Interpolated : TimingSource.Explicit,
            }).ToArray();

            return build(granularity, string.Join(' ', words.Select(w => w.Text)), units);
        }

        private static EditorBeatmap createSubdividedMap(TimingGranularity granularity, string text, double start, double end, double boundary, int split,
                                                          WordPause? pause, double? secondBoundary = null, int? secondSplit = null)
            => build(granularity, text, new[]
            {
                new TimedUnit
                {
                    Text = text,
                    StartTime = start,
                    EndTime = end,
                    Source = TimingSource.Explicit,
                    SyllableBoundaries = secondBoundary is double second ? new[] { boundary, second } : new[] { boundary },
                    SyllableSplits = secondSplit is int secondCut ? new[] { split, secondCut } : new[] { split },
                    Pauses = pause is WordPause rest ? new[] { rest } : Array.Empty<WordPause>(),
                },
            });

        private static EditorBeatmap build(TimingGranularity granularity, string text, IReadOnlyList<TimedUnit> units)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Pause";
            beatmap.Metadata.AudioFile = "audio.mp3";

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = units[0].StartTime,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = text,
                    StartTime = units[0].StartTime,
                    EndTime = Math.Max(units[^1].EndTime, units[0].StartTime + 1000),
                    SingEndTime = units[^1].EndTime,
                    Units = units,
                },
                Granularity = granularity,
            });

            return new EditorBeatmap(beatmap);
        }

        private static TypeBeatHitObject lineAt(EditorBeatmap editorBeatmap, int index)
            => TypeBeatEditorOperations.OrderedLines(editorBeatmap)[index];

        /// <summary>The runs the editor's strip draws a word's characters in, as (text, start, end) triples.</summary>
        private static (string Text, double Start, double End)[] runs(TimedUnit unit)
            => Gameplay.PausedWord.DisplayRuns(unit.Text, unit, unit.StartTime, unit.EndTime)
                        .Select(r => (r.Text, r.StartTime, r.EndTime)).ToArray();

        private static string encode(EditorBeatmap editorBeatmap)
        {
            var sb = new System.Text.StringBuilder();

            using (var writer = new System.IO.StringWriter(sb))
                TypeBeatBeatmapEncoder.Encode(editorBeatmap, writer);

            return sb.ToString();
        }

        private static string writeTemp(string content)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tb_pause_" + Guid.NewGuid().ToString("N") + ".json");
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        #endregion
    }
}
