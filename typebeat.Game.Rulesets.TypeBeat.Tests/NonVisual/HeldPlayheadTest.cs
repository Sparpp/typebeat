// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The playhead over a held letter (<see cref="LyricLineDisplay.SungLocalX"/>): it never jumps, and
    /// while the hold is down it sits on the hold track's fill edge.
    /// </summary>
    public class HeldPlayheadTest
    {
        // "abc" with the 'b' held: advances 10, 12, 10 and a 40 px track after the 'b'.
        private static readonly float[] advances = { 10, 12, 10 };
        private static readonly float[] tracks = { 0, 40, 0 };
        private static readonly float[] lefts = { 0, 10, 62, 72 };

        private static float x(double f) => LyricLineDisplay.SungLocalX(f, lefts, advances, tracks);

        [Test]
        public void ThePlayheadNeverJumps()
        {
            const double step = 0.001;

            for (double f = 0; f < 3; f += step)
                Assert.That(x(f + step) - x(f), Is.InRange(0f, 0.1f), $"at {f}");
        }

        [Test]
        public void WhileHeldThePlayheadIsOnTheFillEdge()
        {
            foreach (double fill in new[] { 0, 0.25, 0.5, 1 })
            {
                // The bar starts at the held letter's right edge; its fill edge is that plus fill x track.
                float fillEdge = lefts[1] + advances[1] + (float)fill * tracks[1];
                Assert.That(x(1 + fill), Is.EqualTo(fillEdge).Within(1e-3), $"fill {fill}");
            }
        }

        [Test]
        public void ALineWithoutHoldsIsUnchanged()
        {
            float[] noTracks = { 0, 0, 0 };
            float[] plainLefts = { 0, 10, 22, 32 };

            Assert.Multiple(() =>
            {
                Assert.That(LyricLineDisplay.SungLocalX(1, plainLefts, advances, noTracks), Is.EqualTo(10));
                Assert.That(LyricLineDisplay.SungLocalX(1.5, plainLefts, advances, noTracks), Is.EqualTo(16));
                Assert.That(LyricLineDisplay.SungLocalX(3, plainLefts, advances, noTracks), Is.EqualTo(32));
            });
        }
    }
}
