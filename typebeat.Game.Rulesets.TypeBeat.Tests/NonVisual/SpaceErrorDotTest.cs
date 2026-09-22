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
            var c = new TypingCell(expected, typeable, 0);
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
        /// The gap must have been DEALT WITH for the WORD's dot. An untyped gap has not been spaced
        /// past at all, a Missed one was never reached, and an Abandoned one was given up without the
        /// space being paid. None of the three earns the word's dot however flawed the word before it
        /// is, and the Untyped arm is what makes a backspace over an accepted space take the dot away
        /// with it.
        ///
        /// <para>A WRONG gap is deliberately NOT in this list any more: it carries its own error, and
        /// the dot is the marker for that (see <see cref="TestAMistypedGapEarnsItsOwnDotFromACleanWord"/>).
        /// It used to be here on the argument that the red character already showed the mistake, which
        /// stopped being true once the marker setting replaced that character with the dot.</para>
        /// </summary>
        [TestCase(CellState.Untyped)]
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
        /// A GAP THAT HOLDS TYPED CHARACTERS STILL CARRIES ITS WORD'S DOT. Under SpaceSkipsWord a
        /// wrong letter on a word gap PARKS the caret there and subsequent letters overwrite the same
        /// character, so the gap sits <see cref="CellState.Wrong"/> with a <see cref="TypingCell.TypedChar"/>
        /// until the player pays the space it is owed. That is a gap the player has already dealt
        /// with, and the word behind it is still flawed: the dot has to survive, or the marker for an
        /// error the player has yet to fix is hidden by the debris of the error itself. The arm above
        /// still holds for a Wrong gap with NO character in it, which is the state no play produces.
        /// </summary>
        [Test]
        public void TestAGapHoldingTypedCharactersKeepsItsWordsDot()
        {
            var cells = new[]
            {
                letter('a', CellState.Abandoned),
                letter('b', CellState.Abandoned),
                gap(CellState.Wrong),
                letter('c', CellState.Untyped),
            };

            cells[2].TypedChar = 'x';

            assertDots(cells, false, false, true, false);
        }

        /// <summary>
        /// A MASHED SPACE EARNS ITS OWN DOT, with every word around it clean. Under Space to Skip a
        /// wrong letter on a word gap parks the caret there and the error lives in the SPACE, not in
        /// the word behind it - so the marker has to come from the gap's own TypedChar. Requiring a
        /// flawed word first meant a player who mistyped straight onto the gap saw no dot at all and
        /// only the red character (which the dot setting is supposed to replace).
        /// </summary>
        [Test]
        public void TestAMistypedGapEarnsItsOwnDotFromACleanWord()
        {
            var cells = new[]
            {
                letter('a', CellState.Correct),
                letter('b', CellState.Correct),
                gap(CellState.Wrong),
                letter('c', CellState.Untyped),
            };

            cells[2].TypedChar = 'q';

            assertDots(cells, false, false, true, false);
        }

        /// <summary>
        /// A CLEANLY PASSED WORD EARNS NOTHING - including when the space itself was typed. A correct
        /// space press records its character on the cell exactly as a wrong one does (the engine keeps
        /// <see cref="TypingCell.TypedChar"/> through every resolution), so a rule that read "the gap
        /// holds a character" dotted every word a player simply spaced past. Only the STATE says the
        /// gap holds a mistake.
        /// </summary>
        [Test]
        public void TestAWordSpacedPastCleanlyEarnsNoDot()
        {
            var cells = new[]
            {
                letter('a', CellState.Correct),
                letter('b', CellState.Correct),
                gap(CellState.Correct),
                letter('c', CellState.Correct),
            };

            cells[2].TypedChar = ' ';

            assertDots(cells, false, false, false, false);
        }

        /// <summary>
        /// THE BOUNCE CURVE a new mistype starts on the dot: undisturbed at both ends of the pulse,
        /// swollen at the peak, and smooth through it. Pinned as a pure function because the animation
        /// itself has no state worth asserting beyond "it was started" and "it is over".
        /// </summary>
        [Test]
        public void TestTheDotPulseCurveStartsAndEndsUndisturbed()
        {
            Assert.That(LyricLineDisplay.SpaceErrorDotPulseScale(0), Is.EqualTo(1f).Within(1e-6), "the pulse starts at rest");
            Assert.That(LyricLineDisplay.SpaceErrorDotPulseScale(LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS), Is.EqualTo(1f), "and ends at rest");
            Assert.That(LyricLineDisplay.SpaceErrorDotPulseScale(LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS * 2f), Is.EqualTo(1f), "past the end it is over");
            Assert.That(LyricLineDisplay.SpaceErrorDotPulseScale(-5f), Is.EqualTo(1f), "and a negative elapsed time cannot shrink it");

            float peak = LyricLineDisplay.SpaceErrorDotPulseScale(LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS / 2f);

            Assert.That(peak, Is.EqualTo(1f + LyricLineDisplay.SPACE_ERROR_DOT_PULSE_SCALE).Within(1e-6), "the midpoint is the peak");
            Assert.That(peak, Is.GreaterThan(1f), "the dot expands, never shrinks");

            // Monotone up to the peak and back down, so the motion reads as one bounce.
            float previous = 1f;

            for (float t = 0; t <= LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS / 2f; t += LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS / 20f)
            {
                float scale = LyricLineDisplay.SpaceErrorDotPulseScale(t);
                Assert.That(scale, Is.GreaterThanOrEqualTo(previous - 1e-6), $"rising at {t} ms");
                previous = scale;
            }

            for (float t = LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS / 2f; t <= LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS; t += LyricLineDisplay.SPACE_ERROR_DOT_PULSE_MS / 20f)
            {
                float scale = LyricLineDisplay.SpaceErrorDotPulseScale(t);
                Assert.That(scale, Is.LessThanOrEqualTo(previous + 1e-6), $"falling at {t} ms");
                previous = scale;
            }
        }

        /// <summary>And the same gap, once the space is paid and the character is stepped over:
        /// Correct with the typed character still recorded, which already earned its dot.</summary>
        [Test]
        public void TestPayingTheSpaceKeepsTheDot()
        {
            var cells = new[]
            {
                letter('a', CellState.Wrong),
                letter('b', CellState.Correct),
                gap(CellState.Correct),
                letter('c', CellState.Correct),
            };

            cells[2].TypedChar = 'x';

            assertDots(cells, false, false, true, false);
        }

        /// <summary>
        /// THE GAP DRAWS THE DOT *INSTEAD OF* THE CHARACTER. Two markers in one slot is what the
        /// player saw before this rule existed: the dot for the flawed word behind, with the red
        /// mistyped character stacked on top of it. With the marker on the dot is the whole mark and
        /// the gap goes blank; with the marker off the character is the only thing that can show a
        /// typo in a space at all, so it comes back and is replaced by each further press (the engine
        /// keeps one character on the cell, so one backspace erases it and one cell carries the miss).
        /// </summary>
        [Test]
        public void TestTheDotReplacesTheCharacterRatherThanStackingOnIt()
        {
            var flawedGap = gap(CellState.Wrong);
            flawedGap.TypedChar = 'x';

            // Marker on, gap dotted: blank. Marker on, gap NOT dotted (its word was clean): the
            // character, which is the only way a typo in a space can be seen with no dot to show it.
            Assert.That(LyricLineDisplay.GapGlyph(spaceErrorDotsEnabled: true, dotted: true, ' ', CellState.Wrong, 'x'),
                Is.EqualTo(' '), "the dot is the whole mark");
            Assert.That(LyricLineDisplay.GapGlyph(spaceErrorDotsEnabled: true, dotted: false, ' ', CellState.Wrong, 'x'),
                Is.EqualTo('x'), "an undotted gap still shows its typo");

            // Marker off: the typed character, replaced as further presses arrive.
            Assert.That(LyricLineDisplay.GapGlyph(spaceErrorDotsEnabled: false, dotted: false, ' ', CellState.Wrong, 'x'),
                Is.EqualTo('x'));
            flawedGap.TypedChar = 'y';
            Assert.That(LyricLineDisplay.GapGlyph(spaceErrorDotsEnabled: false, dotted: true, ' ', CellState.Wrong, 'y'),
                Is.EqualTo('y'), "the latest press replaces the character; it never accumulates");

            // A gap in any other state, and a letter, are untouched by the setting.
            Assert.That(LyricLineDisplay.GapGlyph(spaceErrorDotsEnabled: true, dotted: true, ' ', CellState.Correct, 'x'),
                Is.EqualTo(' '), "a resolved gap with no typo has no character to show");
            Assert.That(LyricLineDisplay.GapGlyph(spaceErrorDotsEnabled: true, dotted: true, 'a', CellState.Wrong, 'a'),
                Is.EqualTo('a'), "lyric cells never route through the gap rule");
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
        /// boundary carries its OWN dot (the mistake is the space) while the cleanly typed word after
        /// it leaves that word's gap undotted.
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
                false, true, false, false, false);
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
