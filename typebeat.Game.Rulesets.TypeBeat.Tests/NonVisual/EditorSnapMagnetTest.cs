// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The magnet rule shared by the editor's two drag snaps (a word edge onto the caret, a
    /// timeline drag onto the beat grid): inside the tolerance the candidate is taken EXACTLY,
    /// outside it the dragged time is returned untouched, so the drag stays continuous.
    /// </summary>
    [TestFixture]
    public class EditorSnapMagnetTest
    {
        [Test]
        public void TestInsideToleranceTakesTheCandidateExactly()
        {
            Assert.That(EditorSnapMagnet.Magnet(1960, 2000, 90), Is.EqualTo(2000));
            Assert.That(EditorSnapMagnet.Magnet(2040, 2000, 90), Is.EqualTo(2000));
        }

        [Test]
        public void TestOutsideToleranceLeavesTheTimeAlone()
        {
            Assert.That(EditorSnapMagnet.Magnet(1800, 2000, 90), Is.EqualTo(1800));
            Assert.That(EditorSnapMagnet.Magnet(2200, 2000, 90), Is.EqualTo(2200));
        }

        [Test]
        public void TestToleranceBoundaryIsInclusive()
        {
            Assert.That(EditorSnapMagnet.Magnet(1910, 2000, 90), Is.EqualTo(2000));
            Assert.That(EditorSnapMagnet.Magnet(2090, 2000, 90), Is.EqualTo(2000));
            Assert.That(EditorSnapMagnet.Magnet(1909.9, 2000, 90), Is.EqualTo(1909.9));
        }

        [Test]
        public void TestZeroSizedSurfaceNeverSnaps()
        {
            // A tolerance of zero is what a not-yet-sized surface computes; it must not pull a
            // drag onto a candidate it happens to sit exactly on either.
            Assert.That(EditorSnapMagnet.Magnet(2000, 2000, 0), Is.EqualTo(2000));
            Assert.That(EditorSnapMagnet.Magnet(1999, 2000, 0), Is.EqualTo(1999));
        }
    }
}
