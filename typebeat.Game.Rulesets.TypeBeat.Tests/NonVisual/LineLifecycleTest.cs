// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Direct pins on the line entry and sealing decisions TypingEngine delegates to LineLifecycle.
// FletcherEngineTest, ManualNewlinesTest, EarlyFinishTest and their WireCompat twins hold the same
// rules through played runs; these hold each decision on its own, against lines whose cell states
// are set by hand, so a failure names the rule rather than a keystroke sequence.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class LineLifecycleTest
    {
        private static readonly LineLifecycle.Rules pinned = new LineLifecycle.Rules(Fletcher: false, BoundedRush: false, FlexibleLineSnap: false, ManualNewlines: false, EarlyFinish: false);
        private static readonly LineLifecycle.Rules flexible = new LineLifecycle.Rules(Fletcher: true, BoundedRush: true, FlexibleLineSnap: true, ManualNewlines: false, EarlyFinish: false);
        private static readonly LineLifecycle.Rules manual = flexible with { ManualNewlines = true };

        private static LyricLine line(string text, double start, double end, double unitStart, double unitEnd)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = unitEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = unitStart, EndTime = unitEnd } },
            };

        /// <summary>Three two-character lines, 4 s apart, each sung for its first second.</summary>
        private static TypingEngine engine() => new TypingEngine(new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[]
            {
                line("ab", 0, 4000, 2000, 3000),
                line("cd", 4000, 8000, 6000, 7000),
                line("ef", 8000, 12000, 10000, 11000),
            },
            Granularity = TimingGranularity.Line,
        });

        private static void typeOut(TypingLine typingLine, CellState state = CellState.Correct)
        {
            foreach (var cell in typingLine.Cells.Where(c => c.IsTypeable))
                cell.State = state;
        }

        [Test]
        public void AnUntypedOrAbandonedCellIsOwedAndAResolvedOneIsNot()
        {
            var lines = engine().Lines;

            Assert.That(LineLifecycle.HasUntypedTypeable(lines[0]), Is.True, "nothing typed yet");

            typeOut(lines[0]);
            Assert.That(LineLifecycle.HasUntypedTypeable(lines[0]), Is.False, "every cell correct");

            typeOut(lines[0], CellState.Wrong);
            Assert.That(LineLifecycle.HasUntypedTypeable(lines[0]), Is.False, "a wrong cell was finished");

            lines[0].Cells.First(c => c.IsTypeable).State = CellState.Abandoned;
            Assert.That(LineLifecycle.HasUntypedTypeable(lines[0]), Is.True, "an abandoned cell is still owed");
        }

        [Test]
        public void ALineSealsAtItsDeadlineOrEarlyOnceNothingIsOwed()
        {
            var e = engine();
            var first = e.Lines[0];
            Func<TypingLine, double> seal = e.EffectiveSealTime;
            double deadline = e.EffectiveSealTime(first);

            Assert.Multiple(() =>
            {
                Assert.That(LineLifecycle.CanSeal(first, first.EndTime - 1, seal), Is.False, "never before EndTime");
                Assert.That(LineLifecycle.CanSeal(first, deadline, seal), Is.True, "always at the hard deadline");
            });

            if (deadline > first.EndTime)
            {
                Assert.That(LineLifecycle.CanSeal(first, first.EndTime, seal), Is.False, "inside the grace with cells owed");
                typeOut(first);
                Assert.That(LineLifecycle.CanSeal(first, first.EndTime, seal), Is.True, "inside the grace with nothing owed");
            }
        }

        [Test]
        public void OnlyTheFinalLineFinishesEarlyAndOnlyWhenEveryCellIsCorrect()
        {
            var lines = engine().Lines;

            typeOut(lines[1]);
            typeOut(lines[2]);

            Assert.Multiple(() =>
            {
                Assert.That(LineLifecycle.FinishedEarly(lines, 1, earlyFinish: true), Is.False, "not the final line");
                Assert.That(LineLifecycle.FinishedEarly(lines, 2, earlyFinish: false), Is.False, "not in the era");
                Assert.That(LineLifecycle.FinishedEarly(lines, 2, earlyFinish: true), Is.True);
            });

            lines[2].Cells.Last(c => c.IsTypeable).State = CellState.Wrong;
            Assert.That(LineLifecycle.FinishedEarly(lines, 2, earlyFinish: true), Is.False, "a wrong cell is still fixable");
        }

        [Test]
        public void TheRushBoundOpensEntryOneDragGraceBeforeActivation()
        {
            var next = engine().Lines[1];
            double opens = next.ActivationTime - TypingEngine.FLETCHER_DRAG_GRACE_MS;

            Assert.Multiple(() =>
            {
                Assert.That(LineLifecycle.EntryOpensAt(next, boundedRush: false), Is.EqualTo(next.ActivationTime));
                Assert.That(LineLifecycle.EntryOpensAt(next, boundedRush: true), Is.EqualTo(opens));
                Assert.That(LineLifecycle.EntryPermitted(next, double.NegativeInfinity, boundedRush: false), Is.True, "unbounded: always");
                Assert.That(LineLifecycle.EntryPermitted(next, opens - 1, boundedRush: true), Is.False);
                Assert.That(LineLifecycle.EntryPermitted(next, opens, boundedRush: true), Is.True);
            });
        }

        [Test]
        public void OnlyAManualCaretOnALiveLineAwaitsEntry()
        {
            var lines = engine().Lines;
            double early = lines[1].ActivationTime - TypingEngine.FLETCHER_DRAG_GRACE_MS - 1;

            Assert.Multiple(() =>
            {
                Assert.That(LineLifecycle.AwaitingEntry(manual, lines, 1, early), Is.True);
                Assert.That(LineLifecycle.AwaitingEntry(manual, lines, 1, lines[1].ActivationTime), Is.False, "the window is open");
                Assert.That(LineLifecycle.AwaitingEntry(manual, lines, -1, early), Is.False, "no live line");
                Assert.That(LineLifecycle.AwaitingEntry(flexible, lines, 1, early), Is.False, "not under manual newlines");
                Assert.That(LineLifecycle.AwaitingEntry(manual with { Fletcher = false }, lines, 1, early), Is.False, "not with a pinned caret");
            });
        }

        [Test]
        public void ADraggingCaretHoldsItsLineOpenForOneDragGrace()
        {
            var e = engine();
            var lines = e.Lines;
            Func<TypingLine, double> seal = e.EffectiveSealTime;
            double deadline = e.EffectiveSealTime(lines[0]);
            double cutoff = deadline + TypingEngine.FLETCHER_DRAG_GRACE_MS;
            var onIt = new LineLifecycle.Caret(0, 0);
            var elsewhere = new LineLifecycle.Caret(1, 0);

            Assert.Multiple(() =>
            {
                Assert.That(LineLifecycle.SealPermitted(pinned, lines, 0, deadline, onIt, abandoned: false, seal), Is.True, "a pinned caret never holds a line");
                Assert.That(LineLifecycle.SealPermitted(flexible, lines, 0, deadline, elsewhere, abandoned: false, seal), Is.True, "nobody is dragging on it");
                Assert.That(LineLifecycle.SealPermitted(flexible, lines, 0, cutoff - 1, onIt, abandoned: false, seal), Is.False, "the drag grace");
                Assert.That(LineLifecycle.SealPermitted(flexible, lines, 0, cutoff, onIt, abandoned: false, seal), Is.True, "the cutoff");
                Assert.That(LineLifecycle.SealPermitted(flexible, lines, 0, cutoff - 1, elsewhere, abandoned: true, seal), Is.False,
                    "a line walked out of with a line skip keeps the grace");
            });

            typeOut(lines[0]);
            Assert.That(LineLifecycle.SealPermitted(flexible, lines, 0, deadline, onIt, abandoned: false, seal), Is.True, "nothing owed, no drag to protect");
        }

        [Test]
        public void ManualNewlinesHoldAFinishedLineUntilTheCutoff()
        {
            var e = engine();
            var lines = e.Lines;
            Func<TypingLine, double> seal = e.EffectiveSealTime;
            double cutoff = e.EffectiveSealTime(lines[0]) + TypingEngine.FLETCHER_DRAG_GRACE_MS;

            Assert.That(LineLifecycle.ManualNewlineHoldsLineOpen(manual, lines, 0, new LineLifecycle.Caret(0, 2)), Is.False, "a line still owing a character is the drag rule's");

            typeOut(lines[0]);
            typeOut(lines[2]);

            Assert.Multiple(() =>
            {
                Assert.That(LineLifecycle.ManualNewlineHoldsLineOpen(manual, lines, 0, new LineLifecycle.Caret(0, 2)), Is.True, "standing on it");
                Assert.That(LineLifecycle.ManualNewlineHoldsLineOpen(manual, lines, 0, new LineLifecycle.Caret(1, 0)), Is.True, "one backspace below it");
                Assert.That(LineLifecycle.ManualNewlineHoldsLineOpen(manual, lines, 0, new LineLifecycle.Caret(1, 1)), Is.False, "typing the next line");
                Assert.That(LineLifecycle.ManualNewlineHoldsLineOpen(flexible, lines, 0, new LineLifecycle.Caret(0, 2)), Is.False, "not under the automatic hand-over");
                Assert.That(LineLifecycle.ManualNewlineHoldsLineOpen(manual, lines, 2, new LineLifecycle.Caret(2, 2)), Is.False, "the last line seals on its own deadline");

                Assert.That(LineLifecycle.SealPermitted(manual, lines, 0, cutoff - 1, new LineLifecycle.Caret(0, 2), abandoned: false, seal), Is.False);
                Assert.That(LineLifecycle.SealPermitted(manual, lines, 0, cutoff, new LineLifecycle.Caret(0, 2), abandoned: false, seal), Is.True);
            });
        }

        [Test]
        public void TimeMovesAFinishedCaretOnlyForAnUnpinnedAutomaticCaret()
        {
            Assert.Multiple(() =>
            {
                Assert.That(LineLifecycle.TimeMayMoveAFinishedCaret(flexible, isFinished: false), Is.True);
                Assert.That(LineLifecycle.TimeMayMoveAFinishedCaret(flexible with { FlexibleLineSnap = false }, isFinished: false), Is.True, "the bound alone still defers the roll");
                Assert.That(LineLifecycle.TimeMayMoveAFinishedCaret(flexible with { FlexibleLineSnap = false, BoundedRush = false }, isFinished: false), Is.False);
                Assert.That(LineLifecycle.TimeMayMoveAFinishedCaret(pinned with { FlexibleLineSnap = true }, isFinished: false), Is.False, "a pinned caret");
                Assert.That(LineLifecycle.TimeMayMoveAFinishedCaret(flexible, isFinished: true), Is.False, "a finished run");
                Assert.That(LineLifecycle.TimeMayMoveAFinishedCaret(manual, isFinished: false), Is.False, "a manual caret is the player's to hand over");
            });
        }
    }
}
