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
    /// The space error dot marks a wrong character typed into a space between words.
    /// <see cref="LyricLineDisplay.ComputeSpaceErrorDots"/> is the rule the renderer uses.
    ///
    /// <para>Cells are hand-built rather than played out of an engine on purpose: the rule reads
    /// exactly four properties (expected char, typeable, state, typed char) and the cases that matter are
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

        /// <summary>A typo in a word does not mark a correctly typed space.</summary>
        [Test]
        public void TestAFlawedWordSpacedPastDoesNotEarnADot()
        {
            assertDots(new[]
                {
                    letter('a', CellState.Wrong),
                    letter('b', CellState.Correct),
                    gap(CellState.Correct),
                    letter('c', CellState.Correct),
                    letter('d', CellState.Correct),
                },
                false, false, false, false, false);
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
        /// An untyped, missed, or abandoned gap has no typed wrong character to mark.
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
        /// A wrong character inside the gap earns a dot even after a skipped word.
        /// </summary>
        [Test]
        public void TestAGapHoldingTypedCharactersEarnsADot()
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

        /// <summary>A resolved gap has no current typo, even if it remembers an earlier character.</summary>
        [Test]
        public void TestPayingTheSpaceClearsTheDot()
        {
            var cells = new[]
            {
                letter('a', CellState.Wrong),
                letter('b', CellState.Correct),
                gap(CellState.Correct),
                letter('c', CellState.Correct),
            };

            cells[2].TypedChar = 'x';

            assertDots(cells, false, false, false, false);
        }

        /// <summary>
        /// The gap draws the dot instead of its wrong character. With the marker on the dot is the
        /// whole mark and the gap goes blank; with the marker off the character is the only thing that can show a
        /// typo in a space at all, so it comes back and is replaced by each further press (the engine
        /// keeps one character on the cell, so one backspace erases it and one cell carries the miss).
        /// </summary>
        [Test]
        public void TestTheDotReplacesTheCharacterRatherThanStackingOnIt()
        {
            var flawedGap = gap(CellState.Wrong);
            flawedGap.TypedChar = 'x';

            // Marker on, gap dotted: blank. If no dot is drawn, the typed character
            // remains visible so an undotted gap typo can still be seen.
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
        /// A word's state never dots a correctly typed following space.
        /// </summary>
        [TestCase(CellState.Wrong)]
        [TestCase(CellState.Missed)]
        [TestCase(CellState.Abandoned)]
        [TestCase(CellState.Correct)]
        [TestCase(CellState.Untyped)]
        public void TestWordStatesDoNotDotAnAcceptedGap(CellState state)
        {
            assertDots(new[]
                {
                    letter('a', CellState.Correct),
                    letter('b', state),
                    gap(CellState.Correct),
                    letter('c', CellState.Correct),
                },
                false, false, false, false);
        }

        /// <summary>
        /// A skipped word leaves no dot on the space passed during the skip.
        /// </summary>
        [Test]
        public void TestSkippingAndReclaimingAWordLeavesNoDot()
        {
            var cells = new[]
            {
                letter('a', CellState.Correct),
                letter('b', CellState.Abandoned),
                gap(CellState.Correct),
                letter('c', CellState.Correct),
            };

            Assert.That(LyricLineDisplay.ComputeSpaceErrorDots(cells)[2], Is.False, "skipping is not typing in the gap");

            cells[1].State = CellState.Untyped;

            Assert.That(LyricLineDisplay.ComputeSpaceErrorDots(cells)[2], Is.False, "reclaiming stays undotted");
        }

        /// <summary>
        /// A typo inside a word does not mark either surrounding space.
        /// </summary>
        [Test]
        public void TestAWordTypoDoesNotMarkSurroundingSpaces()
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
                false, false, false, false, false, false, false, false);
        }

        /// <summary>
        /// A gap's own state is charged to the boundary and never to the word after it: a spoiled
        /// boundary carries its OWN dot (the mistake is the space) while the cleanly typed word after
        /// it leaves that word's gap undotted.
        /// </summary>
        [Test]
        public void TestASpoiledGapDoesNotFlawTheNextWord()
        {
            var cells = new[]
                {
                    letter('a', CellState.Wrong),
                    gap(CellState.Wrong),
                    letter('b', CellState.Correct),
                    gap(CellState.Correct),
                    letter('c', CellState.Correct),
                };
            cells[1].TypedChar = 'x';
            assertDots(cells, false, true, false, false, false);
        }

        /// <summary>
        /// Neither a lyric typo nor punctuation marks an accepted space.
        /// </summary>
        [Test]
        public void TestPunctuationIsNeitherABoundaryNorAFlaw()
        {
            // "a, b": the lyric typo does not mark the gap.
            assertDots(new[]
                {
                    letter('a', CellState.Wrong),
                    mark(','),
                    gap(CellState.Correct),
                    letter('b', CellState.Correct),
                },
                false, false, false, false);

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
