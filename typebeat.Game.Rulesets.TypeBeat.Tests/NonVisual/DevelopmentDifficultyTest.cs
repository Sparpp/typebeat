// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Tests.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class DevelopmentDifficultyTest
    {
        [Test]
        public void RuntimeUsesPublishedCalibrationInEveryBuild()
        {
            Assert.That(ChunkedEndurance.Active, Is.SameAs(ChunkedEndurance.Live));
            Assert.That(ChunkedEndurance.Live.overlap_whole_map, Is.False);
            Assert.That(ChunkedEndurance.Live.overlap_max_seconds, Is.EqualTo(60.75));
            Assert.That(ChunkedEndurance.Live.chunk_length_scale, Is.EqualTo(1500));
            Assert.That(ChunkedEndurance.Development.overlap_whole_map, Is.True);
            Assert.That(ChunkedEndurance.Development.chunk_length_scale, Is.EqualTo(2000));

            var ruleset = new TypeBeatRuleset();
            var beatmap = new TestWorkingBeatmap(new TypeBeatBeatmap());
            var calculator = new TypeBeatDifficultyCalculator(ruleset.RulesetInfo, beatmap);
            Assert.That(calculator.Version, Is.EqualTo(16));
        }

        [TestCase(0.5, 2.9307039332939406)]
        [TestCase(1, 5.681648062227401)]
        [TestCase(1.5, 8.345192199525798)]
        [TestCase(2, 10.94708984803374)]
        public void WholeMapWindowsMatchTheSandboxAtDifferentRates(double rate, double stars)
        {
            var report = LyricDifficulty.RateChunked(steady(100), rate, false, LyricDifficulty.NoScores,
                ChunkedEndurance.Development with { complexity_strength = 0 }).Report;
            Assert.Multiple(() =>
            {
                Assert.That(report.Stars, Is.EqualTo(stars).Within(1e-9));
                Assert.That(report.DurationSeconds, Is.EqualTo(240 / rate).Within(1e-9));
                Assert.That(report.OverlapWindowDurations.Last(), Is.EqualTo(240 / rate).Within(1e-9));
                Assert.That(report.WholeMapWindow.StartMs, Is.EqualTo(17000 / rate).Within(1e-9));
                Assert.That(report.WholeMapWindow.EndMs, Is.EqualTo(257000 / rate).Within(1e-9));
                Assert.That(report.WholeMapWindow.Seconds, Is.EqualTo(report.DurationSeconds).Within(1e-9));
                Assert.That(report.Hardest, Is.GreaterThanOrEqualTo(report.WholeMapWindow.Strain - 1e-12));
                Assert.That(report.LengthBonusApplied, Is.EqualTo(0.15).Within(1e-12));
            });
        }

        [Test]
        public void ExactWholeMapWindowIsScoredEvenWhenNoSamplePlacementMatchesItsEdges()
        {
            var report = LyricDifficulty.RateChunked(steady(75), 1, false, LyricDifficulty.NoScores,
                ChunkedEndurance.Development with { complexity_strength = 0 }).Report;
            // Sample centres are offset by 187.5 ms; none can centre a 180-second window on this map.
            Assert.That(report.WholeMapWindow.Seconds, Is.EqualTo(180));
            Assert.That(report.HardestWindow.StartMs, Is.EqualTo(report.WholeMapWindow.StartMs));
            Assert.That(report.HardestWindow.EndMs, Is.EqualTo(report.WholeMapWindow.EndMs));
            Assert.That(report.Score, Is.EqualTo(report.WholeMapWindow.Strain).Within(1e-12));
        }

        [Test]
        public void WholeMapHorizonIncludesInternalPausesButExcludesLeadingAndTrailingSilence()
        {
            var lines = new[] { phrase(17000, 2400, lineStart: 0), phrase(217000, 2400, lineEnd: 400000) };
            var report = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.NoScores, ChunkedEndurance.Development).Report;
            Assert.That(report.WholeMapWindow.StartMs, Is.EqualTo(17000));
            Assert.That(report.WholeMapWindow.EndMs, Is.EqualTo(219400));
            Assert.That(report.OverlapWindowDurations.Last(), Is.EqualTo(202.4).Within(1e-9));
        }

        [Test]
        public void EmptyAndVeryShortMapsKeepTheBaseWindowFloor()
        {
            var empty = LyricDifficulty.RateChunked(Array.Empty<LyricLine>(), 1, false, LyricDifficulty.NoScores, ChunkedEndurance.Development).Report;
            var tiny = LyricDifficulty.RateChunked(new[] { phrase(17000, 100) }, 1, false, LyricDifficulty.NoScores, ChunkedEndurance.Development).Report;
            Assert.That(empty.Stars, Is.Zero);
            Assert.That(tiny.OverlapWindowDurations, Is.EqualTo(new[] { 1.5 }));
            Assert.That(tiny.WholeMapWindow.Seconds, Is.Zero);
            Assert.That(double.IsFinite(tiny.Stars), Is.True);
            Assert.That(tiny.Stars, Is.GreaterThan(0));
        }

        [Test]
        public void LengthBonusReachesFullCreditAtTwoThousandWeightedCharacters()
        {
            var lines = steady(40);
            var report = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.NoScores, ChunkedEndurance.Development).Report;
            var oldScale = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.NoScores,
                ChunkedEndurance.Development with { chunk_length_scale = 1500 }).Report;
            Assert.That(report.LengthCharacters, Is.GreaterThan(0).And.LessThan(1500));
            Assert.That(report.LengthBonusApplied, Is.EqualTo(0.15 * report.LengthCharacters / 2000).Within(1e-12));
            Assert.That(report.Score, Is.EqualTo(oldScale.Score).Within(1e-12));
            Assert.That(report.LengthCharacters, Is.EqualTo(oldScale.LengthCharacters).Within(1e-9));
            Assert.That(report.Stars, Is.LessThan(oldScale.Stars));
        }

        [Test]
        public void RuntimeRatingUsesThePublishedCalibration()
        {
            var lines = steady(100);
            var expected = LyricDifficulty.RateChunked(lines, 1, false, LyricDifficulty.ShippedScores, ChunkedEndurance.Live).Report;
            var actual = LyricDifficulty.ComputeDetail(lines);
            Assert.That(actual.Stars, Is.EqualTo(expected.Stars).Within(1e-12));
            Assert.That(actual.DifficultCharacters, Is.EqualTo(expected.LengthCharacters).Within(1e-9));
        }

        private static LyricLine[] steady(int count, double start = 17000)
            => Enumerable.Range(0, count).Select(i => phrase(start + i * 2400, 2400)).ToArray();

        private static LyricLine phrase(double start, double span, double? lineStart = null, double? lineEnd = null) => new LyricLine
        {
            RawText = "flame flame flame flame",
            StartTime = lineStart ?? start,
            EndTime = lineEnd ?? start + span,
            SingEndTime = start + span,
            Units = Enumerable.Range(0, 4).Select(i => new TimedUnit
            {
                Text = "flame",
                StartTime = start + i * span / 4,
                EndTime = start + (i + 1) * span / 4,
            }).ToArray(),
        };
    }
}
