// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Framework.Utils;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pure math for the Flashlight mod's character window (see
    /// <see cref="LyricLineDisplay.ComputeWindowAlphas"/>): a fixed number of COUNTABLE chars
    /// (typeable, non-space) reach each side of the caret head, spaces and punctuation do not spend
    /// the budget, and the outermost lit char softens only when hidden line lies beyond it.
    /// </summary>
    [TestFixture]
    public class TypeBeatFlashlightWindowTest
    {
        private const float soft = 0.35f;

        private static TypingLine line(string text)
        {
            var source = new LyricLine
            {
                RawText = text,
                StartTime = 0,
                EndTime = 10000,
                SingEndTime = 10000,
                Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 10000 } },
            };

            return TypingLine.FromLyricLine(source);
        }

        private static float[] window(string text, int caretCellIndex, int radius)
            => LyricLineDisplay.ComputeWindowAlphas(line(text).Cells, caretCellIndex, radius, soft);

        private static void assertWindow(float[] actual, params float[] expected)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), "cell count");

            for (int i = 0; i < expected.Length; i++)
                Assert.That(Precision.AlmostEquals(actual[i], expected[i], 1e-4f), Is.True, $"cell {i}: expected {expected[i]}, got {actual[i]}");
        }

        [Test]
        public void TestSpacesDoNotConsumeBudget()
        {
            // "a b c d e", caret before 'c' (cell index 4). Radius 2 must reach exactly two LETTERS
            // each side (a,b left; c,d right) even though four spaces sit between them; the fifth
            // letter 'e' and the trailing space before it stay hidden. 'd' is the outermost lit char
            // with 'e' hidden beyond it, so it softens.
            assertWindow(window("a b c d e", caretCellIndex: 4, radius: 2),
                /* a  */ 1f, /* ' ' */ 1f, /* b */ 1f, /* ' ' */ 1f, /* c */ 1f, /* ' ' */ 1f, /* d */ soft, /* ' ' */ 0f, /* e */ 0f);

            // The same window with the spaces removed lights the SAME five letters' worth of budget:
            // a,b,c,d lit (d soft), e hidden. Spaces changed nothing about which letters are reached.
            assertWindow(window("abcde", caretCellIndex: 2, radius: 2),
                1f, 1f, 1f, soft, 0f);
        }

        [Test]
        public void TestLineStartIsAHardEdge()
        {
            // Caret before the first char: nothing to the left (line start, no darkness beyond) so
            // the left is a hard edge and 'a' stays full alpha; the right shows two chars, outermost soft.
            assertWindow(window("abcde", caretCellIndex: 0, radius: 2),
                1f, soft, 0f, 0f, 0f);
        }

        [Test]
        public void TestLineEndIsAHardEdge()
        {
            // Caret past the last char (line complete): the last two chars are lit, the inner one
            // softens (hidden line to its left) while the final char 'e' stays full (line end, hard).
            assertWindow(window("abcde", caretCellIndex: 5, radius: 2),
                0f, 0f, 0f, soft, 1f);
        }

        [Test]
        public void TestPunctuationDoesNotConsumeBudgetAndLightsBetweenLitChars()
        {
            // Caret before 'd'. Without punctuation: b,c,d,e lit (b,e soft), a and f hidden.
            assertWindow(window("abcdef", caretCellIndex: 3, radius: 2),
                0f, soft, 1f, 1f, soft, 0f);

            // With a comma between c and d it lights the SAME letters (comma spends no budget) and
            // the comma itself, sitting strictly between the two lit chars c and d, stays lit.
            assertWindow(window("abc,def", caretCellIndex: 4, radius: 2),
                /* a */ 0f, /* b */ soft, /* c */ 1f, /* , */ 1f, /* d */ 1f, /* e */ soft, /* f */ 0f);
        }

        [Test]
        public void TestRadiusFiveWindowSpansTenLettersAcrossSpaces()
        {
            // The shipped radius (5) with a spaced sentence: caret partway through, count the lit
            // LETTERS and confirm there are five each side regardless of the spaces between words.
            const string text = "the quick brown fox";
            var t = line(text);

            // Caret sitting just before the 'q' of "quick" (cell index 4).
            int caret = text.IndexOf('q');
            float[] w = LyricLineDisplay.ComputeWindowAlphas(t.Cells, caret, radius: 5, soft);

            int litLettersLeft = 0, litLettersRight = 0;

            for (int i = 0; i < t.Cells.Count; i++)
            {
                var cell = t.Cells[i];
                bool countable = cell.IsTypeable && cell.Expected != ' ';

                if (!countable || w[i] <= 0f)
                    continue;

                if (i < caret)
                    litLettersLeft++;
                else
                    litLettersRight++;
            }

            // Left of the caret the only letters are t,h,e (3). Radius 5 wants five, but the line has
            // just three to the left, so all three light (hard edge at line start). The right side has
            // "quick" and more beyond, so exactly five light there.
            Assert.That(litLettersLeft, Is.EqualTo(3), "all letters left of caret lit (fewer than radius, hard start edge)");
            Assert.That(litLettersRight, Is.EqualTo(5), "exactly five letters lit to the right across the space");
        }
    }
}
