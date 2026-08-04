// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Utils;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pure math for the width a cell-covering caret style spans at a FRACTIONAL cell index
    /// (<see cref="LyricLineDisplay.AdvanceAtFraction"/>): the sung playhead rides a continuous
    /// position, so a Block/Outline/Underline there has to interpolate the straddled cells'
    /// advances the same way the position interpolates their left edges. The invariant that
    /// matters is the one at every whole index: the covered width is exactly that character's
    /// own advance, so the shape lands squarely on the character being sung at its onset.
    /// </summary>
    [TestFixture]
    public class CaretCellWidthTest
    {
        // Deliberately uneven, as a proportional font's advances are: 'i' next to 'W'.
        private static readonly float[] advances = { 10f, 30f, 20f };

        private static void assertWidth(double fraction, float expected)
        {
            float actual = LyricLineDisplay.AdvanceAtFraction(advances, fraction);
            Assert.That(Precision.AlmostEquals(actual, expected, 1e-4f), Is.True, $"at {fraction}: expected {expected}, got {actual}");
        }

        [Test]
        public void WholeIndicesCoverExactlyThatCell()
        {
            assertWidth(0, 10f);
            assertWidth(1, 30f);
            assertWidth(2, 20f);
        }

        [Test]
        public void BetweenTwoCellsTheWidthMorphsLinearly()
        {
            assertWidth(0.5, 20f);   // halfway 10 -> 30
            assertWidth(0.25, 15f);
            assertWidth(1.5, 25f);   // halfway 30 -> 20
        }

        [Test]
        public void OutOfRangeClampsToTheEndCells()
        {
            // Before the line, and past the last cell (SungPositionAt runs to Cells.Count, one past
            // the last index): the playhead keeps the end character's width rather than collapsing.
            assertWidth(-5, 10f);
            assertWidth(2.5, 20f);
            assertWidth(3, 20f);
            assertWidth(99, 20f);
        }

        [Test]
        public void NanClampsRatherThanIndexingWildly()
        {
            // NaN fails every comparison, so Math.Clamp would pass it straight through to the floor
            // and out of the array. It is guarded explicitly.
            assertWidth(double.NaN, 10f);
        }

        [Test]
        public void EmptyAdvancesAreZeroNotAThrow()
        {
            // A display measured before load has no advances; the caller substitutes its own
            // fallback, so this only has to stay quiet.
            Assert.That(LyricLineDisplay.AdvanceAtFraction(Array.Empty<float>(), 1.5), Is.EqualTo(0f));
        }

        [Test]
        public void ASingleCellLineHasOneWidthEverywhere()
        {
            float[] one = { 17f };

            Assert.That(LyricLineDisplay.AdvanceAtFraction(one, 0), Is.EqualTo(17f));
            Assert.That(LyricLineDisplay.AdvanceAtFraction(one, 0.5), Is.EqualTo(17f));
            Assert.That(LyricLineDisplay.AdvanceAtFraction(one, 1), Is.EqualTo(17f));
        }
    }
}
