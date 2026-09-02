// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 140: correcting a typo RESUMES the combo its wrong keypress broke.
//
// Typos are counted as EVENTS (one per wrong keypress, spent the instant the key lands and never
// refundable), so without this the only thing going back to fix a cell would buy is the accuracy
// and completion the cell itself is worth: the combo would be gone either way, and leaving the
// typo sitting there would cost nothing extra. These pins cover the rule and its three edges: the
// plain fix, the intervening break that ends the claim, and repeated wrong/fix cycles on one cell.
//
// TypeBeatReplayScorerTest.AFixedTypoResumesTheStreakOnlyUnderTheLiveRule is the fourth: the same
// keystrokes re-derived under both eras, through the real score processor.
//
// The last three PAIRS are backlog 176, which traced a player report that a corrected typo did not
// restore. The report was accurate: two wrong keys landed on ADJACENT cells 447 combo deep, and the
// second one rewrote the snapshot the first had taken, with the combo AT THAT MOMENT, which the
// first wrong key had already zeroed. The player then corrected both cells and got nothing back.
//
// The rule that fixes it is that a break takes ownership of the streak only if it HAS a streak to
// own: a break landing while the run is already at zero costs nothing, so it leaves an outstanding
// claim alone instead of replacing it with an empty one. The three shapes that reach it are a wrong
// key on the next cell, a wrong key on the same cell, and a word skip over a typo, and each is
// pinned twice: once live, once under ComboClaimRule.LatestBreakWins, the arm every score stored
// before 176 was played under. JudgementEraTest.ABreakThatCostNothing... holds the same split for a
// whole submitted account, through the real score processor.
//
// The LAST group is backlog 243, which traced a second player report of the same complaint, and it
// is 176 in the one shape 176 could not see. A real play (score 10762: 1303 cells, every break
// walked back and corrected, and a max_combo of 869 instead of 1303) skipped a word and then typo'd
// immediately after it. The skipping space is judged on the word gap it lands on, so it rebuilds the
// run to 1, so the typo broke a streak of ONE and not of zero, cleared 176's test, and overwrote a
// 430-deep claim with a worthless one. The rule that fixes it is that the claim remembers the combo
// its OWN press credited: a break taking no more than that is as passive as one landing at zero, and
// spends the credit as it goes. A correct character in between puts the run at 2 and the next break
// takes the claim exactly as it always did, which is the counter-case pinned below.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ComboRestoreTest
    {
        #region Fixture

        /// <summary>
        /// One line, "abcdefgh", every cell struck dead on its own target so nothing but the wrong
        /// keys can ever break a run. Cell i targets 1000 + 500i.
        /// </summary>
        private const string word = "abcdefgh";

        private static double target(int cellIndex) => 1000 + 500 * cellIndex;

        private static LyricBeatmap map() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = word,
                    StartTime = 1000,
                    EndTime = 60000,
                    SingEndTime = 5000,
                    Units = new[] { new TimedUnit { Text = word, StartTime = 1000, EndTime = 5000 } },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        private static TypingEngine started()
        {
            var engine = new TypingEngine(map());
            engine.Update(1000);
            return engine;
        }

        /// <summary>Type cells [from, to) correctly, each on its own target.</summary>
        private static void typeCorrectly(TypingEngine engine, int from, int to)
        {
            for (int i = from; i < to; i++)
                Assert.That(engine.ProcessKey(word[i], target(i)), Is.True);
        }

        /// <summary>Type a wrong char into the cell the caret is on (which must be <paramref name="cellIndex"/>).</summary>
        private static void typo(TypingEngine engine, int cellIndex)
        {
            Assert.That(engine.CaretIndex, Is.EqualTo(cellIndex));
            Assert.That(engine.ProcessKey('z', target(cellIndex)), Is.True);
        }

        /// <summary>Backspace onto <paramref name="cellIndex"/> and type it correctly.</summary>
        private static void fix(TypingEngine engine, int cellIndex)
        {
            while (engine.CaretIndex > cellIndex)
                Assert.That(engine.ProcessBackspace(), Is.True);

            Assert.That(engine.ProcessKey(word[cellIndex], target(cellIndex)), Is.True);
        }

        /// <summary>
        /// "abcd efg" on one line, for the pins that need a word a skip can abandon something out of:
        /// a = 1000, b = 2000, c = 3000, d = 4000, ' ' = 5000 (the first unit's end), e = 5000,
        /// f = 6000, g = 7000.
        /// </summary>
        private static LyricBeatmap twoWordMap() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "abcd efg",
                    StartTime = 1000,
                    EndTime = 60000,
                    SingEndTime = 8000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "abcd", StartTime = 1000, EndTime = 5000 },
                        new TimedUnit { Text = "efg", StartTime = 5000, EndTime = 8000 },
                    },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// The reported shape: a run, a wrong key on one cell, a wrong key on the NEXT cell while
        /// the run is already at zero, then both corrected oldest first. Cells 0-3 are the run, 4
        /// and 5 are the two spoiled cells.
        /// </summary>
        private static (TypingEngine engine, List<int> restored) twoAdjacentTyposBothFixed(ComboClaimRule claim,
                                                                                          SkipSpaceCreditRule skipCredit = SkipSpaceCreditRule.NotAStreakOfItsOwn)
        {
            var engine = started();
            engine.ComboClaim = claim;
            engine.SkipSpaceCredit = skipCredit;

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            typeCorrectly(engine, 0, 4);
            Assert.That(engine.Combo, Is.EqualTo(4));

            typo(engine, 4);   // snapshots 4 against cell 4
            typo(engine, 5);   // breaks a run of 0, so it has nothing to take the claim with

            fix(engine, 4);
            Assert.That(engine.ProcessKey(word[5], target(5)), Is.True);

            return (engine, restored);
        }

        /// <summary>
        /// The same-cell sibling: fumble cell 2, erase it, fumble it AGAIN, then correct it. The
        /// second wrong key breaks a run of zero, exactly as the adjacent-cell one does.
        /// </summary>
        private static (TypingEngine engine, List<int> restored) sameCellFumbledTwiceThenFixed(ComboClaimRule claim,
                                                                                              SkipSpaceCreditRule skipCredit = SkipSpaceCreditRule.NotAStreakOfItsOwn)
        {
            var engine = started();
            engine.ComboClaim = claim;
            engine.SkipSpaceCredit = skipCredit;

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            typeCorrectly(engine, 0, 2);
            Assert.That(engine.Combo, Is.EqualTo(2));

            typo(engine, 2);                                   // snapshots 2 against cell 2
            Assert.That(engine.ProcessBackspace(), Is.True);   // erases it, caret back on cell 2
            typo(engine, 2);                                   // breaks a run of 0, on the SAME cell

            fix(engine, 2);

            return (engine, restored);
        }

        /// <summary>
        /// The skip sibling, on <see cref="twoWordMap"/>: type "ab", fumble 'c', give up on the word
        /// with a space (abandoning 'd'), then backspace into it and type both cells out.
        /// </summary>
        private static (TypingEngine engine, List<int> restored) wordSkippedOverATypoThenReclaimed(ComboClaimRule claim)
        {
            var engine = new TypingEngine(twoWordMap()) { SpaceSkipsWord = true, ComboClaim = claim };
            engine.Update(1000);

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 2000), Is.True);
            Assert.That(engine.Combo, Is.EqualTo(2));

            // The typo on 'c', which snapshots the run of 2 against cell 2.
            Assert.That(engine.ProcessKey('z', 3000), Is.True);
            Assert.That(engine.Combo, Is.Zero);

            // Space inside the word: 'd' is abandoned, on a run the typo has already zeroed.
            Assert.That(engine.ProcessKey(' ', 4000), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(5), "the space landed on the word gap");

            // Back into the word (one press reclaims 'd' and erases the typo) and type it out.
            Assert.That(engine.ProcessBackspace(), Is.True); // erases the typed space
            Assert.That(engine.ProcessBackspace(), Is.True); // steps over 'd', erases the typo
            Assert.That(engine.CaretIndex, Is.EqualTo(2));

            Assert.That(engine.ProcessKey('c', 3000), Is.True);
            Assert.That(engine.ProcessKey('d', 4000), Is.True);

            return (engine, restored);
        }

        /// <summary>
        /// <see cref="twoWordMap"/> with one more character in the second word ("abcd efgh"), for the
        /// pin that needs a break AFTER the caret has come back inside the second word: h = 8000, and
        /// nothing else differs.
        /// </summary>
        private static LyricBeatmap longerTailMap() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "abcd efgh",
                    StartTime = 1000,
                    EndTime = 60000,
                    SingEndTime = 9000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "abcd", StartTime = 1000, EndTime = 5000 },
                        new TimedUnit { Text = "efgh", StartTime = 5000, EndTime = 9000 },
                    },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>
        /// A live-play engine on <paramref name="beatmap"/> with every flag score 10762 was recorded
        /// under: the space skips a word, a wrong letter takes a word gap, and a spoiled gap parks the
        /// caret. Only the two claim axes are left to the caller.
        /// </summary>
        private static TypingEngine skipEngine(ComboClaimRule claim, SkipSpaceCreditRule skipCredit, LyricBeatmap? beatmap = null, bool lossless = false)
        {
            var engine = new TypingEngine(beatmap ?? twoWordMap())
            {
                SpaceSkipsWord = true,
                WrongInputOnWordGaps = true,
                StrictSpaces = true,
                ComboClaim = claim,
                SkipSpaceCredit = skipCredit,
                LosslessSkipReclaim = lossless,
            };

            engine.Update(1000);
            return engine;
        }

        /// <summary>
        /// BACKLOG 243, the reported shape, transcribed keystroke for keystroke from correction A of
        /// score 10762 ("it would": a character, a space that gives up the rest of the word, two
        /// wrong keys, four backspaces that walk back PAST the abandoned cell, then the word typed
        /// out). On "abcd efg" that is:
        ///
        /// <list type="number">
        /// <item>'a', 'b', 'c' correctly, so the run is 3 (the replay's was about 430),</item>
        /// <item>' ' on cell 3: the skip abandons 'd' and claims it for the run of 3, and the SAME
        /// press is then judged on the word gap, which puts the combo back to 1,</item>
        /// <item>'z' on cell 5: a typo breaking that run of 1, which is the whole of the bug,</item>
        /// <item>'y' on cell 6: a second typo, breaking a run of zero,</item>
        /// <item>four backspaces: the two typos, the gap, then the step over the abandoned 'd' that
        /// reopens it and erases the 'c' behind it,</item>
        /// <item>'c' (an inert retype, it was already judged) and 'd', which is the cell the skip
        /// claimed and so the press that redeems it.</item>
        /// </list>
        /// </summary>
        private static (TypingEngine engine, List<int> restored) wordSkippedThenTypodBesideIt(ComboClaimRule claim, SkipSpaceCreditRule skipCredit, bool lossless = false)
        {
            var engine = skipEngine(claim, skipCredit, lossless: lossless);

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 2000), Is.True);
            Assert.That(engine.ProcessKey('c', 3000), Is.True);
            Assert.That(engine.Combo, Is.EqualTo(3));

            // The skip: 'd' is abandoned and claimed for the run of 3, and the space is then judged
            // on the gap, which is where the combo of 1 comes from.
            Assert.That(engine.ProcessKey(' ', 4000), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(5), "the space was judged on the gap and moved on");
            Assert.That(engine.Combo, Is.EqualTo(1), "the skipping space rebuilt the run to 1 on the gap");

            // The adjacent typo, breaking that run of 1, and a second one breaking a run of zero.
            Assert.That(engine.ProcessKey('z', 5000), Is.True);
            Assert.That(engine.Combo, Is.Zero);
            Assert.That(engine.ProcessKey('y', 6000), Is.True);

            // Four backspaces, the last of which steps over the abandoned 'd' and erases the 'c'.
            for (int i = 0; i < 4; i++)
                Assert.That(engine.ProcessBackspace(), Is.True);

            Assert.That(engine.CaretIndex, Is.EqualTo(2));
            Assert.That(engine.Lines[0].Cells[3].State, Is.EqualTo(CellState.Untyped), "'d' was reclaimed on the way past");

            Assert.That(engine.ProcessKey('c', 3000), Is.True);
            Assert.That(engine.ProcessKey('d', 4000), Is.True);

            return (engine, restored);
        }

        /// <summary>
        /// BACKLOG 243's counter-case, and the one the report itself demanded: the same skip, but with
        /// a CORRECT character typed between it and the typo. The run is genuinely 2 when the typo
        /// lands (the skip's own space, plus a character the player really typed), so the typo has a
        /// streak of its own to take the claim with and the skip's claim is gone for good. Walking
        /// back to the abandoned 'd' then restores nothing, and it is the TYPO's cell that carries the
        /// live claim.
        /// </summary>
        private static (TypingEngine engine, List<int> restored, int restoredAtTheAbandonedCell) wordSkippedThenACharacterThenATypo(
            ComboClaimRule claim, SkipSpaceCreditRule skipCredit)
        {
            var engine = skipEngine(claim, skipCredit);

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 2000), Is.True);
            Assert.That(engine.ProcessKey('c', 3000), Is.True);

            Assert.That(engine.ProcessKey(' ', 4000), Is.True); // abandons 'd', claims it for 3
            Assert.That(engine.ProcessKey('e', 5000), Is.True); // a real character: the run is now 2
            Assert.That(engine.Combo, Is.EqualTo(2));

            Assert.That(engine.ProcessKey('z', 6000), Is.True); // the typo, breaking a run of 2
            Assert.That(engine.Combo, Is.Zero);

            // Back to the head of the first word: the typo, the 'e', the gap, then the step over the
            // abandoned 'd'.
            for (int i = 0; i < 4; i++)
                Assert.That(engine.ProcessBackspace(), Is.True);

            Assert.That(engine.CaretIndex, Is.EqualTo(2));

            Assert.That(engine.ProcessKey('c', 3000), Is.True);
            Assert.That(engine.ProcessKey('d', 4000), Is.True);

            int atTheAbandonedCell = restored.Count;

            // On to the typo's own cell, which is where the live claim actually is.
            Assert.That(engine.ProcessKey(' ', 5000), Is.True);
            Assert.That(engine.ProcessKey('e', 5000), Is.True);
            Assert.That(engine.ProcessKey('f', 6000), Is.True);

            return (engine, restored, atTheAbandonedCell);
        }

        /// <summary>
        /// BACKLOG 243's other half: the credit is SPENT by the break that stands on it, so the
        /// exemption is worth exactly one break and not a standing licence. On "abcd efgh": the skip
        /// claims 'd' and its space rebuilds the run to 1, a typo takes that 1 and is passive, then
        /// the player types one real character (the run is 1 again, this time honestly), and the next
        /// typo takes THAT and discards the claim like any other break with a streak.
        /// </summary>
        private static (TypingEngine engine, List<int> restored) wordSkippedThenAPassiveTypoThenAnEarnedOne(SkipSpaceCreditRule skipCredit)
        {
            var engine = skipEngine(ComboClaimRule.StreakedBreakWins, skipCredit, longerTailMap());

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 2000), Is.True);
            Assert.That(engine.ProcessKey('c', 3000), Is.True);

            Assert.That(engine.ProcessKey(' ', 4000), Is.True); // abandons 'd', claims it for 3
            Assert.That(engine.Combo, Is.EqualTo(1), "the skipping space, on the gap");

            Assert.That(engine.ProcessKey('z', 5000), Is.True); // the passive typo: it spends the credit
            Assert.That(engine.ProcessKey('f', 6000), Is.True); // one real character
            Assert.That(engine.Combo, Is.EqualTo(1), "a run of 1 the player actually typed");

            Assert.That(engine.ProcessKey('y', 7000), Is.True); // the earned break: it takes the claim
            Assert.That(engine.Combo, Is.Zero);

            // Back to the head of the first word, then out through the abandoned cell.
            for (int i = 0; i < 5; i++)
                Assert.That(engine.ProcessBackspace(), Is.True);

            Assert.That(engine.CaretIndex, Is.EqualTo(2));

            Assert.That(engine.ProcessKey('c', 3000), Is.True);
            Assert.That(engine.ProcessKey('d', 4000), Is.True);

            return (engine, restored);
        }

        #endregion

        /// <summary>
        /// The rule itself. A streak of 3, a typo, two cells rebuilt, then the fix: the run resumes
        /// at the snapshot PLUS what was earned since (3 + 2), and the corrected retype is then
        /// judged on top of that, so the press that fixed the cell is priced at the resumed streak
        /// rather than at 3. That ordering is the difference between fixing a typo being worth
        /// score and being worth only accuracy.
        /// </summary>
        [Test]
        public void FixingATypoResumesTheStreakItBroke()
        {
            var engine = started();

            var restored = new List<int>();
            var judgements = new List<CharJudgement>();
            int breaks = 0;
            engine.ComboRestored += restored.Add;
            engine.ComboBroken += () => breaks++;
            engine.CharJudged += judgements.Add;

            typeCorrectly(engine, 0, 3);
            Assert.That(engine.Combo, Is.EqualTo(3));

            typo(engine, 3);
            Assert.That(engine.Combo, Is.Zero);

            // Cells 4 and 5 rebuild a run of 2 while cell 3 sits wrong.
            typeCorrectly(engine, 4, 6);
            Assert.That(engine.Combo, Is.EqualTo(2));

            fix(engine, 3);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 3 }), "the streak the wrong key broke, once");
                Assert.That(engine.Combo, Is.EqualTo(6), "3 restored + 2 earned since + the fix itself");
                Assert.That(engine.MaxCombo, Is.EqualTo(6));

                // The restore lands BEFORE the retype is judged, so the judgement the stage and the
                // score see already carries the resumed run.
                //
                // Ok and not Great even though the fix is struck dead on the cell's target: backlog
                // 210 caps a corrected cell at Ok, and the two rules are orthogonal by design. The
                // COMBO the fix earns back is untouched (that is this test's subject), and what it
                // costs is the accuracy the cap takes.
                Assert.That(judgements[^1].Type, Is.EqualTo(JudgementType.Ok));
                Assert.That(judgements[^1].ComboAfter, Is.EqualTo(6));

                // Exactly one break, at the keypress, and it is not un-counted: the typo stat counts
                // the KEYPRESS, and no correction can unpress it.
                Assert.That(breaks, Is.EqualTo(1));
                Assert.That(engine.Mistypes, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The bound on the rule. An intervening break OWNS the streak: the run the player was on
        /// when they typed the first wrong char has been lost to something else since, and going
        /// back to fix the older cell cannot un-lose it. Only the newest wrong cell holds a claim,
        /// so fixing them in the order they happened restores nothing for the first and everything
        /// for the second.
        /// </summary>
        [Test]
        public void AnInterveningBreakOwnsTheStreakSoTheOlderFixRestoresNothing()
        {
            var engine = started();

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            typeCorrectly(engine, 0, 3);
            typo(engine, 3);          // snapshots 3
            typeCorrectly(engine, 4, 6);
            typo(engine, 6);          // the intervening break: snapshots 2, and drops cell 3's claim

            Assert.That(engine.Combo, Is.Zero);

            fix(engine, 3);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "cell 3's streak died with the second wrong key");
                Assert.That(engine.Combo, Is.EqualTo(1), "the fix earns its own cell and nothing more");
            });

            // Cell 6 is the one still holding a claim, and it is redeemed normally. The caret is on
            // cell 4 after the fix above, so cells 4 and 5 are inert retypes on the way back out
            // (already judged correct, so no combo of their own) and cell 6 is the fix.
            typeCorrectly(engine, 4, 6);
            fix(engine, 6);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 2 }), "the newer cell's snapshot survived");
                Assert.That(engine.Combo, Is.EqualTo(4), "2 restored + the 1 the older fix earned + this fix");
            });
        }

        /// <summary>
        /// Repeated wrong/fix cycles on ONE cell break and restore each time, each cycle
        /// snapshotting whatever the run has grown back to. The second fix is a scoring-inert
        /// retype (the cell was already judged correct by the first fix, so it earns no points and
        /// no combo of its own), which is exactly why the restore is not folded into the judgement:
        /// the streak belongs to the FIX, not to the cell's result.
        /// </summary>
        [Test]
        public void RepeatedWrongFixCyclesOnOneCellBreakAndRestoreEachTime()
        {
            var engine = started();

            var restored = new List<int>();
            int breaks = 0;
            engine.ComboRestored += restored.Add;
            engine.ComboBroken += () => breaks++;

            typeCorrectly(engine, 0, 2);
            Assert.That(engine.Combo, Is.EqualTo(2));

            typo(engine, 2);
            fix(engine, 2);
            Assert.That(engine.Combo, Is.EqualTo(3), "2 restored + the fix");

            // Round two on the same cell, now on a streak of 3.
            Assert.That(engine.ProcessBackspace(), Is.True);
            typo(engine, 2);
            Assert.That(engine.Combo, Is.Zero);

            fix(engine, 2);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 2, 3 }), "each cycle restores what it broke");
                Assert.That(engine.Combo, Is.EqualTo(3), "the retype is inert, so the run is exactly what was resumed");
                Assert.That(engine.MaxCombo, Is.EqualTo(3));
                Assert.That(breaks, Is.EqualTo(2));

                // Two wrong keypresses, two typos: the count is of keypresses, and fixing is not a
                // refund.
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// The era gate at the engine's own level, which is where the rule is IMPLEMENTED (the
        /// replay scorer and live play only select it). Under the pre-140 rule no snapshot is ever
        /// taken, so the fix earns its cell and nothing else, and the event never fires.
        /// </summary>
        [Test]
        public void ThePre140RuleRestoresNothing()
        {
            var engine = started();
            engine.ComboRestore = ComboRestoreRule.Never;

            int restores = 0;
            engine.ComboRestored += _ => restores++;

            typeCorrectly(engine, 0, 3);
            typo(engine, 3);
            fix(engine, 3);

            Assert.Multiple(() =>
            {
                Assert.That(restores, Is.Zero);
                Assert.That(engine.Combo, Is.EqualTo(1), "the corrected cell starts a fresh run");
                Assert.That(engine.Mistypes, Is.EqualTo(1), "the typo itself is rule-independent");
            });
        }

        /// <summary>
        /// BACKLOG 199: an off-time press between a typo and its fix does NOT discard the claim.
        /// Only a BREAK takes a claim away, and since 199 a right character struck outside the
        /// windows is a hit rather than a break: it earns no points, but it keeps the run, raises no
        /// <see cref="TypingEngine.ComboBroken"/>, and leaves the older cell redeemable.
        ///
        /// <para>Pinned against the pre-199 arm rather than alone, because that is what makes the
        /// claim the load-bearing part: the same keystrokes under
        /// <see cref="OffTimeRule.BreaksCombo"/> lose the snapshot on the mistimed press, so the fix
        /// restores nothing at all and the player is charged twice for one fumble.</para>
        /// </summary>
        [Test]
        public void AnOffTimePressBetweenATypoAndItsFixKeepsTheClaim()
        {
            (var live, var liveRestored) = typoThenAnOffTimePressThenTheFix(OffTimeRule.MehHit);
            (var stored, var storedRestored) = typoThenAnOffTimePressThenTheFix(OffTimeRule.BreaksCombo);

            Assert.Multiple(() =>
            {
                Assert.That(liveRestored, Is.EqualTo(new[] { 3 }), "the mistimed press took nothing away");
                Assert.That(live.Combo, Is.EqualTo(6), "3 restored + the 2 earned since + the fix itself");

                Assert.That(storedRestored, Is.Empty, "pre-199 the mistimed press was a break, and breaks discard");
                Assert.That(stored.Combo, Is.EqualTo(2), "the 1 earned since + the fix itself");
            });
        }

        /// <summary>
        /// A streak of 3, a typo on cell 3 (snapshotting it), then cell 4 struck 2100 ms late, which
        /// is off the Line-granularity ladder (MehLate 2000) and so Premature/Lagging, then cell 5
        /// struck 1700 late, which is still inside it and therefore an ordinary Meh, then the fix.
        /// The point of the pair is that only cell 4 changes meaning between the two arms.
        /// </summary>
        private static (TypingEngine engine, List<int> restored) typoThenAnOffTimePressThenTheFix(OffTimeRule offTime)
        {
            var engine = started();
            engine.OffTime = offTime;

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            typeCorrectly(engine, 0, 3);
            Assert.That(engine.Combo, Is.EqualTo(3));

            typo(engine, 3); // snapshots the 3 against cell 3
            Assert.That(engine.Combo, Is.Zero);

            Assert.That(engine.ProcessKey(word[4], target(4) + 2100), Is.True); // off the ladder
            Assert.That(engine.ProcessKey(word[5], target(5) + 1700), Is.True); // still on it: a Meh

            fix(engine, 3);

            return (engine, restored);
        }

        /// <summary>
        /// BACKLOG 176, and the shape a real submitted run took: score 6212 on "Joji - PIXELATED
        /// KISSES [Insane]", 447 combo deep into "if you never hear from me", where the player typed
        /// 'a' onto the 'm' cell and then 'm' onto the 'e' cell, backspaced twice and typed "me" out
        /// correctly. The second wrong key broke a run of ZERO, because the first one had already
        /// taken the 447, so it has nothing to take the claim with and the 'm' cell keeps it: fixing
        /// that cell resumes the run, and the run the player rebuilt from there is the one they
        /// typed.
        ///
        /// <para>Deliberately stronger than
        /// <see cref="AnInterveningBreakOwnsTheStreakSoTheOlderFixRestoresNothing"/>, which is the
        /// same two wrong keys with a run REBUILT between them, so the second break really does cost
        /// something and really does take the claim. Nothing is lost between these two, which is
        /// exactly why the older claim survives.</para>
        /// </summary>
        [Test]
        public void TwoWrongKeysOnAdjacentCellsKeepTheStreakWhenBothAreFixed()
        {
            (var engine, var restored) = twoAdjacentTyposBothFixed(ComboClaimRule.StreakedBreakWins);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 4 }), "the empty break left cell 4's claim alone");
                Assert.That(engine.Combo, Is.EqualTo(6), "4 restored + the two fixes");
                Assert.That(engine.MaxCombo, Is.EqualTo(6));

                // The two wrong KEYPRESSES are still spent: the fix buys back the streak, never the
                // typo count.
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// The same keystrokes under the arm every score stored before backlog 176 was played under,
        /// which is what a re-derivation of one of those rows has to reproduce: the second wrong key
        /// took the claim anyway, with the empty streak it broke, so both fixes restored nothing.
        /// </summary>
        [Test]
        public void ThePre176RuleLetsTheEmptyBreakTakeTheAdjacentCellsClaim()
        {
            (var engine, var restored) = twoAdjacentTyposBothFixed(ComboClaimRule.LatestBreakWins);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "cell 4's claim was replaced by an empty one and cell 5's redeemed nothing");
                Assert.That(engine.Combo, Is.EqualTo(2), "the two fixes earn their own cells and nothing more");
                Assert.That(engine.MaxCombo, Is.EqualTo(4), "the run of 4 is never resumed");
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// Fumbling the SAME cell twice before correcting it keeps the streak the FIRST wrong key
        /// broke, for the same reason: the second one breaks a run of zero. It differs from
        /// <see cref="RepeatedWrongFixCyclesOnOneCellBreakAndRestoreEachTime"/> only in that no
        /// successful fix separates the two wrong keys, so there is no second streak to snapshot,
        /// and the first one's claim is the only one there has ever been.
        /// </summary>
        [Test]
        public void ASecondWrongKeyOnTheSameCellKeepsTheStreakTheFirstOneSnapshotted()
        {
            (var engine, var restored) = sameCellFumbledTwiceThenFixed(ComboClaimRule.StreakedBreakWins);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 2 }), "the first wrong key's claim survived the second");
                Assert.That(engine.Combo, Is.EqualTo(3), "2 restored + the fix");
                Assert.That(engine.MaxCombo, Is.EqualTo(3));
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>The pre-176 arm of the same-cell shape: the second wrong key overwrote the claim.</summary>
        [Test]
        public void ThePre176RuleLetsTheSecondWrongKeyOnACellDropItsOwnClaim()
        {
            (var engine, var restored) = sameCellFumbledTwiceThenFixed(ComboClaimRule.LatestBreakWins);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "the second wrong key overwrote the snapshot with an empty streak");
                Assert.That(engine.Combo, Is.EqualTo(1), "the fix earns its own cell and nothing more");
                Assert.That(engine.MaxCombo, Is.EqualTo(2), "the run of 2 is never resumed");
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// A word skip taken while a typo is still sitting in that word leaves the typo's claim
        /// alone: the skip's own break costs nothing (the typo zeroed the run), so it has no streak
        /// to claim the cell with. The same principle as the two above, applied to the OTHER
        /// redeemable break, and it keeps backlog 167's promise intact in the case that promise is
        /// worth most: the player who fumbles, gives up on the word, then goes back and types the
        /// whole thing out has undone everything they did wrong.
        ///
        /// <para>A skip that DOES break a streak still takes the claim, exactly as any other break
        /// with something to own does: only the empty one is passive.</para>
        /// </summary>
        [Test]
        public void AWordSkipOverATypoLeavesThatTyposSnapshotAlone()
        {
            (var engine, var restored) = wordSkippedOverATypoThenReclaimed(ComboClaimRule.StreakedBreakWins);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 2 }), "the skip had no streak to take the claim with");
                Assert.That(engine.Combo, Is.EqualTo(5), "the space, 2 restored at the 'c', then both cells");
                Assert.That(engine.MaxCombo, Is.EqualTo(5));
            });
        }

        /// <summary>The pre-176 arm of the skip shape: the skip's empty claim replaced the typo's.</summary>
        [Test]
        public void ThePre176RuleLetsAWordSkipTakeOverATyposSnapshot()
        {
            (var engine, var restored) = wordSkippedOverATypoThenReclaimed(ComboClaimRule.LatestBreakWins);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "the skip's own empty claim replaced the typo's");
                Assert.That(engine.Combo, Is.EqualTo(3), "the space, then the two cells typed out");
                Assert.That(engine.MaxCombo, Is.EqualTo(3), "the run of 2 is never resumed");
            });
        }

        /// <summary>
        /// BACKLOG 243, the bug score 10762 lost about 430 combo to. A typo landing on the character
        /// AFTER a word skip breaks a run of 1 rather than of zero, because the skipping space was
        /// itself judged on the word gap it landed on. That 1 is the break's own press and not
        /// progress the player made, so it is not a streak the typo may take the skip's claim with:
        /// walking back and typing the abandoned word out resumes the run the skip broke, which is
        /// what backlog 167 promised and what backlog 176 already says for the shape without the
        /// space in it.
        ///
        /// <para>Backlog 260 finishes the sentence. The passive typo did not merely fail to take the
        /// claim, it SPENT a run of 1 on its way past (its call site had already broken it), and that
        /// increment was the skip's own gap, a cell now resolved and inert on every retype, so it was
        /// gone for good. Folded into the claim it left standing, the redemption is 4 rather than 3
        /// and the corrected run ends on 5. The old 4 is pinned below, under the era arm.</para>
        /// </summary>
        [Test]
        public void ATypoBesideAWordSkipCannotTakeTheClaimWithTheSkipsOwnSpace()
        {
            (var engine, var restored) = wordSkippedThenTypodBesideIt(ComboClaimRule.StreakedBreakWins, SkipSpaceCreditRule.NotAStreakOfItsOwn, lossless: true);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 4 }), "the deep claim survived the typo beside the skip, and swallowed the run it spent");
                Assert.That(engine.Combo, Is.EqualTo(5), "4 restored at the 'd', then the 'd' itself");
                Assert.That(engine.MaxCombo, Is.EqualTo(5));
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// The pre-260 arm of the same keystrokes, and the reproduction pin every stored row depends
        /// on: the passive typo kept the claim (backlog 243) but dropped the gap increment it had just
        /// broken, so the redemption is the 3 the SKIP took and no more, and the run ends one short of
        /// where the same fingers end up today.
        /// </summary>
        [Test]
        public void ThePre260RuleDropsTheRunThePassiveTypoSpent()
        {
            (var engine, var restored) = wordSkippedThenTypodBesideIt(ComboClaimRule.StreakedBreakWins, SkipSpaceCreditRule.NotAStreakOfItsOwn);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 3 }), "only what the skip's own break took");
                Assert.That(engine.Combo, Is.EqualTo(4), "3 restored at the 'd', then the 'd' itself");
                Assert.That(engine.MaxCombo, Is.EqualTo(4));
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// The pre-243 arm of the same keystrokes, which is what every stored row was played under and
        /// what the replay actually scored: the typo's streak of 1 was enough to take the claim, so it
        /// overwrote the skip's with its own, and typing the abandoned word out restored nothing at
        /// all. The claim is not lost, it is merely worthless: it now sits on the typo's cell holding
        /// the 1 that the skipping space had put back.
        /// </summary>
        [Test]
        public void ThePre243RuleLetsTheSkipsOwnSpaceArmTheTypoBesideIt()
        {
            (var engine, var restored) = wordSkippedThenTypodBesideIt(ComboClaimRule.StreakedBreakWins, SkipSpaceCreditRule.AStreakLikeAnyOther);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "the typo's claim of 1 replaced the skip's claim of 3");
                Assert.That(engine.Combo, Is.EqualTo(1), "the retyped 'd' earns its own cell and nothing more");
                Assert.That(engine.MaxCombo, Is.EqualTo(3), "the run of 3 is never resumed");
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// The counter-case the report required, and the boundary of the rule: ONE correct character
        /// between the skip and the typo is enough. The run is really 2 by then, so the typo has a
        /// streak of its own, takes the claim exactly as any break with something to own does, and
        /// coming back to the abandoned cell restores nothing. Pinned under BOTH arms of the new axis
        /// with the same numbers, which is the statement that backlog 243 moved only the case where
        /// the streak was the break's own press.
        /// </summary>
        [Test]
        public void ACharacterBetweenTheSkipAndTheTypoStillLetsTheTypoTakeTheClaim(
            [Values(SkipSpaceCreditRule.NotAStreakOfItsOwn, SkipSpaceCreditRule.AStreakLikeAnyOther)] SkipSpaceCreditRule skipCredit)
        {
            (var engine, var restored, int atTheAbandonedCell) = wordSkippedThenACharacterThenATypo(ComboClaimRule.StreakedBreakWins, skipCredit);

            Assert.Multiple(() =>
            {
                Assert.That(atTheAbandonedCell, Is.Zero, "the skip's claim was discarded by a typo that had a streak of its own");
                Assert.That(restored, Is.EqualTo(new[] { 2 }), "the typo's own claim is the live one, redeemed on its own cell");
                Assert.That(engine.Combo, Is.EqualTo(4), "the retyped 'd', 2 restored at the 'f', then the 'f' itself");
                Assert.That(engine.MaxCombo, Is.EqualTo(4));
                Assert.That(engine.Mistypes, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The exemption is worth exactly ONE break, because the break that stands on the skip's own
        /// credit spends it. One real character later the run is 1 again, honestly this time, and the
        /// next typo takes the claim exactly as any break with a streak does. Both arms of the axis
        /// agree on this shape, which is the point: backlog 243 buys the player the one break their
        /// own skip armed, and nothing after it.
        /// </summary>
        [Test]
        public void TheSkipsOwnCreditIsSpentByTheBreakThatStandsOnIt(
            [Values(SkipSpaceCreditRule.NotAStreakOfItsOwn, SkipSpaceCreditRule.AStreakLikeAnyOther)] SkipSpaceCreditRule skipCredit)
        {
            (var engine, var restored) = wordSkippedThenAPassiveTypoThenAnEarnedOne(skipCredit);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "the second typo had a streak the player earned, so it took the claim");
                Assert.That(engine.Combo, Is.EqualTo(1), "the retyped 'd' earns its own cell and nothing more");
                Assert.That(engine.MaxCombo, Is.EqualTo(3), "the run of 3 is never resumed");
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// Backlog 243 is invisible to every shape backlog 176 was written for: with no word skip in
        /// them nothing ever credits a claim's own press, so the ceiling a passive break is measured
        /// against stays zero and both arms of the new axis agree, cell for cell, with what the 176
        /// pins above already assert.
        /// </summary>
        [Test]
        public void ThePlain176ShapesAreUntouchedByTheSkipCreditRule()
        {
            (var adjacentLive, var adjacentLiveRestored) = twoAdjacentTyposBothFixed(ComboClaimRule.StreakedBreakWins, SkipSpaceCreditRule.NotAStreakOfItsOwn);
            (var adjacentOld, var adjacentOldRestored) = twoAdjacentTyposBothFixed(ComboClaimRule.StreakedBreakWins, SkipSpaceCreditRule.AStreakLikeAnyOther);

            (var sameCellLive, var sameCellLiveRestored) = sameCellFumbledTwiceThenFixed(ComboClaimRule.StreakedBreakWins, SkipSpaceCreditRule.NotAStreakOfItsOwn);
            (var sameCellOld, var sameCellOldRestored) = sameCellFumbledTwiceThenFixed(ComboClaimRule.StreakedBreakWins, SkipSpaceCreditRule.AStreakLikeAnyOther);

            Assert.Multiple(() =>
            {
                Assert.That(adjacentOldRestored, Is.EqualTo(adjacentLiveRestored));
                Assert.That(adjacentOld.Combo, Is.EqualTo(adjacentLive.Combo));
                Assert.That(adjacentOld.MaxCombo, Is.EqualTo(adjacentLive.MaxCombo));

                Assert.That(sameCellOldRestored, Is.EqualTo(sameCellLiveRestored));
                Assert.That(sameCellOld.Combo, Is.EqualTo(sameCellLive.Combo));
                Assert.That(sameCellOld.MaxCombo, Is.EqualTo(sameCellLive.MaxCombo));

                // ...and they are still the values the 176 pins assert, so this is an equality with
                // the right thing rather than two engines agreeing on something new.
                Assert.That(adjacentLiveRestored, Is.EqualTo(new[] { 4 }));
                Assert.That(sameCellLiveRestored, Is.EqualTo(new[] { 2 }));
            });
        }
    }
}
