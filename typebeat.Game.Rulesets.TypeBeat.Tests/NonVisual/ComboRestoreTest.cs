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
// The last three pins are CHARACTERISATION pins added by backlog 176, which traced a player report
// that a corrected typo did not restore. The report was accurate and the engine was behaving as
// designed: two wrong keys landed on ADJACENT cells, and the second one rewrote the snapshot the
// first had taken, with the combo AT THAT MOMENT, which the first wrong key had already zeroed. The
// player then corrected both cells and got nothing back.
//
// The three pins record the family that shape belongs to: a break that costs NOTHING (the combo is
// already zero) still takes ownership of a live claim, whether it is a wrong key on the next cell, a
// wrong key on the same cell, or a word skip over the typo. The discard rule's stated reason ("an
// intervening break is a run the player has already lost") does not obviously cover any of them, so
// they are pinned as what the engine does rather than as what the rule says it should do: changing
// any of them is a scoring change, and needs an era switch plus the JS mirror.

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
                Assert.That(judgements[^1].Type, Is.EqualTo(JudgementType.Great));
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
        /// CHARACTERISATION (backlog 176), and the shape a real submitted run took: score 6212 on
        /// "Joji - PIXELATED KISSES [Insane]", 447 combo deep into "if you never hear from me", where
        /// the player typed 'a' onto the 'm' cell and then 'm' onto the 'e' cell, backspaced twice
        /// and typed "me" out correctly. The first wrong key snapshotted 447 against the 'm' cell;
        /// the second, on the NEXT cell, replaced that snapshot with its own, taken at a combo the
        /// first wrong key had already zeroed. Correcting the 'm' redeemed nothing (its claim was
        /// gone) and correcting the 'e' redeemed a streak of zero, which announces nothing, so the
        /// run ended at 447 and the player rebuilt from 1 having fixed every character they got
        /// wrong.
        ///
        /// <para>Deliberately stronger than
        /// <see cref="AnInterveningBreakOwnsTheStreakSoTheOlderFixRestoresNothing"/>, which is the
        /// same discard with a run REBUILT between the two wrong keys, so the second break really
        /// does cost something. Here nothing at all is lost between them and BOTH spoiled cells are
        /// corrected, which is the case the rule reads least well in.</para>
        /// </summary>
        [Test]
        public void TwoWrongKeysOnAdjacentCellsLoseTheStreakEvenThoughBothAreFixed()
        {
            var engine = started();

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            typeCorrectly(engine, 0, 4);
            Assert.That(engine.Combo, Is.EqualTo(4));

            typo(engine, 4);   // snapshots 4 against cell 4
            typo(engine, 5);   // snapshots 0 against cell 5, and drops cell 4's claim

            // Both spoiled cells corrected, oldest first, exactly as the reported run did.
            fix(engine, 4);
            Assert.That(engine.ProcessKey(word[5], target(5)), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "cell 4's claim was replaced by an empty one and cell 5's redeemed nothing");
                Assert.That(engine.Combo, Is.EqualTo(2), "the two fixes earn their own cells and nothing more");
                Assert.That(engine.MaxCombo, Is.EqualTo(4), "the run of 4 is never resumed");
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// CHARACTERISATION (backlog 176). Fumbling the SAME cell twice before correcting it loses
        /// the streak the first wrong key broke, because each wrong keypress overwrites the snapshot
        /// with whatever the combo is at that moment, and after the first one that is zero. The
        /// same-cell sibling of
        /// <see cref="TwoWrongKeysOnAdjacentCellsLoseTheStreakEvenThoughBothAreFixed"/>, and it
        /// differs from <see cref="RepeatedWrongFixCyclesOnOneCellBreakAndRestoreEachTime"/> only in
        /// that no successful fix separates the two wrong keys, so there is no streak for the second
        /// one to snapshot.
        ///
        /// <para>Not a rule anybody wrote down: the discard exists because an intervening break is a
        /// run already lost, and the second wrong key here loses nothing (the combo it breaks is
        /// already zero) and spoils the very cell the player then comes back and fixes. Pinned as
        /// today's behaviour so that changing it is a deliberate act with an era switch and a JS
        /// mirror edit, not a silent one.</para>
        /// </summary>
        [Test]
        public void ASecondWrongKeyOnTheSameCellDropsTheStreakTheFirstOneSnapshotted()
        {
            var engine = started();

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            typeCorrectly(engine, 0, 2);
            Assert.That(engine.Combo, Is.EqualTo(2));

            typo(engine, 2);                                   // snapshots 2 against cell 2
            Assert.That(engine.ProcessBackspace(), Is.True);   // erases it, caret back on cell 2
            typo(engine, 2);                                   // snapshots 0 against the SAME cell

            fix(engine, 2);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "the second wrong key overwrote the snapshot with an empty streak");
                Assert.That(engine.Combo, Is.EqualTo(1), "the fix earns its own cell and nothing more");
                Assert.That(engine.MaxCombo, Is.EqualTo(2), "the run of 2 is never resumed");
                Assert.That(engine.Mistypes, Is.EqualTo(2));
            });
        }

        /// <summary>
        /// CHARACTERISATION (backlog 176). A word skip taken while a typo is still sitting in that
        /// word takes the snapshot over: backlog 167 has the skip write its own claim against the
        /// first cell it abandons, unconditionally, and it is written with the streak at the time of
        /// the skip, which a typo has already zeroed. So the player who typos, gives up on the word,
        /// then backspaces into it and types both cells out gets no streak back.
        ///
        /// <para>Reachable in ordinary play: space-to-skip-a-word is a plain setting, and the run
        /// this pin came out of was played with it on. Pinned for the same reason as the sibling
        /// above.</para>
        /// </summary>
        [Test]
        public void AWordSkipOverATypoTakesOverTheSnapshotThatTypoLeft()
        {
            var engine = new TypingEngine(twoWordMap()) { SpaceSkipsWord = true };
            engine.Update(1000);

            var restored = new List<int>();
            engine.ComboRestored += restored.Add;

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 2000), Is.True);
            Assert.That(engine.Combo, Is.EqualTo(2));

            // The typo on 'c', which snapshots the run of 2 against cell 2.
            Assert.That(engine.ProcessKey('z', 3000), Is.True);
            Assert.That(engine.Combo, Is.Zero);

            // Space inside the word: 'd' is abandoned and the skip snapshots ITS zero against cell 3.
            Assert.That(engine.ProcessKey(' ', 4000), Is.True);
            Assert.That(engine.CaretIndex, Is.EqualTo(5), "the space landed on the word gap");

            // Back into the word (one press reclaims 'd' and erases the typo) and type it out.
            Assert.That(engine.ProcessBackspace(), Is.True); // erases the typed space
            Assert.That(engine.ProcessBackspace(), Is.True); // steps over 'd', erases the typo
            Assert.That(engine.CaretIndex, Is.EqualTo(2));

            Assert.That(engine.ProcessKey('c', 3000), Is.True);
            Assert.That(engine.ProcessKey('d', 4000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.Empty, "the skip's own empty claim replaced the typo's");
                Assert.That(engine.Combo, Is.EqualTo(3), "the space, then the two cells typed out");
                Assert.That(engine.MaxCombo, Is.EqualTo(3), "the run of 2 is never resumed");
            });
        }
    }
}
