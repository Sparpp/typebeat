// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// The line box's hold marks (backlog 447): '=' straight after a letter holds it, and a run of '*'
// straight after that '=' is the hold's tick count. The marks are editor-only: shown by
// PipeDisplayText, read and stripped on commit, never stored.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class HoldMarkerEditorTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        [Test]
        public void ReadStripsTheMarksAndCountsOnlyTheRunAfterTheMarker()
        {
            var (text, words) = HoldTickMarkers.Read("lo=**ve  f*ck", out bool refused);

            Assert.That(text, Is.EqualTo("love f*ck"));
            Assert.That(refused, Is.False);
            Assert.That(words[0].Holds, Is.EqualTo(new Dictionary<int, int> { [1] = 2 }));
            Assert.That(words[1].Holds, Is.Empty, "a star after an unmarked letter is punctuation");
        }

        [Test]
        public void DisplayRoundTripCommitsUnchanged()
        {
            var beatmap = createBeatmap("love you", withHold: (0, 1, 2));
            var line = lineAt(beatmap, 0);
            var before = line.Line;

            string shown = TypeBeatEditorOperations.PipeDisplayText(before);
            Assert.That(shown, Is.EqualTo("lo=**ve you"));
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(before, withHolds: false), Is.EqualTo("love you"));

            bool refused = false;
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, shown, () => refused = true), Is.True);

            Assert.That(line.Line, Is.SameAs(before), "an unchanged box is a no-op");
            Assert.That(refused, Is.False);
        }

        [Test]
        public void AMarkerAddsTheHoldTheDefaultSpanGives()
        {
            var beatmap = createBeatmap("love you");
            var line = lineAt(beatmap, 0);
            var expected = TypeBeatEditorOperations.DefaultHold(line.Line.Units[0], 1, 0);

            Assert.That(expected, Is.Not.Null);
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "lo=ve you"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("love you"), "the marker is never stored");
            Assert.That(line.Line.Units[0].Objects.Holds, Is.EqualTo(new[] { expected!.Value }));
            Assert.That(line.Line.Units[1].Objects.Holds, Is.Empty);

            // The same span AddHold gives on an untouched copy.
            var other = createBeatmap("love you");
            Assert.That(TypeBeatEditorOperations.AddHold(other, lineAt(other, 0), 0, 1, 0), Is.EqualTo(expected));
        }

        [Test]
        public void DeletingTheMarkerRemovesTheHold()
        {
            var beatmap = createBeatmap("love you", withHold: (0, 1, 0));
            var line = lineAt(beatmap, 0);

            Assert.That(TypeBeatEditorOperations.PipeDisplayText(line.Line), Is.EqualTo("lo=ve you"));
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "love you"), Is.True);

            Assert.That(line.Line.Units[0].Objects.Holds, Is.Empty);
        }

        [Test]
        public void StarsAfterTheMarkerAreTicksAndAKeptHoldKeepsItsTiming()
        {
            var beatmap = createBeatmap("love you", withHold: (0, 1, 2));
            var line = lineAt(beatmap, 0);
            var hold = line.Line.Units[0].Objects.Holds[0];

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "lo=*ve you"), Is.True);

            var kept = line.Line.Units[0].Objects.Holds.Single();
            Assert.That((kept.Char, kept.StartTime, kept.EndTime), Is.EqualTo((hold.Char, hold.StartTime, hold.EndTime)));
            Assert.That(kept.Ticks, Is.EqualTo(HoldTicks.Even(hold.StartTime, hold.EndTime, 1)));

            // A new hold takes its ticks too.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "lo=*ve yo=***u"), Is.True);

            var added = line.Line.Units[1].Objects.Holds.Single();
            Assert.That(added.Char, Is.EqualTo(1));
            Assert.That(added.Ticks, Is.EqualTo(HoldTicks.Even(added.StartTime, added.EndTime, 3)));
            Assert.That(line.Line.Units[0].Objects.Holds.Single().Ticks.Count, Is.EqualTo(1), "the other hold is untouched");
        }

        [Test]
        public void ALiteralStarStaysPunctuation()
        {
            var beatmap = createBeatmap("f*ck it");
            var line = lineAt(beatmap, 0);

            // A star after an unmarked letter, and after a held letter once the run is closed.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "f==*ck it"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("f*ck it"));
            var hold = line.Line.Units[0].Objects.Holds.Single();
            Assert.That((hold.Char, hold.Ticks.Count), Is.EqualTo((0, 0)));

            // The display closes the run itself, so the box reads back exactly.
            string shown = TypeBeatEditorOperations.PipeDisplayText(line.Line);
            Assert.That(shown, Is.EqualTo("f==*ck it"));

            var before = line.Line;
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, shown), Is.True);
            Assert.That(line.Line, Is.SameAs(before));

            // A held word with a tick and a literal star elsewhere in it.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "f*c=*k it"), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("f*ck it"));
            hold = line.Line.Units[0].Objects.Holds.Single();
            Assert.That((hold.Char, hold.Ticks.Count), Is.EqualTo((2, 1)));
        }

        [Test]
        public void AMarkerOnAPausedWordHoldsInsideItsStretch()
        {
            var beatmap = createBeatmap("please go");
            var line = lineAt(beatmap, 0);
            var rest = TypeBeatEditorOperations.InsertWordPause(beatmap, line, 0, 1450);

            Assert.That(rest, Is.Not.Null);
            Assert.That(rest!.Value.SplitChar, Is.EqualTo(3), "the premise: the 'l' is sung before the rest");

            string shown = TypeBeatEditorOperations.PipeDisplayText(line.Line);
            var expected = TypeBeatEditorOperations.DefaultHold(line.Line.Units[0], 1, double.NaN);
            bool refused = false;

            Assert.That(expected, Is.Not.Null);
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, shown.Insert(shown.IndexOf('l') + 1, "="), () => refused = true), Is.True);

            // Backlog 448: a held word may carry rests, and the hold is let go before the rest starts.
            Assert.That(refused, Is.False);
            Assert.That(line.Line.Units[0].Pauses, Is.EqualTo(new[] { rest.Value }), "the rest is kept");
            Assert.That(line.Line.Units[0].Objects.Holds, Is.EqualTo(new[] { expected!.Value }));
            Assert.That(expected.Value.EndTime, Is.LessThanOrEqualTo(rest.Value.StartTime));
        }

        [TestCase("love = you")]
        [TestCase("=love you")]
        [TestCase("love, = you")]
        [TestCase("love,= you")]
        public void AMarkerOnNoLetterIsDroppedAndReported(string box)
        {
            var beatmap = createBeatmap("love, you");
            var line = lineAt(beatmap, 0);
            bool refused = false;

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, box, () => refused = true), Is.True);

            Assert.That(refused, Is.True);
            Assert.That(line.Line.Units.All(u => u.Objects.Holds.Count == 0));
            Assert.That(line.Line.RawText.Contains('='), Is.False);
        }

        [Test]
        public void TheRomanisingCommitReadsTheMarksToo()
        {
            var beatmap = createBeatmap("love you");
            var line = lineAt(beatmap, 0);
            bool refused = false;

            // The Latin word keeps its mark; the Cyrillic one romanises to other letters, so its mark is dropped.
            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "lo=ve ми=р", () => refused = true), Is.True);

            Assert.That(line.Line.RawText, Is.EqualTo("love mir"));
            Assert.That(line.Line.Units[0].Objects.Holds.Single().Char, Is.EqualTo(1));
            Assert.That(line.Line.Units[1].Objects.Holds, Is.Empty);
            Assert.That(refused, Is.True);
        }

        [Test]
        public void OneCommitIsOneUndoStep()
        {
            var beatmap = createBeatmap("love you");
            var line = lineAt(beatmap, 0);
            var changeHandler = new RulesetBeatmapChangeHandler(beatmap, new TypeBeatRuleset());

            Assert.That(TypeBeatEditorOperations.SetLineText(beatmap, line, "lo=**ve yo=u"), Is.True);
            Assert.That(lineAt(beatmap, 0).Line.Units.Sum(u => u.Objects.Holds.Count), Is.EqualTo(2));

            changeHandler.RestoreState(-1);
            Assert.That(lineAt(beatmap, 0).Line.Units.Sum(u => u.Objects.Holds.Count), Is.EqualTo(0));
            Assert.That(changeHandler.CanUndo.Value, Is.False);

            changeHandler.RestoreState(1);
            Assert.That(TypeBeatEditorOperations.PipeDisplayText(lineAt(beatmap, 0).Line), Is.EqualTo("lo=**ve yo=u"));
        }

        [Test]
        public void ARestClickSkipsTheHoldMarks()
        {
            Assert.That(LineListPanel.PipeCaretFor("lo=**ve you", 3), Is.EqualTo(6), "in front of the 'e'");
            Assert.That(LineListPanel.PipeCaretFor("lo=**v|e you", 5), Is.EqualTo(9), "in front of the 'y'");
            Assert.That(HoldTickMarkers.WithoutMarks("f==*c=*k"), Is.EqualTo("f*ck"));
        }

        /// <summary>A Word-granularity line of 1000 ms words from 1000, with an optional hold (word, char, ticks).</summary>
        private static EditorBeatmap createBeatmap(string text, (int Word, int Char, int Ticks)? withHold = null)
        {
            string[] words = text.Split(' ');
            var units = words.Select((w, i) => new TimedUnit
            {
                Text = w,
                StartTime = 1000 + i * 1000,
                EndTime = 2000 + i * 1000,
                Source = TimingSource.Explicit,
            }).ToArray();

            if (withHold is (int word, int c, int ticks))
            {
                var hold = TypeBeatEditorOperations.DefaultHold(units[word], c, 0)!.Value;
                hold = hold with { Ticks = HoldTicks.Even(hold.StartTime, hold.EndTime, ticks) };
                units[word] = units[word].WithObjects(units[word].Objects with { Holds = new[] { hold } });
            }

            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "Op";
            beatmap.Metadata.Title = "Hold";
            beatmap.Metadata.AudioFile = "audio.mp3";

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = units[0].StartTime,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = text,
                    StartTime = units[0].StartTime,
                    EndTime = units[^1].EndTime,
                    SingEndTime = units[^1].EndTime,
                    Units = units,
                },
                Granularity = TimingGranularity.Word,
            });

            return new EditorBeatmap(beatmap);
        }

        private static TypeBeatHitObject lineAt(EditorBeatmap editorBeatmap, int index)
            => TypeBeatEditorOperations.OrderedLines(editorBeatmap)[index];
    }
}
