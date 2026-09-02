// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// The UNPINNED caret's gameplay-core tests. Shipped as the Fletcher mod by backlog 25 and made the
// DEFAULT for every play by backlog 208, which reversed the mod: rush freedom (finish a line and
// you are typing the next one at once), drag freedom (the song moving on does not snatch the line
// you are still finishing), a character-distance rush cap replacing the timing lock, and (new with
// 208) the LINE-START SNAP, which hands a caret that is already past the end of its line to the
// next line the moment that line starts.
//
// So the sense of every arm below is reversed from the file it grew out of: `flexible: true` is now
// the shipped path and `flexible: false` is the mod named Fletcher, which pins the caret back.
// Every expected value is hand-computed beside its assert, in the style of TypingEngineTest, and the
// first fixture pins the two paths against each other: a run that stays in sync scores exactly the
// same either way.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Replays;
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
        /// The PARKED-FINISHED fixture: a caret sitting past the end of its line with NO keypress of
        /// the player's able to move it, and the next line starting under it.
        ///
        /// <para>Two pieces make that state. L1's authored text is pure punctuation, which the
        /// default (non-Literate) stream strips entirely, so the line has NO cells at all: the caret
        /// is complete the instant it lands on it and no press can ever finish it, which is exactly
        /// what the keypress roll-forward cannot cover. And L1's WINDOW OUTLIVES L2's cue (L1 runs to
        /// 20000, L2's first vocal is at 12000 so it activates at 10500), which is what stops the
        /// SEAL from getting there first: on a strictly ordered map the next line cannot start before
        /// the current one's EndTime, so the seal loop's own handover already carries a finished
        /// caret across every boundary. The snap is what makes the rule hold for the engine rather
        /// than for a map shape.</para>
        ///
        /// <para>L0 "ab" [1000, 3000): a = 1000, b = 1500. L2 "cd" [10000, 30000): c = 12000,
        /// d = 12500, so ActivationTime = 12000 - CUE_LEAD_MS = 10500.</para>
        ///
        /// <para>L1 having no cells, its ActivationTime is its StartTime of 3000, so the rush bound
        /// (backlog 218) opens entry into it at 1500, exactly the instant
        /// <see cref="typeIntoTheParkedState"/> finishes L0. That is deliberate: the parked caret
        /// this fixture exists for is the one on L1, and the bound must not be what puts it
        /// there.</para>
        /// </summary>
        private static LyricBeatmap parkedLineMap() => map(TimingGranularity.Line,
            line("ab", 1000, 3000, 2000, unit("ab", 1000, 2000)),
            line("...", 3000, 20000, 19000, unit("...", 3000, 19000)),
            line("cd", 10000, 30000, 13000, unit("cd", 12000, 13000)));

        /// <summary>
        /// Type L0 out, which rolls the caret straight on to the empty L1 and leaves it parked past
        /// that line's (non-existent) last character, with nothing sealed and L2 not yet started.
        /// </summary>
        private static void typeIntoTheParkedState(TypingEngine typing)
        {
            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            Assert.IsTrue(typing.ProcessKey('b', 1500));
        }

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

        /// <summary>
        /// The INSTRUMENTAL-GAP shape, which is what a decoder actually builds and therefore the
        /// shape the rush bound is measured on: line windows are CONTIGUOUS, so the twelve-second
        /// instrumental lives inside L0's own window rather than in a hole between the lines.
        ///
        /// <para>L0 "ab" [1000, 14000), SingEnd 2000: a = 1000, b = 1500, and its window runs to L1's
        /// start. L1 "cdefghij" [14000, 20000), SingEnd 15000: eight chars over [14000, 15000], step
        /// 125, so c = 14000 and j = 14875. ActivationTime is clamped to the line's own StartTime
        /// (max(14000, 14000 - CUE_LEAD_MS) = 14000), so the rush bound opens at
        /// 14000 - FLETCHER_DRAG_GRACE_MS = 12500, a second and a half before L0 could seal.</para>
        /// </summary>
        private static LyricBeatmap instrumentalGapMap() => map(TimingGranularity.Line,
            line("ab", 1000, 14000, 2000, unit("ab", 1000, 2000)),
            line("cdefghij", 14000, 20000, 15000, unit("cdefghij", 14000, 15000)));

        /// <summary>
        /// <paramref name="flexible"/> true is the shipped stack since backlog 208 and 218 (unpinned
        /// caret, the line-start snap and the bounded rush, exactly what
        /// <c>DrawableTypeBeatRuleset.createEngine</c> builds); false is the engine default, which is
        /// both the classic pinned game and what the Fletcher MOD asks for today.
        /// </summary>
        private static TypingEngine engine(LyricBeatmap beatmap, bool flexible)
            => new TypingEngine(beatmap) { FletcherEnabled = flexible, FlexibleLineSnap = flexible, BoundedRush = flexible };

        /// <summary>
        /// The stack as backlog 208 shipped it and every replay stored between 208 and 218 was played
        /// on: unpinned and snapped, but with the rush UNBOUNDED, so finishing a line handed the caret
        /// to the next one however many seconds before its cue.
        /// </summary>
        private static TypingEngine unboundedEraEngine(LyricBeatmap beatmap)
            => new TypingEngine(beatmap) { FletcherEnabled = true, FlexibleLineSnap = true };

        #endregion

        #region Which caret a fresh engine has

        /// <summary>
        /// The flip, at its narrowest (backlog 208). A bare engine is PINNED, because that is the
        /// era every replay written before 208 was played under and the engine default is what a
        /// replay with no CONFIG frame re-derives on. The LIVE stack is the other way round, and
        /// <c>DrawableTypeBeatRuleset.createEngine</c> is where that is decided: unpinned plus the
        /// snap, unless the Fletcher mod is on the list.
        /// </summary>
        [Test]
        public void ABareEngineIsPinnedAndTheShippedStackIsNot()
        {
            var bare = new TypingEngine(twoLineMap());

            Assert.IsFalse(bare.FletcherEnabled, "the engine default is the classic pinned era, for stored replays");
            Assert.IsFalse(bare.FlexibleLineSnap);
            Assert.IsFalse(bare.BoundedRush, "and the unbounded roll, which is what every pre-218 run was played with");
            Assert.IsFalse(bare.FlexibleCaretFromMod);

            var shipped = engine(twoLineMap(), flexible: true);

            Assert.IsTrue(shipped.FletcherEnabled);
            Assert.IsTrue(shipped.FlexibleLineSnap);
            Assert.IsTrue(shipped.BoundedRush);
        }

        #endregion

        #region The line-start snap (backlog 208)

        /// <summary>
        /// THE SNAP. A caret sitting past the end of its line is handed to the next line the moment
        /// that line starts, so an unpinned player who has FINISHED is still carried along by the
        /// song exactly as the pinned game carried them.
        ///
        /// <para>The state it covers is the one the keypress roll-forward cannot: a line the caret
        /// arrived on ALREADY complete, so no press of the player's ever finishes it (see
        /// <see cref="parkedLineMap"/>). Announced exactly once, and only when the next line is due,
        /// not before.</para>
        ///
        /// <para>"Due" is <c>entryOpensAt</c>, which backlog 218 moved: the shipped stack takes the
        /// finished caret <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> BEFORE the line's own
        /// activation, because that is the head start the rush bound grants any finished caret and
        /// the snap is the arm that performs it (a second arm at the later instant could only
        /// re-announce a caret this one had already moved). Both instants are pinned below, the
        /// shipped one against the pre-218 era's.</para>
        /// </summary>
        [Test]
        public void AParkedFinishedCaretIsSnappedWhenTheNextLineIsDue()
        {
            var typing = engine(parkedLineMap(), flexible: true);

            Assert.IsEmpty(typing.Lines[1].Cells, "the fixture's middle line must have no cells at all");
            Assert.AreEqual(10500, typing.Lines[2].ActivationTime); // 12000 - CUE_LEAD_MS
            Assert.Less(typing.Lines[2].ActivationTime, typing.Lines[1].EndTime, "the next line has to start before a seal could hand the caret over");

            var activations = new List<int>();
            typing.LineActivated += i => activations.Add(i);

            typeIntoTheParkedState(typing);

            // Rush freedom put the caret on line 1, where it is complete on arrival and stuck.
            Assert.AreEqual(new[] { 0, 1 }, activations);
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.IsTrue(typing.IsLineComplete);

            // 10500 - 1500 = 9000. One frame short of it nothing has moved: the snap is the next
            // line coming due, not the caret being idle.
            typing.Update(8999);
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(new[] { 0, 1 }, activations);
            Assert.AreEqual(1, typing.NextUnsealedLineIndex, "line 1 has not sealed, so no seal could have moved the caret");

            typing.Update(9000);
            Assert.AreEqual(2, typing.ActiveLineIndex, "line 2 is within the rush bound now, so it takes the finished caret");
            Assert.AreEqual(0, typing.CaretIndex);
            Assert.AreEqual(new[] { 0, 1, 2 }, activations, "exactly one activation per line the caret lands on");

            // And the player types line 2 from its head, on time.
            Assert.IsTrue(typing.ProcessKey('c', 12000));
            Assert.AreEqual(CellState.Correct, typing.Lines[2].Cells[0].State);
            Assert.AreEqual(0, typing.Lines[2].Cells[0].JudgedDelta);

            // The pre-218 era is the same snap at the LATER instant, the line's own activation: a run
            // stored then was carried across at 10500 and not a millisecond before.
            var unbounded = unboundedEraEngine(parkedLineMap());

            typeIntoTheParkedState(unbounded);
            unbounded.Update(10499);
            Assert.AreEqual(1, unbounded.ActiveLineIndex, "without the bound the head start does not exist either");

            unbounded.Update(10500);
            Assert.AreEqual(2, unbounded.ActiveLineIndex);
        }

        /// <summary>
        /// The limit, and the reason the snap is gated on FINISHED rather than on time alone: a line
        /// the player is still typing is never taken from them. Dragging behind is the freedom the
        /// unpinned caret exists to grant, and the same predicate <c>sealPermitted</c> uses ("nothing
        /// left untyped means there is no drag to protect") draws the line here.
        /// </summary>
        [Test]
        public void AnUnfinishedLineIsNeverSnappedAwayFromThePlayer()
        {
            // L0's own window runs to 20000 and L1 activates at 10500, so the only thing that could
            // move this caret before 20000 is the snap.
            var typing = engine(map(TimingGranularity.Line,
                line("ab", 1000, 20000, 2000, unit("ab", 1000, 2000)),
                line("cd", 10000, 30000, 13000, unit("cd", 12000, 13000))), flexible: true);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            Assert.AreEqual(10500, typing.Lines[1].ActivationTime);

            typing.Update(19000); // long past line 1's cue
            Assert.AreEqual(0, typing.ActiveLineIndex, "'b' is still owed, so the line is still the player's");
            Assert.AreEqual(1, typing.CaretIndex);
            Assert.IsEmpty(typing.Lines[1].Cells.Where(c => c.State != CellState.Untyped));

            // Finish it late and the ordinary roll-forward takes over, as it always did.
            Assert.IsTrue(typing.ProcessKey('b', 19000));
            Assert.AreEqual(1, typing.ActiveLineIndex);
        }

        /// <summary>
        /// The snap is the ERA's, not the unpinned caret's. Three engines, one script: the shipped
        /// stack snaps to line 2, an OLD "FT" run (unpinned, bit 5 clear) stays parked exactly where
        /// its player was, and a PINNED run is walked along by the seal and the ordinary activation
        /// arm instead. The middle one is the whole reason the bit exists: re-deriving a stored FT
        /// run with the snap would move its caret onto a line its player never reached and land every
        /// later keystroke on the wrong cell.
        /// </summary>
        [Test]
        public void TheSnapIsGatedOnTheEraBitAndNotOnTheUnpinnedCaretAlone()
        {
            var shipped = engine(parkedLineMap(), flexible: true);
            var oldFletcher = new TypingEngine(parkedLineMap()) { FletcherEnabled = true, FlexibleLineSnap = false };
            var pinned = engine(parkedLineMap(), flexible: false);

            foreach (var typing in new[] { shipped, oldFletcher })
            {
                typeIntoTheParkedState(typing);
                typing.Update(10500);
            }

            Assert.AreEqual(2, shipped.ActiveLineIndex);
            Assert.AreEqual(1, oldFletcher.ActiveLineIndex, "an FT-era caret is left exactly where its player left it");
            Assert.AreEqual(1, oldFletcher.NextUnsealedLineIndex, "and line 1 is where the song is too, so no seal moved it either");

            // Pinned: the caret never left line 0, so line 0 seals at its 3000 boundary and the
            // ordinary time-driven activation hands the player line 1 (empty, and inert).
            typeIntoTheParkedState(pinned);
            pinned.Update(10500);

            Assert.AreEqual(1, pinned.ActiveLineIndex);
            Assert.AreEqual(1, pinned.NextUnsealedLineIndex, "line 0 sealed under the pinned caret");
        }

        #endregion

        #region Default path / in-sync equivalence

        /// <summary>
        /// The caret flags must be inert for a player who stays in sync. The same fixed script of
        /// (char, time) presses is fed to a PINNED engine (the Fletcher mod's arm, and the classic
        /// era every pre-208 replay re-derives under) and to an UNPINNED one (the shipped default),
        /// and every observable has to agree, down to per-cell deltas; the pinned numbers are
        /// additionally pinned by hand so this fixture also guards the classic path against drift.
        /// </summary>
        [Test]
        public void InSyncRunIsIdenticalPinnedOrUnpinned()
        {
            var off = engine(twoLineMap(), flexible: false);
            var on = engine(twoLineMap(), flexible: true);

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
        /// (60 fps quantised) is never force-missed and never combo-broken, on the SHIPPED stack. Rushing
        /// is measured against the playhead, and a rhythm-perfect caret is by construction never ahead
        /// of it, so the cap can never fire on honest play. This is the same drive loop as
        /// TypingEngineTest's unmodded pin.
        /// </summary>
        [Test]
        public void RealMapRhythmPerfectPlayIsUnpenalisedUnpinned()
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

            var typing = engine(beatmap, flexible: true);

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
            var typing = engine(denseMap(), flexible: true);

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

        /// <summary>The cap belongs to the unpinned caret alone: under the Fletcher mod, which pins it
        /// back, the identical burst keeps its combo, because nothing in the pinned engine measures
        /// character distance (it does not need to: the cue lock is what holds the player).</summary>
        [Test]
        public void RushCapDoesNotExistUnderTheFletcherMod()
        {
            var typing = engine(denseMap(), flexible: false);

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
                line("abc de", 1000, 9000, 1500, unit("abc", 1000, 1300), unit("de", 1300, 1500))), flexible: true);

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
        public void FreestyleCellCountsLikeAnyOtherCharWhenUnpinned()
        {
            // "a&c": '&' is a freestyle slot; unit [1000, 1300], k = 3 => a = 1000, & = 1100, c = 1200.
            var typing = engine(map(TimingGranularity.Line,
                line("a" + Typeability.FREESTYLE_MARKER + "c", 1000, 9000, 1300,
                    unit("a" + Typeability.FREESTYLE_MARKER + "c", 1000, 1300))), flexible: true);

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
        /// THE PRE-218 ERA, in which finishing a line handed the caret straight to the next one, cue
        /// or no cue. The chars land and are judged against their OWN target times, so typing eight
        /// seconds before the vocals reads as a huge early delta (Premature): the era freed the
        /// position, never the clock.
        ///
        /// <para>Re-pointed rather than deleted by backlog 218, which BOUNDS that roll (see
        /// <see cref="TheRushBoundParksAFinishedCaretUntilTheNextLineIsNearlyDue"/>): this is exactly
        /// what a replay carrying flags bit 7 clear must still re-derive, keystroke for keystroke, so
        /// the fixture stays and the engine under it becomes the stored era's.</para>
        /// </summary>
        [Test]
        public void UnboundedEraFinishingALineOpensTheNextOneImmediately()
        {
            var typing = unboundedEraEngine(lateSecondLineMap());

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

        /// <summary>
        /// THE RUSH BOUND (backlog 218). Finishing a line no longer opens the next one at any
        /// distance: entry into it opens
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> before its activation, the exact mirror
        /// of the drag grace at the other end of a line. Refused, the caret PARKS past the last cell
        /// of the line it finished, keypresses there are INERT (no cell, no judgement, no typo and no
        /// combo break, the same nothing the pinned game's dead zone answers with), and the
        /// time-driven arm performs the deferred roll the moment the bound opens, announcing it
        /// exactly once.
        /// </summary>
        [Test]
        public void TheRushBoundParksAFinishedCaretUntilTheNextLineIsNearlyDue()
        {
            var typing = engine(instrumentalGapMap(), flexible: true);

            // 14000 (clamped to line 1's own start), so the bound opens at 12500 and line 0 could not
            // seal before 14000 either way: nothing but the bound decides this caret's position.
            Assert.AreEqual(14000, typing.Lines[1].ActivationTime);
            Assert.AreEqual(14000, typing.Lines[0].EndTime);

            var activations = new List<int>();
            typing.LineActivated += i => activations.Add(i);

            int comboBreaks = 0;
            typing.ComboBroken += () => comboBreaks++;

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            Assert.IsTrue(typing.ProcessKey('b', 1500));

            // Line 0 is finished twelve seconds early, and the caret stays on it.
            Assert.AreEqual(0, typing.ActiveLineIndex);
            Assert.IsTrue(typing.IsLineComplete);
            Assert.AreEqual(2, typing.CaretIndex);
            Assert.AreEqual(new[] { 0 }, activations, "the roll was refused, so no line was announced");

            // A press in the park is inert: refused outright, and it costs nothing at all.
            typing.Update(6000);
            Assert.IsFalse(typing.ProcessKey('c', 6000), "a parked-finished caret takes no input");
            Assert.AreEqual(CellState.Untyped, typing.Lines[1].Cells[0].State);
            Assert.AreEqual(2, typing.Combo, "an inert press is not a typo and breaks nothing");
            Assert.AreEqual(0, comboBreaks);
            Assert.AreEqual(0, typing.Mistypes);
            Assert.AreEqual(1.0, typing.LiveAccuracy, "and it never enters the accuracy denominator");

            // One frame short of the bound the caret has still not moved.
            typing.Update(12499);
            Assert.AreEqual(0, typing.ActiveLineIndex);
            Assert.AreEqual(0, typing.NextUnsealedLineIndex, "and no seal has moved it either");

            // 14000 - FLETCHER_DRAG_GRACE_MS: the deferred roll fires, once.
            typing.Update(12500);
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(0, typing.CaretIndex);
            Assert.AreEqual(new[] { 0, 1 }, activations, "exactly one activation for the line the caret lands on");
            Assert.AreEqual(0, typing.NextUnsealedLineIndex, "the song is still on line 0: only the player moved");

            // And the head start is real typing time: the player is on line 1 a second and a half
            // before its cue, judged early (target 14000) exactly as rushing always was.
            Assert.IsTrue(typing.ProcessKey('c', 12500));
            Assert.AreEqual(CellState.Correct, typing.Lines[1].Cells[0].State);
            Assert.AreEqual(-1500, typing.Lines[1].Cells[0].JudgedDelta);
        }

        /// <summary>
        /// THE SYMMETRY, asserted against the one constant. Two engines on one fixture, one script
        /// each, because a player cannot both finish early and drag: the last instant the LAGGING one
        /// still owns line 0 and the first instant the RUSHING one may be on line 1 are each exactly
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> from their line's natural edge.
        /// </summary>
        [Test]
        public void TheRushBoundIsTheDragGraceMirrored()
        {
            // Natural edges: line 0 ends (EndTime + SealGraceMs) at 3000, line 1 starts
            // (ActivationTime) at 3000 as well, this map being back to back.
            var edges = engine(dragMap(), flexible: true);

            Assert.AreEqual(3000, edges.Lines[0].EndTime + edges.Lines[0].SealGraceMs);
            Assert.AreEqual(3000, edges.Lines[1].ActivationTime);

            const double drag_cutoff = 3000 + TypingEngine.FLETCHER_DRAG_GRACE_MS; // 4500
            const double rush_entry = 3000 - TypingEngine.FLETCHER_DRAG_GRACE_MS;  // 1500

            // RUSH: both chars typed at 1000, so line 0 is finished 500 ms before entry into line 1
            // opens. The caret parks, then moves at 1500 exactly.
            var rushing = engine(dragMap(), flexible: true);

            rushing.Update(1000);
            Assert.IsTrue(rushing.ProcessKey('a', 1000));
            Assert.IsTrue(rushing.ProcessKey('b', 1000));
            Assert.IsTrue(rushing.IsLineComplete);
            Assert.AreEqual(0, rushing.ActiveLineIndex, "1000 is before the bound: line 1 is not the player's yet");

            rushing.Update(rush_entry - 1);
            Assert.AreEqual(0, rushing.ActiveLineIndex);

            rushing.Update(rush_entry);
            Assert.AreEqual(1, rushing.ActiveLineIndex, "the earliest instant rush may hold line 1");

            // DRAG: the mirror. Line 0 is still the player's one frame short of the cutoff, and the
            // char they finally type still lands on it.
            var dragging = engine(dragMap(), flexible: true);

            dragging.Update(1000);
            Assert.IsTrue(dragging.ProcessKey('a', 1000));

            dragging.Update(drag_cutoff - 1);
            Assert.AreEqual(0, dragging.ActiveLineIndex, "the latest instant drag may hold line 0");
            Assert.IsTrue(dragging.ProcessKey('b', drag_cutoff - 1));
            Assert.AreEqual(CellState.Correct, dragging.Lines[0].Cells[1].State);

            // The two distances are the SAME constant, which is the whole of backlog 218.
            Assert.AreEqual(TypingEngine.FLETCHER_DRAG_GRACE_MS, drag_cutoff - (edges.Lines[0].EndTime + edges.Lines[0].SealGraceMs));
            Assert.AreEqual(TypingEngine.FLETCHER_DRAG_GRACE_MS, edges.Lines[1].ActivationTime - rush_entry);
        }

        /// <summary>
        /// The near case, which the bound must leave exactly as it was: a player who finishes line 0
        /// while line 1 is already within the bound rolls on to it ON THE KEYPRESS, with no waiting
        /// and no parked state at all. Identical under both eras, so nothing about ordinary
        /// back-to-back play moved.
        /// </summary>
        [Test]
        public void FinishingALineInsideTheBoundStillRollsOnAtOnce()
        {
            foreach (var typing in new[] { engine(dragMap(), flexible: true), unboundedEraEngine(dragMap()) })
            {
                var activations = new List<int>();
                typing.LineActivated += i => activations.Add(i);

                // Line 1's activation is 3000, so entry opened at 1500 and this press is inside it.
                typing.Update(1000);
                Assert.IsTrue(typing.ProcessKey('a', 1000));
                typing.Update(1500);
                Assert.IsTrue(typing.ProcessKey('b', 1500));

                Assert.AreEqual(1, typing.ActiveLineIndex, "the roll happens on the press, not on a later frame");
                Assert.AreEqual(0, typing.CaretIndex);
                Assert.AreEqual(new[] { 0, 1 }, activations);

                // And typing on into line 1 works from that same press onwards.
                Assert.IsTrue(typing.ProcessKey('c', 1500));
                Assert.AreEqual(CellState.Correct, typing.Lines[1].Cells[0].State);
            }
        }

        /// <summary>
        /// The rush CAP is untouched by the bound and still bites on the far side of a permitted
        /// roll: entry buys the player a line, never a licence to run away down it.
        /// </summary>
        [Test]
        public void TheRushCapStillAppliesAfterAPermittedRoll()
        {
            var typing = engine(instrumentalGapMap(), flexible: true);

            int comboBreaks = 0;
            typing.ComboBroken += () => comboBreaks++;

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            Assert.IsTrue(typing.ProcessKey('b', 1500));

            typing.Update(12500); // the bound opens and the deferred roll lands the caret on line 1
            Assert.AreEqual(1, typing.ActiveLineIndex);

            // The playhead has reached two countable chars ('a', 'b'); so has the caret.
            Assert.AreEqual(2, typing.PlayheadCountablePosition(12500));
            Assert.AreEqual(0, typing.CharsAheadOfPlayhead(12500));

            // Five chars of line 1 keep the caret inside the cap, so combo climbs from 2 to 7.
            foreach (char c in "cdefg")
                Assert.IsTrue(typing.ProcessKey(c, 12500));

            Assert.AreEqual(5, typing.CharsAheadOfPlayhead(12500));
            Assert.AreEqual(7, typing.Combo);
            Assert.AreEqual(0, comboBreaks);

            // The sixth is over it, and costs the combo exactly as it does inside one line.
            Assert.IsTrue(typing.ProcessKey('h', 12500));
            Assert.AreEqual(6, typing.CharsAheadOfPlayhead(12500));
            Assert.AreEqual(0, typing.Combo);
            Assert.AreEqual(1, comboBreaks);
        }

        /// <summary>
        /// The bound is asked only of a caret moving ITSELF. The SEAL's hand-over is the song moving
        /// on instead, so it is never refused, even on a map shaped so that it arrives first: a hole
        /// at the HEAD of line 1's window (its vocals are seven seconds into it) puts the bound at
        /// 7000 while line 0 seals at 3000. Entry there is late, not early, and refusing it would
        /// park the player in a dead zone the unpinned caret does not otherwise have.
        ///
        /// <para>A decoder-built map cannot take this shape at the boundary that matters: a line's
        /// ActivationTime is clamped to its own StartTime, which IS the previous line's EndTime, so
        /// the bound opens at worst FLETCHER_DRAG_GRACE_MS before the previous line could seal at
        /// all (see <see cref="instrumentalGapMap"/>, where it does).</para>
        /// </summary>
        [Test]
        public void TheSealsHandOverIsNeverRefusedByTheBound()
        {
            Assert.AreEqual(8500, engine(lateSecondLineMap(), flexible: true).Lines[1].ActivationTime);

            // The ORDINARY seal: line 0 is fully typed, so it seals on its own 3000 deadline and
            // hands the finished caret on, 4000 ms before the bound would have opened.
            var finished = engine(lateSecondLineMap(), flexible: true);

            finished.Update(1000);
            Assert.IsTrue(finished.ProcessKey('a', 1000));
            Assert.IsTrue(finished.ProcessKey('b', 1500));
            Assert.AreEqual(0, finished.ActiveLineIndex, "the bound parked the caret at the end of line 0");

            finished.Update(3000);
            Assert.AreEqual(1, finished.ActiveLineIndex, "the song left line 0, so the caret goes with it");
            Assert.AreEqual(0, finished.BuildResults().Counts[JudgementType.Miss]);

            // The DRAG CUTOFF: the same hand-over for a player who never finished, at
            // 3000 + FLETCHER_DRAG_GRACE_MS. Also inside the refusal window, and also not refused.
            var lagging = engine(lateSecondLineMap(), flexible: true);

            lagging.Update(1000);
            Assert.IsTrue(lagging.ProcessKey('a', 1000));

            lagging.Update(4500);
            Assert.AreEqual(1, lagging.ActiveLineIndex);
            Assert.AreEqual(CellState.Missed, lagging.Lines[0].Cells[1].State);
            Assert.IsTrue(lagging.ProcessKey('c', 4600), "and the line it handed over is typeable at once");
        }

        /// <summary>Under the Fletcher mod the same press in the same dead zone is inert, which is the
        /// behaviour the default lifts.</summary>
        [Test]
        public void UnderTheFletcherModTheDeadZoneStaysInert()
        {
            var typing = engine(lateSecondLineMap(), flexible: false);

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
            var typing = engine(lateSecondLineMap(), flexible: true);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            // Line 0 done. Entry into line 1 opens at 8500 - FLETCHER_DRAG_GRACE_MS = 7000, so the
            // roll is REFUSED here (backlog 218) and the caret parks past the last cell of line 0.
            // The SEAL is what hands it on, on the next frame below (line 0's EndTime is 3000), and
            // that hand-over is never refused: the caret is on line 1 from 4000, well before its cue.
            Assert.IsTrue(typing.ProcessKey('b', 1500));

            // Active time so far: 500 ms (1000 -> 1500). 2 correct cells => (2/5)/(500/60000) = 48 WPM.
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            typing.Update(4000);
            typing.Update(8000); // parked with line 1's vocals still ahead, and NOT typing: no accrual
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            typing.Update(8500);  // line 1's cue: the clock is armed from here
            typing.Update(9000);  // +500 ms
            Assert.AreEqual(24.0, typing.LiveWpm, 1e-9); // (2/5)/(1000/60000)
        }

        /// <summary>
        /// The same rule for the OTHER parked state, the one the rush bound creates (backlog 218): a
        /// caret held past the end of the line it finished cannot type at all, so the clock must not
        /// run through the eleven seconds it sits there. Nothing new implements this, the clock has
        /// always stopped for a COMPLETE line, and the point of the test is that the two parked
        /// states agree.
        /// </summary>
        [Test]
        public void ActiveTimeDoesNotRunWhileParkedPastTheEndOfALine()
        {
            var typing = engine(instrumentalGapMap(), flexible: true);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            Assert.IsTrue(typing.ProcessKey('b', 1500)); // line 0 done, and the bound parks the caret

            // Active time so far: 500 ms (1000 -> 1500). 2 correct cells => (2/5)/(500/60000) = 48 WPM.
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            typing.Update(6000);
            typing.Update(12000); // 10.5 s parked past the end of line 0: no accrual
            Assert.AreEqual(0, typing.ActiveLineIndex);
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            typing.Update(12500); // the deferred roll: on line 1, still ahead of its cue
            typing.Update(14000); // line 1's cue: the clock is armed from here, not before
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            typing.Update(14500); // +500 ms
            Assert.AreEqual(24.0, typing.LiveWpm, 1e-9); // (2/5)/(1000/60000)
        }

        /// <summary>
        /// THE HOLE THE TWO TESTS ABOVE LEAVE (backlog 222): they park and WAIT, and the state that
        /// was broken is parking and TYPING. A caret rolled on to the next line sits there from
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> before its cue and
        /// <c>ProcessKey</c> has no time gate, so the player really can type there; every character
        /// they land counts in the WPM numerator for the rest of the run. Counting them while the
        /// clock stayed stopped walked the HUD readout upward for free, once per line.
        ///
        /// <para>So the clock ARMS LAZILY on the first press made on such a line, and runs from that
        /// press's own time. Two things it deliberately is not: it does not back-date (the arming
        /// press is credited nothing, exactly like a press at the cue + 0), and it does not arm at
        /// <c>entryOpensAt</c>, which would hand the whole 1500 ms head start back as typing time to
        /// a player who never used it (see the idle companion below, and the two tests above).</para>
        /// </summary>
        [Test]
        public void ActiveTimeRunsFromTheFirstPressMadeAheadOfTheCue()
        {
            var typing = engine(twoLineMap(), flexible: true);

            // Line 1 activates at 4000, so entry into it opens at 4000 - 1500 = 2500, which is
            // exactly when 'd' finishes line 0: the roll happens on that press.
            Assert.AreEqual(4000, typing.Lines[1].ActivationTime);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            Assert.IsTrue(typing.ProcessKey('b', 1500));
            typing.Update(2000);
            Assert.IsTrue(typing.ProcessKey(' ', 2000));
            Assert.IsTrue(typing.ProcessKey('c', 2000));
            typing.Update(2500);
            Assert.IsTrue(typing.ProcessKey('d', 2500));

            // Line 0 was clocked in full: 500 + 500 + 500 = 1500 ms for 5 correct cells (the space
            // counts), so (5/5)/(1500/60000) = 40 WPM. The caret is now on line 1, 1500 ms early.
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(40.0, typing.LiveWpm, 1e-9);

            // The frame across the head start credits nothing yet: the player has not typed on line 1.
            typing.Update(2600);
            Assert.AreEqual(40.0, typing.LiveWpm, 1e-9);

            // THE ARMING PRESS. Judged Premature against its 4000 target (rushing frees the position,
            // never the clock) but landed Correct, so it enters the numerator at once. It credits
            // itself NO elapsed time, so this reads (6/5)/(1500/60000) = 48, up from 40 on the
            // strength of the character alone. That step is honest for one press; what follows is
            // the part that used to be free.
            Assert.IsTrue(typing.ProcessKey('e', 2600));
            Assert.AreEqual(CellState.Correct, typing.Lines[1].Cells[0].State);
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            // 100 ms of real typing time, and the clock now counts it: 1500 + 100 = 1600 ms.
            // 7 correct cells => (7/5)/(1600/60000) = 52.5. Before the lazy arm this frame credited
            // nothing and the readout climbed to (7/5)/(1500/60000) = 56 instead.
            // The arm is at the PRESS (2600), not at entry (2500): arming at entry would have made it
            // 1700 ms and 49.41 WPM, and would have paid the idle player below for waiting.
            typing.Update(2700);
            Assert.IsTrue(typing.ProcessKey('f', 2700));
            Assert.AreEqual(52.5, typing.LiveWpm, 1e-9);

            // The HUD's readout, which is the surface that actually broke. The ring holds all 7
            // presses; the oldest stamp is 0 ('a', typed on the activation frame) and the newest is
            // 1600 ('f'), so 6 inter-key gaps over 1600 ms of clocked time = (6/5)/(1600/60000) = 45.
            // With the clock frozen 'd', 'e' and 'f' all carried the same 1500 stamp: the span
            // collapsed to 1500 and the readout read 48.
            Assert.AreEqual(45.0, typing.LiveRollingWpm, 1e-9);
        }

        /// <summary>
        /// The non-vacuity companion to <see cref="ActiveTimeRunsFromTheFirstPressMadeAheadOfTheCue"/>,
        /// on the same fixture and the same script minus the presses: a player who is handed the head
        /// start and does NOT use it is credited nothing for it, which is the rule backlog 208 put
        /// the ActivationTime gate there for. The lazy arm buys the typing player their time without
        /// paying the idle one for waiting.
        /// </summary>
        [Test]
        public void ActiveTimeStillIgnoresAHeadStartThePlayerDoesNotType()
        {
            var typing = engine(twoLineMap(), flexible: true);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            Assert.IsTrue(typing.ProcessKey('b', 1500));
            typing.Update(2000);
            Assert.IsTrue(typing.ProcessKey(' ', 2000));
            Assert.IsTrue(typing.ProcessKey('c', 2000));
            typing.Update(2500);
            Assert.IsTrue(typing.ProcessKey('d', 2500));

            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(40.0, typing.LiveWpm, 1e-9); // 1500 ms, 5 cells

            // The whole 1500 ms head start, on line 1, with no press made on it.
            typing.Update(2600);
            typing.Update(3000);
            typing.Update(3500);
            typing.Update(4000); // line 1's cue (and line 0 seals here, leaving the caret alone)
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(40.0, typing.LiveWpm, 1e-9);

            // From the cue the clock runs on the ordinary rule, unarmed: 1500 + 500 = 2000 ms, so
            // the same 5 cells now read (5/5)/(2000/60000) = 30.
            typing.Update(4500);
            Assert.AreEqual(30.0, typing.LiveWpm, 1e-9);
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
            var typing = engine(dragMap(), flexible: true);

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

        /// <summary>The contrast: the same play under the Fletcher mod loses 'b' at the boundary. This
        /// is the snatch the mod exists to reinstate.</summary>
        [Test]
        public void UnderTheFletcherModTheBoundarySnatchesTheLine()
        {
            var typing = engine(dragMap(), flexible: false);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(3000);

            Assert.AreEqual(1, typing.ActiveLineIndex, "the pinned caret is snatched onto the song's new line");
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
            var typing = engine(dragMap(), flexible: true);

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
                line("gh", 7000, 20000, 8000, unit("gh", 7000, 8000))), flexible: true);

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

        #region The line skip (backlog 241)

        /// <summary>
        /// THE SKIP IS CARET MOVEMENT AND NOTHING ELSE. Enter parks the caret past the last cell of
        /// the line, which is the SAME state a bound-refused roll leaves behind: presses there are
        /// inert, the line reads complete, and the deferred roll performs the hand-over when the
        /// bound opens. Nothing about the line the player walked out of is judged at the press: its
        /// untyped cell is still Untyped, the combo is still whatever they had earned, and no
        /// judgement of any kind has been taken.
        /// </summary>
        [Test]
        public void EnterParksTheCaretPastTheLineItGivesUp()
        {
            var typing = engine(instrumentalGapMap(), flexible: true);

            var activations = new List<int>();
            typing.LineActivated += i => activations.Add(i);

            int comboBreaks = 0;
            typing.ComboBroken += () => comboBreaks++;

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            Assert.AreEqual(1, typing.CaretIndex);

            // Twelve seconds before line 1's entry opens (14000 - 1500 = 12500), so nothing but the
            // skip itself puts the caret where it ends up.
            typing.Update(2000);
            Assert.IsTrue(typing.ProcessEnter(2000));

            Assert.AreEqual(0, typing.ActiveLineIndex, "2000 is far short of the bound: line 1 is not the player's yet");
            Assert.AreEqual(2, typing.CaretIndex);
            Assert.IsTrue(typing.IsLineComplete, "the parked caret reads complete, exactly as a refused roll's does");
            Assert.AreEqual(new[] { 0 }, activations, "the roll was refused, so no line was announced");

            // NOTHING WAS JUDGED. The cell they gave up is untouched until its line's own deadline.
            Assert.AreEqual(CellState.Untyped, typing.Lines[0].Cells[1].State);
            Assert.AreEqual(0, typing.BuildResults().Counts[JudgementType.Miss]);
            Assert.AreEqual(1, typing.Combo);
            Assert.AreEqual(0, comboBreaks);
            Assert.AreEqual(0, typing.Mistypes);
            Assert.AreEqual(1.0, typing.LiveAccuracy);

            // A second Enter has nothing left to give up, and a keypress in the park is inert: the
            // parked state answers both exactly as the bound's own park does.
            Assert.IsFalse(typing.ProcessEnter(2000), "there is nothing left to skip");
            Assert.IsFalse(typing.ProcessKey('c', 2000), "a parked caret takes no input");
            Assert.AreEqual(CellState.Untyped, typing.Lines[1].Cells[0].State);
            Assert.AreEqual(1, typing.Combo);

            // And the existing time-driven arm carries them over at the bound, announcing once.
            typing.Update(12499);
            Assert.AreEqual(0, typing.ActiveLineIndex);

            typing.Update(12500);
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.AreEqual(0, typing.CaretIndex);
            Assert.AreEqual(new[] { 0, 1 }, activations);
        }

        /// <summary>
        /// The hand-over is the ordinary one, so the ENTRY BOUND decides it and nothing else: an Enter
        /// pressed inside the next line's entry window moves the player onto it ON THE PRESS, and one
        /// pressed a millisecond earlier parks them until the bound opens. The skip is the player
        /// moving THEMSELVES, which is the case <c>entryPermitted</c> exists for; the seal's own
        /// hand-overs, which the song causes, are still never refused.
        /// </summary>
        [Test]
        public void EnterHandsOverAtOnceInsideTheBoundAndDefersOutsideIt()
        {
            // Line 1 activates at 3000, so entry into it opens at 3000 - 1500 = 1500.
            const double rush_entry = 3000 - TypingEngine.FLETCHER_DRAG_GRACE_MS;

            var immediate = engine(dragMap(), flexible: true);
            var immediateActivations = new List<int>();
            immediate.LineActivated += i => immediateActivations.Add(i);

            immediate.Update(1000);
            Assert.IsTrue(immediate.ProcessKey('a', 1000));
            immediate.Update(rush_entry);
            Assert.IsTrue(immediate.ProcessEnter(rush_entry));

            Assert.AreEqual(1, immediate.ActiveLineIndex, "inside the bound the skip rolls on the press itself");
            Assert.AreEqual(0, immediate.CaretIndex);
            Assert.AreEqual(new[] { 0, 1 }, immediateActivations);
            Assert.IsTrue(immediate.ProcessKey('c', rush_entry), "and the next line takes input from that press onwards");

            var deferred = engine(dragMap(), flexible: true);
            var deferredActivations = new List<int>();
            deferred.LineActivated += i => deferredActivations.Add(i);

            deferred.Update(1000);
            Assert.IsTrue(deferred.ProcessKey('a', 1000));
            deferred.Update(rush_entry - 1);
            Assert.IsTrue(deferred.ProcessEnter(rush_entry - 1));

            Assert.AreEqual(0, deferred.ActiveLineIndex, "one millisecond short of the bound the caret parks instead");
            Assert.AreEqual(new[] { 0 }, deferredActivations);

            deferred.Update(rush_entry - 1);
            Assert.AreEqual(0, deferred.ActiveLineIndex);

            deferred.Update(rush_entry);
            Assert.AreEqual(1, deferred.ActiveLineIndex, "and the deferred roll makes the move on the frame the bound opens");
            Assert.AreEqual(0, deferred.CaretIndex);
            Assert.AreEqual(new[] { 0, 1 }, deferredActivations);
        }

        /// <summary>
        /// THE LOAD-BEARING PIN, and the reason the skip needs no era bit on the scoring axis: the
        /// line the player walked out of reaches its misses and its ONE combo break at exactly the
        /// instant, and with exactly the contents, it would have had if they had simply stopped
        /// typing and sat there. Two runs on one fixture and one tick schedule, differing only in the
        /// Enter press, compared event for event.
        ///
        /// <para>What holds it is <c>TypingEngine.lineAbandoned</c>: the drag grace defers this line's
        /// seal to 3000 + 1500 = 4500 while a caret is on it, and the skip moves the caret off it at
        /// 1500. Without the flag the seal would land at 3000, a second and a half early, which drains
        /// health early and re-prices at combo zero every keypress made on the next line in
        /// between.</para>
        /// </summary>
        [Test]
        public void AnAbandonedLineSealsExactlyAsItWouldHaveWithoutTheSkip()
        {
            var sat = engine(dragMap(), flexible: true);
            var skipped = engine(dragMap(), flexible: true);

            var satSeals = new List<(double at, LineSealResult result)>();
            var skippedSeals = new List<(double at, LineSealResult result)>();
            var satArrivals = new List<(double at, int line)>();
            var skippedArrivals = new List<(double at, int line)>();
            int satBreaks = 0, skippedBreaks = 0;

            double now = 1000;
            sat.LineSealed += s => satSeals.Add((now, s));
            skipped.LineSealed += s => skippedSeals.Add((now, s));
            sat.LineActivated += i => satArrivals.Add((now, i));
            skipped.LineActivated += i => skippedArrivals.Add((now, i));
            sat.ComboBroken += () => satBreaks++;
            skipped.ComboBroken += () => skippedBreaks++;

            // Both type 'a' and then give up on 'b'; only one of them says so.
            sat.Update(1000);
            Assert.IsTrue(sat.ProcessKey('a', 1000));
            skipped.Update(1000);
            Assert.IsTrue(skipped.ProcessKey('a', 1000));
            Assert.IsTrue(skipped.ProcessEnter(1000));

            for (now = 1000; now <= 5000; now += 100)
            {
                sat.Update(now);
                skipped.Update(now);
            }

            // Line 0's hard deadline is 3000 (no seal grace) and its drag cutoff 3000 + 1500 = 4500.
            // ONE missed cell ('b'), ONE break, at 4500, on both sides.
            Assert.AreEqual(4500, satSeals[0].at);
            Assert.AreEqual(new LineSealResult(0, 1, true), satSeals[0].result);

            Assert.AreEqual(satSeals[0].at, skippedSeals[0].at, "the skip must not move the seal");
            Assert.AreEqual(satSeals[0].result, skippedSeals[0].result);
            Assert.AreEqual(satBreaks, skippedBreaks);
            Assert.AreEqual(1, skippedBreaks);
            Assert.AreEqual(CellState.Missed, skipped.Lines[0].Cells[1].State);

            // The whole account agrees, which is what "no era bit" means in practice.
            Assert.AreEqual(sat.Score, skipped.Score);
            Assert.AreEqual(sat.MaxCombo, skipped.MaxCombo);
            Assert.AreEqual(sat.Combo, skipped.Combo);
            Assert.AreEqual(sat.BuildResults().Counts[JudgementType.Miss], skipped.BuildResults().Counts[JudgementType.Miss]);

            // The one thing that DID move is where the PLAYER was allowed to be, which is the whole
            // feature: the sitting run only reached line 1 when the drag cutoff handed it over at
            // 4500, the skipping one held it from 1500, the instant entry opened.
            Assert.AreEqual(new[] { (1000d, 0), (4500d, 1) }, satArrivals.ToArray());
            Assert.AreEqual(new[] { (1000d, 0), (1500d, 1) }, skippedArrivals.ToArray());

            // And the parity survives backlog 259 (see TheSealsBreakDestroysTheRunUpToItsMissesAndNoMore),
            // which is the load-bearing half of this pin now that the seal's break has a REACH as
            // well as an instant: the two runs type the same characters, so they must lose the same
            // combo to the same break whichever era decides how far back it reaches.
            var satBackDated = engine(dragMap(), flexible: true);
            var skippedBackDated = engine(dragMap(), flexible: true);

            satBackDated.BackDatedSealBreak = true;
            skippedBackDated.BackDatedSealBreak = true;

            var satBackDatedSeals = new List<(double at, LineSealResult result)>();
            var skippedBackDatedSeals = new List<(double at, LineSealResult result)>();

            satBackDated.LineSealed += s => satBackDatedSeals.Add((now, s));
            skippedBackDated.LineSealed += s => skippedBackDatedSeals.Add((now, s));

            satBackDated.Update(1000);
            Assert.IsTrue(satBackDated.ProcessKey('a', 1000));
            skippedBackDated.Update(1000);
            Assert.IsTrue(skippedBackDated.ProcessKey('a', 1000));
            Assert.IsTrue(skippedBackDated.ProcessEnter(1000));

            for (now = 1000; now <= 5000; now += 100)
            {
                satBackDated.Update(now);
                skippedBackDated.Update(now);
            }

            Assert.AreEqual(satBackDatedSeals.ToArray(), skippedBackDatedSeals.ToArray());
            Assert.AreEqual(satBackDated.Combo, skippedBackDated.Combo);
            Assert.AreEqual(satBackDated.MaxCombo, skippedBackDated.MaxCombo);

            // 'a' was earned on cell 0 and the miss is cell 1, so the back-dated break still takes
            // it: this fixture types nothing PAST the miss, which is exactly why the two eras agree
            // on it and why the pins below need a run built on the next line.
            Assert.AreEqual(new[] { (4500d, new LineSealResult(0, 1, true, 0)) }, satBackDatedSeals.ToArray());
            Assert.AreEqual(0, satBackDated.Combo);
        }

        /// <summary>
        /// BACKLOG 259: THE SEAL'S COMBO BREAK IS BACK-DATED to the cells it is about to miss, so a
        /// run built PAST them survives it. A line's misses only exist at its seal, and under the
        /// unpinned caret that seal lands up to <c>FLETCHER_DRAG_GRACE_MS</c> after the song left the
        /// line, by which time the player is on the next one and typing. The break was landing on
        /// that run.
        ///
        /// <para>One script, two engines, differing only in the era flag. 'a' is typed on line 0 and
        /// 'b' is given up; the caret takes line 1 at 1500 and types both its cells; line 0 seals at
        /// 4500 on the one cell nobody typed. The increment earned on cell 0 is at or before that
        /// miss and dies under both rules; the two earned on line 1 are past it and survive only
        /// under the back-dated one.</para>
        /// </summary>
        [Test]
        public void TheSealsBreakDestroysTheRunUpToItsMissesAndNoMore()
        {
            var backDated = engine(dragMap(), flexible: true);
            var classic = engine(dragMap(), flexible: true);

            backDated.BackDatedSealBreak = true;

            var seals = new List<LineSealResult>();
            int breaks = 0;

            backDated.LineSealed += s => seals.Add(s);
            backDated.ComboBroken += () => breaks++;

            foreach (var typing in new[] { backDated, classic })
            {
                typing.Update(1000);
                Assert.IsTrue(typing.ProcessKey('a', 1000)); // combo 1, earned at (0, 0)
                Assert.IsTrue(typing.ProcessEnter(1000));    // 'b' (cell 1) is given up, unjudged

                // The caret is handed line 1 at 1500, when its entry bound opens; line 0 is held
                // unsealed by the grace its abandonment keeps for it.
                for (double now = 1100; now < 3000; now += 100)
                    typing.Update(now);

                Assert.AreEqual(1, typing.ActiveLineIndex);

                typing.Update(3000);
                Assert.IsTrue(typing.ProcessKey('c', 3000)); // combo 2, earned at (1, 0)
                typing.Update(3500);
                Assert.IsTrue(typing.ProcessKey('d', 3500)); // combo 3, earned at (1, 1)

                Assert.AreEqual(3, typing.Combo);
                Assert.IsEmpty(seals, "line 0 is still held open by the grace its abandonment keeps for it");
            }

            for (double now = 3500; now <= 4600; now += 100)
            {
                backDated.Update(now);
                classic.Update(now);
            }

            // ONE break either way, at 4500, on the one missed cell: what moved is its reach.
            Assert.AreEqual(1, breaks);
            Assert.AreEqual(new[] { new LineSealResult(0, 1, true, 2) }, seals.ToArray());

            Assert.AreEqual(2, backDated.Combo, "the two cells typed past the miss survive it");
            Assert.AreEqual(0, classic.Combo, "the classic seal takes the whole run");

            // Neither era revisits a maximum the player really did hold.
            Assert.AreEqual(3, backDated.MaxCombo);
            Assert.AreEqual(3, classic.MaxCombo);
        }

        /// <summary>
        /// THE USER'S REPORT (backlog 259): "when the HP drain from the previous line kicks in, it
        /// breaks my current combo, even tho my combo already broke from those misses at the time."
        /// A typo takes its break at the KEYPRESS; backspaced away and left empty, the cell it leaves
        /// behind is a miss, and the seal was taking a SECOND break for the same fumble, a line
        /// later, on the run the player had rebuilt in between.
        ///
        /// <para>Back-dated, that seal destroys NOTHING: everything at or before the erased cell was
        /// already taken by the typo, and everything since was earned past it. So the net combo
        /// change across the seal is zero, which is exactly what the report asks for.</para>
        /// </summary>
        [Test]
        public void AnErasedTypoTakesNoSecondBreakWhenItsLineSeals()
        {
            var typing = engine(dragMap(), flexible: true);

            typing.BackDatedSealBreak = true;

            var seals = new List<LineSealResult>();
            int breaks = 0;

            typing.LineSealed += s => seals.Add(s);
            typing.ComboBroken += () => breaks++;

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000)); // combo 1, earned at (0, 0)

            // The typo on 'b', and the break it takes AT THE KEYPRESS.
            Assert.IsTrue(typing.ProcessKey('x', 1100));
            Assert.AreEqual(0, typing.Combo);
            Assert.AreEqual(1, breaks);

            // Erased and left empty, so cell 1 is a character the player did not finish.
            Assert.IsTrue(typing.ProcessBackspace());
            Assert.AreEqual(CellState.Untyped, typing.Lines[0].Cells[1].State);
            Assert.IsTrue(typing.ProcessEnter(1100));

            for (double now = 1100; now < 3000; now += 100)
                typing.Update(now);

            typing.Update(3000);
            Assert.IsTrue(typing.ProcessKey('c', 3000));
            typing.Update(3500);
            Assert.IsTrue(typing.ProcessKey('d', 3500));

            int rebuilt = typing.Combo;
            Assert.AreEqual(2, rebuilt, "the run the player is holding when their old line runs out of time");

            for (double now = 3500; now <= 4600; now += 100)
                typing.Update(now);

            Assert.AreEqual(new[] { new LineSealResult(0, 1, true, 2) }, seals.ToArray());
            Assert.AreEqual(rebuilt, typing.Combo, "the seal is a NET ZERO on the combo: that break was already paid");
            Assert.AreEqual(2, breaks, "and the second announcement is the seal's own, not a second cost");
        }

        /// <summary>
        /// A RESTORED streak goes back WHERE IT WAS EARNED, not at the cell that redeemed it, which
        /// is what makes the back-dated break correct across a combo restore (backlog 140 plus 259).
        ///
        /// <para>'a' is typed on line 0 and 'b' given up; on line 1 the first cell is spoiled and then
        /// corrected, so the streak of 1 that typo broke (earned back on line 0, cell 0) resumes.
        /// When line 0 seals on 'b', that restored increment is at or before the miss and dies with
        /// it, while the correction's own increment, earned on line 1, survives. Dating the restored
        /// streak at the cell that redeemed it would keep both.</para>
        /// </summary>
        [Test]
        public void ARestoredStreakIsBackDatedToTheCellsItWasEarnedOn()
        {
            var typing = engine(dragMap(), flexible: true);

            typing.BackDatedSealBreak = true;

            int restored = 0;
            typing.ComboRestored += streak => restored += streak;

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000)); // combo 1, earned at (0, 0)
            Assert.IsTrue(typing.ProcessEnter(1000));    // 'b' given up, unjudged

            for (double now = 1100; now < 3000; now += 100)
                typing.Update(now);

            typing.Update(3000);
            Assert.IsTrue(typing.ProcessKey('x', 3000)); // typo on (1, 0): breaks the run of 1, snapshots it
            Assert.AreEqual(0, typing.Combo);

            Assert.IsTrue(typing.ProcessBackspace());
            Assert.IsTrue(typing.ProcessKey('c', 3100)); // the fix: 1 restored, plus its own increment

            Assert.AreEqual(1, restored);
            Assert.AreEqual(2, typing.Combo);

            for (double now = 3100; now <= 4600; now += 100)
                typing.Update(now);

            Assert.AreEqual(1, typing.Combo, "the restored increment was earned before the miss, so it dies with it");
            Assert.AreEqual(2, typing.MaxCombo);
        }

        /// <summary>
        /// The near miss the pin above would not catch: a line the player TYPED OUT is not abandoned,
        /// so it keeps sealing on its own normal deadline rather than borrowing a drag grace it has
        /// no drag to spend. Enter on such a line is a no-op outright (the two time-driven arms
        /// already own that caret), which is also what lets the key handler pass the press through to
        /// its global binding there.
        /// </summary>
        [Test]
        public void EnterOnAFullyTypedLineIsANoOpAndDoesNotHoldTheSealOpen()
        {
            var typing = engine(dragMap(), flexible: true);

            var seals = new List<LineSealResult>();
            typing.LineSealed += s => seals.Add(s);

            // Both chars at 1000, which is before line 1's entry opens at 1500, so the caret parks.
            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            Assert.IsTrue(typing.ProcessKey('b', 1000));
            Assert.IsTrue(typing.IsLineComplete);
            Assert.AreEqual(0, typing.ActiveLineIndex);

            Assert.IsFalse(typing.ProcessEnter(1000), "there is nothing to give up on a line already typed out");
            Assert.AreEqual(2, typing.CaretIndex);

            // 3000 is line 0's own deadline. It seals there with nothing missed: had the no-op marked
            // the line abandoned, the grace would have held it open to 4500 instead.
            typing.Update(2999);
            Assert.IsEmpty(seals);

            typing.Update(3000);
            Assert.AreEqual(1, seals.Count);
            Assert.AreEqual(new LineSealResult(0, 0, false), seals[0]);
        }

        /// <summary>
        /// The WPM clock across a skip, which needs no bookkeeping of its own and is pinned here
        /// because that claim is easy to break. Parking stops the clock (accrual runs only while the
        /// active line is INCOMPLETE, and a parked caret's line reads complete), and on the line the
        /// player lands on the clock behaves exactly as it does after a refused rush: stopped until
        /// the first press there arms it, from that press's own time, with nothing back-dated. The
        /// idle arm is the companion: a head start nobody types in is still paid for with nothing.
        /// </summary>
        [Test]
        public void TheSkipStopsTheClockAndTheNextLineArmsItOnTheFirstPress()
        {
            // Line 1 activates at 4000, so entry into it opens at 2500.
            var typing = engine(twoLineMap(), flexible: true);
            Assert.AreEqual(4000, typing.Lines[1].ActivationTime);

            typing.Update(1000);
            Assert.IsTrue(typing.ProcessKey('a', 1000));
            typing.Update(1500);
            Assert.IsTrue(typing.ProcessKey('b', 1500));
            typing.Update(2000);
            Assert.IsTrue(typing.ProcessKey(' ', 2000));
            Assert.IsTrue(typing.ProcessKey('c', 2000));

            // 1000 -> 1500 -> 2000 = 1000 ms clocked for 4 correct cells: (4/5)/(1000/60000) = 48.
            Assert.AreEqual(48.0, typing.LiveWpm, 1e-9);

            // The last 500 ms on line 0 is real typing time (they were still on an incomplete line),
            // so it counts: 1500 ms, (4/5)/(1500/60000) = 32. Then they give up on 'd', and 2500 is
            // exactly when entry into line 1 opens, so the skip hands them over on the press.
            typing.Update(2500);
            Assert.AreEqual(32.0, typing.LiveWpm, 1e-9);
            Assert.IsTrue(typing.ProcessEnter(2500));
            Assert.AreEqual(1, typing.ActiveLineIndex);

            // The head start credits nothing while they are not typing in it: the arm from line 0 (if
            // there had been one) is not this line's, so the clock is simply stopped.
            typing.Update(2600);
            Assert.AreEqual(32.0, typing.LiveWpm, 1e-9);

            // THE ARMING PRESS: judged early against its 4000 target but correct, so it enters the
            // numerator at once and credits itself no elapsed time: (5/5)/(1500/60000) = 40.
            Assert.IsTrue(typing.ProcessKey('e', 2600));
            Assert.AreEqual(CellState.Correct, typing.Lines[1].Cells[0].State);
            Assert.AreEqual(40.0, typing.LiveWpm, 1e-9);

            // And from the arm the clock runs: 1500 + 100 = 1600 ms, 6 correct cells,
            // (6/5)/(1600/60000) = 45.
            typing.Update(2700);
            Assert.IsTrue(typing.ProcessKey('f', 2700));
            Assert.AreEqual(45.0, typing.LiveWpm, 1e-9);

            // THE IDLE COMPANION, same script minus the presses on line 1: the whole 1500 ms head
            // start a skip bought is worth nothing to a player who does not use it.
            var idle = engine(twoLineMap(), flexible: true);

            idle.Update(1000);
            Assert.IsTrue(idle.ProcessKey('a', 1000));
            idle.Update(1500);
            Assert.IsTrue(idle.ProcessKey('b', 1500));
            idle.Update(2000);
            Assert.IsTrue(idle.ProcessKey(' ', 2000));
            Assert.IsTrue(idle.ProcessKey('c', 2000));
            idle.Update(2500);
            Assert.IsTrue(idle.ProcessEnter(2500));

            idle.Update(2600);
            idle.Update(3000);
            idle.Update(3500);
            idle.Update(4000); // line 1's cue
            Assert.AreEqual(32.0, idle.LiveWpm, 1e-9);

            // From the cue the ordinary rule runs, unarmed: 1500 + 500 = 2000 ms,
            // (4/5)/(2000/60000) = 24.
            idle.Update(4500);
            Assert.AreEqual(24.0, idle.LiveWpm, 1e-9);
        }

        #endregion

        #region Replay determinism

        /// <summary>
        /// Every play is ranked, so a stored replay has to reproduce the run bit-exactly. The caret
        /// era travels in the replay's own CONFIG frame (flags bit 5, see the era fixtures below),
        /// and every judgement is a pure function of (char, time) plus that era: feeding the recorded
        /// frames into a fresh engine the way the replay feeder does reproduces every cell, the score
        /// and the combo. Losing the flag on the way in changes the result, which is the whole reason
        /// it is recorded rather than assumed.
        /// </summary>
        [Test]
        public void UnpinnedRunRoundTripsThroughRecordedFrames()
        {
            // (char, time) exactly as the key handler would stamp them: integral ms, monotonic.
            (char c, double t)[] script =
            {
                ('a', 1000), ('b', 1500),   // line 0 finished twelve seconds early: the caret parks
                ('c', 12500), ('d', 12600), // and rushes into line 1 the instant the bound opens
            };

            var live = engine(instrumentalGapMap(), flexible: true);
            live.Update(1000);

            foreach ((char c, double t) in script)
            {
                live.Update(t);
                Assert.IsTrue(live.ProcessKey(c, t));
            }

            // 20000 + FLETCHER_DRAG_GRACE_MS: line 1 is unfinished, so drag freedom holds it open
            // past its own deadline before the force-seal ends the run.
            live.Update(21500);
            Assert.IsTrue(live.IsFinished);

            var replayed = engine(instrumentalGapMap(), flexible: true);

            foreach ((char c, double t) in script)
            {
                replayed.Update(t);
                replayed.ProcessKey(c, t);
            }

            replayed.Update(21500);

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

            // The same frames re-derived PINNED: the rushed presses never reach the engine at all, so
            // the flag is load-bearing for playback and must come from the run's own header.
            var unmodded = engine(instrumentalGapMap(), flexible: false);

            foreach ((char c, double t) in script)
            {
                unmodded.Update(t);
                unmodded.ProcessKey(c, t);
            }

            unmodded.Update(21500);
            Assert.AreNotEqual(live.BuildResults().Counts[JudgementType.Miss], unmodded.BuildResults().Counts[JudgementType.Miss]);
        }

        /// <summary>
        /// A LINE SKIP travels in the replay as its own sentinel frame (backlog 241) and re-derives
        /// exactly: same cells, same caret, same totals, same clock. It needs no era bit, because a
        /// replay recorded before the skip existed simply contains no such frame, and the skip itself
        /// changes nothing but which line the caret is on.
        /// </summary>
        [Test]
        public void ALineSkipReDerivesFromItsOwnSentinelFrame()
        {
            // Ticks and inputs shared by both runs, so the only difference is HOW the inputs reach
            // the engine: 'a' is typed, 'b' is given up, and the caret is on line 1 from 1500.
            double[] ticks = { 1000, 1200, 1500, 3000, 3500, 6500 };

            (char c, double t)[] script =
            {
                ('a', 1000),
                (TypeBeatReplayFrame.ENTER, 1200),
                ('c', 3000),
                ('d', 3500),
            };

            var live = engine(dragMap(), flexible: true);
            var replayed = engine(dragMap(), flexible: true);

            foreach (double tick in ticks)
            {
                live.Update(tick);
                replayed.Update(tick);

                foreach ((char c, double t) in script.Where(s => s.t == tick))
                {
                    if (c == TypeBeatReplayFrame.ENTER)
                        Assert.IsTrue(live.ProcessEnter(t));
                    else
                        Assert.IsTrue(live.ProcessKey(c, t));

                    ReplayEngineFeed.Apply(replayed, new TypeBeatReplayFrame(t, c));
                }
            }

            // Sanity: the run really did exercise the skip (line 0 lost 'b' and its one break).
            Assert.IsTrue(live.IsFinished);
            Assert.AreEqual(CellState.Missed, live.Lines[0].Cells[1].State);
            Assert.AreEqual(1, live.BuildResults().Counts[JudgementType.Miss]);

            Assert.AreEqual(live.Score, replayed.Score);
            Assert.AreEqual(live.MaxCombo, replayed.MaxCombo);
            Assert.AreEqual(live.Combo, replayed.Combo);
            Assert.AreEqual(live.CaretIndex, replayed.CaretIndex);
            Assert.AreEqual(live.ActiveLineIndex, replayed.ActiveLineIndex);
            Assert.AreEqual(live.LiveAccuracy, replayed.LiveAccuracy);
            Assert.AreEqual(live.LiveWpm, replayed.LiveWpm, 1e-12);

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
        }

        /// <summary>
        /// FORWARD COMPATIBILITY, which the line skip's sentinel is only the first customer of. A
        /// frame carrying a control code this build does not know is IGNORED, so a run recorded by a
        /// later client degrades here to the inputs this one understands. Without that arm it would
        /// fall through to <c>ProcessKey</c> and be judged as a WRONG KEY, which the third engine
        /// below shows is not a small difference: it takes the combo and enters the accuracy
        /// denominator forever.
        /// </summary>
        [TestCase('\t')] // 0x09
        [TestCase('\r')] // 0x0D
        [TestCase('\u001b')] // ESC, the far end of the control range below the typeable surface
        public void AnUnknownSentinelIsIgnoredRatherThanMisjudged(char unknown)
        {
            var withUnknown = engine(dragMap(), flexible: true);
            var without = engine(dragMap(), flexible: true);

            ReplayEngineFeed.Apply(withUnknown, new TypeBeatReplayFrame(1000, 'a'));
            ReplayEngineFeed.Apply(without, new TypeBeatReplayFrame(1000, 'a'));

            ReplayEngineFeed.Apply(withUnknown, new TypeBeatReplayFrame(1100, unknown));
            without.Update(1100);

            ReplayEngineFeed.Apply(withUnknown, new TypeBeatReplayFrame(1500, 'b'));
            ReplayEngineFeed.Apply(without, new TypeBeatReplayFrame(1500, 'b'));

            Assert.AreEqual(without.Score, withUnknown.Score);
            Assert.AreEqual(without.Combo, withUnknown.Combo);
            Assert.AreEqual(without.Mistypes, withUnknown.Mistypes);
            Assert.AreEqual(without.ConsecutiveWrongKeys, withUnknown.ConsecutiveWrongKeys);
            Assert.AreEqual(without.LiveAccuracy, withUnknown.LiveAccuracy);
            Assert.AreEqual(without.CaretIndex, withUnknown.CaretIndex);
            Assert.AreEqual(2, withUnknown.Combo, "the unknown frame cost the run nothing at all");

            // NON-VACUITY: the same character handed straight to ProcessKey, which is where it went
            // before the guard existed, is a wrong key and is anything but free.
            var misjudged = engine(dragMap(), flexible: true);

            ReplayEngineFeed.Apply(misjudged, new TypeBeatReplayFrame(1000, 'a'));
            misjudged.Update(1100);
            misjudged.ProcessKey(unknown, 1100);

            Assert.AreEqual(0, misjudged.Combo);
            Assert.AreNotEqual(without.LiveAccuracy, misjudged.LiveAccuracy);
        }

        #endregion

        #region Replay eras (CONFIG flags bit 5)

        /// <summary>Feed a header plus a script through the one seam a recorded frame reaches an
        /// engine by, exactly as playback and the headless scorer do.</summary>
        private static void feed(TypingEngine typing, TypeBeatReplayFrame config, params (char c, double t)[] script)
        {
            ReplayEngineFeed.Apply(typing, config);

            foreach ((char c, double t) in script)
                ReplayEngineFeed.Apply(typing, new TypeBeatReplayFrame(t, c));
        }

        /// <summary>
        /// THE COMPAT PIN. A replay recorded before backlog 208 carries flags bit 5 CLEAR and no mod
        /// on its score, and it has to re-derive on the PINNED caret it was actually played with.
        /// Judged against a straight pinned run of the same script, cell for cell: the boundary
        /// snatches the line, 'b' is a Miss, and the caret is on line 1.
        /// </summary>
        [Test]
        public void AnOldReplayWithTheBitClearReDerivesPinned()
        {
            var replayed = new TypingEngine(dragMap());

            feed(replayed, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true), ('a', 1000));

            Assert.IsFalse(replayed.FletcherEnabled, "the header says nothing, and nothing on the score says otherwise");
            Assert.IsFalse(replayed.FlexibleLineSnap);

            replayed.Update(3000);

            var live = engine(dragMap(), flexible: false);
            live.Update(1000);
            Assert.IsTrue(live.ProcessKey('a', 1000));
            live.Update(3000);

            Assert.AreEqual(live.ActiveLineIndex, replayed.ActiveLineIndex);
            Assert.AreEqual(1, replayed.ActiveLineIndex);
            Assert.AreEqual(CellState.Missed, replayed.Lines[0].Cells[1].State);
            Assert.AreEqual(live.Score, replayed.Score);
            Assert.AreEqual(live.BuildResults().Counts[JudgementType.Miss], replayed.BuildResults().Counts[JudgementType.Miss]);
        }

        /// <summary>
        /// The one combination no bit can express on its own: a stored "FT" run. Its header has bit 5
        /// clear (the bit did not exist), so the caret half comes from the mod on its score, which
        /// the two engine factories hand over as
        /// <see cref="TypingEngine.FlexibleCaretFromMod"/>. Apply must OR that in rather than clobber
        /// it back to pinned, and must still take the SNAP straight from the bit, i.e. off.
        /// </summary>
        [Test]
        public void AnOldFletcherModReplayReDerivesUnpinnedWithNoSnap()
        {
            // Exactly what TypeBeatReplayScorer.createEngine writes when it sees the retired mod.
            var replayed = new TypingEngine(dragMap()) { FletcherEnabled = true, FlexibleCaretFromMod = true };

            feed(replayed, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true), ('a', 1000));

            Assert.IsTrue(replayed.FletcherEnabled, "the header must not pin a caret the mod unpinned");
            Assert.IsFalse(replayed.FlexibleLineSnap, "no FT run was ever played with the snap");

            // Drag freedom, which is what the mod bought: the boundary does not take the line.
            replayed.Update(3000);
            Assert.AreEqual(0, replayed.ActiveLineIndex);
            Assert.AreEqual(CellState.Untyped, replayed.Lines[0].Cells[1].State);
        }

        /// <summary>
        /// A replay recorded TODAY: the header carries bit 5, so a fresh engine that knows nothing
        /// about mods re-derives the unpinned caret AND the snap. Non-vacuous through the parked
        /// fixture, whose caret only reaches line 2 if the snap is armed.
        /// </summary>
        [Test]
        public void ANewReplayCarriesTheEraBitAndReDerivesTheSnap()
        {
            var replayed = new TypingEngine(parkedLineMap());

            feed(replayed,
                TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true),
                ('a', 1000), ('b', 1500));

            Assert.IsTrue(replayed.FletcherEnabled);
            Assert.IsTrue(replayed.FlexibleLineSnap);

            replayed.Update(10500);
            Assert.AreEqual(2, replayed.ActiveLineIndex);
        }

        /// <summary>
        /// And a run played under the Fletcher MOD records the bit clear, so it re-derives pinned off
        /// its own header with no mod inspection anywhere: the same decode an ancient replay gets,
        /// which is exactly why the bit means "flexible" rather than "pinned".
        /// </summary>
        [Test]
        public void APinnedModReplayReDerivesPinnedFromItsOwnHeader()
        {
            var replayed = new TypingEngine(dragMap()) { FletcherEnabled = true, FlexibleLineSnap = true };

            feed(replayed, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true), ('a', 1000));

            Assert.IsFalse(replayed.FletcherEnabled, "the header wins over whatever the engine was built with");
            Assert.IsFalse(replayed.FlexibleLineSnap);

            replayed.Update(3000);
            Assert.AreEqual(CellState.Missed, replayed.Lines[0].Cells[1].State);
        }

        /// <summary>
        /// A REPLAY OF THE PRE-218 ERA, which is what every stored flexible run is: bit 5 set and bit
        /// 7 clear, and it has to re-derive with the UNBOUNDED roll its player actually had, or the
        /// keystrokes they typed into a line seconds before its vocals are refused on playback and
        /// the whole account changes. Judged against a straight unbounded run of the same script,
        /// cell for cell.
        /// </summary>
        [Test]
        public void AnOldFlexibleReplayWithBitSevenClearReDerivesTheUnboundedRoll()
        {
            // Line 1's cue is 8500, so the bound would have opened at 7000 and refused both of the
            // rushed presses below.
            (char c, double t)[] script = { ('a', 1000), ('b', 1500), ('c', 1600), ('d', 1650) };

            var replayed = new TypingEngine(lateSecondLineMap());

            feed(replayed, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true), script);

            Assert.IsTrue(replayed.FletcherEnabled);
            Assert.IsTrue(replayed.FlexibleLineSnap);
            Assert.IsFalse(replayed.BoundedRush, "the bit is clear, so the run re-derives unbounded");

            // The reference run: the same era in every other respect (the header above sets the four
            // judgement bits), so the only thing this comparison can be reading is the caret.
            var stored = new TypingEngine(lateSecondLineMap())
            {
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                SyllableTiming = true,
                WrongInputOnWordGaps = true,
                StrictSpaces = true,
                CharTimedStretch = true,
            };

            foreach ((char c, double t) in script)
            {
                stored.Update(t);
                Assert.IsTrue(stored.ProcessKey(c, t));
            }

            replayed.Update(12000);
            stored.Update(12000);

            Assert.AreEqual(stored.Score, replayed.Score);
            Assert.AreEqual(stored.MaxCombo, replayed.MaxCombo);
            Assert.AreEqual(stored.LiveAccuracy, replayed.LiveAccuracy);

            for (int k = 0; k < stored.Lines.Count; k++)
            {
                for (int i = 0; i < stored.Lines[k].Cells.Count; i++)
                {
                    Assert.AreEqual(stored.Lines[k].Cells[i].State, replayed.Lines[k].Cells[i].State, $"cell {k}.{i} state");
                    Assert.AreEqual(stored.Lines[k].Cells[i].JudgedDelta, replayed.Lines[k].Cells[i].JudgedDelta, $"cell {k}.{i} delta");
                }
            }

            // Non-vacuous: the SAME frames under a header that carries bit 7 refuse the rushed
            // presses outright, which is exactly why the bit has to travel with the run.
            var bounded = new TypingEngine(lateSecondLineMap());

            feed(bounded, TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true), script);

            Assert.IsTrue(bounded.BoundedRush);
            Assert.AreEqual(CellState.Correct, replayed.Lines[1].Cells[0].State);
            Assert.AreEqual(CellState.Untyped, bounded.Lines[1].Cells[0].State, "under the bound those keystrokes never reached a cell");

            // The rushed presses were Premature and worth no points, so the SCORE is not what the two
            // arms disagree about: the run itself is. Four cells finished and a streak of four, or
            // two and two.
            Assert.AreEqual(4, replayed.MaxCombo);
            Assert.AreEqual(2, bounded.MaxCombo);
            Assert.AreEqual(2, replayed.BuildResults().Counts[JudgementType.Premature]);
            Assert.AreEqual(0, bounded.BuildResults().Counts[JudgementType.Premature]);
        }

        /// <summary>
        /// Bit 5 is bit 5 and bit 7 is bit 7: values 32 and 128, appended above the bits that were
        /// already there, so every flags word already on disk decodes exactly as it always did and
        /// simply reads the new bits false.
        /// </summary>
        [Test]
        public void TheEraBitRoundTripsThroughTheLegacyEncodingAsBitFive()
        {
            var dummy = new typebeat.Game.Beatmaps.Beatmap();

            var full = TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true);

            Assert.AreEqual(1 + 2 + 4 + 8 + 16 + 32 + 64 + 128, (int)full.ToLegacy(dummy).MouseY!.Value, "bit 5 sits between strict spaces and char-timed stretch, and bit 7 above both");

            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(full.ToLegacy(dummy), dummy);
            Assert.IsTrue(decoded.FlexibleLines);
            Assert.IsTrue(decoded.BoundedRush);

            // The word a replay carried the day before backlog 218: every older bit set, bit 7 clear.
            var pre218 = new TypeBeatReplayFrame();
            pre218.FromLegacy(new typebeat.Game.Replays.Legacy.LegacyReplayFrame(0, 0, 1 + 2 + 4 + 8 + 16 + 32 + 64, typebeat.Game.Replays.Legacy.ReplayButtonState.None), dummy);

            Assert.IsTrue(pre218.FlexibleLines);
            Assert.IsTrue(pre218.CharTimedStretch);
            Assert.IsFalse(pre218.BoundedRush, "the newest bit reads false on every word already on disk");

            // A pre-208 word, every older bit set and nothing above them.
            var old = new TypeBeatReplayFrame();
            old.FromLegacy(new typebeat.Game.Replays.Legacy.LegacyReplayFrame(0, 0, 1 + 2 + 4 + 8 + 16, typebeat.Game.Replays.Legacy.ReplayButtonState.None), dummy);

            Assert.IsTrue(old.AllowWrongInput);
            Assert.IsTrue(old.SpaceSkipsWord);
            Assert.IsTrue(old.SyllableTiming);
            Assert.IsTrue(old.WrongInputOnWordGaps);
            Assert.IsTrue(old.StrictSpaces);
            Assert.IsFalse(old.FlexibleLines, "the new bit reads false on every word already on disk");
            Assert.IsFalse(old.CharTimedStretch);
            Assert.IsFalse(old.BoundedRush);

            // And the default is clear, which is what every older CALL SITE keeps meaning.
            Assert.IsFalse(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true).FlexibleLines);
            Assert.IsFalse(TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true).BoundedRush);
        }

        /// <summary>
        /// A REWIND re-derives the parked state like any other (backlog 218): the caret's position is
        /// a pure function of the frames plus the era, so seeking back into the park puts the caret
        /// back at the end of the line it finished, and seeking past the bound puts it back on the
        /// next line. The era itself comes off the header, which <c>RebuildTo</c> re-feeds.
        /// </summary>
        [Test]
        public void ARebuildReDerivesTheParkedStateAndTheBoundFromTheHeader()
        {
            var frames = new List<typebeat.Game.Rulesets.Replays.ReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(0, allowWrongInput: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true),
                new TypeBeatReplayFrame(1000, 'a'),
                new TypeBeatReplayFrame(1500, 'b'),
            };

            var typing = new TypingEngine(instrumentalGapMap());

            ReplayEngineFeed.RebuildTo(typing, frames, 13000);

            Assert.IsTrue(typing.BoundedRush, "the header carries the era through a rebuild");
            Assert.AreEqual(1, typing.ActiveLineIndex, "13000 is past the bound, so the deferred roll has happened");

            // Backwards, into the park: the caret is at the end of line 0 again, complete and stuck.
            ReplayEngineFeed.RebuildTo(typing, frames, 6000);

            Assert.AreEqual(0, typing.ActiveLineIndex);
            Assert.IsTrue(typing.IsLineComplete);
            Assert.AreEqual(2, typing.CaretIndex);
            Assert.IsFalse(typing.ProcessKey('c', 6000), "and it is inert there, exactly as it was live");

            // And forwards again, to the same answer as the first pass.
            ReplayEngineFeed.RebuildTo(typing, frames, 13000);
            Assert.AreEqual(1, typing.ActiveLineIndex);
        }

        #endregion

        #region Input-gate seams the playfield reads

        /// <summary>
        /// With an unpinned caret a line is active straight through an instrumental gap (the caret is
        /// parked at the next line's head), so <see cref="TypingEngine.LineIsActive"/> alone can no
        /// longer tell the key handler when to let Space through to the skip overlay. These are the
        /// extra predicates it reads: is the SONG inside a line window, is it inside the window of the
        /// line the CARET is on, and has the player started that line.
        /// </summary>
        [Test]
        public void SongWindowAndUntouchedFlagsTrackTheDeadZone()
        {
            var typing = engine(lateSecondLineMap(), flexible: true);

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

        /// <summary>
        /// The predicate the key handler actually gates Space on, and why
        /// <see cref="TypingEngine.SongWindowOpen"/> could not be it. On a REAL map the decoder makes
        /// line windows CONTIGUOUS, so a twelve-second instrumental lives INSIDE the finished line's
        /// own window and SongWindowOpen never goes false: a player parked at the head of the next
        /// line would have their Space eaten as a combo-breaking typo instead of reaching the
        /// mid-song skip overlay. <see cref="TypingEngine.SongIsOnTheCaretsLine"/> asks the narrower
        /// question that is actually being asked, is the song wanting characters HERE, and it goes
        /// false the moment the caret moves ahead of the song's line.
        /// </summary>
        [Test]
        public void SongIsOnTheCaretsLineTracksAContiguousInstrumentalGap()
        {
            // Line 0's window runs all the way to line 1's start: no hole anywhere, the shape every
            // decoder-built map has.
            var typing = engine(instrumentalGapMap(), flexible: true);

            typing.Update(1000);
            Assert.IsTrue(typing.SongIsOnTheCaretsLine, "the song is asking for these very characters");

            Assert.IsTrue(typing.ProcessKey('a', 1000));
            Assert.IsTrue(typing.ProcessKey('b', 1500));

            // Deep inside the instrumental the rush bound (backlog 218) still has the caret parked
            // past the END of line 0, where the key handler's own IsLineComplete fall-through is what
            // lets Space reach the skip overlay, and this predicate is not being asked yet.
            typing.Update(6000);
            Assert.AreEqual(0, typing.ActiveLineIndex);
            Assert.IsTrue(typing.IsLineComplete);

            // The bound opens 1500 before line 1's cue and the caret moves ahead of the song. NOW the
            // predicate is what the key handler needs: the window is contiguous, so SongWindowOpen
            // could never have answered it.
            typing.Update(12500);
            Assert.IsTrue(typing.SongWindowOpen, "the window is contiguous, so it never closes");
            Assert.AreEqual(1, typing.ActiveLineIndex);
            Assert.IsFalse(typing.SongIsOnTheCaretsLine, "but nothing is being asked of the line the caret is on");
            Assert.IsTrue(typing.ActiveLineUntouched);

            // The song arrives on the caret's line and Space is a typing key again.
            typing.Update(14000);
            Assert.IsTrue(typing.SongIsOnTheCaretsLine);

            // Pinned, the two are the same question, which is why the old handler only needed one.
            var pinned = engine(dragMap(), flexible: false);
            pinned.Update(1000);
            Assert.AreEqual(pinned.SongWindowOpen, pinned.SongIsOnTheCaretsLine);
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
            var typing = engine(twoLineMap(), flexible: true);

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
