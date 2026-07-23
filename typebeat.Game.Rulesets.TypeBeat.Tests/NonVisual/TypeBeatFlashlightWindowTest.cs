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
    /// <see cref="LyricLineDisplay.ComputeWindowAlphas(System.Collections.Generic.IReadOnlyList{TypingCell}, int, int, float)"/>
    /// and <see cref="LyricLineDisplay.ComputeStreamWindows"/>): a fixed number of COUNTABLE chars
    /// (typeable, non-space) reach each side of the caret head, spaces and punctuation do not spend
    /// the budget, the outermost lit char softens only when hidden line lies beyond it, and the budget
    /// spills across line boundaries when the stack is read as one continuous stream.
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

        // --- Stream-level window: the budget spills across line boundaries ---

        private static void assertWin(LineWindow w, int lo, int hi, bool softLeft, bool softRight)
        {
            Assert.That(w.Lo, Is.EqualTo(lo), "Lo");
            Assert.That(w.Hi, Is.EqualTo(hi), "Hi");
            Assert.That(w.SoftLeft, Is.EqualTo(softLeft), "SoftLeft");
            Assert.That(w.SoftRight, Is.EqualTo(softRight), "SoftRight");
        }

        [Test]
        public void TestStreamSpillsIntoNextLineWhenUncapped()
        {
            // Two 5-letter lines. Caret near the end of line 0 (4 countable before it, i.e. before the
            // 5th letter). Radius 5, no right cap (the cue-in / line-complete path): the right budget
            // cannot fit in line 0, so it spills into line 1's head. Left of the caret only 4 letters
            // exist (hard start edge, no soften).
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 5, 5 }, caretStreamSlot: 4, radius: 5);

            // Line 0: all five lit, no soft edge either side (start is hard, right continues into line 1).
            assertWin(win[0], lo: 0, hi: 4, softLeft: false, softRight: false);

            // Line 1: first four letters lit; the fourth is the window's outer-right edge with a hidden
            // letter beyond, so it softens; the fifth letter stays dark.
            assertWin(win[1], lo: 0, hi: 3, softLeft: false, softRight: true);
        }

        [Test]
        public void TestMidLineForwardSpillIsCappedToActiveLine()
        {
            // Same geometry and caret as above, but now the caret is mid-line on an INCOMPLETE active
            // line 0, so the stage caps the forward reach at line 0's last countable slot (4). The right
            // budget that would have spilled into line 1 is thrown away: line 1 stays fully dark, and
            // line 0's own last char (the one you must still type) stays a HARD, full-alpha edge.
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 5, 5 }, caretStreamSlot: 4, radius: 5, maxRightSlot: 4);

            assertWin(win[0], lo: 0, hi: 4, softLeft: false, softRight: false);
            Assert.That(win[1].IsHidden, Is.True, "next line stays dark while the active line is still being typed");
        }

        [Test]
        public void TestEarlyFinishRewardLightsNextLineHeadImmediately()
        {
            // The moment the line is complete the cap lifts. Caret sits at the line boundary (line 0 fully
            // typed, stream slot 5) with no cap, exactly as the stage now passes when IsLineComplete: the
            // leftover right budget spills into line 1's head as the early-finish reward, while line 0's
            // whole tail stays lit. Contrast with the mid-line case above, where the same nearness lit
            // nothing in line 1.
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 5, 5 }, caretStreamSlot: 5, radius: 5);

            assertWin(win[0], lo: 0, hi: 4, softLeft: false, softRight: false);
            assertWin(win[1], lo: 0, hi: 4, softLeft: false, softRight: false);
        }

        [Test]
        public void TestCapNeverAffectsBackwardSpill()
        {
            // The cap only limits the FORWARD (right) budget; the left/backward tail spill is untouched.
            // Caret one char into line 1 with the cap set at line 1's last slot (9): the window still
            // reaches back into line 0's tail exactly as when uncapped.
            var capped = LyricLineDisplay.ComputeStreamWindows(new[] { 5, 5 }, caretStreamSlot: 6, radius: 5, maxRightSlot: 9);
            var uncapped = LyricLineDisplay.ComputeStreamWindows(new[] { 5, 5 }, caretStreamSlot: 6, radius: 5);

            assertWin(capped[0], lo: uncapped[0].Lo, hi: uncapped[0].Hi, softLeft: uncapped[0].SoftLeft, softRight: uncapped[0].SoftRight);
            assertWin(capped[0], lo: 1, hi: 4, softLeft: true, softRight: false);
        }

        [Test]
        public void TestStreamSpillsIntoPreviousLine()
        {
            // Caret near the start of line 1 (one countable before it). Stream slot = 5 (line 0) + 1.
            // Radius 5: the left budget spills back into line 0's tail; the right fits inside line 1.
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 5, 5 }, caretStreamSlot: 6, radius: 5);

            // Line 0: last four letters lit (slots 1..4); slot 1 is the window's outer-left edge with a
            // hidden letter (slot 0) beyond, so it softens; the tail continues into line 1 (hard).
            assertWin(win[0], lo: 1, hi: 4, softLeft: true, softRight: false);

            // Line 1: first six letters wanted but only five exist; slots 0..4 lit, outer-right at the
            // stream end (nothing beyond) so it is a hard edge.
            assertWin(win[1], lo: 0, hi: 4, softLeft: false, softRight: false);
        }

        [Test]
        public void TestStreamExactLineBoundaryLightsBothTails()
        {
            // Caret exactly on the boundary between two lines (line 0 fully typed): stream slot 5.
            // Radius 5 lights line 0's whole tail and line 1's whole head, joined hard at the boundary.
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 5, 5 }, caretStreamSlot: 5, radius: 5);

            assertWin(win[0], lo: 0, hi: 4, softLeft: false, softRight: false);
            assertWin(win[1], lo: 0, hi: 4, softLeft: false, softRight: false);
        }

        [Test]
        public void TestStreamFirstLineHeadIsHardStartEdge()
        {
            // Caret at the very start of the first line (cue-in anchor for line 0). Radius 5 lights the
            // first five countable chars; left is the stream start (hard), a hidden char lies to the
            // right so the outermost-right char softens.
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 10, 10 }, caretStreamSlot: 0, radius: 5);

            assertWin(win[0], lo: 0, hi: 4, softLeft: false, softRight: true);
            Assert.That(win[1].IsHidden, Is.True, "second line untouched when the caret is at the stream start");
        }

        [Test]
        public void TestStreamLastLineTailIsHardEndEdge()
        {
            // Caret past the last char of the final line: stream slot = total (20). Radius 5 lights the
            // final line's last five chars; the outer-right is the stream end (hard), a hidden char lies
            // to the left so the outermost-left softens. The earlier line is untouched.
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 10, 10 }, caretStreamSlot: 20, radius: 5);

            Assert.That(win[0].IsHidden, Is.True, "first line untouched when the caret is at the stream end");
            assertWin(win[1], lo: 5, hi: 9, softLeft: true, softRight: false);
        }

        [Test]
        public void TestStreamCueInAnchorLightsPrevTailAndNextHead()
        {
            // Three lines; the middle one is being cued in with the caret anchored at its first char
            // (stream slot = 10). Radius 5 lights the previous line's last five and the cued line's
            // first five, so the player reads the letters they are about to type. Line 2 is untouched.
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 10, 10, 10 }, caretStreamSlot: 10, radius: 5);

            assertWin(win[0], lo: 5, hi: 9, softLeft: true, softRight: false);
            assertWin(win[1], lo: 0, hi: 4, softLeft: false, softRight: true);
            Assert.That(win[2].IsHidden, Is.True, "line after the cued line stays dark");
        }

        [Test]
        public void TestStreamSpillCompletesToAlphasAcrossLines()
        {
            // End-to-end: feed each line's window slice back through the per-line alpha rule and confirm
            // the letters light where expected across the boundary (line 0 "hello", line 1 "world").
            var l0 = line("hello");
            var l1 = line("world");
            var win = LyricLineDisplay.ComputeStreamWindows(new[] { 5, 5 }, caretStreamSlot: 4, radius: 5);

            // Line 0 all lit, no soft edge.
            assertWindow(LyricLineDisplay.ComputeWindowAlphas(l0.Cells, win[0], soft),
                1f, 1f, 1f, 1f, 1f);

            // Line 1: w,o,r lit; l soft (outer-right edge, d hidden beyond); d dark.
            assertWindow(LyricLineDisplay.ComputeWindowAlphas(l1.Cells, win[1], soft),
                1f, 1f, 1f, soft, 0f);
        }
    }
}
