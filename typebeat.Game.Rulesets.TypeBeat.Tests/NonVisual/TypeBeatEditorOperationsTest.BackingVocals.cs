// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The BACKING VOCAL edits (backlog 445): the text field, the lane's move / resize / anchor drags,
    /// and the line edits that have to carry backing words with them. Every one keeps the words inside
    /// their line's window and in start order, never touches a typed word, and reloads exactly as the
    /// editor held it (<see cref="assertReloadStable"/> compares the backing words too).
    /// </summary>
    public partial class TypeBeatEditorOperationsTest
    {
        private static EditorBeatmap createBeatmapWithBacking()
        {
            var editorBeatmap = createBeatmap();

            // Line 1 is [3000..6000], sung to 5500: "gamma" 3000-4200, "delta" 4300-5500.
            Assert.That(TypeBeatEditorOperations.SetBackingVocalText(editorBeatmap, lineAt(editorBeatmap, 1), "ooh yeah"), Is.True);
            return editorBeatmap;
        }

        [Test]
        public void BackingTextSpreadsNewWordsAcrossTheSungSpan()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var line = lineAt(editorBeatmap, 1).Line;

            Assert.That(line.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "ooh", "yeah" }));
            Assert.That(line.BackingVocals[0].StartTime, Is.EqualTo(3000), "the first word starts with the line");
            Assert.That(line.BackingVocals[^1].EndTime, Is.EqualTo(5500), "and the last ends with its sung end");
            Assert.That(line.BackingVocals.All(w => w.Anchor == null), "a new word sits on its default place");

            // The typed line is untouched.
            Assert.That(line.RawText, Is.EqualTo("gamma delta"));
            Assert.That(line.Units.Select(u => (u.StartTime, u.EndTime)), Is.EqualTo(new[] { (3000.0, 4200.0), (4300.0, 5500.0) }));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void RetypingBackingTextKeepsTheTimingOfTheWordsItReplaces()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var hitObject = lineAt(editorBeatmap, 1);

            TypeBeatEditorOperations.MoveBackingVocal(editorBeatmap, hitObject, 0, 3600);
            TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, hitObject, 0, 0.1);
            var moved = hitObject.Line.BackingVocals[0];

            Assert.That(TypeBeatEditorOperations.SetBackingVocalText(editorBeatmap, hitObject, "  aah   yeah  woo "), Is.True);

            var words = hitObject.Line.BackingVocals;
            Assert.That(words.Select(w => w.Text), Is.EqualTo(new[] { "aah", "yeah", "woo" }));
            Assert.That(words[0], Is.EqualTo(moved with { Text = "aah" }), "the first word keeps its time and its anchor");
            Assert.That(words[2].StartTime, Is.GreaterThanOrEqualTo(words[1].EndTime), "a new word goes after the last kept one");
            Assert.That(TypeBeatEditorOperations.BackingVocalText(hitObject.Line), Is.EqualTo("aah yeah woo"));

            // The same text again changes nothing, and an empty text removes them all.
            Assert.That(TypeBeatEditorOperations.SetBackingVocalText(editorBeatmap, hitObject, "aah yeah woo"), Is.False);
            Assert.That(TypeBeatEditorOperations.SetBackingVocalText(editorBeatmap, hitObject, " "), Is.True);
            Assert.That(hitObject.Line.BackingVocals, Is.Empty);

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void BackingWordsStopAtTheirNeighboursStartsAndTheLine()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var hitObject = lineAt(editorBeatmap, 1);
            double secondStart = hitObject.Line.BackingVocals[1].StartTime;

            // Past the next word's start: stops on it, so the order (and the default anchors) holds.
            TypeBeatEditorOperations.MoveBackingVocal(editorBeatmap, hitObject, 0, 5000);
            Assert.That(hitObject.Line.BackingVocals[0].StartTime, Is.EqualTo(secondStart));

            // Before the line: stops at its start.
            TypeBeatEditorOperations.SetBackingVocalTiming(editorBeatmap, hitObject, 0, 1000, 3500);
            Assert.That(hitObject.Line.BackingVocals[0].StartTime, Is.EqualTo(3000));
            Assert.That(hitObject.Line.BackingVocals[0].EndTime, Is.EqualTo(3500));

            // An end before the start is held a minimum span after it; an end past the line, at its end.
            TypeBeatEditorOperations.SetBackingVocalTiming(editorBeatmap, hitObject, 1, secondStart, secondStart - 500);
            Assert.That(hitObject.Line.BackingVocals[1].EndTime, Is.EqualTo(secondStart + BackingVocals.MIN_SPAN_MS));
            TypeBeatEditorOperations.SetBackingVocalTiming(editorBeatmap, hitObject, 1, secondStart, 9999);
            Assert.That(hitObject.Line.BackingVocals[1].EndTime, Is.EqualTo(6000));

            // A rigid move keeps the word's length where the line has room for it.
            TypeBeatEditorOperations.SetBackingVocalTiming(editorBeatmap, hitObject, 1, 4000, 4400);
            TypeBeatEditorOperations.MoveBackingVocal(editorBeatmap, hitObject, 1, 5900);
            Assert.That(hitObject.Line.BackingVocals[1].StartTime, Is.EqualTo(5600));
            Assert.That(hitObject.Line.BackingVocals[1].EndTime, Is.EqualTo(6000));

            // None of it moved a typed word.
            Assert.That(hitObject.Line.Units.Select(u => (u.StartTime, u.EndTime)), Is.EqualTo(new[] { (3000.0, 4200.0), (4300.0, 5500.0) }));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AnAnchorIsStoredRoundedAndDoubleClickPutsItBack()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var hitObject = lineAt(editorBeatmap, 1);

            TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, hitObject, 1, 0.123456);
            Assert.That(hitObject.Line.BackingVocals[1].Anchor, Is.EqualTo(0.1235));
            Assert.That(BackingVocals.AnchorOf(hitObject.Line.BackingVocals, 1), Is.EqualTo(0.1235));
            Assert.That(BackingVocals.AnchorOf(hitObject.Line.BackingVocals, 0), Is.EqualTo(0.25), "the other word keeps its default place");

            TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, hitObject, 1, 7);
            Assert.That(hitObject.Line.BackingVocals[1].Anchor, Is.EqualTo(1), "an anchor never leaves the line");

            assertReloadStable(editorBeatmap);

            TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, hitObject, 1, null);
            Assert.That(hitObject.Line.BackingVocals[1].Anchor, Is.Null);
            Assert.That(BackingVocals.AnchorOf(hitObject.Line.BackingVocals, 1), Is.EqualTo(0.75));

            TypeBeatEditorOperations.RemoveBackingVocal(editorBeatmap, hitObject, 0);
            Assert.That(hitObject.Line.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "yeah" }));
            Assert.That(BackingVocals.AnchorOf(hitObject.Line.BackingVocals, 0), Is.EqualTo(0.5), "a lone word sits in the middle");

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void EachBackingEditIsOneUndoStep()
        {
            using var editorBeatmap = createBeatmapWithBacking();
            using var handler = new RulesetBeatmapChangeHandler(editorBeatmap, new TypeBeatRuleset());
            var before = lineAt(editorBeatmap, 1).Line.BackingVocals.ToArray();

            TypeBeatEditorOperations.MoveBackingVocal(editorBeatmap, lineAt(editorBeatmap, 1), 1, 5200);
            TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, lineAt(editorBeatmap, 1), 1, 0.9);
            var after = lineAt(editorBeatmap, 1).Line.BackingVocals.ToArray();
            Assert.That(after, Is.Not.EqualTo(before));

            // Undo goes through the map's own encoding, so this is the save path's round trip too.
            handler.RestoreState(-1);
            handler.RestoreState(-1);
            Assert.That(lineAt(editorBeatmap, 1).Line.BackingVocals, Is.EqualTo(before));

            handler.RestoreState(2);
            Assert.That(lineAt(editorBeatmap, 1).Line.BackingVocals, Is.EqualTo(after));
        }

        [Test]
        public void ABoundaryDragKeepsBothLinesBackingWordsInsideTheirWindows()
        {
            var editorBeatmap = createBeatmapWithBacking();

            // Drag line 1's start past its first backing word: the word is held at the new start.
            TypeBeatEditorOperations.SetLineStart(editorBeatmap, lineAt(editorBeatmap, 1), 3500);

            var line = lineAt(editorBeatmap, 1).Line;
            Assert.That(line.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "ooh", "yeah" }));
            Assert.That(line.BackingVocals.All(w => w.StartTime >= 3500 && w.EndTime <= line.EndTime));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void SplittingALineHandsEachHalfTheBackingWordsSungInIt()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var hitObject = lineAt(editorBeatmap, 1);

            // "ooh" at 3000, "yeah" placed in the second half and anchored at the line's right end.
            TypeBeatEditorOperations.SetBackingVocalTiming(editorBeatmap, hitObject, 0, 3100, 3400);
            TypeBeatEditorOperations.SetBackingVocalTiming(editorBeatmap, hitObject, 1, 4600, 5000);
            TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, hitObject, 1, 1);

            TypeBeatEditorOperations.SplitLine(editorBeatmap, hitObject, 1);

            var first = lineAt(editorBeatmap, 1).Line;
            var second = lineAt(editorBeatmap, 2).Line;

            Assert.That(first.RawText, Is.EqualTo("gamma"));
            Assert.That(second.RawText, Is.EqualTo("delta"));
            Assert.That(first.BackingVocals.Select(w => (w.Text, w.StartTime)), Is.EqualTo(new[] { ("ooh", 3100.0) }));
            Assert.That(second.BackingVocals.Select(w => (w.Text, w.StartTime)), Is.EqualTo(new[] { ("yeah", 4600.0) }));

            // The right end of the old line is the right end of the half it landed in.
            Assert.That(second.BackingVocals[0].Anchor, Is.EqualTo(1));

            assertReloadStable(editorBeatmap);

            // And merging the halves back joins them in order, the anchor still at the right end.
            TypeBeatEditorOperations.MergeWithNext(editorBeatmap, lineAt(editorBeatmap, 1));

            var merged = lineAt(editorBeatmap, 1).Line;
            Assert.That(merged.BackingVocals.Select(w => (w.Text, w.StartTime)), Is.EqualTo(new[] { ("ooh", 3100.0), ("yeah", 4600.0) }));
            Assert.That(merged.BackingVocals[1].Anchor, Is.EqualTo(1));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void MergingKeepsTheNextLinesBackingWords()
        {
            var editorBeatmap = createBeatmapWithBacking();

            TypeBeatEditorOperations.MergeWithNext(editorBeatmap, lineAt(editorBeatmap, 0));

            var merged = lineAt(editorBeatmap, 0).Line;
            Assert.That(merged.RawText, Is.EqualTo("alpha beta gamma delta"));
            Assert.That(merged.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "ooh", "yeah" }));
            Assert.That(merged.BackingVocals[0].StartTime, Is.EqualTo(3000));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void ShiftingTheMapMovesTheBackingWordsWithIt()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var before = lineAt(editorBeatmap, 1).Line.BackingVocals.Select(w => w.StartTime).ToArray();

            TypeBeatEditorOperations.ShiftAllTimes(editorBeatmap, 100);

            Assert.That(lineAt(editorBeatmap, 1).Line.BackingVocals.Select(w => w.StartTime), Is.EqualTo(before.Select(t => t + 100)));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void ALinePastedWholeBringsItsBackingWords()
        {
            var editorBeatmap = createBeatmapWithBacking();
            TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, lineAt(editorBeatmap, 1), 0, 0.2);
            var source = lineAt(editorBeatmap, 1).Line.BackingVocals;

            var payload = TypeBeatEditorOperations.CopyLineTimings(new[] { lineAt(editorBeatmap, 1) });
            var copied = payload.Lines.Single().Backing;

            Assert.That(copied, Is.Not.Null);
            Assert.That(copied!.Select(b => (b.Text, b.Start, b.Anchor)), Is.EqualTo(new[] { ("ooh", 0.0, (double?)0.2), ("yeah", source[1].StartTime - 3000, null) }));

            // Over line 0 ([1000..3000]): the words land at the same offsets from its start, inside its window.
            TypeBeatEditorOperations.PasteLine(editorBeatmap, new[] { lineAt(editorBeatmap, 0) }, payload);

            var pasted = lineAt(editorBeatmap, 0).Line;
            Assert.That(pasted.RawText, Is.EqualTo("gamma delta"));
            Assert.That(pasted.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "ooh", "yeah" }));
            Assert.That(pasted.BackingVocals[0].StartTime, Is.EqualTo(1000));
            Assert.That(pasted.BackingVocals[0].Anchor, Is.EqualTo(0.2));
            Assert.That(pasted.BackingVocals.All(w => w.StartTime >= pasted.StartTime && w.EndTime <= pasted.EndTime));

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AddWordOnTheBackingTrackAddsABackingWordAtThePlayhead()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var hitObject = lineAt(editorBeatmap, 1);
            string typed = hitObject.Line.RawText;

            int at = TypeBeatEditorOperations.AddBackingVocal(editorBeatmap, hitObject, 3500);

            Assert.That(hitObject.Line.BackingVocals[at], Is.EqualTo(new BackingVocal(TypeBeatEditorOperations.NEW_WORD_TEXT, 3500, 3500 + TypeBeatEditorOperations.NEW_BACKING_WORD_MS)));
            Assert.That(hitObject.Line.BackingVocals.Select(w => w.StartTime), Is.Ordered);
            Assert.That(hitObject.Line.RawText, Is.EqualTo(typed));

            // A playhead outside the line puts it after the last one, held inside the window.
            double lastEnd = hitObject.Line.BackingVocals[^1].EndTime;
            at = TypeBeatEditorOperations.AddBackingVocal(editorBeatmap, hitObject, 100);
            Assert.That(at, Is.EqualTo(hitObject.Line.BackingVocals.Count - 1));
            Assert.That(hitObject.Line.BackingVocals[at].StartTime, Is.EqualTo(Math.Min(lastEnd, 6000 - BackingVocals.MIN_SPAN_MS)));
            Assert.That(hitObject.Line.BackingVocals[at].EndTime, Is.LessThanOrEqualTo(6000));

            Assert.That(TypeBeatEditorOperations.AddBackingVocal(editorBeatmap, hitObject, 3500, "two words"), Is.EqualTo(-1));

            assertReloadStable(editorBeatmap);
        }

        [TestCase("hello (oh oh)", "hello", false, "hello", "oh oh")]
        [TestCase("hello (oh  (yeah))", "hello", false, "hello", "oh  (yeah)")]
        [TestCase("hello", "hello", true, "hello", "")]
        [TestCase("hello (oh)", "hello", true, "hello", "oh")]
        [TestCase("hello there", "hello", true, "hello there", "")]
        [TestCase("hello there", "hello", false, "hello there", null)]
        [TestCase("(whole line)", "x", false, "(whole line)", null)]
        [TestCase("hey (you) (yeah)", "hey (you)", true, "hey (you)", "yeah")]
        [TestCase("hey (you)", "hey (you)", false, "hey (you)", null)]
        [TestCase("hey (they) said", "hey", false, "hey (they) said", null)]
        public void TheLineListSplitsATrailingBracketIntoBackingVocals(string text, string lineText, bool hadBacking, string main, string? backing)
            => Assert.That(TypeBeatEditorOperations.SplitBackingSuffix(text, lineText, hadBacking), Is.EqualTo((main, backing)));

        [Test]
        public void TheLineListShowsBackingVocalsInBracketsOnlyWhenThereAreAny()
        {
            var editorBeatmap = createBeatmapWithBacking();

            Assert.That(TypeBeatEditorOperations.BackingSuffix(lineAt(editorBeatmap, 1).Line), Is.EqualTo(" (ooh yeah)"));
            Assert.That(TypeBeatEditorOperations.BackingSuffix(lineAt(editorBeatmap, 0).Line), Is.Empty);
        }

        [Test]
        public void SeveralBackingWordsGoAsOneStep()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var hitObject = lineAt(editorBeatmap, 1);
            TypeBeatEditorOperations.AddBackingVocal(editorBeatmap, hitObject, 5800, "hey");

            TypeBeatEditorOperations.RemoveBackingVocals(editorBeatmap, hitObject, new[] { 0, 2, 9 });

            Assert.That(hitObject.Line.BackingVocals.Select(w => w.Text), Is.EqualTo(new[] { "yeah" }));
            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void AGroupMovesRigidlyAndStopsAtTheWordsOutsideIt()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var hitObject = lineAt(editorBeatmap, 1);

            // ooh 3000.., yeah .., and a third word at 5600 outside the group.
            TypeBeatEditorOperations.AddBackingVocal(editorBeatmap, hitObject, 5600, "hey");
            var before = hitObject.Line.BackingVocals.ToArray();
            int[] group = { 0, 1 };
            double[] starts = group.Select(i => before[i].StartTime).ToArray();
            double[] ends = group.Select(i => before[i].EndTime).ToArray();

            TypeBeatEditorOperations.MoveBackingVocals(editorBeatmap, hitObject, group, starts, ends, 200);
            Assert.That(hitObject.Line.BackingVocals.Take(2).Select(w => (w.StartTime, w.EndTime)),
                Is.EqualTo(group.Select(i => (before[i].StartTime + 200, Math.Min(6000, before[i].EndTime + 200)))));

            // Far later: held where the group's last word meets "hey" or, keeping its length, the line's
            // end, whichever comes first; far earlier: held at the line's start.
            TypeBeatEditorOperations.MoveBackingVocals(editorBeatmap, hitObject, group, starts, ends, 9000);
            Assert.That(hitObject.Line.BackingVocals[1].StartTime, Is.EqualTo(Math.Min(5600, 6000 - (ends[1] - starts[1]))).Within(1e-6));
            Assert.That(hitObject.Line.BackingVocals[1].StartTime, Is.LessThanOrEqualTo(5600));
            Assert.That(hitObject.Line.BackingVocals[1].StartTime - hitObject.Line.BackingVocals[0].StartTime, Is.EqualTo(starts[1] - starts[0]), "rigid");

            TypeBeatEditorOperations.MoveBackingVocals(editorBeatmap, hitObject, group, starts, ends, -9000);
            Assert.That(hitObject.Line.BackingVocals[0].StartTime, Is.EqualTo(3000));
            Assert.That(hitObject.Line.BackingVocals[2], Is.EqualTo(before[2]), "the word outside the group never moves");

            assertReloadStable(editorBeatmap);
        }

        [Test]
        public void CopiedBackingWordsPasteIntoAnotherLine()
        {
            var editorBeatmap = createBeatmapWithBacking();
            var source = lineAt(editorBeatmap, 1);
            TypeBeatEditorOperations.SetBackingVocalAnchor(editorBeatmap, source, 1, 0.9);
            var copied = source.Line.BackingVocals;

            var payload = TypeBeatEditorOperations.CopyBackingVocals(source, new[] { 1, 0 })!;
            Assert.That(payload.LineOffset, Is.EqualTo(copied[0].StartTime - 3000));

            // Through the clipboard's own text, as the editor carries it; the line parse does not take it.
            string text = LyricTimingClipboard.Serialize(payload);
            payload = LyricTimingClipboard.TryParseBacking(text)!;
            Assert.That(LyricTimingClipboard.TryParse(text), Is.EqualTo(((LyricTimingClipboard.LineTimingsPayload?)null, (LyricTimingClipboard.UnitTimingsPayload?)null)));

            // Line 0 is [1000..3000]: with the playhead outside it the run sits where it sat in its own line.
            var target = lineAt(editorBeatmap, 0);
            int[] pasted = TypeBeatEditorOperations.PasteBackingVocals(editorBeatmap, target, 99999, payload)!;

            Assert.That(pasted, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(target.Line.BackingVocals.Select(w => (w.Text, w.StartTime, w.Anchor)), Is.EqualTo(new[]
            {
                ("ooh", 1000 + copied[0].StartTime - 3000, (double?)null), ("yeah", 1000 + copied[1].StartTime - 3000, (double?)0.9),
            }));
            Assert.That(target.Line.BackingVocals.All(w => w.EndTime <= 3000), "fitted into the line");
            Assert.That(target.Line.RawText, Is.EqualTo("alpha beta"));

            // At the playhead, beside the words already there.
            pasted = TypeBeatEditorOperations.PasteBackingVocals(editorBeatmap, target, 1500, payload)!;
            Assert.That(target.Line.BackingVocals, Has.Count.EqualTo(4));
            Assert.That(pasted.Select(i => target.Line.BackingVocals[i].Text), Is.EqualTo(new[] { "ooh", "yeah" }));
            Assert.That(target.Line.BackingVocals[pasted[0]].StartTime, Is.EqualTo(1500));

            // A run whose words would start past the line pastes nothing.
            int before = target.Line.BackingVocals.Count;
            Assert.That(TypeBeatEditorOperations.PasteBackingVocals(editorBeatmap, target, 2990, payload), Is.Null);
            Assert.That(target.Line.BackingVocals, Has.Count.EqualTo(before));

            assertReloadStable(editorBeatmap);
        }
    }
}
