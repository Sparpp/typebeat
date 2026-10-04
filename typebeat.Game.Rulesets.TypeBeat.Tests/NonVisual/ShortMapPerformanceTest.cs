// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class ShortMapPerformanceTest
    {
        private static LyricLine line(double start, double end, double seal = 300000) => new LyricLine
        {
            RawText = "hello",
            StartTime = start,
            SingEndTime = end,
            EndTime = seal,
            SealGraceMs = 5000,
            Units = new[] { new TimedUnit { Text = "hello", StartTime = start, EndTime = end } },
        };

        [TestCase(0, 0.40)]
        [TestCase(15, 0.6625)]
        [TestCase(30, 0.85)]
        [TestCase(45, 0.9625)]
        [TestCase(60, 1)]
        [TestCase(120, 1)]
        public void DurationCurveTargetsFifteenPercentAtThirtySeconds(double seconds, double multiplier)
            => Assert.That(PerformancePoints.ShortMapMultiplier(seconds), Is.EqualTo(multiplier).Within(1e-12));

        [Test]
        public void DoubleTimeUsesPlayedLengthAndHalfTimeCanClearTheCut()
        {
            var lines = new[] { line(10000, 70000) };
            var doubleTime = new Mod[] { new TypeBeatModDoubleTime() };
            var halfTime = new Mod[] { new TypeBeatModHalfTime() };
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.PlayedDurationFor(lines, null), Is.EqualTo(60));
                Assert.That(PerformancePoints.PlayedDurationFor(lines, doubleTime), Is.EqualTo(40));
                Assert.That(PerformancePoints.PlayedDurationFor(lines, halfTime), Is.EqualTo(80));
                Assert.That(PerformancePoints.ShortMapMultiplier(PerformancePoints.PlayedDurationFor(lines, doubleTime)), Is.LessThan(1));
                Assert.That(PerformancePoints.ShortMapMultiplier(PerformancePoints.PlayedDurationFor(lines, halfTime)), Is.EqualTo(1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FortyFiveSecondMapAtDoubleTimeReceivesFifteenPercentCut(bool nightcore)
        {
            var lines = new[] { line(10000, 55000) };
            Mod[] mods = { nightcore ? new TypeBeatModNightcore() : new TypeBeatModDoubleTime() };
            double duration = PerformancePoints.PlayedDurationFor(lines, mods);
            double original = PerformancePoints.Compute(4, 500, 500, 3, 0.95, 400, mods);
            double reduced = PerformancePoints.Compute(4, 500, 500, 3, 0.95, 400, mods, playedDurationSeconds: duration);
            Assert.Multiple(() =>
            {
                Assert.That(duration, Is.EqualTo(30));
                Assert.That(original, Is.GreaterThan(0));
                Assert.That(reduced / original, Is.EqualTo(0.85).Within(1e-12));
            });
        }

        [Test]
        public void VocalSpanIncludesInternalBreaksButExcludesSealTailsAndEmptyCaptions()
        {
            var empty = new LyricLine
            {
                RawText = "",
                StartTime = 0,
                SingEndTime = 600000,
                EndTime = 600000,
                Units = Array.Empty<TimedUnit>(),
            };
            var lines = new[] { empty, line(10000, 15000), line(65000, 70000) };
            Assert.That(PerformancePoints.PlayedDurationFor(lines, null), Is.EqualTo(60), "use mapped vocal span, including the 50-second break");
        }

        [Test]
        public void ShortMapCutScalesTheWholePriceWithoutChangingPerformanceQuality()
        {
            double normal = PerformancePoints.Compute(4, 500, 500, 3, 0.95, 400, null, playedDurationSeconds: 60);
            double shortMap = PerformancePoints.Compute(4, 500, 500, 3, 0.95, 400, null, playedDurationSeconds: 30);
            Assert.Multiple(() =>
            {
                Assert.That(normal, Is.GreaterThan(0));
                Assert.That(shortMap / normal, Is.EqualTo(0.85).Within(1e-12));
                Assert.That(PerformancePoints.Compute(4, 500, 500, 3, 0.95, 400, null), Is.EqualTo(normal), "unknown-duration compatibility callers preserve their formula");
            });
        }

        [Test]
        public void MalformedDurationCannotProduceNonFinitePointsOrEvadeTheCut()
        {
            Assert.Multiple(() =>
            {
                Assert.That(PerformancePoints.ShortMapMultiplier(double.NaN), Is.EqualTo(0.40).Within(1e-12));
                Assert.That(PerformancePoints.ShortMapMultiplier(double.NegativeInfinity), Is.EqualTo(0.40).Within(1e-12));
                Assert.That(PerformancePoints.ShortMapMultiplier(-1), Is.EqualTo(0.40).Within(1e-12));
                Assert.That(PerformancePoints.PlayedDurationFor(new[] { line(double.NaN, double.PositiveInfinity) }, null), Is.Zero);
            });
        }
    }
}
