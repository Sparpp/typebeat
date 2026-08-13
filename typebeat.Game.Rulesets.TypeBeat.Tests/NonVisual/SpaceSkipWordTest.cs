// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The "space to skip current word" setting (backlog 110): pressing space inside a word abandons
    /// the rest of it, every character of it the player never resolved counts as a miss, and the
    /// caret lands on the next word. Off by default, so every assertion about the OLD behaviour here
    /// is also the pin that the default path is untouched.
    /// </summary>
    [TestFixture]
    public class SpaceSkipWordTest
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
        /// "cat dog", active [1000, 6000), SingEnd 5000, units "cat" [1000, 3000] and "dog" [3000, 5000].
        /// Cells: c = 1000, a = 1000 + 1*2000/3 = 1666.67, t = 1000 + 2*2000/3 = 2333.33,
        /// ' ' = 3000 (unit0 end), d = 3000, o = 3666.67, g = 4333.33. A three-letter word is the
        /// shortest one that can lose MORE than one cell to a skip.
        /// </summary>
        private static LyricBeatmap catDog() => map(line("cat dog", 1000, 6000, 5000,
            unit("cat", 1000, 3000), unit("dog", 3000, 5000)));

        private const double a_target = 1000 + 2000 / 3.0;
        private const double t_target = 1000 + 2 * 2000 / 3.0;

        /// <summary>"ab cd" (the workhorse line): a = 1000, b = 1500, ' ' = 2000, c = 2000, d = 2500.</summary>
        private static LyricBeatmap abCd() => map(line("ab cd", 1000, 4000, 3000,
            unit("ab", 1000, 2000), unit("cd", 2000, 3000)));

        private static TypingEngine started(LyricBeatmap beatmap, bool spaceSkipsWord)
        {
            var engine = new TypingEngine(beatmap) { SpaceSkipsWord = spaceSkipsWord };
            engine.Update(1000);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            return engine;
        }

        #endregion

        [Test]
        public void TheSettingIsOffOnAFreshEngine()
        {
            Assert.IsFalse(new TypingEngine(catDog()).SpaceSkipsWord,
                "space-skip must be opt-in: it changes how a keypress is judged.");
        }

        /// <summary>
        /// With the setting off, a space pressed on a lyric character is REJECTED exactly as it
        /// always was, in every input model: nothing enters the cell and the caret does not move.
        /// </summary>
        [Test]
        public void SpaceInsideAWordIsStillRejectedWhenTheSettingIsOff()
        {
            var engine = started(catDog(), spaceSkipsWord: false);
            char? rejected = null;
            engine.WrongKeyRejected += c => rejected = c;

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey(' ', 2600));

            Assert.AreEqual(' ', rejected);
            Assert.AreEqual(1, engine.CaretIndex); // caret unmoved, still on 'a'
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[2].State);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss]);
            Assert.AreEqual(1, engine.Mistypes); // the rejected space is a mistype
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys);
        }

        /// <summary>
        /// The feature itself: mid-word space gives up every remaining cell of the word as a miss and
        /// lands the caret on the next word, with the word gap judged exactly like a typed space.
        /// </summary>
        [Test]
        public void SpaceInsideAWordAbandonsTheRestOfItAndLandsOnTheNextWord()
        {
            var engine = started(catDog(), spaceSkipsWord: true);
            char? rejected = null;
            engine.WrongKeyRejected += c => rejected = c;

            Assert.IsTrue(engine.ProcessKey('c', 1000)); // delta 0 => Great, 300 * (1 + 0/50) = 300
            Assert.IsTrue(engine.ProcessKey(' ', 2600)); // caret is on 'a': abandon "at"

            Assert.IsNull(rejected, "the space is consumed by the skip, not rejected");
            Assert.AreEqual(0, engine.Mistypes, "abandoning a word is a deliberate action, not a mistype");

            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(CellState.Correct, cells[0].State); // 'c' keeps what it earned
            Assert.AreEqual(CellState.Missed, cells[1].State);  // 'a' given up
            Assert.AreEqual(CellState.Missed, cells[2].State);  // 't' given up
            Assert.AreEqual(CellState.Correct, cells[3].State); // the word gap took the space
            Assert.AreEqual(' ', cells[3].TypedChar);

            // The caret is past the gap, on the first character of the NEXT word.
            Assert.AreEqual(4, engine.CaretIndex);
            Assert.AreEqual('d', cells[engine.CaretIndex].Expected);
            Assert.AreEqual(CellState.Untyped, cells[4].State);

            var results = engine.BuildResults();

            Assert.AreEqual(2, results.Counts[JudgementType.Miss]);
            // The gap's delta is 2600 - 3000 = -400, which the OLD millisecond ladder graded Ok
            // (inside OkEarly 600, outside GreatEarly 250). Since backlog 148 the spacebar is out of
            // the timing challenge, so the space on the gap is judged as though it landed on target:
            // Great, whatever the clock said. The skip's own cost is untouched, the two abandoned
            // cells and the one combo break below.
            Assert.AreEqual(0, results.Counts[JudgementType.Ok]);
            Assert.AreEqual(2, results.Counts[JudgementType.Great]);
            // 300 ('c') + 300 (the space, at combo 0 after the break => x1.00).
            Assert.AreEqual(600, results.Score);
            // The skip itself is not a keypress, so both presses that WERE judged were correct.
            Assert.AreEqual(1.0, results.Accuracy);
            Assert.AreEqual(1, results.MaxCombo); // 'c' made it 1, the skip broke it, the space rebuilt it to 1

            // Typing carries straight on from the next word.
            engine.Update(3000);
            Assert.IsTrue(engine.ProcessKey('d', 3000));
            Assert.AreEqual(CellState.Correct, cells[4].State);
        }

        /// <summary>
        /// The abandonment costs AT MOST ONE combo break (the rule a sealed line's misses follow) and
        /// announces one Miss judgement per cell it gave up, so the stage repaints them and their
        /// scoring drawables take their Miss now instead of at seal time.
        /// </summary>
        [Test]
        public void TheSkipRaisesOneComboBreakAndOneMissJudgementPerAbandonedCell()
        {
            var engine = started(catDog(), spaceSkipsWord: true);

            var judged = new List<CharJudgement>();
            int comboBreaks = 0;
            engine.CharJudged += j => judged.Add(j);
            engine.ComboBroken += () => comboBreaks++;

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            judged.Clear();

            Assert.IsTrue(engine.ProcessKey(' ', 2600));

            Assert.AreEqual(1, comboBreaks, "two cells given up, one break");
            Assert.AreEqual(3, judged.Count); // two misses, then the gap's own judgement

            Assert.AreEqual(1, judged[0].CellIndex);
            Assert.AreEqual(JudgementType.Miss, judged[0].Type);
            Assert.AreEqual(2600 - a_target, judged[0].Delta, 1e-9);
            Assert.AreEqual(0, judged[0].PointsAwarded);
            Assert.AreEqual(0, judged[0].ComboAfter);

            Assert.AreEqual(2, judged[1].CellIndex);
            Assert.AreEqual(JudgementType.Miss, judged[1].Type);
            Assert.AreEqual(2600 - t_target, judged[1].Delta, 1e-9);

            Assert.AreEqual(3, judged[2].CellIndex);
            // The gap took an untimed space (backlog 148): top tier, and the delta it is announced
            // with is the zeroed one it was judged on, not 2600 - 3000.
            Assert.AreEqual(JudgementType.Great, judged[2].Type);
            Assert.AreEqual(0, judged[2].Delta, 1e-9);
            Assert.AreEqual(300, judged[2].PointsAwarded);
            Assert.AreEqual(1, judged[2].ComboAfter);
        }

        /// <summary>
        /// A cell of the abandoned word that the player FINISHED is not given up, and since backlog
        /// 124 that group is the correct cells AND the wrong ones. A Great cannot be revoked (there
        /// is no un-apply); a typo is not a miss, so abandoning the word cannot turn it into one
        /// either. The wrong cell keeps CellState.Wrong and its deferred result, which the seal
        /// decides (as an unfixed typo, not a miss), and until then backspacing back into the word
        /// can still fix it. Backlog 109 had given it up here, because a Miss was then the only fate
        /// an unfixed typo had.
        /// </summary>
        [Test]
        public void AWrongCharInTheAbandonedWordIsNotGivenUp()
        {
            var engine = started(catDog(), spaceSkipsWord: true);

            var judged = new List<CharJudgement>();
            engine.CharJudged += j => judged.Add(j);

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey('x', a_target)); // typed through (default model) onto 'a'
            Assert.AreEqual(CellState.Wrong, engine.Lines[0].Cells[1].State);

            judged.Clear();
            Assert.IsTrue(engine.ProcessKey(' ', 2600)); // caret is on 't': abandon "at"

            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(CellState.Correct, cells[0].State, "'c' keeps the Great it earned");
            Assert.AreEqual(CellState.Wrong, cells[1].State, "the wrong char keeps its red");
            Assert.AreEqual('x', cells[1].TypedChar);
            Assert.AreEqual(CellState.Missed, cells[2].State);

            // ONLY the untyped 't' is announced: the wrong 'a' is a character the player finished,
            // so it is not among the cells the skip gives up.
            Assert.AreEqual(2, judged.Count); // 't', then the word gap's own judgement
            Assert.AreEqual(2, judged[0].CellIndex);
            Assert.AreEqual(JudgementType.Miss, judged[0].Type);

            var results = engine.BuildResults();

            Assert.AreEqual(1, results.Counts[JudgementType.Miss]);      // the untyped 't', and only it
            Assert.AreEqual(1, results.Counts[JudgementType.WrongChar]); // 'x' still counted once
            Assert.AreEqual(1, engine.Mistypes);
        }

        /// <summary>
        /// The other side of the same rule: a cell the player typed correctly and then BACKSPACED is
        /// back to Untyped, so the skip gives it up like any other unresolved cell. Its
        /// FirstCorrectDelta survives (the anti-farming record), and on the drawable side its Great
        /// stands, which is exactly what a line SEAL does with the same cell today.
        /// </summary>
        [Test]
        public void ACellTypedCorrectlyAndThenBackspacedIsGivenUpLikeAnyUntypedCell()
        {
            var engine = started(catDog(), spaceSkipsWord: true);

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey('a', a_target));
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(CellState.Untyped, engine.Lines[0].Cells[1].State);

            Assert.IsTrue(engine.ProcessKey(' ', 2600));

            Assert.AreEqual(CellState.Missed, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(CellState.Missed, engine.Lines[0].Cells[2].State);
            Assert.AreEqual(2, engine.BuildResults().Counts[JudgementType.Miss]);
        }

        /// <summary>
        /// A space pressed ON the word gap keeps its ordinary meaning, setting or no setting: it is
        /// the character the cell expects, so it is simply typed.
        /// </summary>
        [Test]
        public void SpaceOnAWordGapIsUnchanged()
        {
            var engine = started(abCd(), spaceSkipsWord: true);

            Assert.IsTrue(engine.ProcessKey('a', 1000)); // Great, 300
            Assert.IsTrue(engine.ProcessKey('b', 1500)); // Great, 300 * 1.02 = 306
            Assert.IsTrue(engine.ProcessKey(' ', 2000)); // ON the gap: Great, 300 * 1.04 = 312

            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(CellState.Correct, cells[2].State);
            Assert.AreEqual(3, engine.CaretIndex);

            var results = engine.BuildResults();

            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);
            Assert.AreEqual(3, results.Counts[JudgementType.Great]);
            Assert.AreEqual(918, results.Score);
            Assert.AreEqual(3, results.MaxCombo); // never broken
        }

        /// <summary>
        /// The last word of a line has no gap after it, so the caret lands at the end of the line:
        /// the line-complete state, exactly where typing that word out would have left it.
        /// </summary>
        [Test]
        public void SkippingTheLastWordOfALineCompletesTheLine()
        {
            var engine = started(abCd(), spaceSkipsWord: true);

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            Assert.IsTrue(engine.ProcessKey(' ', 2000));

            // Caret on 'c', the first char of the last word: nothing of it has been typed, so the
            // whole word goes.
            Assert.IsTrue(engine.ProcessKey(' ', 2100));

            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(CellState.Missed, cells[3].State);
            Assert.AreEqual(CellState.Missed, cells[4].State);
            Assert.AreEqual(cells.Count, engine.CaretIndex);
            Assert.IsTrue(engine.IsLineComplete);

            int sealMisses = -1;
            bool sealBroke = true;
            engine.LineSealed += r =>
            {
                sealMisses = r.MissedCells;
                sealBroke = r.ComboBroken;
            };

            engine.Update(4000);

            // Nothing is left Untyped, so the seal adds no second miss and no second combo break for
            // the cells the skip already accounted for.
            Assert.AreEqual(0, sealMisses);
            Assert.IsFalse(sealBroke);
            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(2, engine.BuildResults().Counts[JudgementType.Miss]);
        }

        /// <summary>
        /// Gatekeeper and space-skip are orthogonal: one decides what happens to a wrong LETTER, the
        /// other lets you abandon a WORD, so a Gatekeeper player (who cannot type past a character
        /// they keep missing) is if anything the one who needs the escape hatch most.
        /// </summary>
        [Test]
        public void TheSkipWorksUnderGatekeeperToo()
        {
            var engine = new TypingEngine(catDog()) { SpaceSkipsWord = true, AllowWrongInput = false };
            engine.Update(1000);

            char? rejected = null;
            engine.WrongKeyRejected += c => rejected = c;

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey('q', 1600)); // wrong letter: rejected, caret holds on 'a'
            Assert.AreEqual('q', rejected);
            Assert.AreEqual(1, engine.CaretIndex);
            Assert.AreEqual(1, engine.ConsecutiveWrongKeys);

            rejected = null;
            Assert.IsTrue(engine.ProcessKey(' ', 2600)); // ...and space gets them out of it

            Assert.IsNull(rejected);
            Assert.AreEqual(CellState.Missed, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(CellState.Missed, engine.Lines[0].Cells[2].State);
            Assert.AreEqual(4, engine.CaretIndex);
            Assert.AreEqual(2, engine.BuildResults().Counts[JudgementType.Miss]);
            // The gap really was typed, so it resets the mash-fail streak exactly as any accepted
            // character does; the skip itself never touches it.
            Assert.AreEqual(0, engine.ConsecutiveWrongKeys);
        }

        /// <summary>
        /// Mashing (Relax) rewrites every press into the character the caret expects before the skip
        /// is reached, so with both on there is no word left to abandon. Stated as a test because the
        /// combination is reachable and its outcome should not be an accident.
        /// </summary>
        [Test]
        public void MashingLeavesNothingToSkip()
        {
            var engine = started(catDog(), spaceSkipsWord: true);
            engine.MashingEnabled = true;

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey(' ', a_target)); // judged as 'a', the expected char

            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[1].State);
            Assert.AreEqual('a', engine.Lines[0].Cells[1].TypedChar);
            Assert.AreEqual(2, engine.CaretIndex);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss]);
        }
    }
}
