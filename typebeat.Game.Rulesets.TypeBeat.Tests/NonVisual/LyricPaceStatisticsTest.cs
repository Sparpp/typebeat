// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class LyricPaceStatisticsTest
    {
        private static LyricLine makeLine(string text, double start, double end) => new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = end } },
        };

        [Test]
        public void ComputesBoundaryWindowPace()
        {
            // "ab cd": 2 words, 5 typeable cells (a, b, space, c, d). Boundary window
            // = EndTime - StartTime = 3000 ms, regardless of where the unit targets sit.
            // WPM = 2 / (3000ms / 60000) = 40; CPM = 5 / (3000ms / 60000) = 100.
            var pace = LyricPaceStatistics.Compute(new[] { makeLine("ab cd", 1000, 4000) });

            Assert.AreEqual(5, pace.TypeableCellCount);
            Assert.AreEqual(2, pace.WordCount);
            Assert.AreEqual(40.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(100.0, pace.AverageCpm, 1e-9);
        }

        [Test]
        public void AveragesPerLineRatesUnweighted()
        {
            // Line 1: "ab cd" over 3000 ms -> 40 WPM / 100 CPM.
            // Line 2: "ab cd" over 1500 ms -> 80 WPM / 200 CPM.
            // Map = unweighted mean of per-line rates: 60 WPM / 150 CPM.
            var pace = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 1000, 4000),
                makeLine("ab cd", 4000, 5500),
            });

            Assert.AreEqual(10, pace.TypeableCellCount);
            Assert.AreEqual(4, pace.WordCount);
            Assert.AreEqual(60.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(150.0, pace.AverageCpm, 1e-9);
        }

        [Test]
        public void MinimumWindowGuardsDegenerateBoundaries()
        {
            // A 100 ms boundary window clamps to the 500 ms floor:
            // 1 word / (500ms / 60000) = 120 WPM; 5 cells -> 600 CPM.
            var pace = LyricPaceStatistics.Compute(new[] { makeLine("abcde", 1000, 1100) });

            Assert.AreEqual(120.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(600.0, pace.AverageCpm, 1e-9);
        }

        [Test]
        public void EmptyMapIsZero()
        {
            var pace = LyricPaceStatistics.Compute(Array.Empty<LyricLine>());

            Assert.AreEqual(0, pace.TypeableCellCount);
            Assert.AreEqual(0, pace.WordCount);
            Assert.AreEqual(0, pace.AverageWpm);
            Assert.AreEqual(0, pace.AverageCpm);
        }
    }
}
