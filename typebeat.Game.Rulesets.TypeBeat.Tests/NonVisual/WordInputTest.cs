// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 182: the two WORD-LEVEL editing gestures every typing site has, brought into live
// gameplay. Ctrl+Backspace erases the previous word (the gaps behind the caret, then the word behind
// them); Ctrl+A offers back the run from the caret to the start of the word holding the EARLIEST
// unfixed typo (backlog 184 inverted that from the nearest), so every mistake is retyped in one go.
//
// The engine's whole share of that is TWO PURE QUERIES, TypingEngine.WordBackspaceTarget and
// TypingEngine.RetypeSelectionAnchor, which say where each gesture stops and mutate nothing. This
// file pins them, and pins the COMPOSITION the input layer builds on top of them (a run of
// ProcessBackspace calls plus at most one ProcessKey), because that composition is the reason the
// feature needs no new replay frame and no new era bit: a stored run holds exactly the engine calls
// the live run made. TestSceneTypeBeatWordInput drives the same gestures through the real key
// handler and proves the recorded replay re-derives them.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class WordInputTest
    {
        #region Fixture

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        /// <summary>
        /// "ab cd ef": cells a(1000) b(1500) ' '(2000) c(2000) d(2500) ' '(3000) e(3000) f(3500),
        /// so the word gaps are cells 2 and 5 and the words start at 0, 3 and 6. THREE words, not
        /// two, because the load-bearing property of Ctrl+Backspace is that it takes exactly one of
        /// them. The line runs to 60000 so nothing seals mid-test.
        /// </summary>
        private static LyricBeatmap abCdEf() => new LyricBeatmap
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
                    RawText = "ab cd ef",
                    StartTime = 1000,
                    EndTime = 60000,
                    SingEndTime = 4000,
                    Units = new[] { unit("ab", 1000, 2000), unit("cd", 2000, 3000), unit("ef", 3000, 4000) },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        /// <summary>An engine on the fixture with the line active and the live (backlog 181) input
        /// model selected, so a wrong key lands on lyric cells AND on word gaps.</summary>
        private static TypingEngine started()
        {
            var engine = new TypingEngine(abCdEf()) { WrongInputOnWordGaps = true };
            engine.Update(1000);
            Assert.That(engine.ActiveLineIndex, Is.Zero);
            return engine;
        }

        private static IReadOnlyList<TypingCell> cells(TypingEngine engine) => engine.Lines[0].Cells;

        /// <summary>Type a prefix of the line correctly, one cell per char, at each cell's target.</summary>
        private static TypingEngine typed(string prefix)
        {
            var engine = started();

            for (int i = 0; i < prefix.Length; i++)
                Assert.That(engine.ProcessKey(prefix[i], cells(engine)[i].TargetTime), Is.True, $"typing '{prefix[i]}' at cell {i}");

            Assert.That(engine.CaretIndex, Is.EqualTo(prefix.Length));
            return engine;
        }

        /// <summary>
        /// EXACTLY the loop <c>TypeBeatKeyHandler.eraseBackTo</c> runs, mirrored here so the headless
        /// tests below exercise the same composition the real gesture does. Returns how many erases
        /// the run made, which is how many BACKSPACE frames a live run would have recorded.
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

        #endregion

        /// <summary>The fixture's own shape, asserted rather than trusted: every index below is read
        /// off this layout.</summary>
        [Test]
        public void TheFixtureIsThreeWordsAroundTwoGaps()
        {
            var c = cells(started());

            Assert.Multiple(() =>
            {
                Assert.That(c.Select(x => x.Expected), Is.EqualTo(new[] { 'a', 'b', ' ', 'c', 'd', ' ', 'e', 'f' }));
                Assert.That(c.Select(x => x.TargetTime), Is.EqualTo(new[] { 1000d, 1500, 2000, 2000, 2500, 3000, 3000, 3500 }));
                Assert.That(c[2].IsTypeable && c[5].IsTypeable, Is.True, "both gaps are typeable cells");
            });
        }

        // -----------------------------------------------------------------------------------------
        // Ctrl+Backspace: where the word ends
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// At the head of the line there is nothing behind the caret, so the query answers the caret
        /// itself. That is what makes the composed gesture a NO-OP rather than a special case: the
        /// loop never runs, the engine is never called, and nothing is recorded.
        /// </summary>
        [Test]
        public void AtTheHeadOfTheLineTheGestureIsANoOp()
        {
            var engine = started();

            Assert.Multiple(() =>
            {
                Assert.That(engine.WordBackspaceTarget, Is.Zero);
                Assert.That(engine.CaretIndex, Is.Zero);
                Assert.That(eraseBackTo(engine, engine.WordBackspaceTarget), Is.Zero, "nothing to erase, so nothing recorded");
                Assert.That(engine.RetypeSelectionAnchor, Is.EqualTo(-1), "and nothing to select");
            });
        }

        /// <summary>
        /// Caret MID-WORD: back to the start of the word it is inside, and no further. Three cells
        /// typed of "ab cd" leaves the caret on 'd' with "c" behind it inside the same word.
        /// </summary>
        [Test]
        public void MidWordItErasesBackToThatWordsStart()
        {
            var engine = typed("ab c");

            Assert.That(engine.WordBackspaceTarget, Is.EqualTo(3), "the start of \"cd\"");

            int erases = eraseBackTo(engine, engine.WordBackspaceTarget);

            Assert.Multiple(() =>
            {
                Assert.That(erases, Is.EqualTo(1));
                Assert.That(engine.CaretIndex, Is.EqualTo(3));
                Assert.That(cells(engine)[3].State, Is.EqualTo(CellState.Untyped));
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Correct), "the gap in front of the word survives");
            });
        }

        /// <summary>
        /// Caret at a WORD START (the gap immediately behind it): the gap goes AND the word before
        /// it, which is the behaviour that makes holding the gesture walk back one word at a time
        /// instead of stalling on every space. It takes exactly ONE word, not everything behind: from
        /// the head of "ef" the caret lands on the head of "cd", with "ab" untouched.
        /// </summary>
        [Test]
        public void AtAWordStartItTakesTheGapAndThePreviousWord()
        {
            var engine = typed("ab cd ");

            Assert.That(engine.CaretIndex, Is.EqualTo(6), "at the head of \"ef\"");
            Assert.That(engine.WordBackspaceTarget, Is.EqualTo(3), "the head of \"cd\"");

            int erases = eraseBackTo(engine, engine.WordBackspaceTarget);

            Assert.Multiple(() =>
            {
                Assert.That(erases, Is.EqualTo(3), "the gap, then 'd', then 'c'");
                Assert.That(engine.CaretIndex, Is.EqualTo(3));
                Assert.That(cells(engine).Take(2).Select(x => x.State), Is.EqualTo(new[] { CellState.Correct, CellState.Correct }), "\"ab\" is untouched");
                Assert.That(cells(engine).Skip(3).Take(3).Select(x => x.State),
                    Is.EqualTo(new[] { CellState.Untyped, CellState.Untyped, CellState.Untyped }));
            });
        }

        /// <summary>
        /// At the END of a fully typed line, which is where a player who has just finished the line
        /// stands: the last word goes. The caret sits past the last cell, so this also pins that the
        /// query survives an index equal to the cell count.
        /// </summary>
        [Test]
        public void AtTheEndOfTheLineItTakesTheLastWord()
        {
            var engine = typed("ab cd ef");

            Assert.That(engine.IsLineComplete, Is.True);
            Assert.That(engine.WordBackspaceTarget, Is.EqualTo(6));

            Assert.That(eraseBackTo(engine, engine.WordBackspaceTarget), Is.EqualTo(2));
            Assert.That(engine.CaretIndex, Is.EqualTo(6));
        }

        /// <summary>
        /// Two gestures in a row walk two words back, which is what holding the key down does (the
        /// key handler honours OS repeat for Ctrl+Backspace exactly as it does for the plain key).
        /// The second one starts from a caret the first one left at a word head, so this is the
        /// gap-plus-word case chained onto the mid-word one.
        /// </summary>
        [Test]
        public void HoldingItWalksBackWordByWordToTheHeadOfTheLine()
        {
            var engine = typed("ab cd ef");

            eraseBackTo(engine, engine.WordBackspaceTarget);
            Assert.That(engine.CaretIndex, Is.EqualTo(6));

            eraseBackTo(engine, engine.WordBackspaceTarget);
            Assert.That(engine.CaretIndex, Is.EqualTo(3));

            eraseBackTo(engine, engine.WordBackspaceTarget);
            Assert.That(engine.CaretIndex, Is.Zero);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine).All(x => x.State == CellState.Untyped), Is.True, "the whole line is open again");
                Assert.That(engine.WordBackspaceTarget, Is.Zero, "and a fourth press does nothing");
                Assert.That(eraseBackTo(engine, engine.WordBackspaceTarget), Is.Zero);
            });
        }

        /// <summary>
        /// A word given up to a word skip (backlog 167) is reclaimed by the gesture exactly as a
        /// plain backspace reclaims it: ONE ProcessBackspace steps transparently back over the
        /// abandoned cells to the last character actually typed. The composed loop therefore lands
        /// PAST its own target here, which is deliberate and documented on the query: the target is a
        /// floor, and the existing reclaim rule wins over it.
        /// </summary>
        [Test]
        public void ItReclaimsASkippedWordInOnePressLikeThePlainKey()
        {
            var engine = started();
            engine.SpaceSkipsWord = true;

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey(' ', 1200), Is.True, "the space abandons the rest of \"ab\" and takes the gap");

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[1].State, Is.EqualTo(CellState.Abandoned));
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Correct));
                Assert.That(engine.CaretIndex, Is.EqualTo(3), "past the gap, at the head of \"cd\"");
                Assert.That(engine.WordBackspaceTarget, Is.Zero, "the gap, then the word behind it");
            });

            int erases = eraseBackTo(engine, engine.WordBackspaceTarget);

            Assert.Multiple(() =>
            {
                Assert.That(erases, Is.EqualTo(2), "the gap, then one press stepping over the phantom cell onto 'a'");
                Assert.That(engine.CaretIndex, Is.Zero);
                Assert.That(cells(engine)[1].State, Is.EqualTo(CellState.Untyped), "the abandoned cell was reclaimed");
                Assert.That(cells(engine)[0].State, Is.EqualTo(CellState.Untyped), "and 'a' was erased");
            });
        }

        // -----------------------------------------------------------------------------------------
        // Ctrl+A: where the retype starts
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Nothing wrong behind the caret, nothing to offer: -1, and the gesture is a no-op. Asserted
        /// on a clean run AND on one whose typo has already been backspaced away, because the query
        /// reads cell STATE and not the history of the run.
        /// </summary>
        [Test]
        public void WithNoTypoBehindTheCaretThereIsNoAnchor()
        {
            Assert.That(typed("ab cd").RetypeSelectionAnchor, Is.EqualTo(-1));

            var fixedUp = started();
            Assert.That(fixedUp.ProcessKey('x', 1000), Is.True, "a typo on cell 0");
            Assert.That(fixedUp.ProcessBackspace(), Is.True);

            Assert.That(fixedUp.RetypeSelectionAnchor, Is.EqualTo(-1), "an erased typo is not an unfixed one");
        }

        /// <summary>
        /// The headline: a typo two words back anchors on THAT word's start, so the selection is
        /// [word start, caret) and retyping the run from there fixes it.
        /// </summary>
        [Test]
        public void ATypoAnchorsOnItsOwnWordsStart()
        {
            var engine = started();

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('x', 1500), Is.True, "wrong for 'b'");
            Assert.That(engine.ProcessKey(' ', 2000), Is.True);
            Assert.That(engine.ProcessKey('c', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[1].State, Is.EqualTo(CellState.Wrong));
                Assert.That(engine.CaretIndex, Is.EqualTo(4));
                Assert.That(engine.RetypeSelectionAnchor, Is.Zero, "the head of \"ab\"");
            });
        }

        /// <summary>
        /// A typo in the word the player is HALFWAY THROUGH typing anchors on that word's start,
        /// which is the common case. The word behind it was typed correctly, so it holds no typo and
        /// the scan walks straight past it: "earliest" means the earliest UNFIXED one, and a word
        /// already right is never dragged in.
        /// </summary>
        [Test]
        public void ATypoInTheCurrentWordAnchorsOnTheCurrentWord()
        {
            var engine = started();

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 1500), Is.True);
            Assert.That(engine.ProcessKey(' ', 2000), Is.True);
            Assert.That(engine.ProcessKey('z', 2000), Is.True, "wrong for 'c'");
            Assert.That(engine.ProcessKey('d', 2500), Is.True);

            Assert.That(engine.RetypeSelectionAnchor, Is.EqualTo(3), "the head of \"cd\", not of \"ab\"");
        }

        /// <summary>
        /// With typos in two different words the EARLIEST one wins (backlog 184 inverted this): the
        /// gesture is "fix my mistakes" and it is one keystroke, so the selection covers every unfixed
        /// typo behind the caret rather than the shortest retype that fixes something. Offering the
        /// nearest made the gesture a loop the player could not see the end of, and the cells in
        /// between cost nothing to retype, since a correct cell re-typed is scoring-inert.
        /// </summary>
        [Test]
        public void TheEarliestTypoBehindTheCaretWins()
        {
            var engine = started();

            Assert.That(engine.ProcessKey('x', 1000), Is.True, "wrong for 'a'");
            Assert.That(engine.ProcessKey('b', 1500), Is.True);
            Assert.That(engine.ProcessKey(' ', 2000), Is.True);
            Assert.That(engine.ProcessKey('z', 2000), Is.True, "wrong for 'c'");
            Assert.That(engine.ProcessKey('d', 2500), Is.True);

            Assert.That(engine.RetypeSelectionAnchor, Is.Zero, "the head of \"ab\": the earlier typo's word");
        }

        /// <summary>
        /// The same rule across the whole line, with a good word in the middle: typos in words one and
        /// three anchor on word one, so a single consume walks back over "cd" (free, being correct
        /// cells) and reaches both mistakes.
        /// </summary>
        [Test]
        public void TyposInTheFirstAndLastWordsAnchorOnTheFirst()
        {
            var engine = started();

            Assert.That(engine.ProcessKey('x', 1000), Is.True, "wrong for 'a'");
            Assert.That(engine.ProcessKey('b', 1500), Is.True);
            Assert.That(engine.ProcessKey(' ', 2000), Is.True);
            Assert.That(engine.ProcessKey('c', 2000), Is.True);
            Assert.That(engine.ProcessKey('d', 2500), Is.True);
            Assert.That(engine.ProcessKey(' ', 3000), Is.True);
            Assert.That(engine.ProcessKey('q', 3000), Is.True, "wrong for 'e'");

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[0].State, Is.EqualTo(CellState.Wrong));
                Assert.That(cells(engine)[6].State, Is.EqualTo(CellState.Wrong));
                Assert.That(engine.RetypeSelectionAnchor, Is.Zero, "the head of \"ab\", not of \"ef\"");
            });
        }

        /// <summary>
        /// A typo on the WORD GAP (possible since backlog 181, where a wrong letter lands in the gap
        /// cell) anchors on the GAP itself, not on the word in front of it. The gap is the cell that
        /// has to be retyped and it belongs to no word, so walking back from it would swallow a
        /// perfectly good word for nothing.
        /// </summary>
        [Test]
        public void AGapTypoAnchorsOnTheGapItself()
        {
            var engine = started();

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 1500), Is.True);
            Assert.That(engine.ProcessKey('x', 2000), Is.True, "wrong on the gap");
            Assert.That(engine.ProcessKey('c', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Wrong));
                Assert.That(engine.RetypeSelectionAnchor, Is.EqualTo(2), "the gap, so \"ab\" is left alone");
            });
        }

        /// <summary>
        /// The selection is always non-empty when it exists: the typo is strictly behind the caret,
        /// so the anchor is too, and a consume can never be a mass backspace of zero cells.
        /// </summary>
        [Test]
        public void AnAnchorIsAlwaysStrictlyBehindTheCaret()
        {
            var engine = started();
            Assert.That(engine.ProcessKey('x', 1000), Is.True);

            Assert.That(engine.RetypeSelectionAnchor, Is.LessThan(engine.CaretIndex));
        }

        // -----------------------------------------------------------------------------------------
        // Consuming a selection: composed out of the same two engine calls
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The consume path in full: mass backspace to the anchor, then the letter at the anchor
        /// through the ordinary judged <see cref="TypingEngine.ProcessKey"/>. The typo's cell is open
        /// again, the corrected retype earns its real judgement, and the streak the typo broke comes
        /// back through the existing backlog 140 machinery, because nothing about this path is new to
        /// the engine.
        /// </summary>
        [Test]
        public void ConsumingASelectionErasesToTheAnchorAndTypesThere()
        {
            var engine = started();

            int? restored = null;
            engine.ComboRestored += amount => restored = amount;

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('b', 1500), Is.True);
            Assert.That(engine.ProcessKey(' ', 2000), Is.True);
            Assert.That(engine.ProcessKey('z', 2000), Is.True, "wrong for 'c', breaking a streak of 3");
            Assert.That(engine.ProcessKey('d', 2500), Is.True);

            int anchor = engine.RetypeSelectionAnchor;
            Assert.That(anchor, Is.EqualTo(3));

            int erases = eraseBackTo(engine, anchor);

            Assert.Multiple(() =>
            {
                Assert.That(erases, Is.EqualTo(2), "'d' and the typo");
                Assert.That(engine.CaretIndex, Is.EqualTo(3));
                Assert.That(cells(engine)[3].State, Is.EqualTo(CellState.Untyped));
                Assert.That(cells(engine)[4].State, Is.EqualTo(CellState.Untyped));
            });

            Assert.That(engine.ProcessKey('c', 2000), Is.True);

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine)[3].State, Is.EqualTo(CellState.Correct));
                Assert.That(restored, Is.EqualTo(3), "the streak the typo broke comes back at the fix");
                // 1 (the 'd' typed after the typo) + the 3 restored + this press's own increment.
                Assert.That(engine.Combo, Is.EqualTo(5));
            });
        }

        /// <summary>
        /// A selection spanning cells that were CORRECT resets them to Untyped, and retyping them is
        /// scoring-inert: a cell's first correct delta survives the erase, so the
        /// second pass over them adds no score, no combo and no accuracy. Existing engine behaviour
        /// (it is what stops backspace-retype farming), pinned here because Ctrl+A is the first
        /// gesture that walks a player back over good characters in one keystroke.
        /// </summary>
        [Test]
        public void RetypingCorrectCellsInsideASelectionCostsAndEarnsNothing()
        {
            var engine = started();

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('x', 1500), Is.True, "wrong for 'b'");

            long score = engine.Score;
            double accuracy = engine.LiveAccuracy;
            int combo = engine.Combo;

            int anchor = engine.RetypeSelectionAnchor;
            Assert.That(anchor, Is.Zero, "the selection covers the correct 'a' as well as the typo");

            Assert.That(eraseBackTo(engine, anchor), Is.EqualTo(2));
            Assert.That(engine.ProcessKey('a', 9000), Is.True, "retyped, and absurdly late");

            Assert.Multiple(() =>
            {
                Assert.That(engine.Score, Is.EqualTo(score), "an inert retype scores nothing");
                Assert.That(engine.LiveAccuracy, Is.EqualTo(accuracy), "and costs nothing");
                Assert.That(engine.Combo, Is.EqualTo(combo));
                Assert.That(cells(engine)[0].JudgedDelta, Is.Zero, "the first correct judgement still stands");
            });
        }

        /// <summary>
        /// A selection reaching back over cells a word skip ABANDONED collapses through them in the
        /// usual transparent way: <see cref="TypingEngine.ProcessBackspace"/> reclaims them on its
        /// way past, so the run never stalls on a phantom cell and never has to know it was there.
        /// </summary>
        [Test]
        public void ASelectionCollapsesThroughAbandonedCells()
        {
            var engine = started();
            engine.SpaceSkipsWord = true;

            Assert.That(engine.ProcessKey('x', 1000), Is.True, "wrong for 'a'");
            Assert.That(engine.ProcessKey(' ', 1200), Is.True, "the space abandons the rest of \"ab\" and takes the gap");
            Assert.That(cells(engine)[1].State, Is.EqualTo(CellState.Abandoned));

            Assert.That(engine.ProcessKey('c', 2000), Is.True);

            int anchor = engine.RetypeSelectionAnchor;
            Assert.That(anchor, Is.Zero, "the typo is at the head of \"ab\"");

            Assert.That(eraseBackTo(engine, anchor), Is.EqualTo(3), "'c', the gap, then one press over the phantom cell onto the typo");

            Assert.Multiple(() =>
            {
                Assert.That(engine.CaretIndex, Is.Zero);
                Assert.That(cells(engine)[0].State, Is.EqualTo(CellState.Untyped), "the typo is gone");
                Assert.That(cells(engine)[1].State, Is.EqualTo(CellState.Untyped), "the phantom cell was reclaimed");
                Assert.That(cells(engine)[2].State, Is.EqualTo(CellState.Untyped));
                Assert.That(cells(engine)[3].State, Is.EqualTo(CellState.Untyped));
            });
        }

        // -----------------------------------------------------------------------------------------
        // Neither query mutates anything
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Both queries are PURE, which is what lets them be mirrored into the server's JS engine
        /// port as plain functions and what keeps the whole feature inside the input layer. Reading
        /// each of them a hundred times over a mid-run engine must leave every observable exactly
        /// where it was.
        /// </summary>
        [Test]
        public void BothQueriesArePure()
        {
            var engine = started();

            Assert.That(engine.ProcessKey('a', 1000), Is.True);
            Assert.That(engine.ProcessKey('x', 1500), Is.True);
            Assert.That(engine.ProcessKey(' ', 2000), Is.True);

            var before = cells(engine).Select(c => (c.State, c.TypedChar, c.JudgedDelta)).ToArray();
            long score = engine.Score;
            int caret = engine.CaretIndex;
            int combo = engine.Combo;

            for (int i = 0; i < 100; i++)
            {
                Assert.That(engine.WordBackspaceTarget, Is.EqualTo(0));
                Assert.That(engine.RetypeSelectionAnchor, Is.EqualTo(0));
            }

            Assert.Multiple(() =>
            {
                Assert.That(cells(engine).Select(c => (c.State, c.TypedChar, c.JudgedDelta)), Is.EqualTo(before));
                Assert.That(engine.Score, Is.EqualTo(score));
                Assert.That(engine.CaretIndex, Is.EqualTo(caret));
                Assert.That(engine.Combo, Is.EqualTo(combo));
            });
        }

        /// <summary>
        /// With no active line (the pre-roll here) both queries answer harmlessly rather than
        /// throwing or indexing a line that is not there: the caret index back, and no anchor. The
        /// key handler gates on <see cref="TypingEngine.LineIsActive"/> before it ever asks, so this
        /// is belt and braces for the JS port as much as for here.
        /// </summary>
        [Test]
        public void WithNoActiveLineTheQueriesAreInert()
        {
            var engine = new TypingEngine(abCdEf());
            engine.Update(0);

            Assert.Multiple(() =>
            {
                Assert.That(engine.ActiveLineIndex, Is.EqualTo(-1));
                Assert.That(engine.WordBackspaceTarget, Is.EqualTo(engine.CaretIndex));
                Assert.That(engine.RetypeSelectionAnchor, Is.EqualTo(-1));
            });
        }
    }
}
