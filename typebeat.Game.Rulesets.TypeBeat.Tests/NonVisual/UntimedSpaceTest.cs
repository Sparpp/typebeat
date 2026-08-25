// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 148: the SPACEBAR is out of the timing challenge. A space typed on a SPACE CELL is
    /// judged as though it landed dead on that cell's target, so it takes the top tier
    /// (<see cref="JudgementType.Great"/>, the top since backlog 147 dropped Perfect) whatever the
    /// clock said, and can never fall into one of the two zero-point tiers that break combo. The
    /// word gap is where a typist's hands reset; it is not a note to hit.
    ///
    /// <para>The exemption is scoped to the CELL, not to the KEY, and half of this fixture is the
    /// pin on that: a space that does NOT land on a space cell is untouched, and so are the two
    /// guards that hang off its rejection (the mistype and the consecutive-wrong-key mash streak).
    /// The other half pins what an untimed space is still not: free. A space cell nobody pressed is
    /// a character of the map left untyped and misses like any other.</para>
    /// </summary>
    [TestFixture]
    public class UntimedSpaceTest
    {
        #region Fixture builders

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// "ab cd", cells a = 1000, b = 1500, ' ' = 2000 (unit 0's end), c = 2000, d = 2500. The line
        /// runs to 60000 so nothing seals while a press is being made absurdly late on purpose;
        /// <see cref="sealingLine"/> is the same shape with a real deadline.
        /// Line-granularity windows: Great [-250, 400], Ok [-600, 1000], Meh [-1200, 2000].
        /// </summary>
        private static LyricBeatmap abCd() => map(line("ab cd", 1000, 60000, 3000,
            unit("ab", 1000, 2000), unit("cd", 2000, 3000)));

        /// <summary>Same cells as <see cref="abCd"/>, but the line's deadline is 4000 so it seals.</summary>
        private static LyricBeatmap sealingLine() => map(line("ab cd", 1000, 4000, 3000,
            unit("ab", 1000, 2000), unit("cd", 2000, 3000)));

        /// <summary>
        /// Two five-letter words either side of one gap, at a DENSE 100 ms per char:
        /// a = 1000 ... e = 1400, ' ' = 1500, f = 1500 ... j = 1900. Sized for the Fletcher rush cap
        /// (<see cref="TypingEngine.FLETCHER_MAX_CHARS_AHEAD"/> = 5), which is the one rule that can
        /// still break a combo on an accepted press. Dense on purpose, in the style of
        /// FletcherEngineTest's own fixture: six chars of rush is only 600 ms early here, so every
        /// press stays inside the Line-granularity windows and the CHARACTER cap has to be what
        /// breaks the combo, not the clock.
        /// </summary>
        private static LyricBeatmap fiveAndFive() => map(line("abcde fghij", 1000, 60000, 2000,
            unit("abcde", 1000, 1500), unit("fghij", 1500, 2000)));

        private static TypingEngine started(LyricBeatmap beatmap)
        {
            var engine = new TypingEngine(beatmap);
            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            return engine;
        }

        #endregion

        /// <summary>
        /// The headline: a space pressed 5 SECONDS after its cell's target, far outside even the
        /// Meh window (2000 late), scores exactly what the same space pressed dead on target scores.
        /// Asserted as an equality against a control run rather than only as a number, because
        /// "the spacebar is not part of the timing challenge" IS that equality.
        /// </summary>
        [Test]
        public void ASpaceIsJudgedTheSameHoweverLateItIsPressed()
        {
            var late = started(abCd());
            Assert.IsTrue(late.ProcessKey('a', 1000));  // Great, 300 * (1 + 0/50) = 300
            Assert.IsTrue(late.ProcessKey('b', 1500));  // Great, 300 * (1 + 1/50) = 306
            Assert.IsTrue(late.ProcessKey(' ', 7000));  // delta 5000: Lagging before backlog 148
            var lateResults = late.BuildResults();

            var onTime = started(abCd());
            Assert.IsTrue(onTime.ProcessKey('a', 1000));
            Assert.IsTrue(onTime.ProcessKey('b', 1500));
            Assert.IsTrue(onTime.ProcessKey(' ', 2000)); // delta 0
            var onTimeResults = onTime.BuildResults();

            Assert.AreEqual(918, onTimeResults.Score); // 300 + 306 + 312
            Assert.AreEqual(onTimeResults.Score, lateResults.Score);
            Assert.AreEqual(onTimeResults.Counts[JudgementType.Great], lateResults.Counts[JudgementType.Great]);
            Assert.AreEqual(onTimeResults.SyncPercent, lateResults.SyncPercent, 1e-9);
            Assert.AreEqual(onTimeResults.MaxCombo, lateResults.MaxCombo);

            Assert.AreEqual(3, lateResults.Counts[JudgementType.Great]);
            Assert.AreEqual(0, lateResults.Counts[JudgementType.Lagging]);
            Assert.AreEqual(3, late.Combo, "an untimed space cannot break the run it is sitting in");
            Assert.AreEqual(CellState.Correct, late.Lines[0].Cells[2].State);
            Assert.AreEqual(' ', late.Lines[0].Cells[2].TypedChar);
            // Judged on a zeroed delta, and that is the delta stored, because every sync readout
            // reads this field back (see the tint and SyncPercent cases below).
            Assert.AreEqual(0, late.Lines[0].Cells[2].JudgedDelta!.Value, 1e-9);
        }

        /// <summary>
        /// The same press one cell later is NOT exempt: it is the spacebar that left the timing
        /// challenge, not the player's sense of rhythm. A lyric character pressed just as late is
        /// still Lagging, still worth nothing, and still breaks the combo the space kept alive.
        /// </summary>
        [Test]
        public void ALyricCharacterPressedJustAsLateIsStillLagging()
        {
            var engine = started(abCd());

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            Assert.IsTrue(engine.ProcessKey(' ', 7000)); // exempt: Great, combo 3
            Assert.AreEqual(3, engine.Combo);

            Assert.IsTrue(engine.ProcessKey('c', 7100)); // delta 5100 on a lyric char: Lagging

            var results = engine.BuildResults();

            Assert.AreEqual(1, results.Counts[JudgementType.Lagging]);
            Assert.AreEqual(0, engine.Combo);
            Assert.AreEqual(918, results.Score, "Lagging scores nothing, so the total is unmoved");
        }

        /// <summary>
        /// Scoped to the CELL, hole 1: with <see cref="TypingEngine.SpaceSkipsWord"/> off (the
        /// default), a space pressed on a LYRIC character is still rejected outright. Keying the
        /// exemption off "the key was a space" instead would have made space-mashing free.
        ///
        /// <para>On the CLASSIC space era (<see cref="TypingEngine.StrictSpaces"/> false, the default
        /// here). Backlog 184's live arm types that press through as a typo instead, which costs the
        /// player the cell rather than nothing, so the exemption is not loosened either way.</para>
        /// </summary>
        [Test]
        public void ASpaceOnALyricCharacterIsStillRejected()
        {
            var engine = started(abCd());
            char? rejected = null;
            engine.WrongKeyRejected += c => rejected = c;

            Assert.IsFalse(engine.SpaceSkipsWord);
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey(' ', 1500)); // caret is on 'b', dead on ITS target

            Assert.AreEqual(' ', rejected);
            Assert.AreEqual(1, engine.CaretIndex, "caret unmoved: nothing entered the cell");
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(0, engine.Combo);
            Assert.AreEqual(1, engine.Mistypes);
            Assert.AreEqual(0.5, engine.LiveAccuracy, 1e-9); // the rejected space still costs accuracy

            var results = engine.BuildResults();

            Assert.AreEqual(1, results.Counts[JudgementType.Great], "only 'a' was judged");
            Assert.AreEqual(1, results.Counts[JudgementType.WrongChar]);
        }

        /// <summary>
        /// Scoped to the CELL, hole 2: the mash-fail streak (the play fails at
        /// <see cref="Scoring.TypeBeatHealthProcessor.WRONG_KEY_FAIL_STREAK"/> = 13 consecutive
        /// rejected keys) accrues on that same rejection path, so it must still count a held-down
        /// spacebar. Note this is the DEFAULT input model: a wrong LETTER is typed through and never
        /// touches the streak, which makes the space one of the only keys that can build it here.
        ///
        /// <para>On the CLASSIC space era (<see cref="TypingEngine.StrictSpaces"/> false). Under
        /// backlog 184's live arm a mid-word space is typed through, so it stops reaching this branch
        /// and stops feeding the streak: a knock-on the feature accepts, since the guard exists for
        /// Gatekeeper, where every wrong key is still rejected. See
        /// <c>SpaceDisciplineTest.MidWordSpacesNoLongerFeedTheMashFailStreak</c>.</para>
        /// </summary>
        [Test]
        public void TheMashFailStreakStillAccruesOnRejectedSpaces()
        {
            var engine = started(abCd());

            Assert.IsTrue(engine.AllowWrongInput);

            for (int i = 0; i < 13; i++)
            {
                Assert.IsTrue(engine.ProcessKey(' ', 1000 + i));
                Assert.AreEqual(i + 1, engine.ConsecutiveWrongKeys);
            }

            Assert.AreEqual(0, engine.CaretIndex, "13 mashed spaces got the player nowhere");
            Assert.AreEqual(13, engine.Mistypes);

            // ...and any accepted char resets it, exactly as before.
            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys);
        }

        /// <summary>
        /// Scoped to the CELL, hole 3: the Fletcher RUSH CAP counts COUNTABLE characters
        /// (<see cref="TypingCell.IsCountable"/>, which is typeable-and-not-a-space), so a space
        /// spends no budget and can never be the press that crosses the line. That predates backlog
        /// 148 and is deliberately left alone by it: this pins that the exemption did not quietly
        /// loosen the one rule that can still break a combo on an accepted press.
        /// </summary>
        [Test]
        public void TheFletcherRushCapStillCountsOnlyLyricCharacters()
        {
            var engine = new TypingEngine(fiveAndFive()) { FletcherEnabled = true };
            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);

            // Everything is pressed at 1000, where the playhead has passed exactly one countable
            // target ('a'), so the caret's lead grows by one with every countable press. All five
            // are at most 400 ms early, so all five score and the combo builds.
            foreach (char c in "abcde")
                Assert.IsTrue(engine.ProcessKey(c, 1000));

            Assert.AreEqual(5, engine.Combo); // lead 4: inside the cap of 5

            // The gap, pressed 500 ms before its target (an Ok on the ladder). Untimed, so it is a
            // Great; countable-free, so it leaves the lead at 4 and the combo goes on growing.
            Assert.IsTrue(engine.ProcessKey(' ', 1000));
            Assert.AreEqual(6, engine.Combo, "a space spends no rush budget, so it cannot cross the cap");

            Assert.IsTrue(engine.ProcessKey('f', 1000)); // lead 5: still inside the cap
            Assert.AreEqual(7, engine.Combo);

            long scoreBeforeTheRush = engine.Score;

            Assert.IsTrue(engine.ProcessKey('g', 1000)); // lead 6: over it
            Assert.AreEqual(0, engine.Combo, "the cap itself is untouched and still bites");

            // The rushed char still LANDS and still scores, which is what makes the cap a combo
            // penalty rather than a block.
            Assert.Greater(engine.Score, scoreBeforeTheRush);
            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[7].State);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss]);
        }

        /// <summary>
        /// An untimed space is not a FREE space. The cell is a character of the map, so one the
        /// player never pressed at all seals as a Miss with every other untyped cell (the seal loop
        /// in <see cref="TypingEngine.Update"/> tests IsTypeable, which a space cell is; only
        /// IsCountable excludes it). Backlog 148 is about spaces not being a TIMING hazard.
        /// </summary>
        [Test]
        public void AnUntypedSpaceCellStillSealsAsAMiss()
        {
            var engine = started(sealingLine());

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1500));

            engine.Update(4000);

            var cells = engine.Lines[0].Cells;

            Assert.AreEqual(CellState.Missed, cells[2].State, "the untouched word gap");
            Assert.AreEqual(CellState.Missed, cells[3].State);
            Assert.AreEqual(CellState.Missed, cells[4].State);
            Assert.AreEqual(3, engine.BuildResults().Counts[JudgementType.Miss]);
        }

        /// <summary>
        /// The two sync readouts average <see cref="SyncWindows.SyncQuality"/> over the stored
        /// per-cell delta, and a space is stored at the ZEROED delta it was judged on, so leaving it
        /// in would pay a full 1.0 quality per word gap for a press that measured nothing. It is
        /// taken out of the NUMERATOR AND THE DENOMINATOR of both, so a space neither helps nor
        /// hurts. Neutrality, not credit: the exemption is meant to stop spaces being a timing
        /// hazard, and turning them into free sync would have lifted the letter grade (which
        /// <see cref="ResultsSummary.Grade"/> gates on SyncPercent) for nothing.
        /// </summary>
        [Test]
        public void TheExemptSpaceIsLeftOutOfBothSyncReadouts()
        {
            var engine = started(abCd());

            // Every LYRIC character 200 ms late: q = 1 - 200/2000 = 0.9 apiece.
            Assert.IsTrue(engine.ProcessKey('a', 1200));
            Assert.IsTrue(engine.ProcessKey('b', 1700));
            Assert.IsTrue(engine.ProcessKey(' ', 7000)); // exempt, and neutral
            Assert.IsTrue(engine.ProcessKey('c', 2200));
            Assert.IsTrue(engine.ProcessKey('d', 2700));

            // 4 timed cells at 0.9, the space out of both halves. Counted IN at its zeroed delta it
            // would read 100 * (3.6 + 1) / 5 = 92, which is a player at 90 being handed an S.
            Assert.AreEqual(90, engine.LiveSyncPercent, 1e-9);
            Assert.AreEqual(90, engine.BuildResults().SyncPercent, 1e-9);

            // ...and the space's own timing cannot move either readout, which is the whole claim.
            var onTime = started(abCd());
            Assert.IsTrue(onTime.ProcessKey('a', 1200));
            Assert.IsTrue(onTime.ProcessKey('b', 1700));
            Assert.IsTrue(onTime.ProcessKey(' ', 2000)); // dead on target this time
            Assert.IsTrue(onTime.ProcessKey('c', 2200));
            Assert.IsTrue(onTime.ProcessKey('d', 2700));

            Assert.AreEqual(engine.LiveSyncPercent, onTime.LiveSyncPercent, 1e-9);
            Assert.AreEqual(engine.BuildResults().SyncPercent, onTime.BuildResults().SyncPercent, 1e-9);
        }

        /// <summary>
        /// The other half of the denominator rule: a space cell left UNTYPED does not drag the sync
        /// percent down either, where an untyped lyric character does. The completion denominator is
        /// untouched by all of this (the space still misses, see
        /// <see cref="AnUntypedSpaceCellStillSealsAsAMiss"/>); it is only the sync mean that stops
        /// having an opinion about spaces.
        /// </summary>
        [Test]
        public void AnUntypedSpaceDoesNotDragTheSyncPercentDownEither()
        {
            var engine = started(sealingLine());

            Assert.IsTrue(engine.ProcessKey('a', 1000)); // q = 1
            Assert.IsTrue(engine.ProcessKey('b', 1500)); // q = 1

            engine.Update(4000); // seals: the gap, 'c' and 'd' all miss

            var results = engine.BuildResults();

            // 2 of the 4 TIMED cells at quality 1: 50. Over all 5 typeable cells it would be 40, and
            // the difference is entirely the word gap the player never pressed.
            Assert.AreEqual(50, results.SyncPercent, 1e-9);
            Assert.AreEqual(3, results.Counts[JudgementType.Miss], "the miss itself is unaffected");
        }

        /// <summary>
        /// SyncTimeline is offset/sync ANALYSIS, a record of where the player's hands sit against the
        /// map, and an untimed space no longer measures that: its delta is 0 by RULE rather than by
        /// observation, and a player told the spacebar does not matter will type it loosely on
        /// purpose. So it is left OUT of the series entirely rather than contributed as a zero, which
        /// would pull the mean toward "no offset" with a sample that saw nothing.
        /// </summary>
        [Test]
        public void AnUntimedSpaceIsLeftOutOfTheSyncTimeline()
        {
            var engine = started(abCd());

            Assert.IsTrue(engine.ProcessKey('a', 1200));  // delta +200
            Assert.IsTrue(engine.ProcessKey('b', 1700));  // delta +200
            Assert.IsTrue(engine.ProcessKey(' ', 7000));  // exempt: no sample
            Assert.IsTrue(engine.ProcessKey('c', 2200));  // delta +200

            var timeline = engine.BuildResults().SyncTimeline;

            Assert.AreEqual(3, timeline.Count, "the three lyric characters, and only those");
            Assert.AreEqual(new SyncSample(1200, 200), timeline[0]);
            Assert.AreEqual(new SyncSample(1700, 200), timeline[1]);
            Assert.AreEqual(new SyncSample(2200, 200), timeline[2]);
        }

        /// <summary>
        /// The anti-farming rule is unaffected: re-typing a space after backspacing over it is
        /// scoring-inert exactly as for a lyric character, and it re-classifies the stored
        /// FirstCorrectDelta, which for a space is the zeroed one. So a retype is a Great too,
        /// however late it lands, and it still adds nothing.
        /// </summary>
        [Test]
        public void RetypingASpaceIsInertAndStaysAtTheTopTier()
        {
            var engine = started(abCd());

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            Assert.IsTrue(engine.ProcessKey(' ', 7000));

            long scoreAfterFirst = engine.Score;
            Assert.AreEqual(918, scoreAfterFirst);

            Assert.IsTrue(engine.ProcessBackspace());
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[2].State);

            Assert.IsTrue(engine.ProcessKey(' ', 9000));

            var results = engine.BuildResults();

            Assert.AreEqual(scoreAfterFirst, engine.Score, "a retype earns nothing");
            Assert.AreEqual(3, results.Counts[JudgementType.Great], "and is not counted twice");
            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[2].State);
            Assert.AreEqual(0, engine.Lines[0].Cells[2].JudgedDelta!.Value, 1e-9);
            Assert.AreEqual(2, results.SyncTimeline.Count, "and adds no sample either");
        }
    }
}
