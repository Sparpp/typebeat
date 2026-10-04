// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JudgementIndicatorGeometryTest
    {
        [TestCase(JudgementIndicatorMode.BottomToTopFill)]
        public void MasksStayInsideGlyphAndGrowMonotonically(JudgementIndicatorMode mode)
        {
            var glyph = new Vector2(20, 42);
            float previousArea = -1;
            foreach (float progress in new[] { -1f, 0, 0.01f, 0.25f, 0.5f, 0.75f, 1, 2 })
            {
                var mask = LyricLineDisplay.IndicatorMaskGeometry(mode, glyph, progress);
                Assert.That(mask.Position.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(mask.Position.Y, Is.GreaterThanOrEqualTo(0));
                Assert.That(mask.Position.X + mask.Size.X, Is.LessThanOrEqualTo(glyph.X + 0.00001));
                Assert.That(mask.Position.Y + mask.Size.Y, Is.LessThanOrEqualTo(glyph.Y + 0.00001));
                float area = mask.Size.X * mask.Size.Y;
                Assert.That(area, Is.GreaterThanOrEqualTo(previousArea));
                previousArea = area;
            }
            var full = LyricLineDisplay.IndicatorMaskGeometry(mode, glyph, 1);
            Assert.That(full.Position, Is.EqualTo(Vector2.Zero));
            Assert.That(full.Size, Is.EqualTo(glyph));
        }

        [Test]
        public void ExponentialFadeHasExactEndpointsAndAccelerates()
        {
            Assert.That(LyricLineDisplay.ExponentialIndicatorProgress(0), Is.Zero);
            Assert.That(LyricLineDisplay.ExponentialIndicatorProgress(1), Is.EqualTo(1));
            Assert.That(LyricLineDisplay.ExponentialIndicatorProgress(0.5f), Is.EqualTo(31f / 1023f).Within(0.000001));
            float firstHalf = LyricLineDisplay.ExponentialIndicatorProgress(0.5f);
            Assert.That(1 - firstHalf, Is.GreaterThan(firstHalf));
        }

        [Test]
        public void ReleaseStartsAtLateEdgeAndFinishesInOneHundredMilliseconds()
        {
            Assert.That(LyricLineDisplay.IndicatorRelease(900, 1000), Is.EqualTo(1));
            Assert.That(LyricLineDisplay.IndicatorRelease(1000, 1000), Is.EqualTo(1));
            Assert.That(LyricLineDisplay.IndicatorRelease(1050, 1000), Is.EqualTo(0.5f));
            Assert.That(LyricLineDisplay.IndicatorRelease(1100, 1000), Is.Zero);
            Assert.That(LyricLineDisplay.IndicatorRelease(1200, 1000), Is.Zero);
        }
    }
}
