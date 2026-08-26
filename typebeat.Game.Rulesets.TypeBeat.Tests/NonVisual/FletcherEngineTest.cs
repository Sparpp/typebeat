// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Fletcher mod (backlog 25) gameplay-core tests. The mod unpins the player's caret from the song's
// playhead: rush freedom (finish a line and you are typing the next one at once), drag freedom (the
// song moving on does not snatch the line you are still finishing), and a character-distance rush
// cap replacing the timing lock. Every expected value below is hand-computed beside its assert, in
// the style of TypingEngineTest, and the first fixture pins the DEFAULT path byte-identical: a run
// that stays in sync scores exactly the same with the mod on as with it off.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class FletcherEngineTest
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
        /// Two back-to-back lines with round targets.
        /// L0 "ab cd" [1000, 4000), SingEnd 3000: a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500.
        /// L1 "ef" [4000, 6000), SingEnd 5000: e = 4000, f = 4500. Cue-clamped, so L1 activates at 4000.
        /// </summary>
        private static LyricBeatmap twoLineMap() => map(TimingGranularity.Line,
            line("ab cd", 1000, 4000, 3000, unit("ab", 1000, 2000), unit("cd", 2000, 3000)),
            line("ef", 4000, 6000, 5000, unit("ef", 4000, 5000)));

        /// <summary>
        /// A single dense line: 20 letters, no spaces, one unit [1000, 3000] => step 100 ms, so cell i
        /// targets 1000 + 100i. Dense enough that six chars of rush is only 600 ms early, i.e. still
        /// inside the Line-granularity windows: the CHARACTER cap has to be what breaks the combo,
        /// not the clock.
        /// </summary>
        private const string dense_chars = "abcdefghijklmnopqrst";

        private static LyricBeatmap denseMap() => map(TimingGranularity.Line,
            line(dense_chars, 1000, 60000, 3000, unit(dense_chars, 1000, 3000)));

        /// <summary>
        /// L0 "ab" [1000, 3000): a = 1000, b = 1500. L1 "cd" whose vocals do not arrive until 10000,
        /// so its cue (and thus its default activation) is 8500: a long dead zone after L0 is typed.
        /// </summary>
        private static LyricBeatmap lateSecondLineMap() => map(TimingGranularity.Line,
            line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
            line("cd", 3000, 12000, 11000, unit("cd", 10000, 11000)));

        /// <summary>
        /// Back-to-back short lines for the drag cases.
        /// L0 "ab" [1000, 3000): a = 1000, b = 1500. L1 "cd" [3000, 5000): c = 3000, d = 3500.
        /// Neither carries a seal grace, so L0's hard deadline is exactly 3000 and its Fletcher drag
        /// cutoff is 3000 + FLETCHER_DRAG_GRACE_MS = 4500.
        /// </summary>
        private static LyricBeatmap dragMap() => map(TimingGranularity.Line,
            line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
            line("cd", 3000, 5000, 4000, unit("cd", 3000, 4000)));

        private static TypingEngine engine(LyricBeatmap beatmap, bool fletcher)
            => new TypingEngine(beatmap) { FletcherEnabled = fletcher };

        #endregion

        #region Default path / in-sync equivalence

        /// <summary>
        /// The mod flag must be inert for a player who stays in sync. The same fixed script of
        /// (char, time) presses is fed to an engine with Fletcher OFF and one with it ON, and every
        /// observable has to agree, down to per-cell deltas; the flag-off numbers are additionally
        /// pinned by hand so this fixture also guards the default path against drift.
        /// </summary>
        [Test]
        public void InSyncRunIsIdenticalWithAndWithoutFletcher()
        {
            var off = engine(twoLineMap(), fletcher: false);
            var on = engine(twoLineMap(), fletcher: true);

            foreach (var e in new[] { off, on })
            {
                e.Update(0);
                e.Update(1000);
                Assert.IsTrue(e.ProcessKey('a', 1000));
                e.Update(1500);
                Assert.IsTrue(e.ProcessKey('b', 1500));
                e.Update(2000);
                Assert.IsTrue(e.ProcessKey(' ', 2000));
                Assert.IsTrue(e.ProcessKey('c', 2000));
                e.Update(2500);
                Assert.IsTrue(e.ProcessKey('d', 2500));
                e.Update(3000);
                e.Update(4000);
                Assert.IsTrue(e.ProcessKey('e', 4000));
                e.Update(4500);
                Assert.IsTrue(e.ProcessKey('f', 4500));
                e.Update(6000);
            }

            // Hand-computed for the default path: every delta is 0 => Great (300 base), and points
            // read the combo BEFORE the increment, so 300 + 306 + 312 + 318 + 324 + 330 + 336 = 2226.
            Assert.AreEqual(2226, off.Score);
            Assert.AreEqual(7, off.MaxCombo);
            Assert.AreEqual(1.0, off.LiveAccuracy);
            Assert.AreEqual(100.0, off.LiveSyncPercent, 1e-9);
            // Active time = 500 (1000->1500) + 500 + 500 + 500 (4000->4500) = 2000 ms; 7 correct
            // cells => 1.4 words / (2000/60000) min = 42 WPM.
            Assert.AreEqual(42.0, off.BuildResults().Wpm, 1e-9);

            var a = off.BuildResults();
            var b = on.BuildResults();

            Assert.IsTrue(on.IsFinished);
            Assert.AreEqual(a.Score, b.Score);
            Assert.AreEqual(a.MaxCombo, b.MaxCombo);
            Assert.AreEqual(a.Accuracy, b.Accuracy);
            Assert.AreEqual(a.SyncPercent, b.SyncPercent, 1e-12);
            Assert.AreEqual(a.Wpm, b.Wpm, 1e-12);
            Assert.AreEqual(a.SyncTimeline, b.SyncTimeline);

            foreach (JudgementType type in Enum.GetValues<JudgementType>())
                Assert.AreEqual(a.Counts[type], b.Counts[type], $"judgement count for {type}");

            for (int k = 0; k < off.Lines.Count; k++)
            {
                for (int i = 0; i < off.Lines[k].Cells.Count; i++)
                {
                    Assert.AreEqual(off.Lines[k].Cells[i].State, on.Lines[k].Cells[i].State);
                    Assert.AreEqual(off.Lines[k].Cells[i].TypedChar, on.Lines[k].Cells[i].TypedChar);
                    Assert.AreEqual(off.Lines[k].Cells[i].JudgedDelta, on.Lines[k].Cells[i].JudgedDelta);
                }
            }
        }

        /// <summary>
        /// The end-to-end promise on a real map: a player who types every cell at its target time
        /// (60 fps quantised) is never force-missed and never combo-broken, WITH the mod on. Rushing
        /// is measured against the playhead, and a rhythm-perfect caret is by construction never ahead
        /// of it, so the cap can never fire on honest play. This is the same drive loop as
        /// TypingEngineTest's unmodded pin.
        /// </summary>
        [Test]
        public void RealMapRhythmPerfectPlayIsUnpenalisedUnderFletcher()
        {
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

            var typing = engine(beatmap, fletcher: true);

            int comboBreaks = 0;
            typing.ComboBroken += () => comboBreaks++;

            const double frame = 1000.0 / 60;

            for (double t = 0; t <= beatmap.LastLineEnd + 1000 && !typing.IsFinished; t += frame)
            {
                typing.Update(t);

                while (typing.ActiveLineIndex != -1 && !typing.IsLineComplete)
                {
                    var cell = typing.Lines[typing.ActiveLineIndex].Cells[typing.CaretIndex];

                    if (cell.TargetTime > t)
                        break;

                    Assert.IsTrue(typing.ProcessKey(cell.Expected, t));
                }
            }

            typing.Update(beatmap.LastLineEnd + 1100 + TypingEngine.FLETCHER_DRAG_GRACE_MS);

            var results = typing.BuildResults();
            Assert.IsTrue(typing.IsFinished);
            Assert.AreEqual(0, results.Counts[JudgementType.Miss], "rhythm-perfect play must never be force-missed under Fletcher");
            Assert.AreEqual(0, comboBreaks, "a rhythm-perfect caret is never ahead of the playhead, so the rush cap must never fire");
            Assert.AreEqual(1.0, results.Accuracy);
        }

        #endregion

        #region Rush cap (5 countable chars)

        /// <summary>
        /// The cap is a distance, measured on the caret position AFTER the press: five chars ahead is
        /// fine, the sixth breaks the combo. It breaks ONCE per excursion (there is no combo left to
        /// break on the presses that follow), and re-arms as soon as a press lands back inside the
        /// cap, so a second excursion breaks again.
        /// </summary>
        [Test]
        public void SixthCharAheadBreaksComboOnceAndReArms()
        {
            var typing = engine(denseMap(), fletcher: true);

            int comboBreaks = 0;
            typing.ComboBroken += () => comboBreaks++;

            typing.Update(1000);

            // At t = 1000 the song has reached exactly one countable char ('a', target 1000).
            Assert.AreEqual(1, typing.PlayheadCountablePosition(1000));
            Assert.AreEqual(0, typing.CaretCountablePosition);

            // Presses 1..6 at t = 1000 put the caret 0, 1, 2, 3, 4 and 5 chars ahead: all inside the
            // cap, and all still inside the timing windows (-500 ms is Ok at Line granularity), so
            // the combo climbs to 6 untouched.
            for (int i = 0; i < 6; i++)
                Assert.IsTrue(typing.ProcessKey(dense_chars[i], 1000));

            Assert.AreEqual(6, typing.Combo);
            Assert.AreEqual(5, typing.CharsAheadOfPlayhead(1000));
            Assert.AreEqual(0, comboBreaks);

            // The 7th press ('g', target 1600) would sit 6 chars ahead: over the cap. The char still
            // lands, is still judged Ok (delta -600 is the Ok edge) and still scores at the
            // pre-break multiplier, 150 * (1 + 6/50) = 168, but the combo goes.
            long scoreBefore = typing.Score;
            Assert.IsTrue(typing.ProcessKey('g', 1000));
            Assert.AreEqual(CellState.Correct, typing.Lines[0].Cells[6].State);
            Assert.AreEqual(-600, typing.Lines[0].Cells[6].JudgedDelta);
            Assert.AreEqual(168, typing.Score - scoreBefore);
            Assert.AreEqual(0, typing.Combo);
            Assert.AreEqual(6, typing.MaxCombo, "the over-cap press must not extend max combo");
            Assert.AreEqual(1, comboBreaks);

            // Still out past the cap: the press lands and scores (Ok, 50 at 1.0x) but there is no
            // combo left to break, so no second event fires.
            Assert.IsTrue(typing.ProcessKey('h', 1000));
            Assert.AreEqual(7, typing.CharsAheadOfPlayhead(1000));
            Assert.AreEqual(0, typing.Combo);
            Assert.AreEqual(1, comboBreaks, "one break per excursion, not one per press");

            // Let the song catch up: at t = 2000 it has reached 11 countable chars (targets 1000..2000)
            // while the caret sits at 8, three chars BEHIND. Typing resumes inside the cap and combo
            // rebuilds, which is what re-arms the rule.
            typing.Update(2000);
            Assert.AreEqual(11, typing.PlayheadCountablePosition(2000));
            Assert.AreEqual(-3, typing.CharsAheadOfPlayhead(2000));

            for (int i = 8; i < 16; i++) // 'i'..'p': the caret walks from 3 behind to 5 ahead
                Assert.IsTrue(typing.ProcessKey(dense_chars[i], 2000));

            Assert.AreEqual(8, typing.Combo);
            Assert.AreEqual(5, typing.CharsAheadOfPlayhead(2000));
            Assert.AreEqual(1, comboBreaks);

            // Sixth char ahead again => a second, freshly armed break.
            Assert.IsTrue(typing.ProcessKey('q', 2000));
            Assert.AreEqual(0, typing.Combo);
            Assert.AreEqual(2, comboBreaks);
        }

        /// <summary>The cap is Fletcher's alone: without the mod the identical burst keeps its combo,
        /// because nothing in the default engine measures character distance.</summary>
        [Test]
        public void RushCapDoesNotExistWithoutFletcher()
        {
            var typing = engine(denseMap(), fletcher: false);

            int comboBreaks = 0;
            typing.ComboBroken += () => comboBreaks++;

            typing.Update(1000);

            for (int i = 0; i < 8; i++)
                Assert.IsTrue(typing.ProcessKey(dense_chars[i], 1000));

            Assert.AreEqual(8, typing.Combo);
            Assert.AreEqual(0, comboBreaks);
        }

        /// <summary>
        /// A space spends no budget (it is not a COUNTABLE char, the same rule the Flashlight window
        /// uses), so pressing one can never be the press that crosses the cap.
        /// </summary>
        [Test]
        public void SpacesDoNotSpendRushBudget()
        {
            // "abc de": one unit per word, a = 1000, b = 1100, c = 1200, ' ' = 1300, d = 1300, e = 1400.
            var typing = engine(map(TimingGranularity.Line,
                line("abc de", 1000, 9000, 1500, unit("abc", 1000, 1300), unit("de", 1300, 1500))), fletcher: true);

            typing.Update(1000);

            foreach (char c in "abc de")
                Assert.IsTrue(typing.ProcessKey(c, 1000));

            // Six presses, but only five countable chars, so the caret is 5 - 1 = 4 ahead of the
            // playhead (which has reached 'a' only): still inside the cap, combo intact.
            Assert.AreEqual(5, typing.CaretCountablePosition);
            Assert.AreEqual(4, typing.CharsAheadOfPlayhead(1000));
            Assert.AreEqual(6, typing.Combo);
        }

        /// <summary>A freestyle cell is a normal countable char here as everywhere else: any key
        /// satisfies it, and it spends one unit of rush budget.</summary>
        [Test]
        public void FreestyleCellCountsLikeAnyOtherCharUnderFletcher()
        {
            // "a&c": '&' is a freestyle slot; unit [1000, 1300], k = 3 => a = 1000, & = 1100, c = 1200.
            var typing = engine(map(TimingGranularity.Line,
                line("a" + Typeability.FREESTYLE_MARKER + "c", 1000, 9000, 1300,
                    unit("a" + Typeability.FREESTYLE_MARKER + "c", 1000, 1300))), fletcher: true);

            Assert.IsTrue(typing.Lines[0].Cells[1].IsFreestyle);
            Assert.IsTrue(typing.Lines[0].Cells[1].IsCountable);

            typing.Update(1000);

            Assert.IsTrue(typing.ProcessKey('a', 1000));
            Assert.IsTrue(typing.ProcessKey('z', 1000)); // any key satisfies the freestyle cell
            Assert.AreEqual('z', typing.Lines[0].Cells[1].TypedChar);
            Assert.AreEqual(2, typing.CaretCountablePosition, "a freestyle slot spends rush budget like a letter");
            Assert.AreEqual(1, typing.CharsAheadOfPlayhead(1000));
            Assert.AreEqual(2, typing.Combo);
        }

        #endregion

        #region Rush freedom (typing the next line before its cue)

        /// <summary>
        /// Finishing a line hands the caret straight to the next one, cue or no cue. The chars land
        /// and are judged against their OWN target times, so typing eight seconds before the vocals
        /// reads as a huge early delta (Premature): the mod frees the position, never the clock.
        /// </summary>
        [Test]
        public void FinishingALineOpensTheNextOneImmediately()
        {
            var typing = engine(lateSecondLineMap(), fletcher: true);

            var activations = new List<int>();
            typing.LineActivated += i => activations.Add(i);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            Assert.IsTrue(typing.ProcessKey('b', 1500));

            // Line 1's default activation is 10000 - CUE_LEAD_MS = 8500, but the caret is on it at
            // once, while line 0 is still the song's line (unsealed until its 3000 boundary).
            Assert.AreEqual(8500, typing.Lines[1].ActivationTime);
            Assert.AreEqual(new[] { 0, 1 }, activations);
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(0, typing.NextUnsealedLineIndex, "the song is still on line 0");
            Assert.AreEqual(0, typing.CaretIndex);

            // 'c' at 1600 against a 10000 target: accepted, judged Premature on a -8400 delta, 0
            // points. Since backlog 199 the CLOCK no longer breaks the run for that (the press is a
            // hit worth no points, see TypingEngine.OffTime), so the combo carries on at 3. The rush
            // cap is not what fires here either: the caret is level with the playhead (both at 2
            // countable chars) and the press puts it 1 ahead, well inside the cap of 5.
            Assert.AreEqual(0, typing.CharsAheadOfPlayhead(1600));
            Assert.IsTrue(typing.ProcessKey('c', 1600));
            Assert.AreEqual(CellState.Correct, typing.Lines[1].Cells[0].State);
            Assert.AreEqual(-8400, typing.Lines[1].Cells[0].JudgedDelta);
            Assert.AreEqual(3, typing.Combo);
            Assert.AreEqual(1, typing.BuildResults().Counts[JudgementType.Premature]);

            // Line 0 seals on its own normal deadline, with nothing missed: rushing moved the player,
            // not the song.
            var seals = new List<LineSealResult>();
            typing.LineSealed += s => seals.Add(s);
            typing.Update(3000);
            Assert.AreEqual(new[] { new LineSealResult(0, 0, false) }, seals);
            Assert.AreEqual(1, typing.ActiveLineIndex, "the seal must not disturb the player's caret");
            Assert.AreEqual(1, typing.CaretIndex);
        }

        /// <summary>Without the mod the same press in the same dead zone is inert, which is the
        /// behaviour Fletcher is lifting.</summary>
        [Test]
        public void WithoutFletcherTheDeadZoneStaysInert()
        {
            var typing = engine(lateSecondLineMap(), fletcher: false);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            Assert.IsTrue(typing.ProcessKey('b', 1500));
            Assert.IsTrue(typing.IsLineComplete);

            typing.Update(3000); // line 0 seals at its boundary; line 1's cue is still 5.5 s away
            Assert.AreEqual(-1, typing.ActiveLineIndex);
            Assert.IsFalse(typing.ProcessKey('c', 3000));
        }

        /// <summary>
        /// The WPM clock must not run while the caret is parked on a line the song has not reached:
        /// otherwise a 7-second instrumental would count as typing time and halve the readout. It
        /// starts exactly where the line would have activated without the mod.
        /// </summary>
        [Test]
        public void ActiveTimeDoesNotRunWhileParkedAheadOfTheCue()
        {
            var typing = engine(lateSecondLineMap(), fletcher: true);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            Assert.IsTrue(typing.ProcessKey('b', 1500)); // line 0 done, caret rolls on to line 1

            // Active time so far: 500 ms (1000 -> 1500). 2 correct cells => (2/5)/(500/60000) = 48 WPM.
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            typing.Update(4000);
            typing.Update(8000); // 6.5 s parked on line 1 with its vocals still ahead: no accrual
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            typing.Update(8500);  // line 1's cue: the clock is armed from here
            typing.Update(9000);  // +500 ms
            Assert.AreEqual(24.0, typing.LiveWpm, 1e-9); // (2/5)/(1000/60000)
        }

        #endregion

        #region Drag freedom (finishing a line the song has left)

        /// <summary>
        /// The song crossing a line boundary no longer snatches the caret: the line stays open for
        /// FLETCHER_DRAG_GRACE_MS past its hard deadline so a lagging player can finish it. The late
        /// char is judged late (that is the honest penalty), and the line then seals with nothing
        /// missed.
        /// </summary>
        [Test]
        public void LaggingPlayerMayFinishTheLineTheSongHasLeft()
        {
            var typing = engine(dragMap(), fletcher: true);

            var seals = new List<LineSealResult>();
            typing.LineSealed += s => seals.Add(s);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000)); // Great, combo 1

            // The boundary passes. Without the mod line 0 seals here, 'b' becomes a miss and the caret
            // jumps to line 1; with it, the caret is left exactly where it was.
            typing.Update(3000);
            Assert.IsEmpty(seals);
            Assert.AreEqual(0, typing.ActiveLineIndex);
            Assert.AreEqual(0, typing.NextUnsealedLineIndex);
            Assert.AreEqual(1, typing.CaretIndex);

            // 'b' typed 1700 ms after its target: accepted, judged Meh (Line MehLate is 2000), and it
            // still scores, 50 * (1 + 1/50) = 51. Dragging costs judgement quality, not the char.
            Assert.IsTrue(typing.ProcessKey('b', 3200));
            Assert.AreEqual(1700, typing.Lines[0].Cells[1].JudgedDelta);
            Assert.AreEqual(2, typing.Combo);

            // The line is finished, so the caret rolls on to line 1 and line 0 seals with 0 misses on
            // the very next update.
            Assert.AreEqual(1, typing.ActiveLineIndex);
            typing.Update(3300);
            Assert.AreEqual(new[] { new LineSealResult(0, 0, false) }, seals);
            Assert.AreEqual(0, typing.BuildResults().Counts[JudgementType.Miss]);
        }

        /// <summary>The contrast: the same play without the mod loses 'b' at the boundary.</summary>
        [Test]
        public void WithoutFletcherTheBoundarySnatchesTheLine()
        {
            var typing = engine(dragMap(), fletcher: false);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(3000);

            Assert.AreEqual(1, typing.ActiveLineIndex, "the caret is snapped to the song's new line");
            Assert.AreEqual(CellState.Missed, typing.Lines[0].Cells[1].State);
        }

        /// <summary>
        /// Drag freedom is bounded, or a run would never end. Past the cutoff the line force-seals
        /// exactly as it always did (untyped cells missed, one combo break) and the caret lands on the
        /// next line, which gets its OWN drag grace: one cutoff must not cascade into the line the
        /// player was just handed.
        /// </summary>
        [Test]
        public void DragCutoffForceSealsAndHandsOverWithoutCascading()
        {
            var typing = engine(dragMap(), fletcher: true);

            var seals = new List<LineSealResult>();
            var activations = new List<int>();
            int comboBreaks = 0;
            typing.LineSealed += s => seals.Add(s);
            typing.LineActivated += i => activations.Add(i);
            typing.ComboBroken += () => comboBreaks++;

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));

            // Line 0's hard deadline is 3000 and its cutoff is 3000 + 1500 = 4500. One frame short of
            // it the line is still the player's.
            typing.Update(4499);
            Assert.AreEqual(0, typing.ActiveLineIndex);
            Assert.IsEmpty(seals);

            typing.Update(4500);
            Assert.AreEqual(new[] { new LineSealResult(0, 1, true) }, seals);
            Assert.AreEqual(1, comboBreaks);
            Assert.AreEqual(CellState.Missed, typing.Lines[0].Cells[1].State);
            Assert.AreEqual(new[] { 0, 1 }, activations, "the cutoff hands the caret straight to line 1");
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(0, typing.CaretIndex);

            // Line 1 is already past its own hard deadline (4000), yet it is NOT swept up in the same
            // cutoff: it gets its own 1500 ms of drag grace, so the player has something to type.
            Assert.AreEqual(1, typing.NextUnsealedLineIndex);
            Assert.IsTrue(typing.ProcessKey('c', 4600));
            Assert.AreEqual(CellState.Correct, typing.Lines[1].Cells[0].State);

            // Its cutoff is 5000 + 1500 = 6500; there it force-seals too and the run finishes.
            typing.Update(6500);
            Assert.AreEqual(2, seals.Count);
            Assert.AreEqual(new LineSealResult(1, 1, true), seals[1]);
            Assert.IsTrue(typing.IsFinished);
        }

        /// <summary>
        /// An idle player is still caught up to the song: when the drag grace has expired on the line
        /// they were handed as well, the seal loop keeps going and lands them at the song's position,
        /// announcing the new line exactly once however many stale lines it burned through.
        /// </summary>
        [Test]
        public void IdlePlayerIsCaughtUpToTheSongInOneStep()
        {
            var typing = engine(map(TimingGranularity.Line,
                line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
                line("cd", 3000, 5000, 4000, unit("cd", 3000, 4000)),
                line("ef", 5000, 7000, 6000, unit("ef", 5000, 6000)),
                line("gh", 7000, 20000, 8000, unit("gh", 7000, 8000))), fletcher: true);

            var seals = new List<LineSealResult>();
            var activations = new List<int>();
            typing.LineSealed += s => seals.Add(s);
            typing.LineActivated += i => activations.Add(i);

            typing.Update(1000);
            Assert.AreEqual(0, typing.ActiveLineIndex);

            // Nothing typed for eight seconds. Lines 0, 1 and 2 are all past deadline + drag grace, so
            // they all seal in this one update, and line 3 (cutoff 20000 + 1500) is where the player
            // lands: one activation event, not three.
            typing.Update(9000);
            Assert.AreEqual(new[] { 0, 1, 2 }, seals.Select(s => s.LineIndex).ToArray());
            Assert.AreEqual(new[] { 0, 3 }, activations);
            Assert.AreEqual(3, typing.ActiveLineIndex);
            Assert.AreEqual(6, typing.BuildResults().Counts[JudgementType.Miss]);
        }

        #endregion

        #region Replay determinism

        /// <summary>
        /// Fletcher is a RANKED mod, so a stored replay has to reproduce the run bit-exactly. The mod
        /// travels on the score (the drawable ruleset applies the flag before the first frame, exactly
        /// as Literate does), and every judgement is a pure function of (char, time) plus that flag:
        /// feeding the recorded frames into a fresh engine the way the replay feeder does reproduces
        /// every cell, the score and the combo. Losing the flag on the way in changes the result, which
        /// is why it is applied from the score's mod list rather than local config.
        /// </summary>
        [Test]
        public void FletcherRunRoundTripsThroughRecordedFrames()
        {
            // (char, time) exactly as the key handler would stamp them: integral ms, monotonic.
            (char c, double t)[] script =
            {
                ('a', 1000), ('b', 1500), // line 0 finished on time, caret rolls on to line 1
                ('c', 1600), ('d', 1650), // rushed: line 1 typed 8 seconds before its vocals
            };

            var live = engine(lateSecondLineMap(), fletcher: true);
            live.Update(1000);

            foreach ((char c, double t) in script)
            {
                live.Update(t);
                Assert.IsTrue(live.ProcessKey(c, t));
            }

            live.Update(12000);
            Assert.IsTrue(live.IsFinished);

            var replayed = engine(lateSecondLineMap(), fletcher: true);

            foreach ((char c, double t) in script)
            {
                replayed.Update(t);
                replayed.ProcessKey(c, t);
            }

            replayed.Update(12000);

            Assert.AreEqual(live.Score, replayed.Score);
            Assert.AreEqual(live.MaxCombo, replayed.MaxCombo);
            Assert.AreEqual(live.Combo, replayed.Combo);
            Assert.AreEqual(live.LiveAccuracy, replayed.LiveAccuracy);
            Assert.AreEqual(live.BuildResults().SyncPercent, replayed.BuildResults().SyncPercent, 1e-12);

            foreach (JudgementType type in Enum.GetValues<JudgementType>())
                Assert.AreEqual(live.BuildResults().Counts[type], replayed.BuildResults().Counts[type], $"judgement count for {type}");

            for (int k = 0; k < live.Lines.Count; k++)
            {
                for (int i = 0; i < live.Lines[k].Cells.Count; i++)
                {
                    Assert.AreEqual(live.Lines[k].Cells[i].State, replayed.Lines[k].Cells[i].State);
                    Assert.AreEqual(live.Lines[k].Cells[i].JudgedDelta, replayed.Lines[k].Cells[i].JudgedDelta);
                }
            }

            // Same frames without the mod: the rushed presses never reach the engine at all, so the
            // flag is load-bearing for playback and must come from the score's mods.
            var unmodded = engine(lateSecondLineMap(), fletcher: false);

            foreach ((char c, double t) in script)
            {
                unmodded.Update(t);
                unmodded.ProcessKey(c, t);
            }

            unmodded.Update(12000);
            Assert.AreNotEqual(live.BuildResults().Counts[JudgementType.Miss], unmodded.BuildResults().Counts[JudgementType.Miss]);
        }

        #endregion

        #region Input-gate seams the playfield reads

        /// <summary>
        /// Under Fletcher a line is active straight through an instrumental gap (the caret is parked at
        /// the next line's head), so <see cref="TypingEngine.LineIsActive"/> alone can no longer tell
        /// the key handler when to let Space through to the skip overlay. These are the two extra
        /// predicates it reads: is the SONG inside a line window, and has the player started the line.
        /// </summary>
        [Test]
        public void SongWindowAndUntouchedFlagsTrackTheDeadZone()
        {
            var typing = engine(lateSecondLineMap(), fletcher: true);

            typing.Update(0);
            Assert.IsFalse(typing.SongWindowOpen, "pre-roll: the song is in no line window");
            Assert.IsFalse(typing.LineIsActive);

            typing.Update(1000);
            Assert.IsTrue(typing.SongWindowOpen);
            Assert.IsTrue(typing.ActiveLineUntouched);

            Assert.IsTrue(typing.ProcessKey('a', 1000));
            Assert.IsFalse(typing.ActiveLineUntouched, "one char in, the line is the player's");

            Assert.IsTrue(typing.ProcessKey('b', 1500));
            typing.Update(4000); // line 0 sealed; line 1's cue is 8500, so this is a real dead zone

            Assert.IsTrue(typing.LineIsActive, "Fletcher parks the caret on line 1 immediately");
            Assert.IsFalse(typing.SongWindowOpen, "but the song is between lines: Space must reach the skip");
            Assert.IsTrue(typing.ActiveLineUntouched);

            Assert.IsTrue(typing.ProcessKey('c', 4000));
            Assert.IsFalse(typing.ActiveLineUntouched, "rushing into the line takes Space back for typing");

            typing.Update(8500);
            Assert.IsTrue(typing.SongWindowOpen, "line 1's cue has arrived");
        }

        #endregion

        #region Countable stream bookkeeping

        /// <summary>
        /// The rush cap's two coordinates. The playhead position counts countable targets at or before
        /// the time (so it is monotonic and depends only on the beatmap), and the caret position spans
        /// lines, so the distance stays meaningful across a boundary the player has crossed early.
        /// </summary>
        [Test]
        public void CountableStreamSpansTheWholeMap()
        {
            var typing = engine(twoLineMap(), fletcher: true);

            // Countable cells (no spaces): a 1000, b 1500, c 2000, d 2500, e 4000, f 4500.
            Assert.AreEqual(0, typing.PlayheadCountablePosition(999));
            Assert.AreEqual(1, typing.PlayheadCountablePosition(1000)); // inclusive of the target itself
            Assert.AreEqual(4, typing.PlayheadCountablePosition(3999));
            Assert.AreEqual(6, typing.PlayheadCountablePosition(99999));

            typing.Update(1000);
            Assert.AreEqual(0, typing.CaretCountablePosition);

            foreach ((char c, double t) in new[] { ('a', 1000d), ('b', 1500d), (' ', 2000d), ('c', 2000d), ('d', 2500d) })
                Assert.IsTrue(typing.ProcessKey(c, t));

            // Line 0 finished: the caret rolled on to line 1 and its stream position carries over.
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(4, typing.CaretCountablePosition);
            Assert.AreEqual(0, typing.CharsAheadOfPlayhead(2500));
        }

        #endregion
    }
}
