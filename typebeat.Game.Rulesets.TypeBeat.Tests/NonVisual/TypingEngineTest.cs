// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game.Tests/NonVisual/TypingEngineTest.cs.
// type!beat gameplay-core tests: headless NUnit coverage of the whole gameplay/scoring
// state machine on fabricated beatmaps with round-number times. No game host; the
// engine takes explicit double-millisecond times. Every expected value is hand-computed
// in a comment beside its assert. This file is the correctness anchor for the whole game.
// Adaptations on entry: namespaces; Beatmap->LyricBeatmap/BeatmapMetadata->LyricBeatmapMetadata;
// classic asserts aliased for NUnit 4; the real-map pin resolves the standalone repo's maps
// dir via StandaloneMaps (hardcoded path + graceful ignore) instead of BeatmapStore.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypingEngineTest
    {
        #region Fixture builders

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(TimingGranularity granularity, params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = granularity,
        };

        /// <summary>
        /// The workhorse line: "ab cd", active [1000, 4000), SingEnd 3000,
        /// units "ab" [1000, 2000] and "cd" [2000, 3000].
        /// Cell targets: 'a' = 1000 (unit start), 'b' = 1000 + 1*1000/2 = 1500,
        /// ' ' = 2000 (unit0 end), 'c' = 2000, 'd' = 2000 + 1*1000/2 = 2500.
        /// </summary>
        private static LyricLine abcdLine() => line("ab cd", 1000, 4000, 3000,
            unit("ab", 1000, 2000), unit("cd", 2000, 3000));

        #endregion

        [Test]
        public void PerfectRunScoresAllPerfectFullComboSync100()
        {
            var engine = new TypingEngine(map(TimingGranularity.Line, abcdLine()));

            // Flattening sanity: targets as documented on abcdLine().
            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(5, cells.Count);
            Assert.AreEqual(1000, cells[0].TargetTime); // 'a' at unit start
            Assert.AreEqual(1500, cells[1].TargetTime); // 'b' = 1000 + 1*(2000-1000)/2
            Assert.AreEqual(2000, cells[2].TargetTime); // ' ' = unit0.EndTime
            Assert.AreEqual(2000, cells[3].TargetTime); // 'c' at unit1 start
            Assert.AreEqual(2500, cells[4].TargetTime); // 'd' = 2000 + 1*(3000-2000)/2
            Assert.AreEqual(5, engine.Lines[0].TypeableCount);

            // SungPositionAt sanity: polyline (1000,0) a(1000,0) b(1500,1) ' '(2000,2) c(2000,3) d(2500,4) (3000,5).
            Assert.AreEqual(0, engine.Lines[0].SungPositionAt(500));    // clamped before start
            Assert.AreEqual(0.5, engine.Lines[0].SungPositionAt(1250)); // halfway a->b: 0 + 250/500
            Assert.AreEqual(3, engine.Lines[0].SungPositionAt(2000));   // zero-length ' '->c segment skipped: jumps to c's index
            Assert.AreEqual(4.5, engine.Lines[0].SungPositionAt(2750)); // halfway d(2500,4) -> singEnd(3000,5)
            Assert.AreEqual(5, engine.Lines[0].SungPositionAt(9999));   // clamped after sing end

            int finishedCount = 0;
            engine.Finished += () => finishedCount++;

            engine.Update(0);
            Assert.AreEqual(-1, engine.ActiveLineIndex); // lead-in: nothing active
            Assert.AreEqual(1.0, engine.LiveAccuracy);   // 1.0 before any keypress

            engine.Update(1000); // line activates at its StartTime
            Assert.AreEqual(0, engine.ActiveLineIndex);

            // Every key exactly on target => delta 0 => Great (300 base).
            // points = round(300 * (1 + min(comboBefore, 50)/50)):
            //   'a': combo 0 before -> 300 * 1.00 = 300
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            engine.Update(1500);
            //   'b': combo 1 before -> 300 * 1.02 = 306
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            engine.Update(2000);
            //   ' ': combo 2 before -> 300 * 1.04 = 312
            Assert.IsTrue(engine.ProcessKey(' ', 2000));
            //   'c': combo 3 before -> 300 * 1.06 = 318
            Assert.IsTrue(engine.ProcessKey('c', 2000));
            engine.Update(2500);
            //   'd': combo 4 before -> 300 * 1.08 = 324
            Assert.IsTrue(engine.ProcessKey('d', 2500));

            Assert.IsTrue(engine.IsLineComplete);
            Assert.AreEqual(5, engine.CaretIndex); // == Cells.Count when complete

            engine.Update(4000); // line EndTime: seal (nothing missed) and finish
            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(1, finishedCount);
            Assert.AreEqual(-1, engine.ActiveLineIndex);

            var results = engine.BuildResults();

            // Score = 300 + 306 + 312 + 318 + 324 = 1560.
            Assert.AreEqual(1560, results.Score);
            Assert.AreEqual(5, results.MaxCombo);
            Assert.AreEqual(1.0, results.Accuracy);      // 5 correct / 5 keypresses
            Assert.AreEqual(100.0, results.SyncPercent); // all deltas 0 => q = 1 each
            Assert.AreEqual(5, results.Counts[JudgementType.Great]);
            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);
            // Active time (accrued only while active AND incomplete): 1000->1500->2000->2500 = 1500 ms.
            // WPM = (5 correct cells / 5) words / (1500/60000 min) = 1 / 0.025 = 40.
            Assert.AreEqual(40.0, results.Wpm, 1e-9);
            Assert.AreEqual("S", results.Grade); // sync 100 >= 95 && acc 1.0 >= 0.95
        }

        [Test]
        public void WindowBoundariesClassifyExactly()
        {
            // Line granularity, scale 1.0: Great [-250,+400], Ok [-600,+1000], Meh [-1200,+2000].
            var w = SyncWindows.For(TimingGranularity.Line);

            Assert.AreEqual(JudgementType.Ok, w.Classify(-251));      // 1ms outside GreatEarly
            Assert.AreEqual(JudgementType.Great, w.Classify(-250));   // edge inclusive
            Assert.AreEqual(JudgementType.Great, w.Classify(-249));   // 1ms inside
            Assert.AreEqual(JudgementType.Great, w.Classify(399));    // 1ms inside GreatLate
            Assert.AreEqual(JudgementType.Great, w.Classify(400));    // edge inclusive
            Assert.AreEqual(JudgementType.Ok, w.Classify(401));       // 1ms outside
            Assert.AreEqual(JudgementType.Meh, w.Classify(-601));        // 1ms outside OkEarly
            Assert.AreEqual(JudgementType.Ok, w.Classify(-600));      // edge inclusive
            Assert.AreEqual(JudgementType.Ok, w.Classify(-599));      // 1ms inside
            Assert.AreEqual(JudgementType.Ok, w.Classify(999));       // 1ms inside OkLate
            Assert.AreEqual(JudgementType.Ok, w.Classify(1000));      // edge inclusive
            Assert.AreEqual(JudgementType.Meh, w.Classify(1001));        // 1ms outside
            Assert.AreEqual(JudgementType.Premature, w.Classify(-1201)); // 1ms outside MehEarly
            Assert.AreEqual(JudgementType.Meh, w.Classify(-1200));       // edge inclusive
            Assert.AreEqual(JudgementType.Meh, w.Classify(-1199));       // 1ms inside
            Assert.AreEqual(JudgementType.Meh, w.Classify(1999));        // 1ms inside MehLate
            Assert.AreEqual(JudgementType.Meh, w.Classify(2000));        // edge inclusive
            Assert.AreEqual(JudgementType.Lagging, w.Classify(2001));   // 1ms outside

            // Word granularity, scale 0.6: Great [-150,+240], Ok [-360,+600], Meh [-720,+1200].
            var ww = SyncWindows.For(TimingGranularity.Word);

            Assert.AreEqual(0.6, ww.Scale);
            Assert.AreEqual(JudgementType.Ok, ww.Classify(-151));     // 1ms outside GreatEarly (250*0.6=150)
            Assert.AreEqual(JudgementType.Great, ww.Classify(-150));
            Assert.AreEqual(JudgementType.Great, ww.Classify(-149));
            Assert.AreEqual(JudgementType.Great, ww.Classify(239));   // GreatLate = 400*0.6 = 240
            Assert.AreEqual(JudgementType.Great, ww.Classify(240));
            Assert.AreEqual(JudgementType.Ok, ww.Classify(241));
            Assert.AreEqual(JudgementType.Meh, ww.Classify(-361));       // OkEarly = 600*0.6 = 360
            Assert.AreEqual(JudgementType.Ok, ww.Classify(-360));
            Assert.AreEqual(JudgementType.Ok, ww.Classify(-359));
            Assert.AreEqual(JudgementType.Ok, ww.Classify(599));      // OkLate = 1000*0.6 = 600
            Assert.AreEqual(JudgementType.Ok, ww.Classify(600));
            Assert.AreEqual(JudgementType.Meh, ww.Classify(601));
            Assert.AreEqual(JudgementType.Premature, ww.Classify(-721)); // MehEarly = 1200*0.6 = 720
            Assert.AreEqual(JudgementType.Meh, ww.Classify(-720));
            Assert.AreEqual(JudgementType.Meh, ww.Classify(-719));
            Assert.AreEqual(JudgementType.Meh, ww.Classify(1199));       // MehLate = 2000*0.6 = 1200
            Assert.AreEqual(JudgementType.Meh, ww.Classify(1200));
            Assert.AreEqual(JudgementType.Lagging, ww.Classify(1201));

            // An engine on a Word-granularity map uses the scaled windows:
            // 'a' target 1000, typed at 1241 => delta +241 => 1ms past scaled GreatLate => Ok.
            var engine = new TypingEngine(map(TimingGranularity.Word,
                line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000))));
            CharJudgement? judged = null;
            engine.CharJudged += j => judged = j;
            engine.Update(1000);
            engine.ProcessKey('a', 1241);
            Assert.AreEqual(JudgementType.Ok, judged!.Value.Type);
            Assert.AreEqual(241, judged!.Value.Delta);
        }

        /// <summary>
        /// Mashing a line out ahead of the vocals earns the SCORE of one legitimate character and
        /// nothing more: every press past the first is Premature and worth zero points, so the
        /// ladder pays a masher exactly once.
        ///
        /// <para>Since backlog 199 the combo is NOT the other half of that. An off-time press is a
        /// hit (<see cref="TypingEngine.OffTime"/>), so the mashed characters extend the run instead
        /// of breaking it, and the punishment moves entirely to accuracy: each one resolves as an
        /// osu Meh, the cheapest result a judged cell can take. The old rule (three breaks here, a
        /// max_combo of 1) survives as <c>OffTimeRule.BreaksCombo</c> for re-deriving a
        /// stored row, and is pinned in <see cref="JudgementEraTest"/>.</para>
        /// </summary>
        [Test]
        public void MashAheadYieldsPrematureAndNoPoints()
        {
            // "abcd", one unit [1000, 9000], k=4 => targets a=1000, b=3000, c=5000, d=7000.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("abcd", 1000, 10000, 9000, unit("abcd", 1000, 9000))));

            int comboBreaks = 0;
            engine.ComboBroken += () => comboBreaks++;

            engine.Update(1000);

            // Mash the whole line instantly at t=1000:
            //   'a' delta 0     => Great, 300 * (1 + 0/50) = 300, combo 1
            //   'b' delta -2000 => Premature (< -MehEarly 1200), 0 pts, combo 2
            //   'c' delta -4000 => Premature, 0 pts, combo 3
            //   'd' delta -6000 => Premature, 0 pts, combo 4
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1000));
            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey('d', 1000));

            Assert.AreEqual(300, engine.Score); // no POINTS beyond the single legitimate Great
            Assert.AreEqual(4, engine.Combo, "an off-time press is a hit, so the run carries on");
            Assert.AreEqual(4, engine.MaxCombo);
            Assert.AreEqual(0, comboBreaks); // no Premature breaks combo any more
            Assert.AreEqual(1.0, engine.LiveAccuracy); // right chars, wrong time: accuracy is not sync's job

            // LiveSyncPercent over the 4 resolved cells: q(a)=1; q(b)=clamp(1-2000/1200)=0; q(c)=q(d)=0 => 25%.
            Assert.AreEqual(25.0, engine.LiveSyncPercent, 1e-9);

            engine.Update(10000); // seal: nothing Untyped (all Correct), so no combo break here either

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(0, comboBreaks); // unchanged by the seal

            var results = engine.BuildResults();
            Assert.AreEqual(1, results.Counts[JudgementType.Great]);
            Assert.AreEqual(3, results.Counts[JudgementType.Premature]);
            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);
            Assert.AreEqual(25.0, results.SyncPercent, 1e-9);
        }

        [Test]
        public void TypingNothingSealsWithMissesAndOneComboBreak()
        {
            // L0 "ab" [1000, 3000), unit [1000,2000] => a=1000, b=1500.
            // L1 "cd" [3000, 5000), unit [3000,4000] => c=3000, d=3500, never typed.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
                line("cd", 3000, 5000, 4000, unit("cd", 3000, 4000))));

            int comboBreaks = 0;
            var seals = new List<LineSealResult>();
            engine.ComboBroken += () => comboBreaks++;
            engine.LineSealed += s => seals.Add(s);

            engine.Update(1000);
            engine.ProcessKey('a', 1000); // Great, combo 1
            engine.ProcessKey('b', 1500); // Great, combo 2

            engine.Update(3000); // seal L0 (0 missed, combo survives), activate L1
            Assert.AreEqual(1, seals.Count);
            Assert.AreEqual(new LineSealResult(0, 0, false), seals[0]);
            Assert.AreEqual(0, comboBreaks);
            Assert.AreEqual(2, engine.Combo);
            Assert.AreEqual(1, engine.ActiveLineIndex);

            engine.Update(5000); // seal L1: both typeable cells Untyped -> Missed; EXACTLY ONE combo break

            Assert.AreEqual(2, seals.Count);
            Assert.AreEqual(new LineSealResult(1, 2, true), seals[1]);
            Assert.AreEqual(1, comboBreaks); // one break for the whole sealed line, not one per missed cell
            Assert.AreEqual(0, engine.Combo);
            Assert.AreEqual(2, engine.MaxCombo);
            Assert.IsTrue(engine.IsFinished);

            Assert.AreEqual(CellState.Missed, engine.Lines[1].Cells[0].State);
            Assert.AreEqual(CellState.Missed, engine.Lines[1].Cells[1].State);

            var results = engine.BuildResults();
            Assert.AreEqual(2, results.Counts[JudgementType.Miss]);
            Assert.AreEqual(1.0, results.Accuracy); // misses are not keypresses: 2 correct / 2 total
            // Sync over ALL 4 typeable cells: a q=1, b q=1, c q=0, d q=0 => 50%.
            Assert.AreEqual(50.0, results.SyncPercent, 1e-9);
        }

        [Test]
        public void WrongKeyIsRejectedBreaksComboAndTracksStreak()
        {
            // "ab" [1000, 3000), unit [1000,2000] => a=1000, b=1500.
            // AllowWrongInput off = the GATEKEEPER model (backlog 107 made typing-through the
            // default, so the rejection path this pins is now reached only through that mod).
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)))) { AllowWrongInput = false };

            var judgements = new List<CharJudgement>();
            var rejected = new List<char>();
            int comboBreaks = 0;
            engine.CharJudged += j => judgements.Add(j);
            engine.WrongKeyRejected += c => rejected.Add(c);
            engine.ComboBroken += () => comboBreaks++;

            engine.Update(1000);

            // 'x' where 'a' is expected: REJECTED, nothing input, no judgement, streak grows.
            Assert.IsTrue(engine.ProcessKey('x', 1000));
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[0].State);
            Assert.IsNull(engine.Lines[0].Cells[0].TypedChar);
            Assert.AreEqual(0, engine.CaretIndex); // caret did NOT advance
            Assert.AreEqual(0, engine.Combo);
            Assert.AreEqual(1, comboBreaks);
            Assert.AreEqual(0, judgements.Count);
            Assert.AreEqual(new[] { 'x' }, rejected);
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys);

            // A second wrong key keeps growing the streak (one combo break event per press).
            Assert.IsTrue(engine.ProcessKey('q', 1100));
            Assert.AreEqual(2, engine.ConsecutiveWrongKeys);
            Assert.AreEqual(2, comboBreaks);
            Assert.AreEqual(0, engine.CaretIndex);

            // 'a' correct at t=1200 (delta +200 => Great at Line windows, late edge +400):
            // judged at the REAL time; wrong presses never consumed the cell. Streak resets.
            Assert.IsTrue(engine.ProcessKey('a', 1200));
            Assert.AreEqual(new CharJudgement(0, 0, JudgementType.Great, 200, 300, 1), judgements[0]);
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys);
            Assert.AreEqual(1, engine.CaretIndex);

            // 'b' Great at target: combo continues, points 300 * (1 + 1/50) = 306.
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            Assert.AreEqual(new CharJudgement(0, 1, JudgementType.Great, 0, 306, 2), judgements[1]);
            Assert.AreEqual(606, engine.Score);

            // Accuracy = 2 correct / 4 char keypresses (rejected keys stay in the denominator).
            Assert.AreEqual(0.5, engine.LiveAccuracy);

            engine.Update(3000); // seal: both cells Correct => no missed cells, no extra break
            Assert.AreEqual(2, comboBreaks);

            var results = engine.BuildResults();
            Assert.AreEqual(2, results.Counts[JudgementType.WrongChar]);
            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);
            // Sync: q(a) = 1 - 200/2000 = 0.9 (Line MehLate 2000); q(b) = 1. Mean => 95%.
            Assert.AreEqual(95.0, results.SyncPercent, 1e-9);
        }

        [Test]
        public void RejectedWrongKeyLeavesCellTypeable()
        {
            // "ab" [1000, 5000), unit [1000,2000] => a=1000, b=1500. Gatekeeper (rejection) model.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("ab", 1000, 5000, 2000, unit("ab", 1000, 2000)))) { AllowWrongInput = false };

            // Backspace with no active line is inert.
            Assert.IsFalse(engine.ProcessBackspace());

            engine.Update(1000);

            // Backspace with nothing typed is inert.
            Assert.IsFalse(engine.ProcessBackspace());

            engine.ProcessKey('x', 1000); // wrong on 'a': rejected, the cell never held it
            Assert.IsFalse(engine.ProcessBackspace()); // still nothing typed to erase
            Assert.AreEqual(0, engine.CaretIndex);
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[0].State);
            Assert.IsNull(engine.Lines[0].Cells[0].TypedChar);
            Assert.IsNull(engine.Lines[0].Cells[0].JudgedDelta);

            // 'a' at t=2500: judged at the real time. delta = 2500 - 1000 = +1500
            // => Ok (1000 < 1500 <= 2000). Points = round(50 * (1 + 0/50)) = 50.
            Assert.IsTrue(engine.ProcessKey('a', 2500));
            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[0].State);
            Assert.AreEqual(1500, engine.Lines[0].Cells[0].JudgedDelta);

            // 'b' at t=2600: delta = 2600 - 1500 = +1100 => Ok. Points = round(50 * 1.02) = 51.
            Assert.IsTrue(engine.ProcessKey('b', 2600));

            Assert.AreEqual(101, engine.Score); // 50 + 51

            // Accuracy: keypresses x, a, b => 2 correct / 3 total (the rejected key stays in
            // the denominator forever; backspace is not a keypress).
            Assert.AreEqual(2.0 / 3, engine.LiveAccuracy, 1e-12);

            engine.Update(5000);
            var results = engine.BuildResults();

            // Sync uses the correct deltas: q(a) = 1 - 1500/2000 = 0.25; q(b) = 1 - 1100/2000 = 0.45.
            // SyncPercent = 100 * (0.25 + 0.45) / 2 = 35.
            Assert.AreEqual(35.0, results.SyncPercent, 1e-9);
            Assert.AreEqual(2, results.Counts[JudgementType.Meh]);
            Assert.AreEqual(1, results.Counts[JudgementType.WrongChar]);
        }

        [Test]
        public void SupportedPunctuationLeavesTheDefaultStreamEntirely()
        {
            // "beggin' him": tokens "beggin'" (k=6 typeable, the apostrophe never counts) and "him".
            // Unit "beggin'" [1000, 2200], step (2200-1000)/6 = 200:
            //   b=1000 e=1200 g=1400 g=1600 i=1800 n=2000. Unit "him" [2200, 2800]: h=2200 i=2400 m=2600.
            // Without Literate the apostrophe is not a cell at all: the stream IS "beggin him", so
            // what the player sees is exactly what they type.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("beggin' him", 1000, 10000, 2800,
                    unit("beggin'", 1000, 2200), unit("him", 2200, 2800))));

            var tl = engine.Lines[0];
            Assert.AreEqual("beggin him", tl.DisplayText);
            Assert.AreEqual(10, tl.Cells.Count);
            Assert.AreEqual(10, tl.TypeableCount);
            Assert.AreEqual(2200, tl.Cells[6].TargetTime); // space = unit0.EndTime, unmoved by the mark
            Assert.AreEqual(2200, tl.Cells[7].TargetTime); // 'h'

            engine.Update(1000);

            // Type b-e-g-g-i-n exactly on target; all Great. The letter targets are untouched by
            // the presence of the apostrophe in the authored text.
            engine.ProcessKey('b', 1000);
            engine.ProcessKey('e', 1200);
            engine.ProcessKey('g', 1400);
            engine.ProcessKey('g', 1600);
            engine.ProcessKey('i', 1800);
            engine.ProcessKey('n', 2000);

            Assert.AreEqual(6, engine.CaretIndex); // straight on to the space

            engine.ProcessKey(' ', 2200);
            engine.ProcessKey('h', 2200);
            engine.ProcessKey('i', 2400);
            engine.ProcessKey('m', 2600);
            Assert.IsTrue(engine.IsLineComplete);
            Assert.AreEqual(10, engine.Combo); // 10 keypresses, zero required for punctuation
            Assert.AreEqual(1.0, engine.LiveAccuracy);
        }

        [Test]
        public void LiteratePunctuationIsATypedCellTimedBetweenItsNeighbours()
        {
            // The same line under Literate: 11 cells, the apostrophe among them. Its target is
            // interpolated across the gap it sits in, prev + 1*(next-prev)/2 = 2000 + 100 = 2100,
            // and every LETTER keeps the exact target it has without the mod.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("beggin' him", 1000, 10000, 2800,
                    unit("beggin'", 1000, 2200), unit("him", 2200, 2800))), literate: true);

            var tl = engine.Lines[0];
            Assert.AreEqual("beggin' him", tl.DisplayText);
            Assert.AreEqual(11, tl.Cells.Count);
            Assert.AreEqual(11, tl.TypeableCount); // the mark is typed now
            Assert.IsTrue(tl.Cells[6].IsTypeable);
            Assert.IsTrue(tl.Cells[6].IsCountable); // a real keypress, so it spends character budget
            Assert.AreEqual(2100, tl.Cells[6].TargetTime);
            Assert.AreEqual(2000, tl.Cells[5].TargetTime); // 'n', unmoved
            Assert.AreEqual(2200, tl.Cells[7].TargetTime); // space, unmoved

            engine.Update(1000);

            engine.ProcessKey('b', 1000);
            engine.ProcessKey('e', 1200);
            engine.ProcessKey('g', 1400);
            engine.ProcessKey('g', 1600);
            engine.ProcessKey('i', 1800);
            engine.ProcessKey('n', 2000);

            // No auto-skip: the caret waits on the apostrophe, and a letter there is rejected.
            Assert.AreEqual(6, engine.CaretIndex);
            Assert.IsTrue(engine.ProcessKey(' ', 2100));
            Assert.AreEqual(CellState.Untyped, tl.Cells[6].State);
            Assert.AreEqual(6, engine.CaretIndex);
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys);

            // Pressed exactly on the interpolated target, so it is judged Great like any letter.
            Assert.IsTrue(engine.ProcessKey('\'', 2100));
            Assert.AreEqual(CellState.Correct, tl.Cells[6].State);
            Assert.AreEqual(0, tl.Cells[6].JudgedDelta);
            Assert.AreEqual(7, engine.CaretIndex);

            engine.ProcessKey(' ', 2200);
            engine.ProcessKey('h', 2200);
            engine.ProcessKey('i', 2400);
            engine.ProcessKey('m', 2600);
            Assert.IsTrue(engine.IsLineComplete);
        }

        [Test]
        public void AutoSkipUnsupportedCharsNeverRequiresAKey()
        {
            // A char outside both the typeable surface and the supported punctuation set (Normalize
            // strips these, so this is the defensive path for hand-built or legacy data) is still a
            // non-typeable cell the caret hops. "~ab~", unit [1000, 2000], k=2 => a=1000, b=1500.
            // The leading '~' has nothing before it so it takes the FOLLOWING target (1000); the
            // trailing '~' has nothing after it so it takes the PRECEDING one (1500).
            // ('*' stood in here until backlog 202 made it a supported mark.)
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("~ab~", 1000, 4000, 2000, unit("~ab~", 1000, 2000))));

            engine.Update(1000);
            // Leading '~' is auto-skipped at activation: caret starts on 'a' (idx 1).
            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(CellState.AutoSkipped, engine.Lines[0].Cells[0].State);
            Assert.AreEqual(1000, engine.Lines[0].Cells[0].TargetTime);
            Assert.AreEqual(1500, engine.Lines[0].Cells[3].TargetTime);

            engine.ProcessKey('a', 1000);
            engine.ProcessKey('b', 1500);
            // Trailing '~' auto-skipped; line completes with just two keys.
            Assert.IsTrue(engine.IsLineComplete);
            Assert.AreEqual(CellState.AutoSkipped, engine.Lines[0].Cells[3].State);

            // Backspace steps back OVER the auto-skipped cell and un-skips it.
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[3].State);
        }

        [Test]
        public void AccuracyCountsAllKeypressesForever()
        {
            // L0 "ab" [1000,3000); L1 "cd" [3000,5000) left entirely untyped.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
                line("cd", 3000, 5000, 4000, unit("cd", 3000, 4000))));

            engine.Update(1000);

            engine.ProcessKey('x', 1000);      // wrong (denominator 1, correct 0)
            engine.ProcessBackspace();          // NOT a keypress; changes nothing in the counts
            engine.ProcessKey('a', 1200);      // correct (2, 1)
            engine.ProcessKey('b', 1500);      // correct (3, 2)

            // The corrected error stays in the denominator: 2/3.
            Assert.AreEqual(2.0 / 3, engine.LiveAccuracy, 1e-12);

            engine.Update(3000);
            engine.Update(5000); // L1 sealed with 2 Missed cells; misses are NOT keypresses

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(2.0 / 3, engine.LiveAccuracy, 1e-12);
            Assert.AreEqual(2.0 / 3, engine.BuildResults().Accuracy, 1e-12);
        }

        [Test]
        public void ActiveTimeWpmIgnoresGapsAndPostLineWaits()
        {
            // L0 "ab cd" active [1000, 10000) but sung by 3000, finish early, then a long wait.
            // L1 "ef" [10000, 12000), unit [10000, 11000] => e=10000, f=10500.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("ab cd", 1000, 10000, 3000, unit("ab", 1000, 2000), unit("cd", 2000, 3000)),
                line("ef", 10000, 12000, 11000, unit("ef", 10000, 11000))));

            engine.Update(0);    // lead-in: no accrual (no active line)
            engine.Update(500);  // still lead-in: +0
            engine.Update(1000); // activation frame: accrual happens BEFORE activation => +0

            engine.ProcessKey('a', 1000);
            engine.Update(1500); // active & incomplete: +500
            engine.ProcessKey('b', 1500);
            engine.Update(2000); // +500
            engine.ProcessKey(' ', 2000);
            engine.ProcessKey('c', 2000);
            engine.Update(2500); // +500
            engine.ProcessKey('d', 2500); // line complete at t=2500

            engine.Update(9000);  // line complete: +0 (post-line wait ignored)
            engine.Update(10000); // +0; seal L0, activate L1

            engine.ProcessKey('e', 10000);
            engine.Update(10500); // active & incomplete: +500
            engine.ProcessKey('f', 10500); // complete

            engine.Update(12000); // complete: +0; seal L1, finished

            // activeTime = 500+500+500+500 = 2000 ms = 1/30 min.
            // correct cells = 7 (5 + 2, spaces included) => 7/5 = 1.4 words.
            // WPM = 1.4 / (1/30) = 42.
            Assert.AreEqual(42.0, engine.LiveWpm, 1e-9);
            Assert.AreEqual(42.0, engine.BuildResults().Wpm, 1e-9);
        }

        /// <summary>
        /// THE CLOCK INVARIANT (backlog 222), stated as the number a stopwatch would give. The test
        /// above pins what the clock EXCLUDES (gaps, post-line waits); this one pins what it may never
        /// exclude: a stretch of real time in which the player was typing. Every accepted keypress has
        /// to lie inside a clocked interval, or characters land in the WPM numerator over time the
        /// denominator refused to count and the readout inflates for free.
        ///
        /// <para>Driven on the SHIPPED stack rather than the pinned engine every other test in this
        /// file uses, because the pinned caret has no such stretch to get wrong: it is the flexible
        /// caret that can be sitting on a line up to <c>FLETCHER_DRAG_GRACE_MS</c> before its cue,
        /// typing, while the song has not reached it (see <c>FletcherEngineTest</c>'s rush-freedom
        /// region for the mechanism). The script types continuously with no idle stretch at all, so
        /// there is nothing legitimate to exclude and the engine's WPM must equal the honest gross
        /// WPM over the span from the first keystroke to the last.</para>
        /// </summary>
        [Test]
        public void ContinuousTypingAcrossALineBoundaryIsClockedEndToEnd()
        {
            // L0 "abcd" [1000, 4000), unit [1000, 3000] => step 500: a=1000, b=1500, c=2000, d=2500.
            // L1 "efgh" [4000, 8000), unit [4000, 6000] => step 500: e=4000, f=4500, g=5000, h=5500.
            // L1's activation is clamped to its own start (max(4000, 4000 - CUE_LEAD_MS)), so entry
            // into it opens at 4000 - FLETCHER_DRAG_GRACE_MS = 2500: exactly where 'd' finishes L0.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("abcd", 1000, 4000, 3000, unit("abcd", 1000, 3000)),
                line("efgh", 4000, 8000, 6000, unit("efgh", 4000, 6000))))
            {
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
            };

            Assert.AreEqual(1000, engine.Lines[0].ActivationTime);
            Assert.AreEqual(4000, engine.Lines[1].ActivationTime);

            // One char every 500 ms from 1000 to 4000, unbroken, with the line change at 2500 (where
            // 'd' finishes L0 and 'e' opens L1 on the same frame). Every press is at most 3 countable
            // chars ahead of the playhead, so the rush cap never fires and the run is a clean combo.
            var script = new (double time, string chars)[]
            {
                (1000, "a"), (1500, "b"), (2000, "c"), (2500, "de"), (3000, "f"), (3500, "g"), (4000, "h"),
            };

            int next = 0;

            for (double t = 500; t <= 4000; t += 100)
            {
                engine.Update(t);

                if (next >= script.Length || script[next].time != t)
                    continue;

                foreach (char c in script[next].chars)
                    Assert.IsTrue(engine.ProcessKey(c, t), $"press '{c}' at {t}");

                next++;
            }

            Assert.AreEqual(script.Length, next, "every scripted press landed on a frame");
            Assert.AreEqual(8, engine.MaxCombo, "no press was ever refused or capped");

            // The stopwatch reading: first keystroke 1000, last 4000, so the player typed 8 characters
            // in exactly 3000 ms, and gross WPM is (8/5)/(3000/60000) = 1.6 * 20 = 32.
            const double honest_wpm = 32.0;

            Assert.AreEqual(honest_wpm, engine.LiveWpm, 1e-9);
            Assert.AreEqual(honest_wpm, engine.BuildResults().Wpm, 1e-9);

            // The rolling readout over the same 3000 ms of clocked time: 8 stamps bound 7 inter-key
            // gaps, so (7/5)/(3000/60000) = 28.
            Assert.AreEqual(28.0, engine.LiveRollingWpm, 1e-9);

            // The 1500 ms from 2500 to 4000 is the whole of the head start line 1 grants, and it is
            // half the run. A clock armed only at the cue would have counted 1500 ms for 8 characters
            // and reported exactly double the truth.
            Assert.AreEqual(2 * honest_wpm, (8 / 5.0) / (1500 / 60000.0), 1e-9);
        }

        /// <summary>
        /// The exact run of <see cref="ActiveTimeWpmIgnoresGapsAndPostLineWaits"/>, with a clock rate
        /// supplied for each of its FOUR accruing 500 ms segments: [1000,1500], [1500,2000],
        /// [2000,2500] and [10000,10500]. Every other frame accrues nothing (lead-in, post-completion
        /// wait, dead gap), so the rate passed there cannot move the result. 2000 ms of beatmap active
        /// time throughout, so at rate 1 this still reads 42 WPM.
        /// </summary>
        private static TypingEngine wpmRunAtRates(double rate1, double rate2, double rate3, double rate4)
        {
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("ab cd", 1000, 10000, 3000, unit("ab", 1000, 2000), unit("cd", 2000, 3000)),
                line("ef", 10000, 12000, 11000, unit("ef", 10000, 11000))));

            engine.Update(0, rate1);
            engine.Update(500, rate1);
            engine.Update(1000, rate1);

            engine.ProcessKey('a', 1000);
            engine.Update(1500, rate1); // segment 1
            engine.ProcessKey('b', 1500);
            engine.Update(2000, rate2); // segment 2
            engine.ProcessKey(' ', 2000);
            engine.ProcessKey('c', 2000);
            engine.Update(2500, rate3); // segment 3
            engine.ProcessKey('d', 2500); // line complete at t=2500

            engine.Update(9000, rate3);  // line complete: no accrual
            engine.Update(10000, rate3); // no accrual; seal L0, activate L1

            engine.ProcessKey('e', 10000);
            engine.Update(10500, rate4); // segment 4
            engine.ProcessKey('f', 10500);

            engine.Update(12000, rate4); // complete: no accrual; seal L1, finished

            return engine;
        }

        [Test]
        public void WpmDividesActiveTimeByTheClockRate()
        {
            // Rate 1 is the unmodded run, byte for byte: 2000 beatmap ms of active time is 2000 real ms.
            Assert.AreEqual(42.0, wpmRunAtRates(1, 1, 1, 1).LiveWpm, 1e-9);

            // Half Time: those 2000 beatmap ms took 2000/0.75 = 2666.666... ms in the real world, so the
            // same 1.4 words read 1.4/(2666.666.../60000) = 31.5, which is 42 * 0.75. Accruing beatmap
            // milliseconds instead reported 42 and flattered every HT play by exactly 1/0.75.
            var slow = wpmRunAtRates(0.75, 0.75, 0.75, 0.75);
            Assert.AreEqual(31.5, slow.LiveWpm, 1e-9);
            Assert.AreEqual(31.5, slow.BuildResults().Wpm, 1e-9);

            // Double Time: 2000/1.5 = 1333.333... real ms => 1.4/(1333.333.../60000) = 63 = 42 * 1.5.
            var fast = wpmRunAtRates(1.5, 1.5, 1.5, 1.5);
            Assert.AreEqual(63.0, fast.LiveWpm, 1e-9);
            Assert.AreEqual(63.0, fast.BuildResults().Wpm, 1e-9);
        }

        [Test]
        public void WpmAccruesAVaryingRatePiecewise()
        {
            // The ModWindUp / ModWindDown case, which no single whole-run multiplier can describe.
            // First two segments at 1.5x (500/1.5 = 333.333... real ms each), last two at 0.5x
            // (500/0.5 = 1000 each): 333.333... + 333.333... + 1000 + 1000 = 2666.666... real ms, which
            // happens to be the same total as a flat 0.75x, so this reads 31.5 as well.
            var engine = wpmRunAtRates(1.5, 1.5, 0.5, 0.5);

            Assert.AreEqual(31.5, engine.LiveWpm, 1e-9);
            Assert.AreEqual(31.5, engine.BuildResults().Wpm, 1e-9);
        }

        [Test]
        public void WpmClockRateIsSanitisedToAUsableMagnitude()
        {
            // A stopped clock (0) and a non-finite one carry no speed information at all, so they fall
            // back to 1x rather than divide by zero (infinite WPM) or write a NaN into the accumulator
            // that every later readout, this run's results included, would inherit.
            Assert.AreEqual(42.0, wpmRunAtRates(0, 0, 0, 0).LiveWpm, 1e-9);
            Assert.AreEqual(42.0, wpmRunAtRates(double.NaN, double.NaN, double.NaN, double.NaN).LiveWpm, 1e-9);
            Assert.AreEqual(42.0, wpmRunAtRates(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity).LiveWpm, 1e-9);

            // A rewinding clock reports a NEGATIVE rate, but the sign is a direction and not a speed:
            // the MAGNITUDE is what the WPM clock divides by. So -1 behaves as 1 ...
            Assert.AreEqual(42.0, wpmRunAtRates(-1, -1, -1, -1).LiveWpm, 1e-9);
            // ... and -1.5 behaves as 1.5, not as the blanket 1x fallback: rewinding under Double Time
            // is still Double Time.
            Assert.AreEqual(63.0, wpmRunAtRates(-1.5, -1.5, -1.5, -1.5).LiveWpm, 1e-9);

            // One degenerate frame must not contaminate the rest of the run: only its own segment takes
            // the fallback. 500/1.5 + 500/1 + 500/0.5 + 500/0.5 = 2833.333... real ms.
            var mixed = wpmRunAtRates(1.5, double.NaN, 0.5, 0.5);
            Assert.AreEqual(84000.0 / (500 / 1.5 + 500 + 1000 + 1000), mixed.LiveWpm, 1e-9);
        }

        [Test]
        public void InputInertDuringLeadInGapAndAfterCompletion()
        {
            // L0 "ab" [1000, 2000); L1 "cd" [5000, 6000): a real dead gap [2000, 5000).
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("ab", 1000, 2000, 2000, unit("ab", 1000, 2000)),
                line("cd", 5000, 6000, 6000, unit("cd", 5000, 6000))));

            // Lead-in (negative time is legal): nothing active, keys inert and NOT counted.
            engine.Update(-1500);
            Assert.AreEqual(-1, engine.ActiveLineIndex);
            Assert.IsFalse(engine.ProcessKey('a', -1500));
            Assert.IsNull(engine.CurrentLeadLag(-1500));
            Assert.AreEqual(1.0, engine.LiveAccuracy); // inert keys don't touch the counts

            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.AreEqual(0, engine.CurrentLeadLag(1000)); // caret on 'a', target 1000
            engine.ProcessKey('a', 1000);
            engine.ProcessKey('b', 1500);
            Assert.IsTrue(engine.IsLineComplete);

            // Line complete: further keys inert ("line complete, wait for the song").
            Assert.IsFalse(engine.ProcessKey('x', 1600));
            Assert.IsNull(engine.CurrentLeadLag(1600));
            Assert.AreEqual(1.0, engine.LiveAccuracy);

            // Gap between lines: L0 sealed, L1 not started => inert.
            engine.Update(3000);
            Assert.AreEqual(-1, engine.ActiveLineIndex);
            Assert.IsFalse(engine.ProcessKey('c', 3000));

            engine.Update(5000);
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.IsTrue(engine.ProcessKey('c', 5000));

            engine.Update(6000); // seal L1 ('d' missed), all lines sealed => finished
            Assert.IsTrue(engine.IsFinished);

            // After finish: everything inert.
            Assert.IsFalse(engine.ProcessKey('d', 6100));
            Assert.IsFalse(engine.ProcessBackspace());
            Assert.IsNull(engine.CurrentLeadLag(6100));
        }

        [Test]
        public void SyncQualityAsymmetricAndTimelineCaptured()
        {
            // Asymmetric normalization (Line scale): the SAME 600ms offset scores differently by sign:
            //   early: q = 1 - 600/MehEarly(1200) = 0.5
            //   late:  q = 1 - 600/MehLate(2000)  = 0.7
            var w = SyncWindows.For(TimingGranularity.Line);
            Assert.AreEqual(0.5, w.SyncQuality(-600), 1e-12);
            Assert.AreEqual(0.7, w.SyncQuality(600), 1e-12);
            Assert.AreEqual(1.0, w.SyncQuality(0), 1e-12);
            Assert.AreEqual(0.0, w.SyncQuality(-1200), 1e-12); // early edge hits exactly 0
            Assert.AreEqual(0.0, w.SyncQuality(-5000), 1e-12); // clamped below
            Assert.AreEqual(0.0, w.SyncQuality(2000), 1e-12);  // late edge hits exactly 0
            Assert.AreEqual(0.0, w.SyncQuality(9999), 1e-12);  // clamped

            // "abc", one unit [1000, 2500], k=3 => a=1000, b=1500, c=2000. Gatekeeper (rejection)
            // model, so a wrong key leaves the cell untyped and contributes no timeline sample.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("abc", 1000, 10000, 2500, unit("abc", 1000, 2500)))) { AllowWrongInput = false };

            engine.Update(1000);
            engine.ProcessKey('a', 400);  // delta -600 => Ok; sample (400, -600)
            engine.ProcessKey('x', 1500); // WRONG on 'b': rejected, caret stays, no timeline sample
            engine.ProcessKey('c', 2600); // ALSO wrong ('b' expected): rejected, no sample

            engine.Update(10000); // seal: 'b' and 'c' force-missed
            var results = engine.BuildResults();

            // Timeline captured per CORRECT judgement only.
            Assert.AreEqual(1, results.SyncTimeline.Count);
            Assert.AreEqual(new SyncSample(400, -600), results.SyncTimeline[0]);

            // SyncPercent = 100 * (q(a) + q(b) + q(c)) / 3 = 100 * (0.5 + 0 + 0) / 3.
            Assert.AreEqual(50.0 / 3, results.SyncPercent, 1e-9);
            Assert.AreEqual(2, results.Counts[JudgementType.WrongChar]);
            Assert.AreEqual(2, results.Counts[JudgementType.Miss]);
        }

        [Test]
        public void ComboMultiplierUsesPreIncrementValueAndCapsAt50()
        {
            // 60 x 'a', one unit [1000, 61000], k=60 => step 1000: target_j = 1000 + j*1000.
            string text = new string('a', 60);
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line(text, 1000, 70000, 61000, unit(text, 1000, 61000))));

            var points = new List<int>();
            engine.CharJudged += j => points.Add(j.PointsAwarded);

            engine.Update(1000);

            for (int j = 0; j < 60; j++)
            {
                double t = 1000 + j * 1000;
                engine.Update(t);
                Assert.IsTrue(engine.ProcessKey('a', t)); // delta 0 => Great every time
            }

            // points_j = round(300 * (1 + min(comboBefore, 50)/50)) with comboBefore = j
            //          = 300 + 6*min(j, 50)  (exactly integral, no rounding ambiguity).
            Assert.AreEqual(300, points[0]);  // combo 0 before  => 1.00x
            Assert.AreEqual(306, points[1]);  // combo 1 before  => 1.02x (pre-increment value!)
            Assert.AreEqual(594, points[49]); // combo 49 before => 1.98x
            Assert.AreEqual(600, points[50]); // combo 50 before => capped 2.00x
            Assert.AreEqual(600, points[59]); // stays capped

            // Total = sum_{j=0}^{49} (300 + 6j) + 10*600 = 15000 + 6*1225 + 6000 = 28350.
            Assert.AreEqual(28350, engine.Score);
            Assert.AreEqual(60, engine.MaxCombo);
        }

        [Test]
        public void SealActivationOrderDeterministicOnSharedBoundary()
        {
            // Shared boundary at t=3000: EndTime_0 == StartTime_1 == 3000.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
                line("cd", 3000, 5000, 4000, unit("cd", 3000, 4000))));

            var events = new List<string>();
            engine.LineActivated += i => events.Add($"activated:{i}");
            engine.LineSealed += s => events.Add($"sealed:{s.LineIndex}");
            engine.Finished += () => events.Add("finished");

            engine.Update(1000);
            Assert.AreEqual(new[] { "activated:0" }, events);

            // At exactly t == EndTime_0 == StartTime_1: seal line 0 FIRST, THEN activate line 1,
            // in the SAME Update call (line active on [Start, End); End belongs to the next line).
            engine.Update(3000);
            Assert.AreEqual(new[] { "activated:0", "sealed:0", "activated:1" }, events);
            Assert.AreEqual(1, engine.ActiveLineIndex);

            // The new line is immediately typeable at the boundary time: 'c' target 3000 => Great.
            var judgements = new List<CharJudgement>();
            engine.CharJudged += j => judgements.Add(j);
            Assert.IsTrue(engine.ProcessKey('c', 3000));
            Assert.AreEqual(JudgementType.Great, judgements[0].Type);
            Assert.AreEqual(1, judgements[0].LineIndex);

            engine.Update(5000);
            Assert.AreEqual(new[] { "activated:0", "sealed:0", "activated:1", "sealed:1", "finished" }, events);
        }

        [Test]
        public void BuildResultsMatchesHandComputedSummary()
        {
            // WORD granularity: windows scale 0.6 => Great [-150,+240], Ok [-360,+600], Meh [-720,+1200].
            // L0 "ab" [1000, 3000), unit [1000,2000] => a=1000, b=1500.
            // L1 "cd" [3000, 5000), unit [3000,4000] => c=3000, d=3500.
            var engine = new TypingEngine(map(TimingGranularity.Word,
                line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
                line("cd", 3000, 5000, 4000, unit("cd", 3000, 4000))));

            engine.Update(1000);   // activate L0 (accrual before activation => +0)
            engine.Update(1200);   // +200 active time
            engine.ProcessKey('a', 1200); // delta +200 => Great (<= 240). 300 * (1 + 0/50) = 300. combo 1.
            engine.Update(2000);   // +800
            engine.ProcessKey('b', 2000); // delta +500 => Ok (<= 600). round(150 * 1.02) = 153. combo 2.
            engine.Update(3000);   // line complete => +0; seal L0 (0 missed); activate L1
            engine.Update(4000);   // +1000
            engine.ProcessKey('c', 4000); // delta +1000 => Ok (<= 1200). round(50 * 1.04) = 52. combo 3.
            engine.Update(5000);   // L1 active & incomplete ('d' pending) => +1000; seal L1: 'd' Missed, combo break

            Assert.IsTrue(engine.IsFinished);

            var results = engine.BuildResults();

            Assert.AreEqual(505, results.Score);            // 300 + 153 + 52
            Assert.AreEqual(1.0, results.Accuracy);         // 3 correct / 3 keypresses
            Assert.AreEqual(3, results.MaxCombo);

            // Sync qualities (Word scale: MehEarly 720, MehLate 1200):
            //   q(a) = 1 - 200/1200  = 5/6
            //   q(b) = 1 - 500/1200  = 7/12
            //   q(c) = 1 - 1000/1200 = 1/6
            //   q(d) = 0 (Missed)
            // SyncPercent = 100 * (5/6 + 7/12 + 1/6 + 0) / 4 = 100 * (19/12) / 4 = 1900/48 = 39.58333...
            Assert.AreEqual(1900.0 / 48, results.SyncPercent, 1e-9);

            // Active time = 200 + 800 + 1000 + 1000 = 3000 ms = 0.05 min.
            // Correct cells = 3 => 0.6 words => WPM = 0.6 / 0.05 = 12.
            Assert.AreEqual(12.0, results.Wpm, 1e-9);

            // Counts: all 8 keys present, exact values.
            Assert.AreEqual(8, results.Counts.Count);
            Assert.AreEqual(1, results.Counts[JudgementType.Great]);
            Assert.AreEqual(1, results.Counts[JudgementType.Ok]);
            Assert.AreEqual(1, results.Counts[JudgementType.Meh]);
            Assert.AreEqual(0, results.Counts[JudgementType.Premature]);
            Assert.AreEqual(0, results.Counts[JudgementType.Lagging]);
            Assert.AreEqual(0, results.Counts[JudgementType.WrongChar]);
            Assert.AreEqual(1, results.Counts[JudgementType.Miss]);
            // Nothing was abandoned, and nothing ever counts one anyway: the key exists so the
            // dictionary is total over the enum, and a skip's cells are counted as the Misses they
            // turn out to be at the seal (backlog 167).
            Assert.AreEqual(0, results.Counts[JudgementType.Abandoned]);

            Assert.AreEqual(3, results.SyncTimeline.Count);
            Assert.AreEqual(new SyncSample(1200, 200), results.SyncTimeline[0]);

            Assert.AreEqual("Test", results.Artist);
            Assert.AreEqual("Song", results.Title);
            Assert.AreEqual("D", results.Grade); // sync 39.58 fails every tier floor => D
        }

        [Test]
        public void GradeThresholds()
        {
            // Both thresholds must hold at a tier; otherwise fall to the highest tier where both do.
            Assert.AreEqual("S", summary(95, 0.95).Grade);      // exactly on the S floor
            Assert.AreEqual("A", summary(94.999, 1.0).Grade);   // sync just below S => A (acc fine)
            Assert.AreEqual("A", summary(100, 0.949).Grade);    // acc just below S => A (sync fine)
            Assert.AreEqual("A", summary(90, 0.90).Grade);      // exactly on the A floor
            Assert.AreEqual("B", summary(89.999, 1.0).Grade);   // sync just below A
            Assert.AreEqual("B", summary(80, 0.80).Grade);      // exactly on the B floor
            Assert.AreEqual("C", summary(79.999, 0.80).Grade);  // sync just below B
            Assert.AreEqual("C", summary(65, 0.65).Grade);      // exactly on the C floor
            Assert.AreEqual("D", summary(64.999, 1.0).Grade);   // sync below every floor
            Assert.AreEqual("D", summary(100, 0.5).Grade);      // perfect sync can't rescue bad accuracy

            static ResultsSummary summary(double syncPercent, double accuracy) => new ResultsSummary
            {
                Score = 0,
                Accuracy = accuracy,
                Wpm = 0,
                SyncPercent = syncPercent,
                MaxCombo = 0,
                Counts = new Dictionary<JudgementType, int>(),
                SyncTimeline = System.Array.Empty<SyncSample>(),
                Artist = "Test",
                Title = "Song",
            };
        }

        [Test]
        public void BackspaceRetypeIsScoringInert()
        {
            var engine = new TypingEngine(map(TimingGranularity.Line, abcdLine()));

            engine.Update(1000);
            Assert.IsTrue(engine.ProcessKey('a', 1000)); // Great at target: 300 * (1 + 0/50) = 300.
            Assert.AreEqual(300, engine.Score);
            Assert.AreEqual(1, engine.Combo);

            // Backspace-retype the same correct cell repeatedly (score/combo/accuracy farming attempt).
            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(engine.ProcessBackspace());
                Assert.IsTrue(engine.ProcessKey('a', 1400 + i));
            }

            // Fully inert: nothing accrued beyond the original judgement.
            Assert.AreEqual(300, engine.Score);
            Assert.AreEqual(1, engine.Combo);
            Assert.AreEqual(1, engine.MaxCombo);
            Assert.AreEqual(1.0, engine.LiveAccuracy);                    // retypes don't enter the denominator
            Assert.AreEqual(0, engine.Lines[0].Cells[0].JudgedDelta!.Value); // original Great delta kept

            engine.Update(4000); // seal line 0
            var results = engine.BuildResults();
            Assert.AreEqual(1, results.SyncTimeline.Count);               // one sample, not 101
            Assert.AreEqual(1, results.Counts[JudgementType.Great]);    // one Great, not 101
        }

        /// <summary>
        /// Overlap fixture mirroring the loader's output for overlapping vocals: line A's tail
        /// unit is clamped onto its EndTime (3000) with the real 600ms overrun recorded as
        /// SealGraceMs; line B starts exactly at A's boundary.
        /// Cells of A: 'a'=1000, 'b'=1500, ' '=2000, 'c'=3000, 'd'=3000 (pinned).
        /// </summary>
        private static LyricBeatmap overlapMap() => map(TimingGranularity.Word,
            new LyricLine
            {
                RawText = "ab cd", StartTime = 1000, EndTime = 3000, SingEndTime = 3000,
                Units = new[] { unit("ab", 1000, 2000), unit("cd", 3000, 3000) },
                SealGraceMs = 600,
            },
            line("ef", 3000, 5000, 4000, unit("ef", 3000, 4000)));

        [Test]
        public void SealGraceKeepsOverlapPinnedTailHittable()
        {
            var engine = new TypingEngine(overlapMap());

            int comboBreaks = 0;
            engine.ComboBroken += () => comboBreaks++;

            engine.Update(1000);
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            Assert.IsTrue(engine.ProcessKey(' ', 2000));

            // The frame lands past the boundary before the pinned cells could be typed in
            // rhythm; pre-fix this frame force-missed 'c' and 'd' and broke combo.
            engine.Update(3016);
            Assert.AreEqual(0, engine.ActiveLineIndex);   // grace holds the line open
            Assert.IsTrue(engine.ProcessKey('c', 3016));  // delta +16 vs pinned 3000 => Great
            Assert.IsTrue(engine.ProcessKey('d', 3200));  // delta +200 => Great (Word late 240)

            engine.Update(3216);                          // fully typed => seals early, B activates
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.AreEqual(0, comboBreaks);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss]);
            Assert.AreEqual(5, engine.Combo);             // rhythm play keeps the full combo
        }

        [Test]
        public void SealGraceExpiryForceSealsUntypedTail()
        {
            var engine = new TypingEngine(overlapMap());

            int comboBreaks = 0;
            engine.ComboBroken += () => comboBreaks++;

            engine.Update(1000);
            engine.Update(3616); // grace (600) expired with nothing typed => A force-seals

            Assert.AreEqual(1, engine.ActiveLineIndex);   // B active (3616 < 5000)
            Assert.AreEqual(5, engine.BuildResults().Counts[JudgementType.Miss]);
            Assert.AreEqual(1, comboBreaks);              // at most one break per sealed line
        }

        [Test]
        public void EstimatedLineJudgedAtLineWindows()
        {
            // Word-granularity beatmap, but the line is aligner-estimated (no acoustic
            // evidence); its cells judge at the wider Line windows.
            var est = new LyricLine
            {
                RawText = "ab cd", StartTime = 1000, EndTime = 4000, SingEndTime = 3000,
                Units = new[] { unit("ab", 1000, 2000), unit("cd", 2000, 3000) },
                Estimated = true,
            };
            var engine = new TypingEngine(map(TimingGranularity.Word, est));

            int comboBreaks = 0;
            engine.ComboBroken += () => comboBreaks++;

            engine.Update(1000);

            // delta +800 on 'a': past Word MehLate (1200 * 0.6 = 720) => would be Lagging +
            // combo break; at Line windows (OkLate 1000) it's Ok with points.
            Assert.IsTrue(engine.ProcessKey('a', 1800));
            Assert.AreEqual(0, comboBreaks);
            Assert.AreEqual(1, engine.Combo);
            Assert.AreEqual(150, engine.Score); // Ok = 150 * (1 + 0/50)
        }

        [Test]
        public void LowConfidenceWordJudgedAtLineWindowsOnly()
        {
            var l = new LyricLine
            {
                RawText = "ab cd", StartTime = 1000, EndTime = 4000, SingEndTime = 3000,
                Units = new[]
                {
                    new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 2000, Confidence = 0.01 },
                    new TimedUnit { Text = "cd", StartTime = 2000, EndTime = 3000 }, // trusted (1)
                },
            };
            var engine = new TypingEngine(map(TimingGranularity.Word, l));
            var cells = engine.Lines[0].Cells;

            Assert.AreEqual(TimingGranularity.Line, cells[0].JudgeGranularity); // low-score word widened
            Assert.AreEqual(TimingGranularity.Line, cells[2].JudgeGranularity); // its trailing space too
            Assert.AreEqual(TimingGranularity.Word, cells[3].JudgeGranularity); // trusted word stays tight
        }

        [Test]
        public void RealSpectatorRhythmPerfectPlayHasZeroMissesAndUnbrokenCombo()
        {
            // End-to-end guarantee for the game's core promise: a player who types every cell
            // at exactly its target time (quantized to 60fps frames) through the ENTIRE real
            // map must never be force-missed or combo-broken. Pre-seal-grace, the overlapping
            // backing-vocal line ("Dying for a way to let go") force-missed its last 7 cells.
            string path = StandaloneMaps.Require("Friday Pilots Club - Spectator", "timing.json");
            Assert.IsTrue(TimingJsonLoader.TryLoad(path, out var lyricLines));

            var beatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata
                {
                    Artist = "Friday Pilots Club",
                    Title = "Spectator",
                    FolderPath = Path.GetDirectoryName(path)!,
                    AudioFileName = "unused.mp3",
                    HasWordTiming = true,
                },
                Lines = lyricLines,
                Granularity = TimingGranularity.Word,
            };

            var engine = new TypingEngine(beatmap);

            int comboBreaks = 0;
            engine.ComboBroken += () => comboBreaks++;

            const double frame = 1000.0 / 60;

            for (double t = 0; t <= beatmap.LastLineEnd + 1000 && !engine.IsFinished; t += frame)
            {
                engine.Update(t);

                // Rhythm-perfect player: type each caret cell on the first frame at/after its target.
                while (engine.ActiveLineIndex != -1 && !engine.IsLineComplete)
                {
                    var cell = engine.Lines[engine.ActiveLineIndex].Cells[engine.CaretIndex];

                    if (cell.TargetTime > t)
                        break;

                    Assert.IsTrue(engine.ProcessKey(cell.Expected, t));
                }
            }

            engine.Update(beatmap.LastLineEnd + 1100);

            var results = engine.BuildResults();
            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(0, results.Counts[JudgementType.Miss], "rhythm-perfect play must never be force-missed");
            Assert.AreEqual(0, comboBreaks, "rhythm-perfect play must never break combo");
            Assert.AreEqual(1.0, results.Accuracy);
        }

        [Test]
        public void UnreachableNonAsciiCharsAutoSkip()
        {
            // 'ß' has no FormD decomposition and no key can produce it; it must classify as
            // non-typeable (auto-skip), never strand the caret. 'é' decomposes to 'e' upstream
            // in Typeability.Normalize, so it never reaches the cells un-decomposed.
            Assert.AreEqual("cafe", Typeability.Normalize("café"));
            Assert.IsFalse(Typeability.IsTypeable('ß'));
            Assert.IsFalse(Typeability.IsTypeable('ø'));

            var l = line("straße", 1000, 3000, 2000, unit("straße", 1000, 2000));
            var engine = new TypingEngine(map(TimingGranularity.Line, l));

            engine.Update(1000);
            Assert.IsTrue(engine.ProcessKey('s', 1000));
            Assert.IsTrue(engine.ProcessKey('t', 1100));
            Assert.IsTrue(engine.ProcessKey('r', 1200));
            Assert.IsTrue(engine.ProcessKey('a', 1300));
            // 'ß' auto-skips; caret lands on 'e'.
            Assert.IsTrue(engine.ProcessKey('e', 1400));
            Assert.IsTrue(engine.IsLineComplete);
            Assert.AreEqual(CellState.AutoSkipped, engine.Lines[0].Cells[4].State);
        }

        [Test]
        public void LateVocalsActivateAtCueNotBoundary()
        {
            // Window opens at 1000 but the first word starts at 8000: the line becomes typeable
            // at firstWord - CUE_LEAD_MS = 8000 - 1500 = 6500, not at the boundary.
            var l = line("ab", 1000, 10000, 9000, unit("ab", 8000, 9000));
            var engine = new TypingEngine(map(TimingGranularity.Word, l));

            Assert.AreEqual(6500, engine.Lines[0].ActivationTime);

            engine.Update(1000);
            Assert.AreEqual(-1, engine.ActiveLineIndex, "boundary alone must not activate");

            engine.Update(6499);
            Assert.AreEqual(-1, engine.ActiveLineIndex);
            Assert.IsFalse(engine.ProcessKey('a', 6499), "typing before the cue is inert");

            engine.Update(6500);
            Assert.AreEqual(0, engine.ActiveLineIndex, "cue reached: line typeable");
            Assert.IsTrue(engine.ProcessKey('a', 6500));
        }

        [Test]
        public void DeadZoneBetweenSealAndCueHasNoActiveLine()
        {
            // Line 0 seals at its boundary (4000); line 1's first word is at 10000, so its cue is
            // 8500. In between, no line is active (input inert) but line 1 is already the
            // upcoming line; the stage scrolls at the seal, dimmed until the cue.
            var l0 = abcdLine();
            var l1 = line("ef", 4000, 12000, 11000, unit("ef", 10000, 11000));
            var engine = new TypingEngine(map(TimingGranularity.Word, l0, l1));

            engine.Update(3999);
            Assert.AreEqual(0, engine.NextUnsealedLineIndex);

            engine.Update(4000);
            Assert.AreEqual(1, engine.NextUnsealedLineIndex, "line 0 sealed at its boundary");
            Assert.AreEqual(-1, engine.ActiveLineIndex, "dead zone: nothing active yet");
            Assert.IsFalse(engine.ProcessKey('e', 4000), "dead-zone typing is inert");

            engine.Update(8499);
            Assert.AreEqual(-1, engine.ActiveLineIndex);

            engine.Update(8500);
            Assert.AreEqual(1, engine.ActiveLineIndex, "line 1 activates at its cue");
        }

        [Test]
        public void ImmediateVocalsActivateAtBoundaryAsBefore()
        {
            // When a line's first word starts on its boundary, the cue clamps to the boundary
            // (a line can never activate before the previous one can seal); the pre-cue
            // behavior is unchanged for back-to-back lines.
            var l0 = abcdLine();
            var l1 = line("ef", 4000, 6000, 5500, unit("ef", 4000, 5000));
            var engine = new TypingEngine(map(TimingGranularity.Word, l0, l1));

            Assert.AreEqual(4000, engine.Lines[1].ActivationTime);

            engine.Update(4000);
            Assert.AreEqual(1, engine.ActiveLineIndex, "seal and next activation share the boundary frame");
        }

        [Test]
        public void CaseSensitiveRejectsWrongCaseLikeWrongChar()
        {
            // "aB": 'a' lower-case target @1000, 'B' upper-case target @1500. Under Literate the
            // cells carry the authored case (without it they would be flattened to "ab").
            // Gatekeeper on, so a wrong-case letter is REJECTED rather than typed through; the
            // wrong-case-under-the-default-model case is covered by
            // CaseSensitiveTypesWrongCaseThroughByDefault below.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("aB", 1000, 3000, 2000, unit("aB", 1000, 2000))), literate: true) { AllowWrongInput = false };

            Assert.IsTrue(engine.CaseSensitive);
            Assert.AreEqual("aB", engine.Lines[0].DisplayText);

            var rejected = new List<char>();
            int comboBreaks = 0;
            engine.WrongKeyRejected += c => rejected.Add(c);
            engine.ComboBroken += () => comboBreaks++;

            engine.Update(1000);

            // Matching lower-case 'a' is accepted (right char, right case).
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[0].State);
            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(1, engine.Combo);

            // Lower-case 'b' where 'B' is expected: WRONG-CASE => rejected exactly like a wrong
            // char, nothing input, caret held, combo broken, streak grown.
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[1].State);
            Assert.IsNull(engine.Lines[0].Cells[1].TypedChar);
            Assert.AreEqual(1, engine.CaretIndex); // caret did NOT advance
            Assert.AreEqual(0, engine.Combo);
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys);
            Assert.AreEqual(new[] { 'b' }, rejected);

            // The correct capital is accepted.
            Assert.IsTrue(engine.ProcessKey('B', 1500));
            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys);

            engine.Update(3000); // seal: both cells Correct => no missed cells

            var results = engine.BuildResults();
            Assert.AreEqual(1, results.Counts[JudgementType.WrongChar]); // the wrong-case 'b'
            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);
            // 2 correct / 3 keypresses (the rejected wrong-case key stays in the denominator).
            Assert.AreEqual(2.0 / 3.0, engine.LiveAccuracy, 1e-9);
        }

        [Test]
        public void CaseSensitiveTypesWrongCaseThroughByDefault()
        {
            // The same wrong-case press as above, but WITHOUT Gatekeeper, i.e. the default model:
            // the letter lands in the cell as a Wrong char, the caret moves on, and the mash-fail
            // streak is deliberately not fed.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("aB", 1000, 3000, 2000, unit("aB", 1000, 2000))), literate: true);

            Assert.IsTrue(engine.AllowWrongInput);

            var rejected = new List<char>();
            var judgements = new List<CharJudgement>();
            engine.WrongKeyRejected += c => rejected.Add(c);
            engine.CharJudged += j => judgements.Add(j);

            engine.Update(1000);

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1500)); // wrong case for 'B'

            Assert.AreEqual(CellState.Wrong, engine.Lines[0].Cells[1].State);
            Assert.AreEqual('b', engine.Lines[0].Cells[1].TypedChar);
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.AreEqual(0, engine.Combo);
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys); // no mash streak off the default path
            Assert.IsEmpty(rejected);                       // no rejection event either
            Assert.AreEqual(JudgementType.WrongChar, judgements[1].Type);

            // Seal with the wrong char still sitting there: nothing is left UNTYPED, and the wrong
            // cell is NOT a miss (backlog 124), because the player finished that character and only
            // got it wrong. It keeps CellState.Wrong on screen, so the stack still says which
            // character went wrong, and the mistype is the trace it leaves.
            engine.Update(3000);

            Assert.AreEqual(CellState.Wrong, engine.Lines[0].Cells[1].State);
            Assert.IsTrue(engine.CellLeftWrong(0, 1));

            var results = engine.BuildResults();
            Assert.AreEqual(1, results.Counts[JudgementType.WrongChar]);
            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);
            Assert.AreEqual(0.5, engine.LiveAccuracy, 1e-9); // 1 correct / 2 keypresses
        }

        [Test]
        public void CaseInsensitiveByDefaultAcceptsWrongCase()
        {
            // Same line, but CaseSensitive left OFF (default); behaviour is unchanged:
            // lower-case input matches an upper-case target through Fold.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("aB", 1000, 3000, 2000, unit("aB", 1000, 2000))));

            Assert.IsFalse(engine.CaseSensitive);

            var rejected = new List<char>();
            engine.WrongKeyRejected += c => rejected.Add(c);

            engine.Update(1000);

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            // 'b' folds to match the 'B' target: accepted, caret advances, combo intact.
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.AreEqual(2, engine.Combo);
            Assert.IsEmpty(rejected);
            Assert.AreEqual(1.0, engine.LiveAccuracy);
        }

        #region Syllable subdivisions (piecewise per-char timing)

        private static TimedUnit subdividedUnit(string text, double start, double end, params double[] boundaries)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, SyllableBoundaries = boundaries };

        [Test]
        public void SyllableBoundaryWarpsPerCharTargetsNonUniformly()
        {
            // Word "abcd" over [1000, 2000], one boundary at 1200 (off-centre; the flat midpoint is 1500).
            // k = 4 chars, 2 segments. Chars split evenly by index: a,b in seg1 [1000,1200], c,d in seg2 [1200,2000].
            //   a (j0) = 1000                      (first char always at unit start)
            //   b (j1) = 1000 + (1-0)/2 * (1200-1000) = 1100
            //   c (j2) = 1200                      (lands exactly on the boundary)
            //   d (j3) = 1200 + (3-2)/2 * (2000-1200) = 1600
            var divided = TypingLine.FromLyricLine(
                line("abcd", 1000, 3000, 2000, subdividedUnit("abcd", 1000, 2000, 1200)),
                TimingGranularity.Syllable);

            Assert.AreEqual(1000, divided.Cells[0].TargetTime);
            Assert.AreEqual(1100, divided.Cells[1].TargetTime);
            Assert.AreEqual(1200, divided.Cells[2].TargetTime);
            Assert.AreEqual(1600, divided.Cells[3].TargetTime);

            // Non-uniform: the first two chars are 100 ms apart, the last two 400 ms; the caret
            // slows down in the longer second syllable instead of a constant 250 ms/char.
            Assert.AreNotEqual(divided.Cells[1].TargetTime - divided.Cells[0].TargetTime,
                divided.Cells[3].TargetTime - divided.Cells[2].TargetTime);

            // The caret polyline follows the boundary: at the boundary time it sits on char c (index 2),
            // and it advances faster through seg1 than seg2.
            Assert.AreEqual(2, divided.SungPositionAt(1200));          // on the boundary => on char c
            Assert.AreEqual(0.5, divided.SungPositionAt(1050));        // 50/100 through a->b (fast segment)
            Assert.AreEqual(2.5, divided.SungPositionAt(1600 - 200));  // c(1200,2)->d(1600,3): 200/400 = 0.5
        }

        [Test]
        public void UndividedWordKeepsFlatInterpolation()
        {
            // Identical word with NO boundary: unchanged flat ramp 1000/1250/1500/1750; the fix must
            // leave every existing (undivided) map byte-identical.
            var flat = TypingLine.FromLyricLine(
                line("abcd", 1000, 3000, 2000, unit("abcd", 1000, 2000)),
                TimingGranularity.Syllable);

            Assert.AreEqual(1000, flat.Cells[0].TargetTime);
            Assert.AreEqual(1250, flat.Cells[1].TargetTime); // 1000 + 1*(2000-1000)/4
            Assert.AreEqual(1500, flat.Cells[2].TargetTime); // 1000 + 2*(2000-1000)/4
            Assert.AreEqual(1750, flat.Cells[3].TargetTime); // 1000 + 3*(2000-1000)/4
        }

        [Test]
        public void MultipleBoundariesSplitEvenlyByCharIndex()
        {
            // Word "abcdef" over [0, 1200] with two boundaries -> 3 segments, k = 6, so 2 chars/segment.
            // Boundaries 300, 900 (deliberately uneven durations 300/600/300).
            //   seg0 [0,300]:   a (j0)=0,   b (j1)= 0 + (1-0)/2*300 = 150
            //   seg1 [300,900]: c (j2)=300, d (j3)= 300 + (3-2)/2*600 = 600
            //   seg2 [900,1200]:e (j4)=900, f (j5)= 900 + (5-4)/2*300 = 1050
            var t = TypingLine.FromLyricLine(
                line("abcdef", 0, 2000, 1200, subdividedUnit("abcdef", 0, 1200, 300, 900)),
                TimingGranularity.Syllable);

            Assert.AreEqual(0, t.Cells[0].TargetTime);
            Assert.AreEqual(150, t.Cells[1].TargetTime);
            Assert.AreEqual(300, t.Cells[2].TargetTime);
            Assert.AreEqual(600, t.Cells[3].TargetTime);
            Assert.AreEqual(900, t.Cells[4].TargetTime);
            Assert.AreEqual(1050, t.Cells[5].TargetTime);

            // Targets stay strictly non-decreasing across the whole word.
            for (int i = 1; i < t.Cells.Count; i++)
                Assert.LessOrEqual(t.Cells[i - 1].TargetTime, t.Cells[i].TargetTime);
        }

        #endregion

        #region Rolling live WPM (HUD readout)

        /// <summary>
        /// 36 letters, no spaces and no punctuation, so press i is simply <c>long_chars[i]</c> with no
        /// auto-skip in between. One unit over [1000, 37000] with k = 36 puts cell i at 1000 + i*1000,
        /// and the line window runs to 200000 so it never seals mid-test. The line activates at 1000
        /// (max(StartTime, firstTarget - CUE_LEAD_MS)), and accrual starts the frame after, so through
        /// every test below activeTime == time - 1000 while the line is active and incomplete.
        /// </summary>
        private const string long_chars = "abcdefghijklmnopqrstuvwxyzabcdefghij";

        private static LyricBeatmap longMap() => map(TimingGranularity.Line,
            line(long_chars, 1000, 200000, 37000, unit(long_chars, 1000, 37000)));

        [Test]
        public void RollingWpmFallsBackToWholeRunUntilTwoSpreadPresses()
        {
            var engine = new TypingEngine(map(TimingGranularity.Line, abcdLine()));

            engine.Update(0);
            Assert.AreEqual(0, engine.LiveWpm);
            Assert.AreEqual(0, engine.LiveRollingWpm); // no presses at all: the whole-run 0

            engine.Update(1000); // activation frame: accrual runs before activation, activeTime = 0
            Assert.IsTrue(engine.ProcessKey('a', 1000)); // stamp @ 0
            engine.Update(2000);                         // active & incomplete: activeTime = 1000

            // ONE press: no span to measure, so the readout is the whole-run figure, (1/5)/(1000/60000).
            Assert.AreEqual(12.0, engine.LiveWpm, 1e-9);
            Assert.AreEqual(12.0, engine.LiveRollingWpm, 1e-9);

            Assert.IsTrue(engine.ProcessKey('b', 2000)); // stamp @ 1000

            // Two presses 1000 ms apart: the window takes over. n-1 = 1 char over 1000 ms => 12 WPM,
            // while the whole-run figure counts both chars over the same 1000 ms => 24.
            Assert.AreEqual(24.0, engine.LiveWpm, 1e-9);
            Assert.AreEqual(12.0, engine.LiveRollingWpm, 1e-9);
        }

        [Test]
        public void RollingWpmFallsBackWhenEveryPressLandsInOneFrame()
        {
            var engine = new TypingEngine(map(TimingGranularity.Line, abcdLine()));

            engine.Update(0);
            engine.Update(1000);

            // Active time only advances in Update, so both presses stamp 0: a zero-span window.
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1500));

            engine.Update(2000); // activeTime = 1000

            // Zero span => whole-run fallback, (2/5)/(1000/60000), not a divide by zero.
            Assert.AreEqual(24.0, engine.LiveWpm, 1e-9);
            Assert.AreEqual(24.0, engine.LiveRollingWpm, 1e-9);
        }

        [Test]
        public void RollingWpmTracksSteadyCadence()
        {
            var engine = new TypingEngine(longMap());

            engine.Update(0);

            // Ten presses one second of active time apart: stamps 0, 1000, ..., 9000.
            for (int i = 0; i < 10; i++)
            {
                double t = 1000 + i * 1000;
                engine.Update(t);
                Assert.IsTrue(engine.ProcessKey(long_chars[i], t));
            }

            // 1 char/s = 60 chars/min = 12 WPM; the n-1 convention reproduces the cadence exactly:
            // ((10-1)/5)/(9000/60000) = 1.8/0.15 = 12.
            Assert.AreEqual(12.0, engine.LiveRollingWpm, 1e-9);

            // The whole-run figure divides 10 chars by the same 9000 ms and reads 11% high.
            Assert.AreEqual(120000.0 / 9000.0, engine.LiveWpm, 1e-9);
        }

        [Test]
        public void RollingWpmScalesWithTheClockRateExactlyOnce()
        {
            var engine = new TypingEngine(longMap());

            engine.Update(0, 0.5);

            // The identical beatmap cadence to RollingWpmTracksSteadyCadence (one press per 1000 beatmap
            // ms), but at 0.5x every one of those seconds really took two, so the stamps, which are
            // active REAL time, land at 0, 2000, ..., 18000 rather than 0, 1000, ..., 9000.
            for (int i = 0; i < 10; i++)
            {
                double t = 1000 + i * 1000;
                engine.Update(t, 0.5);
                Assert.IsTrue(engine.ProcessKey(long_chars[i], t));
            }

            // ((10-1)/5)/(18000/60000) = 1.8/0.3 = 6: exactly half the 12 the unmodded run reads, which
            // is right, half speed means the player really typed half as fast. The correction is applied
            // ONCE, in the accumulator the stamps come from; scaling the span again here would read 3.
            Assert.AreEqual(6.0, engine.LiveRollingWpm, 1e-9);

            // Whole-run over the same real span: (10/5)/(18000/60000), half of the 1x figure.
            Assert.AreEqual(120000.0 / 18000.0, engine.LiveWpm, 1e-9);
        }

        [Test]
        public void RollingWpmBurstAfterSlowStartExceedsWholeRun()
        {
            var engine = new TypingEngine(longMap());

            engine.Update(0);

            // Six presses one per 5 s: stamps 0, 5000, ..., 25000.
            for (int i = 0; i < 6; i++)
            {
                double t = 1000 + i * 5000;
                engine.Update(t);
                Assert.IsTrue(engine.ProcessKey(long_chars[i], t));
            }

            // Then 30 presses one per 100 ms: stamps 25100, 25200, ..., 28000. Being exactly a full
            // window, these evict every slow press.
            for (int i = 6; i < 36; i++)
            {
                double t = 26000 + (i - 5) * 100;
                engine.Update(t);
                Assert.IsTrue(engine.ProcessKey(long_chars[i], t));
            }

            // ((30-1)/5)/(2900/60000) = 5.8/0.0483... = 120: 10 chars/s, the burst and nothing else.
            Assert.AreEqual(120.0, engine.LiveRollingWpm, 1e-9);

            // Whole-run: 36 chars over 28000 ms of active time = 15.43, dragged down by the slow start.
            Assert.AreEqual(432000.0 / 28000.0, engine.LiveWpm, 1e-9);
            Assert.Greater(engine.LiveRollingWpm, engine.LiveWpm);

            // The results screen keeps the whole-run number: the rolling window is display-only.
            Assert.AreEqual(engine.LiveWpm, engine.BuildResults().Wpm, 1e-9);
        }

        [Test]
        public void RollingWpmWindowCapsAtThirtyPresses()
        {
            var engine = new TypingEngine(longMap());

            engine.Update(0);
            engine.Update(1000);
            Assert.IsTrue(engine.ProcessKey(long_chars[0], 1000)); // press 1, stamp @ 0

            // Presses 2..30, one per second, starting 30 s of active time after the first:
            // stamps 30000, 31000, ..., 58000.
            for (int i = 1; i <= 29; i++)
            {
                double t = 31000 + (i - 1) * 1000;
                engine.Update(t);
                Assert.IsTrue(engine.ProcessKey(long_chars[i], t));
            }

            // Window exactly full (30): the ancient first press still anchors it.
            // ((30-1)/5)/(58000/60000) = 5.8/0.9666... = 6.
            Assert.AreEqual(6.0, engine.LiveRollingWpm, 1e-9);

            engine.Update(60000);
            Assert.IsTrue(engine.ProcessKey(long_chars[30], 60000)); // press 31, stamp @ 59000

            // The 31st press evicts the oldest, so the window is now 30000..59000:
            // ((30-1)/5)/(29000/60000) = 12, the true recent cadence. Without the cap it would be
            // ((31-1)/5)/(59000/60000) = 6.10, still anchored to a press half a minute old.
            Assert.AreEqual(12.0, engine.LiveRollingWpm, 1e-9);
        }

        [Test]
        public void RollingWpmDoesNotDecayAcrossInactiveTime()
        {
            // L0 "abc" [1000, 5000): a = 1000, b = 2000, c = 3000.
            // L1 "def" [20000, 24000): d = 20000, e = 21000, f = 22000, after a 15 s dead gap.
            var engine = new TypingEngine(map(TimingGranularity.Line,
                line("abc", 1000, 5000, 4000, unit("abc", 1000, 4000)),
                line("def", 20000, 24000, 23000, unit("def", 20000, 23000))));

            engine.Update(0);
            engine.Update(1000); // activate L0, activeTime = 0
            Assert.IsTrue(engine.ProcessKey('a', 1000)); // stamp @ 0
            engine.Update(2000);
            Assert.IsTrue(engine.ProcessKey('b', 2000)); // stamp @ 1000
            engine.Update(3000);
            Assert.IsTrue(engine.ProcessKey('c', 3000)); // stamp @ 2000, L0 complete

            // ((3-1)/5)/(2000/60000) = 12.
            Assert.AreEqual(12.0, engine.LiveRollingWpm, 1e-9);

            engine.Update(5000);  // L0 was complete: no accrual, then it seals
            engine.Update(12000); // dead gap: nothing active, no accrual
            engine.Update(19000);

            // 16 s of song went by with nothing to type: the readout must not have decayed.
            Assert.AreEqual(12.0, engine.LiveRollingWpm, 1e-9);

            engine.Update(20000);                        // activate L1; still no accrual this frame
            Assert.IsTrue(engine.ProcessKey('d', 20000)); // stamp @ 2000: the gap bought no active time

            // ((4-1)/5)/(2000/60000) = 18: typing resumes exactly where it left off.
            Assert.AreEqual(18.0, engine.LiveRollingWpm, 1e-9);

            engine.Update(21000); // activeTime = 3000
            Assert.IsTrue(engine.ProcessKey('e', 21000)); // stamp @ 3000

            // ((5-1)/5)/(3000/60000) = 16. On a wall clock the span would be 20000 ms (t=1000 to
            // t=21000) and this would read 2.4: active time is what keeps the gap out of it.
            Assert.AreEqual(16.0, engine.LiveRollingWpm, 1e-9);
        }

        [Test]
        public void RollingWpmKeepsHistoryAcrossBackspaceRetype()
        {
            var engine = new TypingEngine(map(TimingGranularity.Line, abcdLine()));

            engine.Update(0);
            engine.Update(1000);
            Assert.IsTrue(engine.ProcessKey('a', 1000)); // stamp @ 0
            engine.Update(2000);
            Assert.IsTrue(engine.ProcessKey('b', 2000)); // stamp @ 1000
            Assert.AreEqual(12.0, engine.LiveRollingWpm, 1e-9);

            long scoreBeforeRetype = engine.Score;

            // Backspace does NOT pop: the window is a log of keystrokes, not of cell states.
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.AreEqual(12.0, engine.LiveRollingWpm, 1e-9);

            engine.Update(2500);                         // activeTime = 1500
            Assert.IsTrue(engine.ProcessKey('b', 2500)); // scoring-inert retype, but a real keystroke

            // The retype is still inert everywhere it was before, only the keystroke log grew.
            Assert.AreEqual(scoreBeforeRetype, engine.Score);

            // Stamps 0 / 1000 / 1500 => ((3-1)/5)/(1500/60000) = 16.
            Assert.AreEqual(16.0, engine.LiveRollingWpm, 1e-9);
        }

        #endregion
    }
}
