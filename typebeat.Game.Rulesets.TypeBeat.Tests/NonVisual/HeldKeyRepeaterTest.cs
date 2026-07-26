// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Song-paced held-key repeat (backlog 32). Holding a character key re-fires it at the cadence
    /// the SONG sings the line at (one press per upcoming cell target, exactly where autoplay would
    /// press), never at the OS repeat rate and never adjusted for how far ahead or behind the player
    /// is. Everything here drives <see cref="HeldKeyRepeater"/> against a real
    /// <see cref="TypingEngine"/> with the same call sequence the playfield's engine ticker makes,
    /// so the times are hand-computable and frame-rate independent.
    /// </summary>
    [TestFixture]
    public class HeldKeyRepeaterTest
    {
        private const int frame_step_ms = 10;

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "T", Title = "S", FolderPath = @"X:\n", AudioFileName = "a.mp3" },
            Lines = lines,
            Granularity = TimingGranularity.Word,
        };

        /// <summary>
        /// Eleven cells at 1000, 1100 .. 2000: ten 'a's then an 'h'. A 100ms cadence is dense
        /// enough that the engage window covers two cells, which is the interesting case.
        /// </summary>
        private static LyricBeatmap denseRun(string text = "aaaaaaaaaah")
            => map(line(text, 1000, 9000, 2100, unit(text, 1000, 2100)));

        /// <summary>Four cells at 1000, 1400, 1800, 2200: a cadence wider than the engage delay.</summary>
        private static LyricBeatmap slowRun(string text = "aaaa")
            => map(line(text, 1000, 9000, 2600, unit(text, 1000, 2600)));

        /// <summary>Frames the playfield's engine ticker: pump the repeater, then tick the engine.</summary>
        private static void run(HeldKeyRepeater repeater, TypingEngine engine, int from, int to)
        {
            for (int t = from + frame_step_ms; t <= to; t += frame_step_ms)
            {
                repeater.Pump(t);
                engine.Update(t);
            }
        }

        private static (TypingEngine engine, HeldKeyRepeater repeater, List<(char c, double time)> recorded) start(LyricBeatmap beatmap)
        {
            var engine = new TypingEngine(beatmap);
            var recorded = new List<(char, double)>();
            var repeater = new HeldKeyRepeater(engine, (c, t) => recorded.Add((c, t)));

            return (engine, repeater, recorded);
        }

        /// <summary>The live key handler's exact sequence for one physical key-down.</summary>
        private static void press(TypingEngine engine, HeldKeyRepeater repeater, List<(char c, double time)> recorded, Key key, char c, double time)
        {
            engine.Update(time);

            if (engine.ProcessKey(c, time))
                recorded.Add((c, time));

            repeater.BeginHold(key, c, time);
        }

        /// <summary>
        /// The schedule IS the song's: every cell past the engage window is pressed exactly on its
        /// own target time, which is where <see cref="Replays.TypeBeatAutoGenerator"/> presses, so a
        /// sustained run judges Perfect (delta 0) all the way down. The two cells whose targets fall
        /// inside the engage window are not dropped, they are clamped to the end of it.
        /// </summary>
        [Test]
        public void HeldKeyFiresOnSuccessiveCellTargets()
        {
            var (engine, repeater, recorded) = start(denseRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            Assert.IsTrue(repeater.IsHolding, "the hold arms from the initial press");

            run(repeater, engine, 1000, 2100);

            // Cells 1..10 scheduled; targets 1100 and 1200 sit inside [1000, 1250) so both clamp to
            // 1250, everything after fires on its own target.
            Assert.AreEqual(
                new double[] { 1000, 1250, 1250, 1300, 1400, 1500, 1600, 1700, 1800, 1900, 2000 },
                recorded.Select(r => r.time).ToArray());

            Assert.IsTrue(recorded.All(r => r.c == 'a'), "every repeat fires the char captured at the initial press");

            // The clamped pair lands late but well inside the Word-granularity Perfect window; every
            // later cell is dead on its target.
            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(150, cells[1].JudgedDelta);
            Assert.AreEqual(50, cells[2].JudgedDelta);

            for (int i = 3; i <= 9; i++)
                Assert.AreEqual(0, cells[i].JudgedDelta, $"cell {i} pressed exactly on its target");

            Assert.IsTrue(Enumerable.Range(0, 10).All(i => cells[i].State == CellState.Correct && cells[i].TypedChar == 'a'));
        }

        /// <summary>
        /// NO BOUNDARY DETECTION: the repeat does not look at what the next cell expects. Holding
        /// 'a' through "aaaaaaaaaah" fires an 'a' at the 'h' cell's target and takes the ordinary
        /// strict-mode punishment (rejected, combo broken, wrong-key streak).
        /// </summary>
        [Test]
        public void HoldingPastTheRunIsPunishedAtTheNextCell()
        {
            var (engine, repeater, recorded) = start(denseRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 2100);

            var h = engine.Lines[0].Cells[10];

            Assert.AreEqual('h', h.Expected);
            Assert.AreEqual(CellState.Untyped, h.State, "strict play rejects the wrong char rather than landing it");
            Assert.AreEqual(10, engine.CaretIndex, "a rejected repeat does not advance the caret");
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys);
            Assert.AreEqual(0, engine.Combo, "the overrun breaks combo like any other wrong key");

            // The rejected repeat is still an EFFECTIVE input, so it is recorded like a live one.
            Assert.AreEqual(2000, recorded[^1].time);
        }

        /// <summary>Allow-wrong-input is the mode where the punishment is literally "the h gets an a".</summary>
        [Test]
        public void HoldingPastTheRunFillsTheNextCellWhenWrongInputIsAllowed()
        {
            var (engine, repeater, recorded) = start(denseRun());
            engine.AllowWrongInput = true;

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 2100);

            var h = engine.Lines[0].Cells[10];

            Assert.AreEqual(CellState.Wrong, h.State);
            Assert.AreEqual('a', h.TypedChar, "the h is filled with an a");
            Assert.IsTrue(engine.IsLineComplete);
            Assert.IsFalse(repeater.IsHolding, "completing the line ends the hold");
        }

        /// <summary>
        /// The engage delay is what keeps ordinary typing safe: a key held for a normal keystroke's
        /// dwell (well under <see cref="HeldKeyRepeater.ENGAGE_DELAY_MS"/>) fires nothing at all,
        /// even on a line dense enough that two cell targets pass while the key is down.
        /// </summary>
        [Test]
        public void ADiscretePressNeverRepeats()
        {
            var (engine, repeater, recorded) = start(denseRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);

            // 240ms of dwell, spanning the 1100 and 1200 targets: still one keystroke.
            run(repeater, engine, 1000, 1240);
            repeater.Release(Key.A);

            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(1, recorded.Count);
            Assert.IsFalse(repeater.IsHolding);

            // Nothing fires after the release either.
            run(repeater, engine, 1240, 2100);
            Assert.AreEqual(1, engine.CaretIndex);
        }

        /// <summary>The other side of the same boundary: 10ms more dwell and the hold engages.</summary>
        [Test]
        public void HoldingPastTheEngageDelayFires()
        {
            var (engine, repeater, recorded) = start(denseRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 1250);

            Assert.AreEqual(3, engine.CaretIndex, "both engage-window cells land the moment the hold engages");
            Assert.AreEqual(3, recorded.Count);
        }

        /// <summary>
        /// With a cadence wider than the engage delay there is no clamping at all: the first repeat
        /// waits for the next cell's target and not a millisecond earlier.
        /// </summary>
        [Test]
        public void TheFirstRepeatWaitsForTheNextCellTarget()
        {
            var (engine, repeater, recorded) = start(slowRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);

            run(repeater, engine, 1000, 1390);
            Assert.AreEqual(1, engine.CaretIndex, "engaged but the song has not reached the next cell yet");

            run(repeater, engine, 1390, 1400);
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.AreEqual(1400, recorded[^1].time);
            Assert.AreEqual(0, engine.Lines[0].Cells[1].JudgedDelta, "pressed exactly where autoplay would");
        }

        /// <summary>
        /// The char is captured at the initial press, post-layout and post-Shift, so a Literate-mod
        /// hold of Shift+A repeats 'A'. Shift state changing mid-hold cannot change it.
        /// </summary>
        [Test]
        public void LiterateHoldRepeatsTheCapital()
        {
            var (engine, repeater, recorded) = start(slowRun("AAAA"));
            engine.CaseSensitive = true;

            press(engine, repeater, recorded, Key.A, 'A', 1000);
            run(repeater, engine, 1000, 2300);

            Assert.IsTrue(recorded.All(r => r.c == 'A'));
            Assert.IsTrue(engine.Lines[0].Cells.All(c => c.State == CellState.Correct && c.TypedChar == 'A'));
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys);
        }

        /// <summary>Under Literate the lower-case hold is wrong every single time, repeats included.</summary>
        [Test]
        public void LiterateHoldOfTheWrongCaseIsRejectedThroughout()
        {
            var (engine, repeater, recorded) = start(slowRun("AAAA"));
            engine.CaseSensitive = true;

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 2300);

            Assert.IsTrue(engine.Lines[0].Cells.All(c => c.State == CellState.Untyped));
            Assert.AreEqual(0, engine.CaretIndex);

            // The initial press plus one repeat per scheduled cell (the caret never advanced, so the
            // caret cell's own target was still ahead of the press and got scheduled too).
            Assert.AreEqual(5, engine.ConsecutiveWrongKeys);
        }

        /// <summary>Mashing rewrites a repeat exactly like a real press: the hold types the whole word.</summary>
        [Test]
        public void MashingRewritesRepeats()
        {
            var (engine, repeater, recorded) = start(slowRun("abcd"));
            engine.MashingEnabled = true;

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 2300);

            Assert.AreEqual("abcd", string.Concat(engine.Lines[0].Cells.Select(c => c.TypedChar)));
            Assert.IsTrue(engine.Lines[0].Cells.All(c => c.State == CellState.Correct));
            Assert.IsTrue(recorded.All(r => r.c == 'a'), "what is RECORDED is the key pressed; the engine does the rewriting");
        }

        /// <summary>
        /// A hold is scoped to the line it started on: finishing that line ends it, and it never
        /// spills into the next line's cells even though the key is still physically down.
        /// </summary>
        [Test]
        public void TheHoldStopsAtTheEndOfItsLine()
        {
            var l0 = line("aaa", 1000, 2500, 2200, unit("aaa", 1000, 2200));
            var l1 = line("aaa", 2500, 5000, 4200, unit("aaa", 3000, 4200));
            var (engine, repeater, recorded) = start(map(l0, l1));

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 1800);

            Assert.IsTrue(engine.IsLineComplete);
            Assert.IsFalse(repeater.IsHolding, "the hold ends with the line, it does not carry over");

            // Line 1 activates and runs its whole window with the key still down: untouched, and it
            // seals with every cell missed.
            run(repeater, engine, 1800, 5100);
            Assert.IsTrue(engine.Lines[1].Cells.All(c => c.State == CellState.Missed));
            Assert.AreEqual(3, recorded.Count);
        }

        /// <summary>Fletcher rolls the caret straight on to the next line; the hold ends there too.</summary>
        [Test]
        public void FletcherRollForwardEndsTheHold()
        {
            var l0 = line("aaa", 1000, 2500, 2200, unit("aaa", 1000, 2200));
            var l1 = line("aaa", 2500, 5000, 4200, unit("aaa", 3000, 4200));
            var (engine, repeater, recorded) = start(map(l0, l1));
            engine.FletcherEnabled = true;

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 1800);

            Assert.AreEqual(1, engine.ActiveLineIndex, "Fletcher parks the caret on the next line");
            Assert.IsFalse(repeater.IsHolding);
            Assert.AreEqual(3, recorded.Count);
            Assert.IsTrue(engine.Lines[1].Cells.All(c => c.State == CellState.Untyped));
        }

        /// <summary>An instrumental gap has no active line, so there is nothing to hold on to.</summary>
        [Test]
        public void NoLineActiveMeansNoHold()
        {
            var (engine, repeater, recorded) = start(denseRun());

            engine.Update(0);
            Assert.IsFalse(engine.LineIsActive);

            press(engine, repeater, recorded, Key.A, 'a', 0);

            Assert.IsFalse(repeater.IsHolding);
            Assert.IsEmpty(recorded);

            run(repeater, engine, 0, 9100);
            Assert.IsTrue(engine.Lines[0].Cells.All(c => c.State == CellState.Missed));
        }

        /// <summary>
        /// Unpause / seek / stall safety: a clock discontinuity larger than
        /// <see cref="HeldKeyRepeater.MAX_ADVANCE_MS"/> drops the hold instead of dumping the
        /// backlog of repeats it would have accumulated.
        /// </summary>
        [Test]
        public void AClockJumpDropsTheHoldInsteadOfBursting()
        {
            var (engine, repeater, recorded) = start(denseRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            Assert.AreEqual(10, repeater.PendingRepeats, "repeats were scheduled and would all be due");

            Assert.AreEqual(0, repeater.Pump(1600));
            Assert.IsFalse(repeater.IsHolding);
            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(1, recorded.Count);
        }

        /// <summary>
        /// The player's drift is preserved, never corrected: a hold started late does not
        /// retroactively type the cells the song has already sung past, and the repeats that do fire
        /// land on the cells the caret is behind on, judged honestly late.
        /// </summary>
        [Test]
        public void AHoldStartedLateStaysLate()
        {
            var (engine, repeater, recorded) = start(denseRun());

            // First press lands at 1500, five cells after the song has moved on.
            engine.Update(1500);
            press(engine, repeater, recorded, Key.A, 'a', 1500);

            Assert.AreEqual(500, engine.Lines[0].Cells[0].JudgedDelta);

            // Only cells 5..10 (targets 1500..2000) are scheduled; cells 1..4 are already behind the
            // song and are never typed for free.
            Assert.AreEqual(6, repeater.PendingRepeats);

            run(repeater, engine, 1500, 2100);

            Assert.AreEqual(new double[] { 1500, 1750, 1750, 1750, 1800, 1900, 2000 }, recorded.Select(r => r.time).ToArray());
            Assert.AreEqual(7, engine.CaretIndex, "the caret still trails the playhead by four cells");
            Assert.AreEqual(650, engine.Lines[0].Cells[1].JudgedDelta, "the repeat lands on the cell the player is behind on");
        }

        /// <summary>
        /// DETERMINISM/REPLAY CONTRACT: a synthesized repeat is recorded as an ordinary frame at an
        /// integral millisecond, and feeding the recorded (char, time) stream back through a fresh
        /// engine (exactly what the replay feeder does) reproduces the live run bit for bit.
        /// </summary>
        [Test]
        public void RecordedRepeatsReplayBitExact()
        {
            var (live, repeater, recorded) = start(denseRun());
            live.AllowWrongInput = true;

            press(live, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, live, 1000, 2100);

            Assert.IsTrue(recorded.All(r => r.time == Math.Round(r.time)), "repeat times are integral, lossless in .osr");
            Assert.IsTrue(recorded.Zip(recorded.Skip(1)).All(p => p.First.time <= p.Second.time), "monotonic");

            var replayed = new TypingEngine(denseRun()) { AllowWrongInput = true };

            foreach (var (c, time) in recorded)
            {
                replayed.Update(time);
                replayed.ProcessKey(c, time);
            }

            // Drive the replayed engine to the same end point so sealing matches.
            replayed.Update(9000);
            live.Update(9000);

            Assert.AreEqual(live.Score, replayed.Score);
            Assert.AreEqual(live.MaxCombo, replayed.MaxCombo);
            Assert.AreEqual(live.LiveAccuracy, replayed.LiveAccuracy);
            Assert.AreEqual(live.BuildResults().SyncPercent, replayed.BuildResults().SyncPercent);

            Assert.IsTrue(live.Lines[0].Cells.Zip(replayed.Lines[0].Cells)
                              .All(p => p.First.State == p.Second.State
                                        && p.First.TypedChar == p.Second.TypedChar
                                        && Nullable.Equals(p.First.JudgedDelta, p.Second.JudgedDelta)));
        }

        /// <summary>
        /// The feature cannot touch anyone who types normally: an identical discrete-press run with
        /// the repeater armed on every keystroke produces byte-identical engine state to the same
        /// run without it.
        /// </summary>
        [Test]
        public void ADiscretePressRunIsUnchangedByTheFeature()
        {
            var script = new (char c, int time)[] { ('a', 1020), ('a', 1130), ('a', 1210), ('a', 1350), ('a', 1460), ('h', 2010) };

            var bare = new TypingEngine(denseRun());
            var (armed, repeater, recorded) = start(denseRun());

            int cursor = 1000;
            bare.Update(cursor);
            armed.Update(cursor);

            foreach (var (c, time) in script)
            {
                // Frames up to the keystroke, then the keystroke itself; the armed run additionally
                // holds each key for 80ms of dwell before releasing it.
                for (int t = cursor + frame_step_ms; t <= time; t += frame_step_ms)
                {
                    bare.Update(t);
                    repeater.Pump(t);
                    armed.Update(t);
                }

                bare.Update(time);
                bare.ProcessKey(c, time);

                press(armed, repeater, recorded, c == 'h' ? Key.H : Key.A, c, time);

                for (int t = time + frame_step_ms; t <= time + 80; t += frame_step_ms)
                {
                    bare.Update(t);
                    repeater.Pump(t);
                    armed.Update(t);
                }

                repeater.Release(c == 'h' ? Key.H : Key.A);
                cursor = time + 80;
            }

            bare.Update(9000);
            repeater.Pump(9000);
            armed.Update(9000);

            Assert.AreEqual(script.Length, recorded.Count, "no synthesized press crept in");
            Assert.AreEqual(bare.Score, armed.Score);
            Assert.AreEqual(bare.MaxCombo, armed.MaxCombo);
            Assert.AreEqual(bare.LiveAccuracy, armed.LiveAccuracy);
            Assert.AreEqual(bare.BuildResults().SyncPercent, armed.BuildResults().SyncPercent);
            Assert.AreEqual(bare.CaretIndex, armed.CaretIndex);

            Assert.IsTrue(bare.Lines[0].Cells.Zip(armed.Lines[0].Cells)
                              .All(p => p.First.State == p.Second.State
                                        && p.First.TypedChar == p.Second.TypedChar
                                        && Nullable.Equals(p.First.JudgedDelta, p.Second.JudgedDelta)));
        }
    }
}
