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
            // CPM = 5 / (3000ms / 60000) = 100; WPM = CPM / 5 = 20.
            //
            // The real-word convention this replaced would have said 2 / 0.05 = 40 WPM. The line
            // averages 5/2 = 2.5 cells per word, half the 5 the unit assumes, so the new figure is
            // half the old one: exactly the proportionality AverageCharsPerWord now advertises.
            var pace = LyricPaceStatistics.Compute(new[] { makeLine("ab cd", 1000, 4000) });

            Assert.AreEqual(5, pace.TypeableCellCount);
            Assert.AreEqual(2, pace.WordCount);
            Assert.AreEqual(20.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(100.0, pace.AverageCpm, 1e-9);
            Assert.AreEqual(2.5, pace.AverageCharsPerWord, 1e-9);
        }

        [Test]
        public void AveragesPerLineRatesUnweighted()
        {
            // Line 1: "ab cd" over 3000 ms -> 100 CPM / 20 WPM.
            // Line 2: "ab cd" over 1500 ms -> 200 CPM / 40 WPM.
            // Map = unweighted mean of per-line rates: 150 CPM / 30 WPM.
            var pace = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 1000, 4000),
                makeLine("ab cd", 4000, 5500),
            });

            Assert.AreEqual(10, pace.TypeableCellCount);
            Assert.AreEqual(4, pace.WordCount);
            Assert.AreEqual(30.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(150.0, pace.AverageCpm, 1e-9);
            Assert.AreEqual(2.5, pace.AverageCharsPerWord, 1e-9);
        }

        [Test]
        public void MinimumWindowGuardsDegenerateBoundaries()
        {
            // A 100 ms boundary window clamps to the 500 ms floor:
            // 5 cells / (500ms / 60000) = 600 CPM; WPM = 600 / 5 = 120.
            var pace = LyricPaceStatistics.Compute(new[] { makeLine("abcde", 1000, 1100) });

            Assert.AreEqual(120.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(600.0, pace.AverageCpm, 1e-9);
        }

        /// <summary>
        /// The one identity the whole convention change rests on, and the reason the new metric
        /// counts inter-word spaces: a line whose average word is exactly 5 CELLS long has the same
        /// WPM under the typing-test convention (cells/5) as under the real-word one (words), so
        /// the change is a reweighting around 5, not an arbitrary rescaling, and
        /// AverageCharsPerWord is precisely the old CPM:WPM ratio made visible.
        ///
        /// <para>Stated at LINE granularity, and the fixture gives every line an average of exactly
        /// 5 rather than only the map total: WPM and CPM are unweighted means of per-line rates, so
        /// a map that averages 5 overall while its individual lines do not would not satisfy the
        /// identity line by line.</para>
        /// </summary>
        [Test]
        public void FiveCellWordsMakeTheNewWpmEqualTheOldOne()
        {
            // Line 1, "abcd efghi" over 3000 ms: 2 words, 4 + 1 + 5 = 10 cells, 10/2 = 5 exactly.
            //   old WPM = 2 words / 0.05 min           = 40
            //   CPM     = 10 cells / 0.05 min          = 200
            //   new WPM = 200 / 5                      = 40   (equal)
            //
            // Line 2, "abcd efgh ijkl mnopq" over 1500 ms: 4 words, 17 chars + 3 spaces = 20 cells,
            // 20/4 = 5 exactly.
            //   old WPM = 4 words / 0.025 min          = 160
            //   CPM     = 20 cells / 0.025 min         = 800
            //   new WPM = 800 / 5                      = 160  (equal)
            //
            // Map: mean CPM = (200 + 800) / 2 = 500, mean WPM = (40 + 160) / 2 = 100 = 500 / 5,
            // and chars/word = (10 + 20) / (2 + 4) = 30 / 6 = 5.
            var lines = new[]
            {
                makeLine("abcd efghi", 1000, 4000),
                makeLine("abcd efgh ijkl mnopq", 4000, 5500),
            };

            var pace = LyricPaceStatistics.Compute(lines);

            Assert.AreEqual(30, pace.TypeableCellCount);
            Assert.AreEqual(6, pace.WordCount);
            Assert.AreEqual(5.0, pace.AverageCharsPerWord, 1e-9);
            Assert.AreEqual(500.0, pace.AverageCpm, 1e-9);
            Assert.AreEqual(100.0, pace.AverageWpm, 1e-9);

            // And the old convention, recomputed here from the same boundary windows, agrees:
            // mean of (words / minutes) over the two lines.
            double oldConventionWpm = (2 / (3000 / 60000.0) + 4 / (1500 / 60000.0)) / 2;

            Assert.AreEqual(oldConventionWpm, pace.AverageWpm, 1e-9);
        }

        [Test]
        public void WpmIsCpmOverFiveWhateverTheWordLength()
        {
            // The identity above is conditional on 5-cell words; THIS one is unconditional, which is
            // the point of deriving AverageWpm from AverageCpm instead of summing it separately.
            // Lines chosen to average nothing like 5: 5/2 = 2.5 and 18/2 = 9.0 cells per word,
            // 23/4 = 5.75 over the map.
            var pace = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 1000, 4000),
                makeLine("abcdefgh ijklmnopq", 4000, 9000),
            });

            Assert.AreEqual(pace.AverageCpm / 5.0, pace.AverageWpm, 1e-12);
            Assert.AreNotEqual(5.0, pace.AverageCharsPerWord);
        }

        [Test]
        public void CharsPerWordCountsInterWordSpaces()
        {
            // "ab cd ef": 3 words, 6 chars + 2 spaces = 8 cells, so 8/3 and not 6/3. Spaces are in
            // because the 5 in "5 chars = 1 word" counts them: they are keystrokes like any other,
            // and leaving them out here would put the two metrics in different units and break the
            // identity pinned above.
            var pace = LyricPaceStatistics.Compute(new[] { makeLine("ab cd ef", 1000, 4000) });

            Assert.AreEqual(8, pace.TypeableCellCount);
            Assert.AreEqual(3, pace.WordCount);
            Assert.AreEqual(8 / 3.0, pace.AverageCharsPerWord, 1e-9);
        }

        [Test]
        public void EmptyMapIsZero()
        {
            var pace = LyricPaceStatistics.Compute(Array.Empty<LyricLine>());

            Assert.AreEqual(0, pace.TypeableCellCount);
            Assert.AreEqual(0, pace.WordCount);
            Assert.AreEqual(0, pace.AverageWpm);
            Assert.AreEqual(0, pace.AverageCpm);

            // No words to divide by: 0 rather than a NaN that would render as "NaN" in the wedge.
            Assert.AreEqual(0, pace.AverageCharsPerWord);
        }
    }
}
