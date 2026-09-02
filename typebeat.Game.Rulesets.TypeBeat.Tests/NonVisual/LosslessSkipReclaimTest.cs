// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 260: ONE LAW, an accidental word skip that is then typed out in full costs the run
// NOTHING. A player finished a map with every one of its 920 cells typed, 0 misses, and a max combo
// of 919. The increment missing was the WORD GAP the skipping space was itself judged on, and it was
// being dropped in two different ways:
//
//   A  the RUSH CAP charged the space for the word it abandoned. skipCurrentWord walks the caret
//      past the whole word before the same press is judged on the gap, and the cap measures the
//      caret POSITIONALLY, so the abandoned tail was spent out of a budget the player never touched.
//      Over the cap the gap earned no combo, silently: the skip's own break had already zeroed the
//      run, so there was no ComboBroken and no claim discarded, and the gap still resolved Correct,
//      which makes every later retype of it inert.
//
//   B  the PASSIVE CLAIM arm (backlog 243) dropped the run it stood on. The break's call site has
//      already run breakRun, so a passive break that keeps the older claim and discards its own
//      brokenStreak throws those increments away with nothing left to redeem them.
//
// Both move a stored replay's max_combo, so both are gated on the one era flag
// (TypingEngine.LosslessSkipReclaim, CONFIG frame bit 11) and the pins below are written in pairs:
// the live arm, and the arm every stored row was played under.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class LosslessSkipReclaimTest
    {
        #region Fixture

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

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
        /// THE REPORTED SHAPE'S FIXTURE: a short word, a LONG one, and a short one after it, dense
        /// enough that the long word alone is far more than <see cref="TypingEngine.FLETCHER_MAX_CHARS_AHEAD"/>.
        ///
        /// <para>"ab cdefghijkl mn op", one unit per word: ab [1000, 1200], cdefghijkl [1200, 2200],
        /// mn [2200, 2400], op [2400, 2600]. Cells (index: char = target) are
        /// 0:a = 1000, 1:b = 1100, 2:' ' = 1200, 3:c = 1200 .. 12:l = 2100, 13:' ' = 2200,
        /// 14:m = 2200, 15:n = 2300, 16:' ' = 2400, 17:o = 2400, 18:p = 2500. Nineteen cells, sixteen
        /// of them countable (the three gaps are not), and the line runs to 60000 so nothing seals
        /// mid-test.</para>
        /// </summary>
        private static LyricBeatmap longWordMap() => map(new LyricLine
        {
            RawText = "ab cdefghijkl mn op",
            StartTime = 1000,
            EndTime = 60000,
            SingEndTime = 2600,
            Units = new[]
            {
                unit("ab", 1000, 1200),
                unit("cdefghijkl", 1200, 2200),
                unit("mn", 2200, 2400),
                unit("op", 2400, 2600),
            },
        });

        /// <summary>
        /// The live stack (<c>DrawableTypeBeatRuleset.createEngine</c>) as far as this file is
        /// concerned: the unpinned caret with its rush cap, the spacebar as the word boundary, and
        /// <paramref name="lossless"/> selecting backlog 260's arm.
        /// </summary>
        private static TypingEngine started(LyricBeatmap beatmap, bool lossless)
        {
            var engine = new TypingEngine(beatmap)
            {
                SpaceSkipsWord = true,
                StrictSpaces = true,
                WrongInputOnWordGaps = true,
                FletcherEnabled = true,
                FlexibleLineSnap = true,
                BoundedRush = true,
                LosslessSkipReclaim = lossless,
            };

            engine.Update(1000);
            Assert.That(engine.ActiveLineIndex, Is.Zero);
            return engine;
        }

        private static IReadOnlyList<TypingCell> cells(TypingEngine engine) => engine.Lines[0].Cells;

        /// <summary>Type cells [from, to) correctly, each dead on its own target.</summary>
        private static void typeCells(TypingEngine engine, int from, int to)
        {
            for (int i = from; i < to; i++)
                Assert.That(engine.ProcessKey(cells(engine)[i].Expected, cells(engine)[i].TargetTime), Is.True, $"cell {i}");
        }

        /// <summary>
        /// EXACTLY the loop <c>TypeBeatKeyHandler.eraseBackTo</c> runs (the same mirror
        /// <see cref="WordInputTest"/> keeps), so these tests exercise the composition the real
        /// Ctrl+A gesture builds rather than a private one.
        /// </summary>
        private static int eraseBackTo(TypingEngine engine, int target)
        {
            int erases = 0;

            while (engine.CaretIndex > target)
            {
                int before = engine.CaretIndex;

                if (!engine.ProcessBackspace())
                    break;

                erases++;

                if (engine.CaretIndex >= before)
                    break;
            }

            return erases;
        }

        /// <summary>The clean run: every cell of the fixture typed in order, dead on its target.</summary>
        private static TypingEngine cleanRun(bool lossless)
        {
            var engine = started(longWordMap(), lossless);

            typeCells(engine, 0, cells(engine).Count);
            return engine;
        }

        /// <summary>
        /// The whole gesture as the input layer composes it: read the anchor, mass backspace to it,
        /// then type every cell from the anchor to the end of the line. Returns the anchor and the
        /// caret the collapse landed on, which is the pair backlog 260's input-layer half is about.
        /// </summary>
        private static (int anchor, int caretAfterCollapse) correctAndFinish(TypingEngine engine)
        {
            int anchor = engine.RetypeSelectionAnchor;

            eraseBackTo(engine, anchor);

            int landed = engine.CaretIndex;

            typeCells(engine, landed, cells(engine).Count);
            return (anchor, landed);
        }

        #endregion

        // -----------------------------------------------------------------------------------------
        // A: the rush cap must not charge the skipping space for the word it gave up
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE PRIMARY DEFECT, in isolation. The caret sits at the head of a ten-character word with
        /// the playhead alongside it, so the player is not rushing at all; the space that gives that
        /// word up then lands on a gap the skip has already walked the caret twelve countable
        /// characters forward of. Measured there the press is nine chars past a cap of five and earns
        /// nothing, which is the increment the report is missing. Measured where the press was
        /// actually made it is one char BEHIND the playhead, and the gap is credited like any other.
        /// </summary>
        [Test]
        public void TheSkippingSpaceIsNotChargedForTheWordItAbandoned()
        {
            var live = started(longWordMap(), lossless: true);
            var stored = started(longWordMap(), lossless: false);

            foreach (var engine in new[] { live, stored })
            {
                typeCells(engine, 0, 3); // "ab" and its gap, each on target
                Assert.That(engine.Combo, Is.EqualTo(3));
                Assert.That(engine.CharsAheadOfPlayhead(1200), Is.EqualTo(-1), "the player is not rushing");

                Assert.That(engine.ProcessKey(' ', 1200), Is.True, "the accidental space, at the head of the long word");
                Assert.That(engine.CaretIndex, Is.EqualTo(14), "past the gap the skip parked on");
            }

            Assert.Multiple(() =>
            {
                // The abandoned tail is ten countable chars, so measured after the skip the gap sits
                // 12 - 3 = 9 past the playhead, four over the cap of five.
                Assert.That(live.CharsAheadOfPlayhead(1200), Is.EqualTo(9), "the caret really is out past the cap now");

                Assert.That(live.Combo, Is.EqualTo(1), "the gap is credited: the word it gave up is not the player's budget");
                Assert.That(stored.Combo, Is.Zero, "the pre-260 arm charged the abandoned tail and refused the gap");

                // Silently, which is why it was never reported as a break: the skip's own break had
                // already zeroed the run, so the refusal raised nothing at all.
                Assert.That(cells(stored)[13].State, Is.EqualTo(CellState.Correct));
                Assert.That(cells(stored)[13].FirstCorrectDelta, Is.Not.Null, "and the gap is resolved, so no retype can earn it back");
            });
        }

        /// <summary>
        /// THE REPORT, end to end and through the real gesture composition: a space struck at the head
        /// of a long word, Ctrl+A, and the line typed out. Live, the run ends on exactly the max combo
        /// the clean run holds, with the same tier counts and no misses. Under the stored arm it ends
        /// one short, which is the 919 of 920 the player saw.
        /// </summary>
        [Test]
        public void AHeadOfWordSkipFullyCorrectedReachesTheCleanRunsMaxCombo()
        {
            var clean = cleanRun(lossless: true);

            var live = started(longWordMap(), lossless: true);
            var stored = started(longWordMap(), lossless: false);

            foreach (var engine in new[] { live, stored })
            {
                typeCells(engine, 0, 3);
                Assert.That(engine.ProcessKey(' ', 1200), Is.True);
                Assert.That(correctAndFinish(engine).anchor, Is.EqualTo(2), "the gap in front of the wholly abandoned word");
            }

            Assert.Multiple(() =>
            {
                Assert.That(clean.MaxCombo, Is.EqualTo(19), "nineteen cells, nineteen increments");

                Assert.That(live.MaxCombo, Is.EqualTo(19), "the skip cost the corrected run nothing at all");
                Assert.That(live.Combo, Is.EqualTo(19));
                Assert.That(stored.MaxCombo, Is.EqualTo(18), "the reported shape: one short of the clean run");

                // Everything the axis does not reach: the same cells, the same tiers, no misses, and
                // no mistype, because nothing was ever typed wrong.
                foreach (var engine in new[] { live, stored })
                {
                    var results = engine.BuildResults();

                    Assert.That(results.Counts[JudgementType.Great], Is.EqualTo(19));
                    Assert.That(results.Counts[JudgementType.Miss], Is.Zero);
                    Assert.That(engine.Mistypes, Is.Zero);
                    Assert.That(results.Accuracy, Is.EqualTo(1.0));
                }
            });
        }

        /// <summary>
        /// The bound on defect A's fix: an ORDINARY press is measured exactly where it always was.
        /// Six countable chars ahead of the playhead still breaks the run on the sixth, under both
        /// arms, because nothing about a press that skipped nothing has moved.
        /// </summary>
        [Test]
        public void AnOrdinaryPressKeepsTheCapItAlwaysHad(
            [Values(false, true)] bool lossless)
        {
            var engine = started(longWordMap(), lossless);

            int breaks = 0;
            engine.ComboBroken += () => breaks++;

            // "ab" and its gap on target, then c..j all struck at 1200, where the playhead has
            // reached three countable chars (a, b, c). Each press leaves the caret one further
            // countable char along, so 'c' is level with the playhead and 'h' is the fifth ahead,
            // which is still inside the cap; 'i' is the sixth and is not.
            typeCells(engine, 0, 3);

            for (int i = 3; i < 11; i++)
                Assert.That(engine.ProcessKey(cells(engine)[i].Expected, 1200), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(engine.MaxCombo, Is.EqualTo(9), "a, b, the gap and c..h, the last of them exactly five ahead");
                Assert.That(engine.Combo, Is.Zero, "the sixth char ahead broke it");
                Assert.That(breaks, Is.EqualTo(1), "once per excursion, exactly as before");
            });
        }

        // -----------------------------------------------------------------------------------------
        // B: a passive break folds the run it spent into the claim it left standing
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE SECONDARY DEFECT, in isolation, on the shape that reaches it without the rush cap: a
        /// DOUBLE SPACE. The first space gives up a word and is credited on the gap; the second gives
        /// up the next word and breaks that run of one. That break is PASSIVE (backlog 243: the one it
        /// broke was the first skip's own press, not progress), so it keeps the older claim, and
        /// before backlog 260 it dropped the increment it had just broken on the floor, where no
        /// retype could reach it, because the gap that earned it is resolved.
        ///
        /// <para>Fletcher is off here so the cap cannot fire: the only thing separating the two arms
        /// is the fold.</para>
        /// </summary>
        [Test]
        public void ADoubleSpaceFoldsTheSpentRunIntoTheClaimItLeavesStanding()
        {
            var arms = new List<(bool lossless, TypingEngine engine, List<int> restored)>();

            foreach (bool lossless in new[] { true, false })
            {
                var engine = new TypingEngine(longWordMap())
                {
                    SpaceSkipsWord = true,
                    StrictSpaces = true,
                    WrongInputOnWordGaps = true,
                    LosslessSkipReclaim = lossless,
                };

                engine.Update(1000);

                var restored = new List<int>();
                engine.ComboRestored += restored.Add;

                typeCells(engine, 0, 3);
                Assert.That(engine.Combo, Is.EqualTo(3));

                Assert.That(engine.ProcessKey(' ', 1200), Is.True, "gives up \"cdefghijkl\", claims it for 3, takes the gap");
                Assert.That(engine.Combo, Is.EqualTo(1), "the skip's own space, on the gap");

                Assert.That(engine.ProcessKey(' ', 2200), Is.True, "the second space: gives up \"mn\" and breaks that run of 1");
                Assert.That(engine.Combo, Is.EqualTo(1), "the second skip's own space, on the second gap");

                correctAndFinish(engine);
                arms.Add((lossless, engine, restored));
            }

            var live = arms[0];
            var stored = arms[1];

            Assert.Multiple(() =>
            {
                Assert.That(live.restored, Is.EqualTo(new[] { 4 }), "the run of 3 the first skip broke, plus the gap the passive break spent");
                Assert.That(stored.restored, Is.EqualTo(new[] { 3 }), "the pre-260 arm restores only what the FIRST break took");

                Assert.That(live.engine.MaxCombo, Is.EqualTo(19), "two accidental spaces, fully corrected, still cost nothing");
                Assert.That(stored.engine.MaxCombo, Is.EqualTo(18));

                foreach (var arm in arms)
                {
                    Assert.That(arm.engine.BuildResults().Counts[JudgementType.Miss], Is.Zero);
                    Assert.That(arm.engine.Mistypes, Is.Zero);
                }
            });
        }

        /// <summary>
        /// The ledger invariant the fold has to keep (backlog 259): one entry per unit of combo. A
        /// folded claim restores its positions AT THE HEAD of the run, so a seal back-dated against an
        /// earlier cell can still tell which of them it is entitled to destroy. Asserted through the
        /// only observable that can see the list length, the restored streak itself, plus the combo it
        /// produces: the two would disagree if the streak and the positions had drifted apart.
        /// </summary>
        [Test]
        public void TheFoldedClaimsStreakAndPositionsStayInStep()
        {
            var engine = new TypingEngine(longWordMap())
            {
                SpaceSkipsWord = true,
                StrictSpaces = true,
                LosslessSkipReclaim = true,
            };

            engine.Update(1000);

            int restored = 0;
            engine.ComboRestored += streak => restored += streak;

            typeCells(engine, 0, 3);
            Assert.That(engine.ProcessKey(' ', 1200), Is.True);
            Assert.That(engine.ProcessKey(' ', 2200), Is.True);

            int comboBeforeTheFix = engine.Combo;

            correctAndFinish(engine);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(4));
                Assert.That(comboBeforeTheFix, Is.EqualTo(1));

                // 1 standing + 4 restored + the 14 cells retyped from the anchor that were not already
                // judged (c..l, m, n, o, p), the two gaps among them being inert retypes.
                Assert.That(engine.Combo, Is.EqualTo(19));
                Assert.That(engine.MaxCombo, Is.EqualTo(engine.Combo), "the run only ever grew");
            });
        }

        // -----------------------------------------------------------------------------------------
        // C: the collapse must not erase past its own anchor (input layer, no era)
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// THE INPUT-LAYER DEFECT, and the reason the reported correction manufactured a typo on its
        /// way. A word given up WHOLE has no typed cell of its own for the mass backspace to stop on:
        /// the transparent step-over walks the entire abandoned run and erases the nearest typed cell
        /// behind it, which is the gap in FRONT of the word. Anchored on the word's head the collapse
        /// therefore ended up one cell behind its own selection, and the first letter of the retype
        /// landed on that already-judged gap as a fresh typo.
        ///
        /// <para>The selection is widened to that gap instead, so the anchor is a cell the collapse
        /// can actually land on. Era-free on purpose: it changes which frames the LIVE gesture emits
        /// and nothing about how a recorded one is played back, unlike a bounded backspace, which
        /// would stop somewhere its own BACKSPACE frames could not reproduce.</para>
        /// </summary>
        [Test]
        public void TheCollapseOverAWhollyAbandonedWordLandsOnItsAnchor(
            [Values(false, true)] bool lossless)
        {
            var engine = started(longWordMap(), lossless);

            typeCells(engine, 0, 3);
            Assert.That(engine.ProcessKey(' ', 1200), Is.True);

            int anchor = engine.RetypeSelectionAnchor;

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[3].State, Is.EqualTo(CellState.Abandoned), "the word head is a cell nobody typed");
                Assert.That(anchor, Is.EqualTo(2), "the gap in front of it, not the word head at 3");
            });

            int erases = eraseBackTo(engine, anchor);

            Assert.Multiple(() =>
            {
                Assert.That(erases, Is.EqualTo(2), "the gap the skip took, then one press over the abandoned run onto the gap before it");
                Assert.That(engine.CaretIndex, Is.EqualTo(anchor), "the collapse ends ON the anchor, never behind it");

                for (int i = 3; i < 13; i++)
                    Assert.That(cells(engine)[i].State, Is.EqualTo(CellState.Untyped), $"cell {i} was reclaimed on the way past");
            });

            // The retype now starts on the gap, which is inert, and every letter lands on the cell it
            // is meant for. Before the widening this first press landed on the gap as a typo.
            typeCells(engine, anchor, cells(engine).Count);

            Assert.Multiple(() =>
            {
                Assert.That(engine.Mistypes, Is.Zero, "the correction manufactured no mistake of its own");
                Assert.That(engine.BuildResults().Counts[JudgementType.WrongChar], Is.Zero);

                foreach (var cell in cells(engine))
                    Assert.That(cell.State, Is.EqualTo(CellState.Correct));
            });
        }

        /// <summary>
        /// The bound on that widening: a word with ANY cell of its own typed still anchors on its
        /// head, because the collapse can stop there. This is the mid-word shape backlog 244 shipped
        /// and it must not move (see <c>WordInputTest.CollapsingASelectionOverASkipRedeemsItsComboClaim</c>
        /// and <c>SpaceSkipWordTest.AReclaimedSkipGivesTheComboBackToWhereItWouldHaveBeen</c>).
        /// </summary>
        [Test]
        public void AWordWithACellOfItsOwnTypedStillAnchorsOnItsHead()
        {
            var engine = started(longWordMap(), lossless: true);

            typeCells(engine, 0, 4);            // "ab", the gap, then 'c'
            Assert.That(engine.ProcessKey(' ', 1300), Is.True, "the space lands mid-word, giving up d..l");

            Assert.That(engine.RetypeSelectionAnchor, Is.EqualTo(3), "the head of the word, which is typed and can be landed on");

            int erases = eraseBackTo(engine, 3);

            Assert.Multiple(() =>
            {
                Assert.That(erases, Is.EqualTo(2), "the gap, then one press over the abandoned run onto 'c'");
                Assert.That(engine.CaretIndex, Is.EqualTo(3));
            });
        }
    }
}
