// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 262: A BREAK FOLDS THE CLAIM IT DISPLACES, which is the same law backlogs 243 and 260 wrote
// for the word skip, applied to the shape those two could not see: two accidents, both fully
// corrected, cost the run NOTHING.
//
// The report is score 13383. The player was 477 combo deep and clean when they typed 'b' onto the
// 'g' of "go" (a break, claim holding 477), noticed nothing and typed the 'o' correctly (a run of 1),
// then typed 't' onto the word gap after it (a second break, standing on a streak of 1 it had really
// earned, so NOT passive under backlog 243). That break took the claim, and the overwrite arm of
// snapshotRedeemableBreak threw the 477 away. Three backspaces and a perfect retype then restored 1.
// The play finished with 0 misses, 100% completion, every keypress corrected, and a max combo of 477
// out of 894.
//
// The rule: the displacing break's claim becomes displacedStreak + brokenStreak against its OWN cell,
// with the displaced positions in front of its own, so redeeming the newest of the broken cells
// restores the whole chain. It chains transitively, and backlog 259's back-dated seal break is what
// keeps it honest: a restored increment goes back where it was EARNED, so a line sealing on cells
// nobody typed still destroys every increment at or before its last unforeseen miss.
//
// Era-gated on TypingEngine.FoldsDisplacedClaim (CONFIG frame bit 12), so every pin below is a pair:
// the live arm, and the arm every stored row was played under.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class DisplacedClaimFoldTest
    {
        #region Fixture

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = units,
            };

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
        /// THE REPORTED SHAPE'S FIXTURE: a run of cells, then a two-letter word with a gap after it,
        /// which is the "... go to ..." the report broke on. "abcde fg hi", one unit per word, on one
        /// line that runs to 60000 so nothing seals mid-test. Eleven cells: 0:a 1:b 2:c 3:d 4:e
        /// 5:' ' 6:f 7:g 8:' ' 9:h 10:i.
        /// </summary>
        private static LyricBeatmap reportMap() => map(line("abcde fg hi", 1000, 60000, 1900,
            unit("abcde", 1000, 1500),
            unit("fg", 1500, 1700),
            unit("hi", 1700, 1900)));

        /// <summary>
        /// The live stack as far as this file is concerned, minus the caret: the spacebar as the word
        /// boundary, gap typos typed through, and backlog 260's fold on the passive arm, so the ONE
        /// thing separating the two arms of every pin below is <paramref name="folds"/> (backlog 262).
        /// Fletcher is deliberately off: the rush cap has nothing to say about a claim.
        /// </summary>
        private static TypingEngine started(LyricBeatmap beatmap, bool folds)
        {
            var engine = new TypingEngine(beatmap)
            {
                SpaceSkipsWord = true,
                StrictSpaces = true,
                WrongInputOnWordGaps = true,
                LosslessSkipReclaim = true,
                FoldsDisplacedClaim = folds,
            };

            engine.Update(1000);
            Assert.That(engine.ActiveLineIndex, Is.Zero);
            return engine;
        }

        private static IReadOnlyList<TypingCell> cells(TypingEngine engine, int lineIndex = 0) => engine.Lines[lineIndex].Cells;

        /// <summary>Type cells [from, to) of line 0 correctly, each dead on its own target.</summary>
        private static void typeCells(TypingEngine engine, int from, int to)
        {
            for (int i = from; i < to; i++)
                Assert.That(engine.ProcessKey(cells(engine)[i].Expected, cells(engine)[i].TargetTime), Is.True, $"cell {i}");
        }

        /// <summary>Type a wrong char into the cell the caret is on, which must be <paramref name="cellIndex"/>.</summary>
        private static void typo(TypingEngine engine, int cellIndex)
        {
            Assert.That(engine.CaretIndex, Is.EqualTo(cellIndex));
            Assert.That(engine.ProcessKey('z', cells(engine)[cellIndex].TargetTime), Is.True);
        }

        private static void backspace(TypingEngine engine, int times)
        {
            for (int i = 0; i < times; i++)
                Assert.That(engine.ProcessBackspace(), Is.True);
        }

        /// <summary>The clean run: every cell of <see cref="reportMap"/> typed in order, on target.</summary>
        private static TypingEngine cleanRun()
        {
            var engine = started(reportMap(), folds: true);

            typeCells(engine, 0, cells(engine).Count);
            return engine;
        }

        #endregion

        /// <summary>
        /// THE REPORT, in miniature and keystroke for keystroke. Six cells clean (the report's 477),
        /// a wrong letter on the head of a word, the word's second letter typed correctly (the run is
        /// 1, and it is progress the player really made, so the next break is NOT passive), a wrong
        /// letter on the word gap, then three backspaces and the letter, letter, space retyped.
        ///
        /// <para>Under the fold the retyped head is the cell's FIRST correct press and earns 1 fresh,
        /// the second letter is an inert retype (it was judged before the backspace took it), and the
        /// space redeems the whole chain: the run stands at 9 on the gap, which is exactly the nine
        /// cells a clean run holds there, and typing on to the end of the map reaches the clean run's
        /// full combo of 11. Under the stored arm the same fingers redeem 1 and end six lower, which
        /// is the run the first break was holding.</para>
        /// </summary>
        [Test]
        public void TheReportedShapeFullyCorrectedReachesTheCleanRunsCombo()
        {
            var clean = cleanRun();

            var arms = new List<(bool folds, TypingEngine engine, List<int> restored, int atTheGap)>();

            foreach (bool folds in new[] { true, false })
            {
                var engine = started(reportMap(), folds);

                var restored = new List<int>();
                engine.ComboRestored += restored.Add;

                typeCells(engine, 0, 6); // "abcde" and its gap
                Assert.That(engine.Combo, Is.EqualTo(6), "the clean run the first break is about to take");

                typo(engine, 6);                                                  // 'z' on the head of "fg"
                Assert.That(engine.ProcessKey('g', cells(engine)[7].TargetTime), Is.True); // the second letter, correct
                Assert.That(engine.Combo, Is.EqualTo(1), "a run of 1 the player really typed");

                typo(engine, 8);                                                  // 'z' on the word gap: the displacing break
                Assert.That(engine.Combo, Is.Zero);

                backspace(engine, 3);
                Assert.That(engine.CaretIndex, Is.EqualTo(6), "the gap typo cleared in place, then 'g', then the spoiled head");

                typeCells(engine, 6, 9); // the head (fresh), the second letter (inert), the gap (the redemption)

                int atTheGap = engine.Combo;

                typeCells(engine, 9, cells(engine).Count);
                arms.Add((folds, engine, restored, atTheGap));
            }

            var live = arms[0];
            var stored = arms[1];

            Assert.Multiple(() =>
            {
                Assert.That(clean.MaxCombo, Is.EqualTo(11), "eleven cells, eleven increments");

                Assert.That(live.restored, Is.EqualTo(new[] { 7 }), "the run of 6 the first break took, plus the 1 the second one did");
                Assert.That(live.atTheGap, Is.EqualTo(9), "the nine cells a clean run holds on that gap");
                Assert.That(live.engine.Combo, Is.EqualTo(11), "two accidents, both fully corrected, cost the run nothing");
                Assert.That(live.engine.MaxCombo, Is.EqualTo(11));

                Assert.That(stored.restored, Is.EqualTo(new[] { 1 }), "the reported 1: only what the second break itself took");
                Assert.That(stored.atTheGap, Is.EqualTo(3));
                Assert.That(stored.engine.Combo, Is.EqualTo(5), "six lower, which is the run the first break was holding");
                Assert.That(stored.engine.MaxCombo, Is.EqualTo(6), "and its maximum is that same run, never reached again");

                // Everything the axis does not reach: the same cells, the same two mistypes, nothing
                // missed. Only the combo moves.
                foreach (var arm in arms)
                {
                    Assert.That(arm.engine.Mistypes, Is.EqualTo(2));
                    Assert.That(arm.engine.BuildResults().Counts[JudgementType.Miss], Is.Zero);

                    foreach (var cell in cells(arm.engine))
                        Assert.That(cell.State, Is.EqualTo(CellState.Correct));
                }
            });
        }

        /// <summary>
        /// THE CHAIN. Three breaks with a correct character between each pair, so every one of them
        /// owns a streak and takes the claim: the second folds the first's, the third folds that pair,
        /// and the one redemption on the third cell puts back all of it. Typing the rest of the line
        /// out then reaches the clean run's full combo, which is the law again with three accidents in
        /// it rather than two.
        ///
        /// <para>The stored arm ends three lower, which is exactly the two streaks the middle break
        /// dropped (2 from the first break, 1 from the second).</para>
        /// </summary>
        [Test]
        public void ThreeBreaksFoldTransitivelyIntoOneClaim()
        {
            var arms = new List<(TypingEngine engine, List<int> restored)>();

            foreach (bool folds in new[] { true, false })
            {
                var engine = started(reportMap(), folds);

                var restored = new List<int>();
                engine.ComboRestored += restored.Add;

                typeCells(engine, 0, 2);  // a, b
                typo(engine, 2);          // break 1: claims cell 2 for a streak of 2
                typeCells(engine, 3, 4);  // 'd', a run of 1 the player earned
                typo(engine, 4);          // break 2: owns that 1, and folds break 1 in
                typeCells(engine, 5, 6);  // the word gap, a run of 1 again
                typo(engine, 6);          // break 3: owns that 1, and folds the pair in

                Assert.That(engine.Combo, Is.Zero);

                backspace(engine, 5);
                Assert.That(engine.CaretIndex, Is.EqualTo(2), "back to the first spoiled cell");

                typeCells(engine, 2, cells(engine).Count);
                arms.Add((engine, restored));
            }

            var live = arms[0];
            var stored = arms[1];

            Assert.Multiple(() =>
            {
                Assert.That(live.restored, Is.EqualTo(new[] { 4 }), "one redemption, worth 2 + 1 + 1");
                Assert.That(live.engine.Combo, Is.EqualTo(11), "the clean run's full combo, three accidents notwithstanding");
                Assert.That(live.engine.MaxCombo, Is.EqualTo(11), "and it never exceeds the clean run either");

                Assert.That(stored.restored, Is.EqualTo(new[] { 1 }), "only what the third break itself took");
                Assert.That(stored.engine.Combo, Is.EqualTo(8), "three lower: the two claims the chain dropped");

                foreach (var arm in arms)
                    Assert.That(arm.engine.Mistypes, Is.EqualTo(3));
            });
        }

        /// <summary>
        /// A WORD SKIP AS THE DISPLACING BREAK. The two redeemable breaks funnel through the one
        /// snapshot site, so a skip that owns a streak displaces an older typo's claim exactly as a
        /// typo does, and folds it exactly as a typo does. Here a typo claims cell 2 for a run of 2,
        /// one character is typed (a run of 1 the player really made), and the space that gives up the
        /// rest of the word breaks THAT: the skip folds the typo's claim into its own against the
        /// abandoned cell.
        ///
        /// <para>The other half of the pin is backlog 243's credit, which the fold must not lose: the
        /// same space is then judged on the word gap and rebuilds the run to 1, that 1 is recorded on
        /// the FOLDED claim, and the typo landing on the next character is therefore passive and
        /// leaves the claim where it is (folding its own spent 1 in under backlog 260). The
        /// discriminator is the combo the retyped abandoned cell produces: 6 with the claim still on
        /// it, 2 if the passive typo had been allowed to walk off with it.</para>
        /// </summary>
        [Test]
        public void AWordSkipDisplacingATypoFoldsItAndKeepsTheClaimsOwnPressCredit()
        {
            var arms = new List<(TypingEngine engine, List<int> restored, int atTheAbandonedCell)>();

            foreach (bool folds in new[] { true, false })
            {
                var engine = started(reportMap(), folds);

                var restored = new List<int>();
                engine.ComboRestored += restored.Add;

                typeCells(engine, 0, 2);  // a, b
                typo(engine, 2);          // claims cell 2 for a streak of 2
                typeCells(engine, 3, 4);  // 'd': a run of 1 the player earned

                // The space, struck where 'e' was owed: it gives up 'e', breaks that run of 1 (so it
                // owns the claim and folds the typo's in), and is then judged on the gap at cell 5,
                // which rebuilds the run to 1 and records that 1 on the claim it is holding.
                Assert.That(engine.ProcessKey(' ', cells(engine)[4].TargetTime), Is.True);
                Assert.That(engine.CaretIndex, Is.EqualTo(6), "past the gap the skip parked on");
                Assert.That(engine.Combo, Is.EqualTo(1), "the skip's own space, on the gap");
                Assert.That(cells(engine)[4].State, Is.EqualTo(CellState.Abandoned));

                typo(engine, 6);          // passive: it stands on nothing but the skip's own press
                Assert.That(engine.Combo, Is.Zero);

                backspace(engine, 4);
                Assert.That(engine.CaretIndex, Is.EqualTo(2), "the typo, the gap, the step over the abandoned cell, the spoiled cell 2");

                typeCells(engine, 2, 5);  // cell 2 fresh, 'd' inert, then the abandoned cell: the redemption

                int atTheAbandonedCell = engine.Combo;

                typeCells(engine, 5, cells(engine).Count);
                arms.Add((engine, restored, atTheAbandonedCell));
            }

            var live = arms[0];
            var stored = arms[1];

            Assert.Multiple(() =>
            {
                Assert.That(live.restored, Is.EqualTo(new[] { 4 }), "the typo's 2, the skip's own 1, and the passive typo's spent 1");
                Assert.That(live.atTheAbandonedCell, Is.EqualTo(6), "the claim was still on the abandoned cell, so the credit survived the fold");
                Assert.That(live.engine.Combo, Is.EqualTo(11), "the clean run's full combo");
                Assert.That(live.engine.MaxCombo, Is.EqualTo(11));

                Assert.That(stored.restored, Is.EqualTo(new[] { 2 }), "the skip's own break, plus the passive typo's spent 1");
                Assert.That(stored.atTheAbandonedCell, Is.EqualTo(4));
                Assert.That(stored.engine.Combo, Is.EqualTo(9), "two lower: the claim the skip displaced");

                foreach (var arm in arms)
                {
                    Assert.That(arm.engine.Mistypes, Is.EqualTo(2));
                    Assert.That(arm.engine.BuildResults().Counts[JudgementType.Miss], Is.Zero, "the abandoned cell was reclaimed and typed");
                }
            });
        }

        #region Across a line boundary, where the seal is the clawback

        /// <summary>
        /// TWO LINES, and the unpinned caret that makes an earlier line's claim outlive the player's
        /// arrival on the next one. L0 "abcd" [1000, 4000), unit [1000, 3000]: a = 1000, b = 1500,
        /// c = 2000, d = 2500. L1 "efgh" [4000, 8000), unit [4000, 6000]: e = 4000, f = 4500,
        /// g = 5000, h = 5500. Giving L0 up holds its seal for the drag grace, so it seals at
        /// 4000 + 1500 = 5500, by which time the player has typed three cells of L1.
        /// </summary>
        private static LyricBeatmap twoLineMap() => map(
            line("abcd", 1000, 4000, 3000, unit("abcd", 1000, 3000)),
            line("efgh", 4000, 8000, 6000, unit("efgh", 4000, 6000)));

        /// <summary>The live caret (backlog 208/218) with the seal back-dated (backlog 259), which is
        /// the stack the cross-line shapes below only exist under.</summary>
        private static TypingEngine startedOnTwoLines(bool folds)
        {
            var engine = new TypingEngine(twoLineMap())
            {
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
                BackDatedSealBreak = true,
                FoldsDisplacedClaim = folds,
            };

            engine.Update(1000);
            Assert.That(engine.ActiveLineIndex, Is.Zero);
            return engine;
        }

        /// <summary>
        /// The shared script: two cells of L0 typed, a typo on the third (which claims a streak of 2),
        /// the rest of L0 given up with Enter, then two cells of L1 typed and a typo on its third,
        /// which owns a streak of 2 of its own and therefore takes the claim, folding L0's into it.
        /// Leaves the caret one past the L1 typo, with L0 still unsealed and one cell nobody typed.
        /// </summary>
        private static TypingEngine foldedAcrossTheLineBoundary(bool folds, List<int> restored)
        {
            var engine = startedOnTwoLines(folds);

            engine.ComboRestored += restored.Add;

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 1500), Is.True);
            Assert.That(engine.Combo, Is.EqualTo(2));

            Assert.That(engine.ProcessKey('z', 2000), Is.True, "the typo on 'c': it claims the run of 2");
            Assert.That(engine.ProcessEnter(2500), Is.True, "'d' is given up, unjudged");

            for (double now = 2500; now < 4000; now += 100)
                engine.Update(now);

            Assert.That(engine.ActiveLineIndex, Is.EqualTo(1), "the caret took L1 when its entry bound opened");

            engine.Update(4000);
            Assert.That(engine.ProcessKey('e', 4000), Is.True);
            engine.Update(4500);
            Assert.That(engine.ProcessKey('f', 4500), Is.True);
            Assert.That(engine.Combo, Is.EqualTo(2), "a run of 2 built on L1");

            engine.Update(5000);
            Assert.That(engine.ProcessKey('z', 5000), Is.True, "the typo on 'g': it owns that run of 2 and takes the claim");
            Assert.That(engine.Combo, Is.Zero);

            return engine;
        }

        /// <summary>
        /// The seal's UNCONDITIONAL claim discard is untouched by the fold. L0 is still holding a cell
        /// nobody typed when the player folds its claim into a break on L1, and when the drag grace
        /// runs out that seal breaks the run and ends every outstanding claim, folded or not. Coming
        /// back to the L1 cell afterwards therefore restores nothing, and both arms agree cell for
        /// cell: what the fold carries forward, the seal is still entitled to take.
        /// </summary>
        [Test]
        public void AFoldedClaimIsStillEndedByTheSealOfTheLineItCameFrom()
        {
            var arms = new List<(TypingEngine engine, List<int> restored)>();

            foreach (bool folds in new[] { true, false })
            {
                var restored = new List<int>();
                var engine = foldedAcrossTheLineBoundary(folds, restored);

                engine.Update(5500); // L0's drag cutoff: 'd' is missed, and the seal takes the claim

                Assert.That(engine.Lines[0].Cells[3].State, Is.EqualTo(CellState.Missed));

                Assert.That(engine.ProcessBackspace(), Is.True);
                Assert.That(engine.ProcessKey('g', 5000), Is.True, "the fix, after the seal");
                Assert.That(engine.ProcessKey('h', 5500), Is.True);

                arms.Add((engine, restored));
            }

            Assert.Multiple(() =>
            {
                foreach (var arm in arms)
                {
                    Assert.That(arm.restored, Is.Empty, "the seal ended the claim, whatever it was carrying");
                    Assert.That(arm.engine.Combo, Is.EqualTo(2), "the two cells typed after it, and nothing given back");
                }

                Assert.That(arms[0].engine.MaxCombo, Is.EqualTo(arms[1].engine.MaxCombo), "the eras agree once the seal has taken the claim");
            });
        }

        /// <summary>
        /// THE CLAWBACK, and what makes the fold honest. Redeem the L1 cell BEFORE L0's drag cutoff
        /// and the whole chain comes back, L0's two increments included, even though the cell that
        /// spoiled L0 was never fixed. Then L0 seals on the cell nobody typed, and backlog 259's
        /// back-dated break destroys every increment earned at or before it: the two restored L0 ones
        /// die and the three earned on L1 survive.
        ///
        /// <para>So the fold buys a bigger maximum (5 against 4) and both arms come out of the seal
        /// holding the same 3, which is the statement that folding cannot smuggle an increment past a
        /// line that never earned it. The restored positions going back WHERE THEY WERE EARNED is the
        /// whole mechanism: dated at the cell that redeemed them they would all have been on L1 and
        /// all have survived.</para>
        /// </summary>
        [Test]
        public void TheSealsBackDatedBreakClawsBackAFoldRedeemedBeforeIt()
        {
            var arms = new List<(TypingEngine engine, List<int> restored, int beforeTheSeal, int max)>();

            foreach (bool folds in new[] { true, false })
            {
                var restored = new List<int>();
                var engine = foldedAcrossTheLineBoundary(folds, restored);

                Assert.That(engine.ProcessBackspace(), Is.True);
                Assert.That(engine.ProcessKey('g', 5000), Is.True, "the fix, before the seal: it redeems the claim");

                int beforeTheSeal = engine.Combo;
                int max = engine.MaxCombo;

                engine.Update(5500); // L0's drag cutoff: 'd' is missed, and the break is back-dated to it

                arms.Add((engine, restored, beforeTheSeal, max));
            }

            var live = arms[0];
            var stored = arms[1];

            Assert.Multiple(() =>
            {
                Assert.That(live.restored, Is.EqualTo(new[] { 4 }), "L0's run of 2 folded into L1's own 2");
                Assert.That(live.beforeTheSeal, Is.EqualTo(5), "4 restored plus the fix itself");
                Assert.That(live.max, Is.EqualTo(5));

                Assert.That(stored.restored, Is.EqualTo(new[] { 2 }), "only the run the L1 break took");
                Assert.That(stored.beforeTheSeal, Is.EqualTo(3));
                Assert.That(stored.max, Is.EqualTo(3));

                // The seal is dated at L0's cell 3, so everything earned on L0 dies and everything
                // earned on L1 lives, under both arms: the fold's extra two increments were exactly
                // the ones the seal was entitled to take.
                Assert.That(live.engine.Combo, Is.EqualTo(3), "e, f and the fix, all earned past the missed cell");
                Assert.That(stored.engine.Combo, Is.EqualTo(3), "the same three, from a claim that never held L0's");

                Assert.That(live.engine.BuildResults().Counts[JudgementType.Miss], Is.EqualTo(1));
                Assert.That(stored.engine.BuildResults().Counts[JudgementType.Miss], Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The same two lines with L1's cells packed into the head of its window, so a whole sequence
        /// of breaks and a redemption fit between the caret's arrival on L1 and L0's drag cutoff at
        /// 5500. L0 "abcd" [1000, 4000), given up whole. L1 "efghij" [4000, 10000), unit
        /// [4000, 5200]: e = 4000, f = 4240, g = 4480, h = 4720, i = 4960, j = 5200.
        /// </summary>
        private static LyricBeatmap denseSecondLineMap() => map(
            line("abcd", 1000, 4000, 3000, unit("abcd", 1000, 3000)),
            line("efghij", 4000, 10000, 5200, unit("efghij", 4000, 5200)));

        /// <summary>
        /// THE LEDGER INVARIANT (backlog 259): one entry per unit of combo, through the fold. The
        /// streak and the positions have to move together or a later back-dated seal answers "how much
        /// of this run did you earn past my misses" with the wrong list.
        ///
        /// <para>The whole of L0 is given up, so it seals on four cells nobody typed, and every
        /// increment in the run is earned on L1, strictly past all of them. The seal is therefore
        /// entitled to destroy NOTHING, and the run has to come out of it exactly as it went in. It
        /// only does if the folded claim restored one position per unit of streak: hand back four
        /// units of combo with two positions behind them and the seal, which rebuilds combo from the
        /// surviving positions, silently halves the run.</para>
        /// </summary>
        [Test]
        public void TheFoldedClaimsStreakAndPositionsStayInStep()
        {
            var arms = new List<(TypingEngine engine, List<int> restored, int beforeTheSeal)>();

            foreach (bool folds in new[] { true, false })
            {
                var engine = new TypingEngine(denseSecondLineMap())
                {
                    FletcherEnabled = true,
                    FlexibleLineSnap = true,
                    BoundedRush = true,
                    BackDatedSealBreak = true,
                    FoldsDisplacedClaim = folds,
                };

                var restored = new List<int>();
                engine.ComboRestored += restored.Add;

                engine.Update(1000);
                Assert.That(engine.ProcessEnter(1000), Is.True, "the whole of L0 is given up, unjudged");

                for (double now = 1000; now < 4000; now += 100)
                    engine.Update(now);

                Assert.That(engine.ActiveLineIndex, Is.EqualTo(1));

                engine.Update(4000);
                Assert.That(engine.ProcessKey('e', 4000), Is.True);
                Assert.That(engine.ProcessKey('f', 4240), Is.True);
                Assert.That(engine.Combo, Is.EqualTo(2), "a run of 2, earned at (1, 0) and (1, 1)");

                Assert.That(engine.ProcessKey('z', 4480), Is.True, "the typo on 'g': it claims that run of 2");
                Assert.That(engine.ProcessKey('h', 4720), Is.True, "a run of 1, earned at (1, 3)");
                Assert.That(engine.ProcessKey('z', 4960), Is.True, "the typo on 'i': it owns that 1 and folds the claim in");

                Assert.That(engine.ProcessBackspace(), Is.True);
                Assert.That(engine.ProcessKey('i', 4960), Is.True, "the fix, which redeems the claim");

                int beforeTheSeal = engine.Combo;

                engine.Update(5500); // L0's drag cutoff: four misses, and the break is dated at (0, 3)

                Assert.That(engine.Lines[0].Cells[3].State, Is.EqualTo(CellState.Missed));
                arms.Add((engine, restored, beforeTheSeal));
            }

            var live = arms[0];
            var stored = arms[1];

            Assert.Multiple(() =>
            {
                Assert.That(live.restored, Is.EqualTo(new[] { 3 }), "the run of 2 the first typo took, plus the 1 the second one did");
                Assert.That(live.beforeTheSeal, Is.EqualTo(4), "3 restored plus the fix itself");
                Assert.That(live.engine.Combo, Is.EqualTo(4), "every increment was earned past L0's misses, so the seal destroys none of them");

                Assert.That(stored.restored, Is.EqualTo(new[] { 1 }));
                Assert.That(stored.beforeTheSeal, Is.EqualTo(2));
                Assert.That(stored.engine.Combo, Is.EqualTo(2), "the same statement one claim lighter");

                foreach (var arm in arms)
                    Assert.That(arm.engine.BuildResults().Counts[JudgementType.Miss], Is.EqualTo(4), "the whole of L0");
            });
        }

        #endregion
    }
}
