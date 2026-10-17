// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class DifficultyMetricScaleTest
    {
        [Test]
        public void CurrentReferenceExtremesMapToOneAndTen()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DifficultyMetricScale.Complexity(DifficultyMetricScale.MostTypableZ), Is.EqualTo(1));
                Assert.That(DifficultyMetricScale.Complexity(DifficultyMetricScale.LeastTypableLiterateZ), Is.EqualTo(10).Within(1e-12));
                Assert.That(DifficultyMetricScale.Rhythm(DifficultyMetricScale.LeastRhythmLoad), Is.EqualTo(1));
                Assert.That(DifficultyMetricScale.Rhythm(DifficultyMetricScale.MostHardRockRhythmLoad), Is.EqualTo(10).Within(1e-12));
            });
        }

        [Test]
        public void FutureTextCanExtendBothEndsWithOnlyAZeroFloor()
        {
            double range = DifficultyMetricScale.MostTypableZ - DifficultyMetricScale.LeastTypableLiterateZ;
            Assert.That(DifficultyMetricScale.Complexity(DifficultyMetricScale.LeastTypableLiterateZ - range / 9), Is.EqualTo(11).Within(1e-12));
            Assert.That(DifficultyMetricScale.Complexity(DifficultyMetricScale.MostTypableZ + range / 18), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(DifficultyMetricScale.Complexity(DifficultyMetricScale.MostTypableZ + range), Is.Zero);
            Assert.That(DifficultyMetricScale.BarLength(11), Is.EqualTo(1), "only the visual bar is capped");
        }

        [Test]
        public void FutureRhythmIsNotCappedAtTen()
        {
            double range = DifficultyMetricScale.MostHardRockRhythmLoad - DifficultyMetricScale.LeastRhythmLoad;
            Assert.That(DifficultyMetricScale.Rhythm(DifficultyMetricScale.MostHardRockRhythmLoad + range / 9), Is.EqualTo(11).Within(1e-12));
            Assert.That(DifficultyMetricScale.Rhythm(DifficultyMetricScale.LeastRhythmLoad - range / 9), Is.Zero.Within(1e-12));
            Assert.That(DifficultyMetricScale.Rhythm(-range), Is.Zero);
        }
    }
}
