// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Song select's count statistics. The one that matters here is agreement: the "Words" total and
    /// the "Average WPM" rendered directly beneath it must come from the same word definition, or the
    /// wedge shows two numbers that contradict each other.
    /// </summary>
    [TestFixture]
    public class TypeBeatBeatmapStatisticsTest
    {
        private static LyricLine makeLine(string text, double start, double end) => new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = end } },
        };

        private static TypeBeatBeatmap makeBeatmap(IEnumerable<LyricLine> lines)
        {
            var beatmap = new TypeBeatBeatmap();
            int index = 0;

            foreach (var line in lines)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    Line = line,
                    LineIndex = index++,
                    StartTime = line.StartTime,
                });
            }

            return beatmap;
        }

        /// <summary>
        /// A fixture picked so that the naive "split RawText on spaces" answer is WRONG in both
        /// directions, and so that the two errors do not cancel in the total:
        ///
        /// <list type="bullet">
        /// <item>"The bad-cat sat." has 3 space-separated tokens but types as "the bad cat sat",
        /// 4 words: the hyphen is a word break in the default stream.</item>
        /// <item>"hey ... ... you" has 4 space-separated tokens but types as "hey   you", 2 words:
        /// the punctuation-only tokens hold no typeable cell at all.</item>
        /// <item>"oh oh oh" is 3 either way.</item>
        /// </list>
        ///
        /// Real total 4 + 2 + 3 = 9; the naive split would say 3 + 4 + 3 = 10.
        /// </summary>
        private static IReadOnlyList<LyricLine> mixedFixture() => new[]
        {
            makeLine("The bad-cat sat.", 1000, 4000),
            makeLine("hey ... ... you", 4000, 7000),
            makeLine("oh oh oh", 7000, 10000),
        };

        private static BeatmapStatistic wordStatistic(TypeBeatBeatmap beatmap)
            => beatmap.GetStatistics().Single(s => s.Name.ToString() == "Words");

        [Test]
        public void PinsWordCountForKnownFixture()
        {
            var stat = wordStatistic(makeBeatmap(mixedFixture()));

            Assert.AreEqual("9", stat.Content);
        }

        [Test]
        public void WordCountAgreesWithPaceStatistics()
        {
            var lines = mixedFixture();
            var pace = LyricPaceStatistics.Compute(lines);
            var stat = wordStatistic(makeBeatmap(lines));

            // The whole point of the statistic: it is a rendering of pace.WordCount, not a second
            // count taken alongside it. Anything that re-derives words in TypeBeatBeatmap breaks
            // here before it can ship a total that disagrees with the WPM below it.
            Assert.AreEqual(pace.WordCount.ToString("N0"), stat.Content);
        }

        [Test]
        public void SingleLineWordCountAgreesWithPaceStatistics()
        {
            // Same property on a per-line basis, so a bug that only shows up on one shape of line
            // (hyphen break, punctuation-only token, plain text) is caught with the line named.
            foreach (var line in mixedFixture())
            {
                var single = new[] { line };
                var pace = LyricPaceStatistics.Compute(single);

                Assert.AreEqual(pace.WordCount.ToString("N0"), wordStatistic(makeBeatmap(single)).Content, line.RawText);
            }
        }

        [Test]
        public void WordCountIsNotRateAdjusted()
        {
            var statistics = makeBeatmap(mixedFixture()).GetStatistics().ToList();

            // A rate mod changes when the words arrive, not how many there are, so the word count
            // carries no RateAdjusted. Nor does the average word LENGTH, for the same reason. The
            // two rate statistics between them must still carry one.
            Assert.IsNull(statistics.Single(s => s.Name.ToString() == "Words").RateAdjusted);
            Assert.IsNotNull(statistics.Single(s => s.Name.ToString() == "Average WPM").RateAdjusted);
            Assert.IsNotNull(statistics.Single(s => s.Name.ToString() == "Average CPM").RateAdjusted);
            Assert.IsNull(statistics.Single(s => s.Name.ToString() == "Chars/word").RateAdjusted);
        }

        [Test]
        public void CharsPerWordSitsRightOfAverageCpmAndRendersOneDecimal()
        {
            // Order is the wedge's left-to-right order, and this one belongs immediately right of
            // Average CPM: it is the number that converts CPM into the WPM two places left of it.
            var statistics = makeBeatmap(mixedFixture()).GetStatistics().ToList();

            Assert.AreEqual(new[] { "Words", "Average WPM", "Average CPM", "Chars/word" }, statistics.Select(s => s.Name.ToString()).ToArray());

            // The fixture types as "the bad cat sat" (15 cells, 4 words), "hey   you" (9 cells,
            // 2 words) and "oh oh oh" (8 cells, 3 words): 32 cells over 9 words = 3.555..., which
            // renders to one decimal as 3.6. Rounded to whole characters it would read "4" and every
            // map in the library would read 3, 4 or 5.
            Assert.AreEqual(32, LyricPaceStatistics.Compute(mixedFixture()).TypeableCellCount);
            Assert.AreEqual("3.6", statistics.Single(s => s.Name.ToString() == "Chars/word").Content);
        }

        [Test]
        public void CharsPerWordIsTheRatioOfTheTwoCounts()
        {
            // Rendered from pace.AverageCharsPerWord, not re-derived, so it cannot contradict the
            // "Words" total sitting three places to its left.
            var lines = mixedFixture();
            var pace = LyricPaceStatistics.Compute(lines);
            var stat = makeBeatmap(lines).GetStatistics().Single(s => s.Name.ToString() == "Chars/word");

            Assert.AreEqual(((double)pace.TypeableCellCount / pace.WordCount).ToString("0.0"), stat.Content);
            Assert.AreEqual((float)(pace.AverageCharsPerWord / 10), stat.BarDisplayLength);
        }

        [Test]
        public void BarIsCalibratedForWordsNotLines()
        {
            // 40 lines of 6 words is a normal map (the shipped maps run 30 to 50 lines at 4.9 to 7.1
            // words per line). Under the old line calibration of /100 that would read 240/100 and pin
            // the bar full; the word calibration has to leave it partial and comparable.
            var lines = Enumerable.Range(0, 40).Select(i => makeLine("one two three four five six", i * 3000, (i + 1) * 3000)).ToList();
            var stat = wordStatistic(makeBeatmap(lines));

            Assert.AreEqual("240", stat.Content);
            Assert.AreEqual(240 / 600f, stat.BarDisplayLength);

            // And a map long enough to exceed the cap still clamps rather than overflowing.
            var longLines = Enumerable.Range(0, 200).Select(i => makeLine("one two three four five six", i * 3000, (i + 1) * 3000)).ToList();

            Assert.AreEqual(1f, wordStatistic(makeBeatmap(longLines)).BarDisplayLength);
        }

        [Test]
        public void EmptyBeatmapYieldsNoStatistics()
        {
            Assert.IsEmpty(makeBeatmap(Array.Empty<LyricLine>()).GetStatistics().ToList());
        }
    }
}
