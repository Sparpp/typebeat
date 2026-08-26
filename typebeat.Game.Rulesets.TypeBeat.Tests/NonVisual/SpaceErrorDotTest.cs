// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The SPACE ERROR DOT rule (backlog 197), the TypeGG-style marker for a word you left carrying
    /// an error and then spaced past: <see cref="LyricLineDisplay.ComputeSpaceErrorDots"/> is the one
    /// pure function the rendering routes through, so pinning it pins the feature. Display only, off
    /// by default, and nothing here is about a score.
    ///
    /// <para>Cells are hand-built rather than played out of an engine on purpose: the rule reads
    /// exactly three properties (expected char, typeable, state) and the cases that matter are
    /// combinations no single play produces on one line. Pinning the function over those directly
    /// keeps this a test of the rule rather than a second test of the engine.</para>
    /// </summary>
    [TestFixture]
    public class SpaceErrorDotTest
    {
        private static TypingCell cell(char expected, bool typeable, CellState state)
        {
            var c = new TypingCell(expected, typeable, 0, TimingGranularity.Word);
            c.State = state;
            return c;
        }

        /// <summary>An ordinary lyric character.</summary>
        private static TypingCell letter(char expected, CellState state) => cell(expected, true, state);

        /// <summary>The inter-word gap: a TYPEABLE space cell, which is what makes it a boundary.</summary>
        private static TypingCell gap(CellState state) => cell(' ', true, state);

        /// <summary>Punctuation the default stream keeps: not typeable, so it rides inside its word
        /// and is never a boundary. The engine leaves these <see cref="CellState.AutoSkipped"/>.</summary>
        private static TypingCell mark(char expected) => cell(expected, false, CellState.AutoSkipped);

        private static void assertDots(IReadOnlyList<TypingCell> cells, params bool[] expected)
        {
            bool[] actual = LyricLineDisplay.ComputeSpaceErrorDots(cells);

            Assert.That(actual.Length, Is.EqualTo(cells.Count), "one flag per cell");

            for (int i = 0; i < expected.Length; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]), $"cell {i}");
        }

        /// <summary>"ab cd" with the 'a' mistyped and the space accepted: the headline case.</summary>
        [Test]
        public void TestAFlawedWordSpacedPastEarnsADot()
        {
            assertDots(new[]
                {
                    letter('a', CellState.Wrong),
                    letter('b', CellState.Correct),
                    gap(CellState.Correct),
                    letter('c', CellState.Correct),
                    letter('d', CellState.Correct),
                },
                false, false, true, false, false);
        }

        /// <summary>The same line typed cleanly earns nothing: the dot marks an error, not a space.</summary>
        [Test]
        public void TestACleanWordEarnsNothing()
        {
            assertDots(new[]
                {
                    letter('a', CellState.Correct),
                    letter('b', CellState.Correct),
                    gap(CellState.Correct),
                    letter('c', CellState.Correct),
                    letter('d', CellState.Correct),
                },
                false, false, false, false, false);
        }

        /// <summary>
        /// The gap must have been ACCEPTED. An untyped gap has not been spaced past at all, a Wrong
        /// one is already showing the offending character in the error red, and a Missed one was
        /// never reached. None of the three earns a dot however flawed the word before it is, and the
        /// Untyped arm is what makes a backspace over an accepted space take the dot away with it.
        /// </summary>
        [TestCase(CellState.Untyped)]
        [TestCase(CellState.Wrong)]
        [TestCase(CellState.Missed)]
        [TestCase(CellState.Abandoned)]
        public void TestAnUnacceptedGapNeverEarnsADot(CellState gapState)
        {
            assertDots(new[]
                {
                    letter('a', CellState.Wrong),
                    letter('b', CellState.Missed),
                    gap(gapState),
                    letter('c', CellState.Untyped),
                },
                false, false, false, false);
        }

        /// <summary>
        /// What counts as leaving a word flawed: typed wrong, run out of time on, or given up to a
        /// word skip. Abandoned is in the list deliberately, and Untyped and Correct are deliberately
        /// out of it.
        /// </summary>
        [TestCase(CellState.Wrong, true)]
        [TestCase(CellState.Missed, true)]
        [TestCase(CellState.Abandoned, true)]
        [TestCase(CellState.Correct, false)]
        [TestCase(CellState.Untyped, false)]
        public void TestWhichStatesLeaveAWordFlawed(CellState state, bool dotted)
        {
            assertDots(new[]
                {
                    letter('a', CellState.Correct),
                    letter('b', state),
                    gap(CellState.Correct),
                    letter('c', CellState.Correct),
                },
                false, false, dotted, false);
        }

        /// <summary>
        /// State is re-read on every repaint, never remembered from when the skip happened, so
        /// backspacing into an abandoned word (which returns its cells to
        /// <see cref="CellState.Untyped"/>) clears that word's dot with no event of its own.
        /// </summary>
        [Test]
        public void TestReclaimingAnAbandonedWordClearsItsDot()
        {
            var cells = new[]
            {
                letter('a', CellState.Correct),
                letter('b', CellState.Abandoned),
                gap(CellState.Correct),
                letter('c', CellState.Correct),
            };

            Assert.That(LyricLineDisplay.ComputeSpaceErrorDots(cells)[2], Is.True, "the skipped word is dotted");

            cells[1].State = CellState.Untyped;

            Assert.That(LyricLineDisplay.ComputeSpaceErrorDots(cells)[2], Is.False, "reclaiming it clears the dot");
        }

        /// <summary>
        /// Each gap reads its OWN word: the contiguous run of cells back to the previous gap, never
        /// the whole line so far. "ab cd ef" with only the middle word spoiled dots only the gap
        /// after that word.
        /// </summary>
        [Test]
        public void TestEachGapReadsOnlyItsOwnWord()
        {
            assertDots(new[]
                {
                    letter('a', CellState.Correct),
                    letter('b', CellState.Correct),
                    gap(CellState.Correct),
                    letter('c', CellState.Correct),
                    letter('d', CellState.Wrong),
                    gap(CellState.Correct),
                    letter('e', CellState.Correct),
                    letter('f', CellState.Correct),
                },
                false, false, false, false, false, true, false, false);
        }

        /// <summary>
        /// A gap's own state is charged to the boundary and never to the word after it: a spoiled
        /// boundary followed by a cleanly typed word leaves that word's own gap undotted.
        /// </summary>
        [Test]
        public void TestASpoiledGapDoesNotFlawTheNextWord()
        {
            assertDots(new[]
                {
                    letter('a', CellState.Wrong),
                    gap(CellState.Wrong),
                    letter('b', CellState.Correct),
                    gap(CellState.Correct),
                    letter('c', CellState.Correct),
                },
                false, false, false, false, false);
        }

        /// <summary>
        /// Punctuation is not a word boundary (the engine's own <c>isWordGap</c> requires a TYPEABLE
        /// space), so the run reads straight through it, and the
        /// <see cref="CellState.AutoSkipped"/> the engine leaves on it is not a flaw: the player was
        /// never asked to type it.
        /// </summary>
        [Test]
        public void TestPunctuationIsNeitherABoundaryNorAFlaw()
        {
            // "a, b": the mark carries the flaw of 'a' through to the gap.
            assertDots(new[]
                {
                    letter('a', CellState.Wrong),
                    mark(','),
                    gap(CellState.Correct),
                    letter('b', CellState.Correct),
                },
                false, false, true, false);

            // And on its own it flaws nothing.
            assertDots(new[]
                {
                    letter('a', CellState.Correct),
                    mark(','),
                    gap(CellState.Correct),
                    letter('b', CellState.Correct),
                },
                false, false, false, false);
        }

        /// <summary>A one-word line has no gap to draw in, however badly it went, and an empty line
        /// returns an empty result rather than throwing.</summary>
        [Test]
        public void TestALineWithNoGapsHasNoDots()
        {
            assertDots(new[]
                {
                    letter('a', CellState.Wrong),
                    letter('b', CellState.Missed),
                },
                false, false);

            Assert.That(LyricLineDisplay.ComputeSpaceErrorDots(System.Array.Empty<TypingCell>()), Is.Empty);
        }
    }
}
