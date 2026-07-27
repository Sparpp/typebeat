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
        /// The schedule IS the song's: with no engage delay, every scheduled cell is pressed exactly
        /// on its own target time (no clamping, whatever the cadence), which is where
        /// <see cref="Replays.TypeBeatAutoGenerator"/> presses, so a sustained run judges Perfect
        /// (delta 0) all the way down, even on a line this dense.
        /// </summary>
        [Test]
        public void HeldKeyFiresOnSuccessiveCellTargets()
        {
            var (engine, repeater, recorded) = start(denseRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            Assert.IsTrue(repeater.IsHolding, "the hold arms from the initial press");

            run(repeater, engine, 1000, 2100);

            // Cells 1..9 (the rest of the a-run) each fire on their own target, no clamping. The
            // 'h' at 2000 is where the run ends, so the match gate stops there.
            Assert.AreEqual(
                new double[] { 1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700, 1800, 1900 },
                recorded.Select(r => r.time).ToArray());

            Assert.IsTrue(recorded.All(r => r.c == 'a'), "every repeat fires the char captured at the initial press");

            var cells = engine.Lines[0].Cells;

            for (int i = 0; i <= 9; i++)
                Assert.AreEqual(0, cells[i].JudgedDelta, $"cell {i} pressed exactly on its target");

            Assert.IsTrue(Enumerable.Range(0, 10).All(i => cells[i].State == CellState.Correct && cells[i].TypedChar == 'a'));
        }

        /// <summary>
        /// MATCH GATE: a hold sustains a RUN and stops where the run does. Holding 'a' through
        /// "aaaaaaaaaah" types the ten a's and sends nothing at all at the 'h': no press, no
        /// rejection, no combo break, no wrong-key streak, and the hold itself ends there.
        /// </summary>
        [Test]
        public void HoldingPastTheRunFiresNothingFurther()
        {
            var (engine, repeater, recorded) = start(denseRun());
            var rejected = new List<char>();
            engine.WrongKeyRejected += rejected.Add;

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 2100);

            var h = engine.Lines[0].Cells[10];

            Assert.AreEqual('h', h.Expected);
            Assert.AreEqual(CellState.Untyped, h.State, "the repeat is never sent, so nothing lands on the h");
            Assert.AreEqual(10, engine.CaretIndex, "the caret stops at the end of the run");
            Assert.IsEmpty(rejected, "a synthesized press can never be a wrong key");
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys);
            Assert.AreEqual(10, engine.Combo, "the run's combo is intact; nothing broke it");
            Assert.IsFalse(repeater.IsHolding, "the gate ends the hold where the run ends");

            // The last thing recorded is the last real cell of the run, not an overrun.
            Assert.AreEqual(1900, recorded[^1].time);
        }

        /// <summary>
        /// Allow-wrong-input is the mode where an overrun used to be literally "the h gets an a".
        /// The gate is mode-independent: a repeat that would not be judged correct is never sent, so
        /// this mode gets no red Wrong fills out of a hold either.
        /// </summary>
        [Test]
        public void HoldingPastTheRunFillsNothingEvenWhenWrongInputIsAllowed()
        {
            var (engine, repeater, recorded) = start(denseRun());
            engine.AllowWrongInput = true;

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 2100);

            var h = engine.Lines[0].Cells[10];

            Assert.AreEqual(CellState.Untyped, h.State, "no red fill: the press was never sent");
            Assert.IsNull(h.TypedChar);
            Assert.IsFalse(engine.IsLineComplete, "the line is left one cell short, for the player to type");
            Assert.IsFalse(repeater.IsHolding, "the gate ends the hold where the run ends");
            Assert.AreEqual(10, recorded.Count, "the initial press plus nine matching repeats");
        }

        /// <summary>
        /// With no engage delay, release is now the ONLY thing that stops a repeat: releasing before
        /// the next cell's target arrives, exactly like a normal keystroke's release, fires nothing
        /// at all, even on a line dense enough that the target is close behind.
        /// </summary>
        [Test]
        public void ReleasedBeforeTheNextTargetFiresNothing()
        {
            var (engine, repeater, recorded) = start(denseRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);

            // 80ms of dwell, a normal keystroke's release, well before the 1100 target.
            run(repeater, engine, 1000, 1080);
            repeater.Release(Key.A);

            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(1, recorded.Count);
            Assert.IsFalse(repeater.IsHolding);

            // Nothing fires after the release either, even once the song reaches the target the
            // hold would otherwise have caught.
            run(repeater, engine, 1080, 2100);
            Assert.AreEqual(1, engine.CaretIndex);
        }

        /// <summary>
        /// The other side of the same boundary: still physically held when the next cell's target
        /// arrives, the repeat fires right there, no minimum dwell required and nothing clamped late.
        /// This is the accepted tradeoff: an ordinarily-typed key that is not released crisply on a
        /// dense line will double-fire exactly like this.
        /// </summary>
        [Test]
        public void StillHeldAtTheNextTargetFires()
        {
            var (engine, repeater, recorded) = start(denseRun());

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 1100);

            Assert.AreEqual(2, engine.CaretIndex, "the repeat fired the moment its target arrived, no engage delay");
            Assert.AreEqual(2, recorded.Count);
            Assert.AreEqual(1100, recorded[^1].time);
            Assert.AreEqual(0, engine.Lines[0].Cells[1].JudgedDelta, "pressed exactly on target, not clamped late");
        }

        /// <summary>
        /// The flow-in is seamless even when the very next target lands only a handful of
        /// milliseconds after the press: there is no floor under how soon the first repeat can fire.
        /// </summary>
        [Test]
        public void HoldFlowsStraightIntoTheNextTargetEvenAFewMsLater()
        {
            var tightRun = map(line("aa", 1000, 9000, 1008, unit("aa", 1000, 1008)));
            var (engine, repeater, recorded) = start(tightRun);

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            Assert.AreEqual(1004, engine.Lines[0].Cells[1].TargetTime, "the next cell's target sits only 4ms after the press");

            // A frame landing right on the target; no OS-style engage delay stands in the way.
            repeater.Pump(1004);
            engine.Update(1004);

            Assert.AreEqual(2, engine.CaretIndex, "the repeat fired the instant its target arrived");
            Assert.AreEqual(2, recorded.Count);
            Assert.AreEqual(1004, recorded[^1].time);
            Assert.AreEqual(0, engine.Lines[0].Cells[1].JudgedDelta, "pressed exactly on target");
        }

        /// <summary>
        /// The first repeat waits for the next cell's target and not a millisecond earlier; there is
        /// no clamping at any cadence, wide or narrow.
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

        /// <summary>
        /// The gate applies the Literate mod's exact-case rule too, so a wrong-case hold costs
        /// exactly the ONE real keystroke the player physically made. The repeats that would each
        /// have been another wrong key are never sent.
        /// </summary>
        [Test]
        public void LiterateHoldOfTheWrongCaseCostsOnlyTheInitialPress()
        {
            var (engine, repeater, recorded) = start(slowRun("AAAA"));
            engine.CaseSensitive = true;

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            run(repeater, engine, 1000, 2300);

            Assert.IsTrue(engine.Lines[0].Cells.All(c => c.State == CellState.Untyped));
            Assert.AreEqual(0, engine.CaretIndex);
            Assert.IsFalse(repeater.IsHolding);

            // Only the physical press the player made; the mash-fail streak is not fed by synthesis.
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys);
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
        /// REGRESSION (backlog 43, first half): a lagging player's ordinary keystroke must stay ONE
        /// keystroke. Scheduling repeats at raw absolute cell targets put the first one on a cell the
        /// caret had not reached yet, which for a lagging player falls INSIDE an ordinary keystroke's
        /// 60-100ms dwell, so a duplicate press was synthesized a few ms behind the real one. The
        /// first repeat is now a whole song cadence away whatever the drift.
        ///
        /// <para>Deliberately a run of the SAME letter, where the duplicate would be a perfectly
        /// correct press: the match gate cannot mask this one, so it pins the drift-shifted schedule
        /// on its own.</para>
        /// </summary>
        [Test]
        public void ALaggingCorrectPressFiresNoRepeatInsideTheKeyDwell()
        {
            var (engine, repeater, recorded) = start(denseRun());

            // 250ms behind the song: cell 0's target was 1000 and the player gets there at 1250.
            // Cells 1 and 2 (targets 1100, 1200) are behind the press; cell 3's 1300 is what the
            // absolute schedule fired at, comfortably inside the dwell below.
            engine.Update(1250);
            press(engine, repeater, recorded, Key.A, 'a', 1250);

            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[0].State, "the press itself is correct and accepted");

            // 80ms of dwell then release, exactly the ordinary keystroke of
            // ReleasedBeforeTheNextTargetFiresNothing, which fires nothing for a player in time.
            run(repeater, engine, 1250, 1330);
            repeater.Release(Key.A);

            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(1, recorded.Count, "one physical press, one input");
            Assert.AreEqual(1, engine.Combo, "nothing broke the combo the correct press just started");
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[1].State, "the hold did not type ahead of the player");
        }

        /// <summary>
        /// REGRESSION (backlog 43, residue): the drift-shifted schedule alone was not enough. It only
        /// guarantees the first repeat is one SONG CADENCE after the press, and a cadence can be
        /// shorter than a keystroke's dwell: on dense timing a still-held key is legitimately due
        /// again while it is physically down. Where the run of the held char ends, that firing landed
        /// on a cell wanting a different letter and popped the error letter out of a keystroke the
        /// player had typed correctly.
        ///
        /// <para>"aab" at a 30ms cadence puts BOTH remaining cell targets inside one 80ms dwell: the
        /// first repeat is a real 'a' and still fires, the second would be an 'a' at the 'b' and must
        /// not be sent at all.</para>
        /// </summary>
        [Test]
        public void ARepeatIsNeverSentToACellExpectingAnotherChar()
        {
            var (engine, repeater, recorded) = start(map(line("aab", 1000, 9000, 1090, unit("aab", 1000, 1090))));
            var rejected = new List<char>();
            engine.WrongKeyRejected += rejected.Add;

            press(engine, repeater, recorded, Key.A, 'a', 1000);

            Assert.AreEqual(new double[] { 1000, 1030, 1060 }, engine.Lines[0].Cells.Select(c => c.TargetTime).ToArray(),
                "a 30ms cadence, well inside an ordinary keystroke's dwell");

            // 80ms of dwell, the same ordinary keystroke every other test here releases on.
            run(repeater, engine, 1000, 1080);
            repeater.Release(Key.A);

            Assert.AreEqual(new double[] { 1000, 1030 }, recorded.Select(r => r.time).ToArray(),
                "the matching repeat is sent, the diverging one never is");
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.IsEmpty(rejected, "no error letter out of a keystroke the player typed correctly");
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[2].State, "no fill on the b, in any mode");
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys);
            Assert.AreEqual(2, engine.Combo, "the combo the player earned survives");
            Assert.IsFalse(repeater.IsHolding, "the gated repeat ended the hold rather than skipping one firing");
        }

        /// <summary>
        /// The player's drift is preserved exactly, neither corrected nor deepened: a hold started
        /// late carries the lag of the press that armed it, so every repeat lands on the cell the
        /// caret is behind on at the SONG'S cadence and is judged honestly late by that same lag.
        /// It never retroactively types the cells the song has already sung past, and (the bug this
        /// pins, backlog 43) it never squeezes a repeat in right behind the arming press.
        /// </summary>
        [Test]
        public void AHoldStartedLateStaysLate()
        {
            var (engine, repeater, recorded) = start(denseRun());

            // First press lands at 1500, five cells after the song has moved on.
            engine.Update(1500);
            press(engine, repeater, recorded, Key.A, 'a', 1500);

            Assert.AreEqual(500, engine.Lines[0].Cells[0].JudgedDelta);

            // Cells 1..10 are all still to be typed, so all ten are scheduled, each shifted by the
            // 500ms the arming press was late. Nothing is due before 1600, a full cadence away.
            Assert.AreEqual(10, repeater.PendingRepeats);

            run(repeater, engine, 1500, 2100);

            Assert.AreEqual(new double[] { 1500, 1600, 1700, 1800, 1900, 2000, 2100 }, recorded.Select(r => r.time).ToArray());
            Assert.AreEqual(7, engine.CaretIndex, "the caret still trails the playhead by four cells");

            for (int i = 0; i <= 6; i++)
                Assert.AreEqual(500, engine.Lines[0].Cells[i].JudgedDelta, $"cell {i} lands on the same 500ms lag, no clamp to soften it");
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
        /// With no engage delay, discrete typing is only untouched when the release beats the next
        /// cell's target (see StillHeldAtTheNextTargetFires for the other side of that boundary): an
        /// identical discrete-press run, released crisply well inside the gap to whatever cell comes
        /// next, produces byte-identical engine state to the same run without the repeater armed.
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

            // 20ms of dwell: comfortably shorter than every gap this script leaves to the next cell
            // target (the tightest is 40ms), so the release always lands before a repeat could fire.
            const int dwell_ms = 20;

            foreach (var (c, time) in script)
            {
                // Frames up to the keystroke, then the keystroke itself; the armed run additionally
                // holds each key for a normal keystroke's dwell before releasing it.
                for (int t = cursor + frame_step_ms; t <= time; t += frame_step_ms)
                {
                    bare.Update(t);
                    repeater.Pump(t);
                    armed.Update(t);
                }

                bare.Update(time);
                bare.ProcessKey(c, time);

                press(armed, repeater, recorded, c == 'h' ? Key.H : Key.A, c, time);

                for (int t = time + frame_step_ms; t <= time + dwell_ms; t += frame_step_ms)
                {
                    bare.Update(t);
                    repeater.Pump(t);
                    armed.Update(t);
                }

                repeater.Release(c == 'h' ? Key.H : Key.A);
                cursor = time + dwell_ms;
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

        /// <summary>Mirrors denseRun's shape but with a run of SPACE cells instead of 'a's: ten spaces
        /// then a non-space cell where the run ends.</summary>
        private static LyricBeatmap denseSpaceRun(string text = "          x")
            => map(line(text, 1000, 9000, 2100, unit(text, 1000, 2100)));

        /// <summary>
        /// SPACE EXCLUSION (backlog 49): holding space never synthesizes a repeat, however long the
        /// run of space cells ahead of it. The initial physical press is still judged completely
        /// normally, exactly like any other keystroke; it is only the hold that is inert for space.
        /// </summary>
        [Test]
        public void HoldingSpaceFiresNothingBeyondTheInitialPress()
        {
            var (engine, repeater, recorded) = start(denseSpaceRun());

            press(engine, repeater, recorded, Key.Space, ' ', 1000);

            Assert.IsFalse(repeater.IsHolding, "space never arms a hold, unlike every other key");
            Assert.AreEqual(0, repeater.PendingRepeats);

            var cell0 = engine.Lines[0].Cells[0];
            Assert.AreEqual(CellState.Correct, cell0.State, "the initial press is judged the ordinary way");
            Assert.AreEqual(' ', cell0.TypedChar);
            Assert.AreEqual(1, engine.CaretIndex);

            // Pump the whole run's worth of frames: with nothing armed, nothing more can fire.
            run(repeater, engine, 1000, 2100);

            Assert.AreEqual(1, recorded.Count, "no repeats synthesized for the other nine space cells");
            Assert.AreEqual(1, engine.CaretIndex, "the caret never moved past the single physical press");
            Assert.IsTrue(engine.Lines[0].Cells.Skip(1).Take(9).All(c => c.State == CellState.Untyped),
                "the rest of the space run sits untyped, exactly as if no hold feature existed");
        }

        /// <summary>
        /// Holding space is a genuine no-op for the hold machinery, not a special "armed but inert"
        /// state: a letter pressed while space is still physically down arms its own hold completely
        /// normally, from the caret the space press left behind.
        /// </summary>
        [Test]
        public void HoldingSpaceThenPressingALetterArmsTheLettersHoldNormally()
        {
            var mixed = map(line(" aaaa", 1000, 9000, 1500, unit(" aaaa", 1000, 1500)));
            var (engine, repeater, recorded) = start(mixed);

            press(engine, repeater, recorded, Key.Space, ' ', 1000);
            Assert.IsFalse(repeater.IsHolding, "space still never arms");

            // The letter key going down while space is (physically) still held: a distinct key, so
            // it is a fresh OnKeyDown, not the discarded OS auto-repeat.
            press(engine, repeater, recorded, Key.A, 'a', 1100);
            Assert.IsTrue(repeater.IsHolding, "an ordinary key arms its hold exactly as if space had never been pressed");
            Assert.AreEqual('a', repeater.HeldChar);

            run(repeater, engine, 1100, 1500);

            Assert.AreEqual(5, recorded.Count, "the space press, the 'a' press, and three synthesized repeats");
            Assert.AreEqual(5, engine.CaretIndex, "the whole line typed");
            Assert.IsTrue(engine.Lines[0].Cells.Skip(1).All(c => c.State == CellState.Correct && c.TypedChar == 'a'));
        }

        /// <summary>
        /// The other direction: space pressed while a letter's hold is active. Space is a real
        /// keystroke like any other, so it still ENDS the existing hold (the class doc's "a new key
        /// always ends the previous hold" rule is untouched); what changes is only that space then
        /// does not start a hold of its own in its place, per the exclusion.
        /// </summary>
        [Test]
        public void PressingSpaceWhileHoldingALetterCancelsTheHoldWithoutRearming()
        {
            var (engine, repeater, recorded) = start(denseRun());
            var rejected = new List<char>();
            engine.WrongKeyRejected += rejected.Add;

            press(engine, repeater, recorded, Key.A, 'a', 1000);
            Assert.IsTrue(repeater.IsHolding);
            Assert.AreEqual(10, repeater.PendingRepeats);

            // Space physically goes down before the first 'a' repeat is due (target 1100). The next
            // cell wants 'a', so this space is a wrong key, exactly like it would be without any hold
            // in play; the hold ending is a separate effect from that judgement.
            press(engine, repeater, recorded, Key.Space, ' ', 1050);

            Assert.AreEqual(1, rejected.Count, "the space itself is judged as an ordinary wrong key");
            Assert.IsFalse(repeater.IsHolding, "the letter's hold ends, same as pressing any other key would");
            Assert.AreEqual(0, repeater.PendingRepeats);

            // Run the rest of the run's window: nothing from the cancelled 'a' hold survives, and
            // space armed nothing to replace it.
            run(repeater, engine, 1050, 2100);

            Assert.AreEqual(2, recorded.Count, "the initial 'a' press and the rejected space, nothing else");
            Assert.AreEqual(1, engine.CaretIndex, "no phantom 'a' repeats crept in after the cancel");
            Assert.IsTrue(engine.Lines[0].Cells.Skip(1).All(c => c.State == CellState.Untyped));
        }
    }
}
