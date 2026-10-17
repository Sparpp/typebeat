// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Screens.Select;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using Assert = NUnit.Framework.Legacy.ClassicAssert;
using CollectionAssert = NUnit.Framework.Legacy.CollectionAssert;

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
            // carries no RateAdjusted. Text complexity also describes the text itself; pace and rhythm
            // re-read their timing windows at the selected rate.
            Assert.IsNull(statistics.Single(s => s.Name.ToString() == "Words").RateAdjusted);
            Assert.IsNotNull(statistics.Single(s => s.Name.ToString() == "Average WPM").RateAdjusted);
            Assert.IsNotNull(statistics.Single(s => s.Name.ToString() == "Target WPM").RateAdjusted);
            Assert.IsNull(statistics.Single(s => s.Name.ToString() == "Complexity").RateAdjusted);
            Assert.IsNotNull(statistics.Single(s => s.Name.ToString() == "Rhythm").RateAdjusted);
        }

        [Test]
        public void TargetWpmIsTheModelsSpeedWindowFigureOnTheWpmBarScale()
        {
            // The statistic that replaced Average CPM. Two things are pinned: it is
            // LyricPaceStatistics.TargetWpm rendered, never a second derivation, and its bar is
            // normalised against the SAME 150 WPM cap as Average WPM (the departing CPM used
            // 150 * 5, and carrying that cap over would have drawn a full-length bar on every map).
            //
            // The fixture is DENSE on purpose. Target WPM is the map's hardest window by raw speed
            // re-expressed at a fixed duration, and a window only becomes a candidate once it holds
            // the model's own character floor, so a map of three short lines has a perfectly good
            // average and no target at all. Twelve packed lines clear it; the mixed fixture above
            // is what the floor refuses.
            var lines = denseFixture();
            var pace = LyricPaceStatistics.Compute(lines);
            var stat = makeBeatmap(lines).GetStatistics().Single(s => s.Name.ToString() == "Target WPM");

            Assert.Greater(pace.TargetWpm, 0, "the dense fixture has to clear the peak character floor");
            Assert.Greater(pace.TargetWpm, pace.AverageWpm, "the front-loaded fixture's peak is the higher figure");

            Assert.AreEqual(pace.TargetWpm.ToString("0"), stat.Content);
            Assert.AreEqual((float)Math.Min(1, pace.TargetWpm / 150), stat.BarDisplayLength);

            // And the rate RE-READS it, where the average beside it only scales. The target is the
            // map's hardest window re-expressed at a fixed reading duration, and a faster clock shortens
            // that duration too, so the figure the row prints at 1.5x is the MODEL's own target at 1.5x -
            // the same number the pace chart prints (TypingPaceRateTest pins the two surfaces together).
            Assert.AreEqual(LyricPaceStatistics.Compute(lines, false, 1.5).TargetWpm.ToString("0"), stat.RateAdjusted!(1.5).Item1);
        }

        /// <summary>
        /// Twelve packed eight-word lines, no rests between them, FRONT-LOADED: the first half runs at
        /// 700 ms a line and the second at 1300, so the map is dense enough that a scheduled window
        /// holds the model's weighted-character floor and peaked enough that its target sits clear of
        /// its whole-map average rather than being floored up to it.
        /// </summary>
        private static IReadOnlyList<LyricLine> denseFixture()
        {
            var lines = new List<LyricLine>();
            double at = 1000;

            for (int i = 0; i < 12; i++)
            {
                double ms = i < 6 ? 700 : 1300;
                lines.Add(makeLine("a b c d e f g h", at, at + ms));
                at += ms;
            }

            return lines;
        }

        [Test]
        public void LiterateMarksReachTheStatisticsThroughTheBeatmap()
        {
            // The statistics are figures of the CONVERTED beatmap, so a line the Literate mod
            // stamped has to be measured on the authored stream. The stamp is what the converter
            // leaves for the display to read, which is how a caller that converted with the
            // selected mods reaches this without the beatmap itself knowing what a mod is.
            var lines = literateFixture();
            var beatmap = makeBeatmap(lines);

            double plainAverage = paceStatistic(beatmap, "Average WPM");
            double plainTarget = paceStatistic(beatmap, "Target WPM");

            new TypeBeatModLiterate().ApplyToBeatmap(beatmap);

            var expected = LyricPaceStatistics.Compute(lines, literate: true);

            Assert.AreEqual(expected.AverageWpm.ToString("0"), beatmap.GetStatistics().Single(s => s.Name.ToString() == "Average WPM").Content);
            Assert.AreEqual(expected.TargetWpm.ToString("0"), beatmap.GetStatistics().Single(s => s.Name.ToString() == "Target WPM").Content);

            // The extra marks ask for more keys inside the same sung time, so the pace the map
            // advertises has to rise with them.
            Assert.Greater(paceStatistic(beatmap, "Average WPM"), plainAverage);
            Assert.Greater(paceStatistic(beatmap, "Target WPM"), plainTarget);

            // The stats are still rendered from the pace model, never re-derived here: the bar
            // follows the same figure the text does.
            Assert.AreEqual((float)Math.Min(1, expected.AverageWpm / 150), beatmap.GetStatistics().Single(s => s.Name.ToString() == "Average WPM").BarDisplayLength);
        }

        [Test]
        public void LiterateMarksMoveThePaceProfileButNotTheSharedCurve()
        {
            // The metadata wedge's readouts share the beatmap's stamp, so Target and Average follow
            // the mod. The curve deliberately does not: it is LyricWpmCurve, mirrored byte for byte
            // on the website. The displayed peak also respects each stream's average floor.
            var lines = literateFixture();
            var beatmap = makeBeatmap(lines);

            var plain = beatmap.GetTypingPace()!;

            new TypeBeatModLiterate().ApplyToBeatmap(beatmap);

            var literate = beatmap.GetTypingPace()!;

            Assert.Greater(literate.AverageWpm, plain.AverageWpm);
            Assert.Greater(literate.TargetWpm, plain.TargetWpm);

            double rawPeak = LyricWpmCurve.Compute(lines).PeakWpm;
            Assert.AreEqual(Math.Max(rawPeak, plain.AverageWpm), plain.PeakWpm, 1e-12);
            Assert.AreEqual(Math.Max(rawPeak, literate.AverageWpm), literate.PeakWpm, 1e-12);
            CollectionAssert.AreEqual(plain.WpmCurve, literate.WpmCurve);
        }

        /// <summary>
        /// Twelve packed lines of "The bad-cat sat.", the shape whose two streams differ in BOTH
        /// counts (see LyricPaceStatisticsTest). Twelve lines are enough for the difficulty model
        /// to have a real window to read the target from rather than only the pace floor.
        /// </summary>
        private static IReadOnlyList<LyricLine> literateFixture()
        {
            var lines = new List<LyricLine>();
            double at = 1000;

            for (int i = 0; i < 12; i++)
            {
                double ms = i < 6 ? 700 : 1300;
                lines.Add(makeLine("The bad-cat sat.", at, at + ms));
                at += ms;
            }

            return lines;
        }

        private static double paceStatistic(TypeBeatBeatmap beatmap, string name)
            => double.Parse(beatmap.GetStatistics().Single(s => s.Name.ToString() == name).Content, CultureInfo.InvariantCulture);

        [Test]
        public void ComplexityReplacesCharsPerWordAndRhythmSitsToItsRight()
        {
            var statistics = makeBeatmap(mixedFixture()).GetStatistics().ToList();
            Assert.AreEqual(new[] { "Words", "Average WPM", "Target WPM", "Complexity", "Rhythm" }, statistics.Select(s => s.Name.ToString()).ToArray());
        }

        [TestCase("Asphyxiated", false)]
        [TestCase("with one hand on the door", true)]
        public void ComplexityShowsMeasuredTextDifficulty(string text, bool easy)
        {
            var lines = new[] { makeLine(text, 0, 4000) };
            var rating = LyricDifficulty.ComputeDetail(lines);
            var stat = makeBeatmap(lines).GetStatistics().Single(s => s.Name.ToString() == "Complexity");
            Assert.IsTrue(rating.TypabilityApplied);
            Assert.AreEqual(DifficultyMetricScale.Complexity(rating.TypabilityMeanZ).ToString("0.00", CultureInfo.InvariantCulture), stat.Content);
            Assert.IsNull(stat.RateAdjusted);
            double neutral = DifficultyMetricScale.Complexity(0);
            Assert.AreEqual(!easy, double.Parse(stat.Content, CultureInfo.InvariantCulture) > neutral, "harder text has a higher complexity score");
        }

        [Test]
        public void UnscoredTextDoesNotDisplayAsMeasuredNeutralComplexity()
        {
            var beatmap = makeBeatmap(new[] { makeLine("zzqx wvvt qqzz", 0, 4000) });
            var stat = beatmap.GetStatistics().Single(s => s.Name.ToString() == "Complexity");
            Assert.AreEqual("N/A", stat.Content);
            Assert.AreEqual(0f, stat.BarDisplayLength);
        }

        [TestCase(0.75)]
        [TestCase(1)]
        [TestCase(1.5)]
        public void RhythmReadsTheMapsActualWindowsAtTheSelectedRate(double rate)
        {
            // Subdivided uneven words force pace changes between small judgement windows.
            string[] words = { "flame", "river", "cider", "amber" };
            double[] fractions = { 0, 0.025, 0.5, 0.525, 1 };
            var lines = Enumerable.Range(0, 20).Select(i => new LyricLine
            {
                RawText = "flame river cider amber",
                StartTime = i * 4000,
                EndTime = (i + 1) * 4000,
                SingEndTime = (i + 1) * 4000,
                Units = words.Select((word, w) => new TimedUnit
                {
                    Text = word,
                    StartTime = i * 4000 + 4000 * fractions[w],
                    EndTime = i * 4000 + 4000 * fractions[w + 1],
                    SyllableBoundaries = Enumerable.Range(1, word.Length - 1)
                        .Select(j => i * 4000 + 4000 * (fractions[w] + (fractions[w + 1] - fractions[w]) * j / word.Length)).ToArray(),
                    SyllableSplits = Enumerable.Range(1, word.Length - 1).ToArray(),
                }).ToArray(),
            }).ToArray();
            var report = LyricDifficulty.RateChunked(lines, rate, false, LyricDifficulty.ShippedScores, ChunkedEndurance.Active).Report;
            double rhythm = DifficultyMetricScale.Rhythm(Math.Max(report.WholeMapRhythmLoadPerSecond, report.HardestWindowRhythmLoadPerSecond));
            var stat = makeBeatmap(lines).GetStatistics().Single(s => s.Name.ToString() == "Rhythm");
            var rendered = stat.RateAdjusted!(rate);
            Assert.Greater(rhythm, 1);
            Assert.AreEqual(rhythm.ToString("0.00", CultureInfo.InvariantCulture), rendered.Content);
            Assert.Greater(rendered.BarDisplayLength!.Value, 0);
            Assert.LessOrEqual(rendered.BarDisplayLength.Value, 1);
            if (rate == 1)
            {
                Assert.AreEqual(stat.Content, rendered.Content);
                Assert.AreEqual(stat.BarDisplayLength, rendered.BarDisplayLength);
            }
        }

        [Test]
        public void LiterateComplexityUsesTheConvertedText()
        {
            var lines = literateFixture();
            var beatmap = makeBeatmap(lines);
            new TypeBeatModLiterate().ApplyToBeatmap(beatmap);
            var expected = LyricDifficulty.ComputeDetail(lines, literate: true);
            Assert.IsTrue(expected.TypabilityApplied);
            Assert.AreEqual(DifficultyMetricScale.Complexity(expected.TypabilityMeanZ).ToString("0.00", CultureInfo.InvariantCulture),
                beatmap.GetStatistics().Single(s => s.Name.ToString() == "Complexity").Content);
        }

        [TestCase(1)]
        [TestCase(1.5)]
        public void HardRockChangesTheSongSelectRhythmAtTheSameClock(double rate)
        {
            var lines = literateFixture();
            var statistics = makeBeatmap(lines).GetStatistics().ToList();
            string render(Mod[] mods) => BeatmapTitleWedge.DifficultyDisplay.RenderStatistics(statistics, rate, mods)
                .Single(s => s.Label.ToString() == "Rhythm").Content!;
            var settings = ChunkedEndurance.Active with { complexity_mod = ChunkedEndurance.mod_hardrock };
            var report = LyricDifficulty.RateChunked(lines, rate, false, LyricDifficulty.ShippedScores, settings).Report;
            double expected = DifficultyMetricScale.Rhythm(Math.Max(report.WholeMapRhythmLoadPerSecond, report.HardestWindowRhythmLoadPerSecond));
            string plain = render(Array.Empty<Mod>());
            string hardRock = render(new Mod[] { new TypeBeatModHardRock() });
            Assert.AreEqual(expected.ToString("0.00", CultureInfo.InvariantCulture), hardRock);
            Assert.Greater(double.Parse(hardRock, CultureInfo.InvariantCulture), double.Parse(plain, CultureInfo.InvariantCulture));
            Assert.AreEqual(plain, render(Array.Empty<Mod>()), "removing HR must restore the baseline, including cached readings");
            Assert.AreEqual(hardRock, render(new Mod[] { new TypeBeatModHardRock() }), "re-enabling HR must restore the HR reading");
        }

        [Test]
        public void DisplayFallbackPreservesRateAdjustedAndFixedStatistics()
        {
            var statistics = makeBeatmap(mixedFixture()).GetStatistics().ToList();
            foreach (var stat in statistics.Where(s => s.Name != "Rhythm"))
            {
                var expected = stat.RateAdjusted != null ? stat.RateAdjusted(1.5) : (stat.Content, stat.BarDisplayLength);
                Assert.AreEqual(expected, stat.GetDisplayValue(1.5, new Mod[] { new TypeBeatModHardRock() }));
            }
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
