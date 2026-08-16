// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The "space to skip current word" setting (backlog 110): pressing space inside a word abandons
    /// the rest of it and the caret lands on the next word. Off by default, so every assertion about
    /// the OLD behaviour here is also the pin that the default path is untouched.
    ///
    /// <para>Backlog 167 changed what "abandons" means: the cells enter a PHANTOM state instead of
    /// being missed on the spot, one backspace re-enters the word, and re-typing them earns them for
    /// real (the judgement, the HP and the streak the skip broke). The skip still takes exactly one
    /// combo break, immediately; what moved to the seal is the miss COUNT and the osu RESULTS, which
    /// is what a cell needs to have left to be earnable at all. So a skip nobody goes back for costs
    /// exactly what it always cost, and the pins here are written as that pair: what a return buys,
    /// and what a non-return still costs.</para>
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
        private const double o_target = 3000 + 2000 / 3.0;
        private const double g_target = 3000 + 2 * 2000 / 3.0;

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

        /// <summary>The whole of "cat dog" typed in order, every cell dead on its target.</summary>
        private static void typeItAll(TypingEngine engine)
        {
            engine.ProcessKey('c', 1000);
            engine.ProcessKey('a', a_target);
            engine.ProcessKey('t', t_target);
            engine.ProcessKey(' ', 3000);
            engine.ProcessKey('d', 3000);
            engine.ProcessKey('o', o_target);
            engine.ProcessKey('g', g_target);
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
        /// The feature itself: mid-word space gives up every remaining cell of the word into the
        /// PHANTOM state and lands the caret on the next word, with the word gap judged exactly like
        /// a typed space. Nothing is resolved yet: the cells hold no miss, because the player can
        /// still come back for them.
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
            Assert.AreEqual(CellState.Correct, cells[0].State);   // 'c' keeps what it earned
            Assert.AreEqual(CellState.Abandoned, cells[1].State); // 'a' given up, not lost
            Assert.AreEqual(CellState.Abandoned, cells[2].State); // 't' given up, not lost
            Assert.AreEqual(CellState.Correct, cells[3].State);   // the word gap took the space
            Assert.AreEqual(' ', cells[3].TypedChar);

            // The caret is past the gap, on the first character of the NEXT word.
            Assert.AreEqual(4, engine.CaretIndex);
            Assert.AreEqual('d', cells[engine.CaretIndex].Expected);
            Assert.AreEqual(CellState.Untyped, cells[4].State);

            var results = engine.BuildResults();

            // NOTHING is counted for the abandoned cells yet: the miss is the cell's resolution and
            // the line has not run out of time on it. The count arrives at the seal, and only for
            // the cells the player never came back for.
            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);
            Assert.AreEqual(0, results.Counts[JudgementType.Abandoned],
                "the announced Abandoned judgement is a repaint, not a tally");

            // The gap's delta is 2600 - 3000 = -400, which the OLD millisecond ladder graded Ok
            // (inside OkEarly 600, outside GreatEarly 250). Since backlog 148 the spacebar is out of
            // the timing challenge, so the space on the gap is judged as though it landed on target:
            // Great, whatever the clock said.
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
        /// The abandonment costs AT MOST ONE combo break, taken immediately, and announces one
        /// judgement per cell it gave up so the stage repaints them. The judgements are
        /// <see cref="JudgementType.Abandoned"/>, which resolves NO osu result: that is the whole of
        /// what makes the cells earnable later, since a cell takes only its first result.
        /// </summary>
        [Test]
        public void TheSkipRaisesOneComboBreakAndOneDeferredJudgementPerAbandonedCell()
        {
            var engine = started(catDog(), spaceSkipsWord: true);

            var judged = new List<CharJudgement>();
            var abandonments = new List<AbandonedCells>();
            int comboBreaks = 0;
            engine.CharJudged += j => judged.Add(j);
            engine.ComboBroken += () => comboBreaks++;
            engine.WordAbandoned += a => abandonments.Add(a);

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            judged.Clear();

            Assert.IsTrue(engine.ProcessKey(' ', 2600));

            Assert.AreEqual(1, comboBreaks, "two cells given up, one break");
            Assert.AreEqual(3, judged.Count); // two abandoned cells, then the gap's own judgement

            Assert.AreEqual(1, judged[0].CellIndex);
            Assert.AreEqual(JudgementType.Abandoned, judged[0].Type);
            Assert.AreEqual(2600 - a_target, judged[0].Delta, 1e-9);
            Assert.AreEqual(0, judged[0].PointsAwarded);
            Assert.AreEqual(0, judged[0].ComboAfter);

            Assert.AreEqual(2, judged[1].CellIndex);
            Assert.AreEqual(JudgementType.Abandoned, judged[1].Type);
            Assert.AreEqual(2600 - t_target, judged[1].Delta, 1e-9);

            Assert.AreEqual(3, judged[2].CellIndex);
            // The gap took an untimed space (backlog 148): top tier, and the delta it is announced
            // with is the zeroed one it was judged on, not 2600 - 3000.
            Assert.AreEqual(JudgementType.Great, judged[2].Type);
            Assert.AreEqual(0, judged[2].Delta, 1e-9);
            Assert.AreEqual(300, judged[2].PointsAwarded);
            Assert.AreEqual(1, judged[2].ComboAfter);

            // The seam the hand-mirrored break and the HP drain ride on: once per skip, carrying
            // every cell it gave up, because no judgement result is left to carry either.
            Assert.AreEqual(1, abandonments.Count);
            Assert.AreEqual(0, abandonments[0].LineIndex);
            Assert.AreEqual(new[] { 1, 2 }, abandonments[0].CellIndices);
            Assert.AreEqual(2, abandonments[0].Count);

            // The judgement carries no osu result, in the mapping both live play and recalculation
            // read: that is what leaves the cell's one result available to the retype.
            Assert.IsNull(TypeBeatResultMapping.CellResult(JudgementType.Abandoned, TypoRule.Deferred));
        }

        /// <summary>
        /// A cell of the abandoned word that the player FINISHED is not given up, and since backlog
        /// 124 that group is the correct cells AND the wrong ones. A Great cannot be revoked (there
        /// is no un-apply); a typo is not a miss, so abandoning the word cannot turn it into one
        /// either. The wrong cell keeps CellState.Wrong and its deferred result, which the seal
        /// decides (as an unfixed typo, not a miss), and until then backspacing back into the word
        /// can still fix it.
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
            Assert.AreEqual(CellState.Abandoned, cells[2].State);

            // ONLY the untyped 't' is announced: the wrong 'a' is a character the player finished,
            // so it is not among the cells the skip gives up.
            Assert.AreEqual(2, judged.Count); // 't', then the word gap's own judgement
            Assert.AreEqual(2, judged[0].CellIndex);
            Assert.AreEqual(JudgementType.Abandoned, judged[0].Type);

            var results = engine.BuildResults();

            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);      // 't' is deferred, not missed
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

            Assert.AreEqual(CellState.Abandoned, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(CellState.Abandoned, engine.Lines[0].Cells[2].State);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss]);
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
        /// the line-complete state, exactly where typing that word out would have left it. The cells
        /// are still reclaimable from there (the line stays open until its own deadline), and if
        /// nobody comes back the seal resolves them.
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
            Assert.AreEqual(CellState.Abandoned, cells[3].State);
            Assert.AreEqual(CellState.Abandoned, cells[4].State);
            Assert.AreEqual(cells.Count, engine.CaretIndex);
            Assert.IsTrue(engine.IsLineComplete);

            int sealMisses = -1;
            bool sealBroke = true;
            var settled = new List<AbandonedCells>();
            engine.LineSealed += r =>
            {
                sealMisses = r.MissedCells;
                sealBroke = r.ComboBroken;
            };
            engine.AbandonSealed += a => settled.Add(a);

            engine.Update(4000);

            // The cells resolve here, as the misses they turned out to be, and they resolve exactly
            // once: announced as settled before the seal so their deferred costs can be closed out.
            Assert.AreEqual(2, sealMisses);
            Assert.AreEqual(1, settled.Count);
            Assert.AreEqual(new[] { 3, 4 }, settled[0].CellIndices);
            // ...and WITHOUT a second combo break. That break was taken at the skip, and charging it
            // again here would cost a run the player rebuilt through the rest of the line.
            Assert.IsFalse(sealBroke);
            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(CellState.Missed, cells[3].State);
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
            Assert.AreEqual(CellState.Abandoned, engine.Lines[0].Cells[1].State);
            Assert.AreEqual(CellState.Abandoned, engine.Lines[0].Cells[2].State);
            Assert.AreEqual(4, engine.CaretIndex);
            Assert.AreEqual(0, engine.BuildResults().Counts[JudgementType.Miss]);
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

        // -----------------------------------------------------------------------------------------
        // Backlog 167: the skipped word is RE-TYPEABLE.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE PROPERTY (backlog 167). From the word gap, ONE backspace re-enters the skipped word:
        /// every phantom cell it steps over goes back to Untyped and the caret lands on the last
        /// character actually typed, however many characters were given up. The step-over is
        /// transparent for the same reason the one over auto-skipped punctuation is: nothing the
        /// player put there is being erased.
        /// </summary>
        [Test]
        public void OneBackspaceFromTheGapReOpensTheWholeSkippedWord()
        {
            var engine = started(catDog(), spaceSkipsWord: true);
            var reclaims = new List<AbandonedCells>();
            engine.AbandonReclaimed += a => reclaims.Add(a);

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey(' ', 2600)); // abandon "at", the space lands on the gap

            var cells = engine.Lines[0].Cells;

            // One press to take the typed space back off the gap (an ordinary erase), and one MORE
            // to cross the whole abandoned run. The second is the one under test.
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.AreEqual(3, engine.CaretIndex);
            Assert.AreEqual(0, reclaims.Count, "the gap is a typed cell, not an abandoned one");

            Assert.IsTrue(engine.ProcessBackspace());

            Assert.AreEqual(0, engine.CaretIndex, "the caret lands on the last character actually typed");
            Assert.AreEqual(CellState.Untyped, cells[1].State);
            Assert.AreEqual(CellState.Untyped, cells[2].State);
            Assert.AreEqual(CellState.Untyped, cells[0].State, "the cell it landed on is erased, as any backspace erases it");

            Assert.AreEqual(1, reclaims.Count);
            Assert.AreEqual(new[] { 1, 2 }, reclaims[0].CellIndices);
        }

        /// <summary>
        /// And the cells really are earnable again: re-typing them produces ordinary judgements with
        /// ordinary points, not the scoring-inert retype an already-earned cell produces. That is the
        /// whole point of withholding the osu result at the skip.
        /// </summary>
        [Test]
        public void RetypingAReclaimedWordEarnsRealJudgements()
        {
            var engine = started(catDog(), spaceSkipsWord: true);

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey(' ', 2600));
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.IsTrue(engine.ProcessBackspace());

            var judged = new List<CharJudgement>();
            engine.CharJudged += j => judged.Add(j);

            Assert.IsTrue(engine.ProcessKey('c', 1000));      // inert: this cell was already earned
            Assert.IsTrue(engine.ProcessKey('a', a_target));  // the first abandoned cell, earned for real
            Assert.IsTrue(engine.ProcessKey('t', t_target));

            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(CellState.Correct, cells[1].State);
            Assert.AreEqual(CellState.Correct, cells[2].State);
            Assert.AreEqual('a', cells[1].TypedChar);

            Assert.AreEqual(3, judged.Count);
            Assert.AreEqual(JudgementType.Great, judged[1].Type);
            Assert.IsTrue(judged[1].PointsAwarded > 0, "a reclaimed cell scores; an inert retype would not");
            Assert.AreEqual(JudgementType.Great, judged[2].Type);
            Assert.IsTrue(judged[2].PointsAwarded > 0);

            var results = engine.BuildResults();

            // Four cells typed correctly (c, the gap, a, t), each counted once: the inert retype of
            // 'c' adds nothing, and the reclaimed cells are not double-counted either.
            Assert.AreEqual(4, results.Counts[JudgementType.Great]);
            Assert.AreEqual(0, results.Counts[JudgementType.Miss]);
            Assert.AreEqual(1.0, results.Accuracy);
        }

        /// <summary>
        /// The combo the skip broke comes back, on the cell the skip abandoned first, through the
        /// same snapshot machinery a corrected typo redeems (backlog 140). Typing the whole line out
        /// after a skip and a full reclaim therefore ends on exactly the combo, and the exact max
        /// combo, that typing it straight through would have.
        /// </summary>
        [Test]
        public void AReclaimedSkipGivesTheComboBackToWhereItWouldHaveBeen()
        {
            var straight = started(catDog(), spaceSkipsWord: true);
            typeItAll(straight);

            var reclaimed = started(catDog(), spaceSkipsWord: true);
            int restored = 0;
            reclaimed.ComboRestored += streak => restored += streak;

            reclaimed.ProcessKey('c', 1000);
            reclaimed.ProcessKey(' ', 2600); // skip "at": one break, the streak of 1 snapshotted on 'a'
            Assert.AreEqual(0, restored);

            reclaimed.ProcessBackspace();     // off the gap
            reclaimed.ProcessBackspace();     // through the abandoned run, onto 'c'
            reclaimed.ProcessKey('c', 1000);  // inert retype
            Assert.AreEqual(0, restored, "the erase alone restores nothing");

            reclaimed.ProcessKey('a', a_target); // the snapshot cell: the run resumes here
            Assert.AreEqual(1, restored);

            reclaimed.ProcessKey('t', t_target);
            reclaimed.ProcessKey(' ', 3000);     // inert retype of the gap
            reclaimed.ProcessKey('d', 3000);
            reclaimed.ProcessKey('o', o_target);
            reclaimed.ProcessKey('g', g_target);

            Assert.AreEqual(7, straight.Combo);
            Assert.AreEqual(7, reclaimed.Combo, "the run ends where it would have without the skip");
            Assert.AreEqual(straight.MaxCombo, reclaimed.MaxCombo);
            Assert.AreEqual(straight.BuildResults().Counts[JudgementType.Great], reclaimed.BuildResults().Counts[JudgementType.Great]);
            Assert.AreEqual(0, reclaimed.BuildResults().Counts[JudgementType.Miss]);
        }

        /// <summary>
        /// A word abandoned at the very START of a line has no keypress behind it, and the ordinary
        /// "nothing to erase" answer would make it the one unreclaimable word on the map. One
        /// backspace re-opens it and parks the caret at the head of the line.
        /// </summary>
        [Test]
        public void TheFirstWordOfALineIsReclaimableToo()
        {
            var engine = started(catDog(), spaceSkipsWord: true);
            var reclaims = new List<AbandonedCells>();
            engine.AbandonReclaimed += a => reclaims.Add(a);

            Assert.IsTrue(engine.ProcessKey(' ', 1100)); // nothing typed at all: the whole of "cat" goes
            Assert.AreEqual(4, engine.CaretIndex);

            Assert.IsTrue(engine.ProcessBackspace()); // erases the typed gap
            Assert.AreEqual(3, engine.CaretIndex);

            Assert.IsTrue(engine.ProcessBackspace(), "a reclaim IS a state change, so the press is not inert");
            Assert.AreEqual(0, engine.CaretIndex);

            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(CellState.Untyped, cells[0].State);
            Assert.AreEqual(CellState.Untyped, cells[1].State);
            Assert.AreEqual(CellState.Untyped, cells[2].State);
            Assert.AreEqual(new[] { 0, 1, 2 }, reclaims[0].CellIndices);

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.AreEqual(CellState.Correct, cells[0].State);
            Assert.IsTrue(engine.BuildResults().Score > 0);
        }

        /// <summary>
        /// With nothing abandoned behind it, a backspace at the head of a line is still inert, which
        /// is the pin that the reclaim branch did not widen "nothing to erase" for everyone else.
        /// </summary>
        [Test]
        public void BackspaceAtTheHeadOfALineIsStillInert()
        {
            var engine = started(catDog(), spaceSkipsWord: true);

            Assert.IsFalse(engine.ProcessBackspace());
            Assert.AreEqual(0, engine.CaretIndex);
        }

        /// <summary>
        /// EVERY abandoned cell leaves the phantom state exactly once, by exactly one of the two
        /// exits, and the two exits together account for every cell the skips gave up. That is the
        /// structural half of "no cell is charged twice": the deferred HP cost is charged per cell on
        /// entry and refunded per cell on exit, so a cell that could exit twice, or not at all, would
        /// be a cell whose cost was wrong.
        /// </summary>
        [Test]
        public void EveryAbandonedCellLeavesThePhantomStateExactlyOnce()
        {
            var engine = started(catDog(), spaceSkipsWord: true);

            var entered = new List<int>();
            var left = new List<int>();

            engine.WordAbandoned += a => entered.AddRange(a.CellIndices);
            engine.AbandonReclaimed += a => left.AddRange(a.CellIndices);
            engine.AbandonSealed += a => left.AddRange(a.CellIndices);

            // Skip "cat", come back for it, then skip "dog" and never return.
            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey(' ', 2600));
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey('a', a_target));
            Assert.IsTrue(engine.ProcessKey('t', t_target));
            Assert.IsTrue(engine.ProcessKey(' ', 3000));
            Assert.IsTrue(engine.ProcessKey('d', 3000));
            Assert.IsTrue(engine.ProcessKey(' ', 3800)); // abandon the rest of "dog"

            engine.Update(6000);

            Assert.AreEqual(new[] { 1, 2, 5, 6 }, entered);
            Assert.AreEqual(new[] { 1, 2, 5, 6 }, left, "one exit per cell, and no cell left behind");

            foreach (var cell in engine.Lines[0].Cells)
                Assert.AreNotEqual(CellState.Abandoned, cell.State, "no cell may still be phantom after the seal");

            Assert.AreEqual(2, engine.BuildResults().Counts[JudgementType.Miss], "only the word nobody came back for");
        }

        /// <summary>
        /// A line holds its seal open for an abandoned cell exactly as it does for an untyped one, so
        /// the reclaim window runs to the line's own deadline. Without that, a skip near the end of a
        /// line would trip the EARLY seal ("nothing left to type, do not hold the next line up") and
        /// close the window in the very grace period that exists for finishing.
        /// </summary>
        [Test]
        public void AnAbandonedCellHoldsTheLineOpenLikeAnUntypedOne()
        {
            // Vocals overrun the line boundary, so the last cell's target sits ON it and the line
            // carries a 250 ms finishing grace: "cd" spans [2000, 4000] inside a line ending at 3000,
            // putting c at 2000 and d at 3000.
            var beatmap = map(line("ab cd", 1000, 3000, 4000,
                unit("ab", 1000, 2000), unit("cd", 2000, 4000)));

            var engine = started(beatmap, spaceSkipsWord: true);

            Assert.IsTrue(engine.ProcessKey('a', 1000));
            Assert.IsTrue(engine.ProcessKey('b', 1500));
            Assert.IsTrue(engine.ProcessKey(' ', 2000));
            Assert.IsTrue(engine.ProcessKey(' ', 2100)); // abandon "cd", the rest of the line

            engine.Update(3100); // past the deadline, inside the grace

            Assert.AreEqual(0, engine.ActiveLineIndex, "the line must still be open to come back into");
            Assert.IsTrue(engine.ProcessBackspace());
            Assert.IsTrue(engine.ProcessKey(' ', 3100));
            Assert.IsTrue(engine.ProcessKey('c', 3100));

            Assert.AreEqual(CellState.Correct, engine.Lines[0].Cells[3].State);

            // ...and the grace is still bounded: past it the line seals whatever is left.
            engine.Update(3300);
            Assert.AreEqual(-1, engine.ActiveLineIndex);
            Assert.AreEqual(1, engine.BuildResults().Counts[JudgementType.Miss], "only the 'd' nobody got back to");
        }

        /// <summary>
        /// The pin the whole design rests on: a skip the player never returns to costs EXACTLY what
        /// it cost before backlog 167. The same keystrokes judged under the two era rules produce the
        /// same submitted account, character for character; all that differs is WHEN the two cells
        /// were written off, which is the only thing that could make them earnable.
        /// </summary>
        [Test]
        public void ASkipNeverReturnedToCostsWhatItAlwaysCost()
        {
            static ResultsSummary run(WordSkipRule rule)
            {
                var engine = new TypingEngine(catDog()) { SpaceSkipsWord = true, WordSkip = rule };
                engine.Update(1000);

                engine.ProcessKey('c', 1000);
                engine.ProcessKey(' ', 2600); // abandon "at", never come back
                engine.ProcessKey('d', 3000);
                engine.ProcessKey('o', o_target);
                engine.ProcessKey('g', g_target);
                engine.Update(6000);

                return engine.BuildResults();
            }

            var today = run(WordSkipRule.Reclaimable);
            var stored = run(WordSkipRule.ImmediateMiss);

            Assert.AreEqual(2, stored.Counts[JudgementType.Miss]);
            Assert.AreEqual(stored.Counts[JudgementType.Miss], today.Counts[JudgementType.Miss]);
            Assert.AreEqual(stored.Counts[JudgementType.Great], today.Counts[JudgementType.Great]);
            Assert.AreEqual(stored.Score, today.Score);
            Assert.AreEqual(stored.MaxCombo, today.MaxCombo);
            Assert.AreEqual(stored.Accuracy, today.Accuracy, 1e-12);
            Assert.AreEqual(stored.SyncPercent, today.SyncPercent, 1e-12);
            Assert.AreEqual(stored.Grade, today.Grade);
        }

        /// <summary>
        /// The era switch itself: under the pre-167 rule the cells are missed on the spot, the count
        /// lands at the keypress, and no backspace can get back into the word. Live play never
        /// selects it; only recalculation of a row played before the reclaim existed does.
        /// </summary>
        [Test]
        public void ThePreReclaimEraMissesTheWordOnTheSpot()
        {
            var engine = new TypingEngine(catDog()) { SpaceSkipsWord = true, WordSkip = WordSkipRule.ImmediateMiss };
            engine.Update(1000);

            var judged = new List<CharJudgement>();
            var abandonments = new List<AbandonedCells>();
            engine.CharJudged += j => judged.Add(j);
            engine.WordAbandoned += a => abandonments.Add(a);

            Assert.IsTrue(engine.ProcessKey('c', 1000));
            Assert.IsTrue(engine.ProcessKey(' ', 2600));

            var cells = engine.Lines[0].Cells;
            Assert.AreEqual(CellState.Missed, cells[1].State);
            Assert.AreEqual(CellState.Missed, cells[2].State);
            Assert.AreEqual(2, engine.BuildResults().Counts[JudgementType.Miss]);
            Assert.AreEqual(1, judged[1].CellIndex); // judged[0] is 'c'
            Assert.AreEqual(JudgementType.Miss, judged[1].Type);
            Assert.AreEqual(0, abandonments.Count, "with no phantom cell there is nothing to announce");

            // ...and the word is gone for good: backspace walks back over the missed cells the way
            // it always did, which is to say it stops on the first one.
            Assert.IsTrue(engine.ProcessBackspace()); // the gap
            Assert.IsTrue(engine.ProcessBackspace()); // the missed 't', erased as a typed cell would be
            Assert.AreEqual(2, engine.CaretIndex);

            Assert.AreEqual(WordSkipRule.Reclaimable, new TypingEngine(catDog()).WordSkip,
                "live play takes the reclaim, and nothing but a recalculation may select the other arm");
        }
    }
}
