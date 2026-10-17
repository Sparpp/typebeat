// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// THE PACE CHART'S FIGURES FOLLOW THE CLOCK, and they do not all follow it the same way.
    ///
    /// <para>The peak and curve are recomputed over a fixed playback-time window, whose authored
    /// span changes with the clock. The average follows the clock, but a pause capped at one playback second
    /// can change its fraction of the total time, so it is recomputed at each rate.</para>
    ///
    /// <para>The TARGET is not one of those. It is the map's hardest window re-expressed as the pace an
    /// equally demanding <see cref="LyricDifficulty.TargetWindowSeconds"/> stretch would ask for, i.e.
    /// read against the capability curve at that fixed duration - and a faster clock shortens the
    /// duration the window actually spans, so the conversion factor itself moves. Multiplying the rate-1
    /// target by the rate would answer a different question, so the target is RECOMPUTED through the
    /// difficulty model at the playing clock instead, exactly as the rating does.</para>
    /// </summary>
    public class TypingPaceRateTest
    {
        [Test]
        public void APlainPlayReadsTheMapAsAuthored()
        {
            TypeBeatBeatmap beatmap = map();
            var lines = beatmap.HitObjects.Select(h => h.Line).ToList();

            TypingPaceProfile raw = beatmap.GetTypingPace()!;
            TypingPaceProfile again = beatmap.GetTypingPace(1)!;

            double model = LyricDifficulty.ComputeDetail(lines, 1, false, LyricDifficulty.EnduranceAxis.Envelope).TargetWpm;

            Assert.Multiple(() =>
            {
                Assert.That(again.PeakWpm, Is.EqualTo(raw.PeakWpm).Within(1e-12), "rate 1 is the authored reading");
                Assert.That(again.AverageWpm, Is.EqualTo(raw.AverageWpm).Within(1e-12));
                Assert.That(again.TargetWpm, Is.EqualTo(raw.TargetWpm).Within(1e-12));
                Assert.That(raw.TargetWpm, Is.EqualTo(Math.Max(model, raw.AverageWpm)).Within(1e-9),
                    "and the target is the model's own, floored at the map's average");
            });
        }

        /// <summary>
        /// This fixture's gaps stay under one playback second at each tested rate, so its average
        /// scales exactly with the clock.
        /// </summary>
        [Test]
        public void TheAverageScalesWithTheClock()
        {
            TypeBeatBeatmap beatmap = map();
            TypingPaceProfile raw = beatmap.GetTypingPace()!;
            TypingPaceProfile fast = beatmap.GetTypingPace(1.5)!;
            TypingPaceProfile slow = beatmap.GetTypingPace(0.75)!;

            Assert.Multiple(() =>
            {
                Assert.That(raw.PeakWpm, Is.GreaterThan(0), "not vacuous: the fixture has a peak");
                Assert.That(raw.AverageWpm, Is.GreaterThan(0));
                Assert.That(fast.AverageWpm, Is.EqualTo(raw.AverageWpm * 1.5).Within(1e-9), "and so is the average");
                Assert.That(slow.AverageWpm, Is.EqualTo(raw.AverageWpm * 0.75).Within(1e-9), "down as well as up");

            });
        }

        [TestCase(0.75)]
        [TestCase(1.0)]
        [TestCase(1.5)]
        public void PeakBelowTheAverageIsRaisedAtEachPlaybackRate(double rate)
        {
            var beatmap = new TypeBeatBeatmap();
            addLine(beatmap, 0, 0, 1000, new List<TimedUnit>
            {
                new TimedUnit { Text = new string('a', 16), StartTime = 0, EndTime = 1000 },
            });

            var lines = beatmap.HitObjects.Select(h => h.Line).ToArray();
            var curve = LyricWpmCurve.Compute(lines, rate: rate);
            TypingPaceProfile pace = beatmap.GetTypingPace(rate)!;

            Assert.That(curve.PeakWpm, Is.LessThan(pace.AverageWpm), "the short burst is diluted by the peak's minimum window");
            Assert.That(pace.PeakWpm, Is.EqualTo(pace.AverageWpm).Within(1e-9));
            Assert.That(pace.WpmCurve, Is.EqualTo(curve.Curve), "the display floor must preserve the raw curve");
        }

        [TestCase(1.0, 128.0)]
        [TestCase(1.5, 184.0)]
        [TestCase(0.75, 96.0)]
        public void ThePeakUsesAPlaybackTimeWindowAtEachClock(double rate, double expectedPeak)
        {
            var beatmap = new TypeBeatBeatmap();
            addLine(beatmap, 0, 0, 5000, new List<TimedUnit>
            {
                new TimedUnit { Text = new string('a', 50), StartTime = 0, EndTime = 5000 },
            });

            // Cells are 100 ms apart. At 1x the inclusive 1.5s window holds 16 cells;
            // at 1.5x its 2250 map-ms hold 23. At 0.75x the 16-cell floor keeps the
            // window at 1500 map-ms. Each cell is 1/5 of a word.
            TypingPaceProfile pace = beatmap.GetTypingPace(rate)!;
            Assert.That(pace.PeakWpm, Is.EqualTo(expectedPeak).Within(1e-9));
            Assert.That(pace.WpmCurve.Max(), Is.EqualTo(expectedPeak).Within(1e-9));
        }

        /// <summary>
        /// THE ONE THE PLAYER SEES. The target is the difficulty model's own figure at the playing
        /// clock - the SR algorithm's capability-curve conversion at the shortened window duration -
        /// and not the rate-1 figure multiplied by the rate.
        /// </summary>
        [Test]
        public void TheTargetIsRecomputedAtTheClockRatherThanMultiplied()
        {
            TypeBeatBeatmap beatmap = map();
            var lines = beatmap.HitObjects.Select(h => h.Line).ToList();

            TypingPaceProfile raw = beatmap.GetTypingPace()!;
            TypingPaceProfile fast = beatmap.GetTypingPace(1.5)!;

            // The SR algorithm's own target at 1.5x, floored at the map's own average exactly as the pace
            // figure is (see LyricPaceStatistics.pace_floor_target).
            double model = LyricDifficulty.ComputeDetail(lines, 1.5, false, LyricDifficulty.EnduranceAxis.Envelope).TargetWpm;

            Assert.Multiple(() =>
            {
                Assert.That(raw.TargetWpm, Is.GreaterThan(0), "not vacuous: the fixture has a target");
                Assert.That(model, Is.GreaterThan(0), "and the model reads one at 1.5x");
                Assert.That(fast.TargetWpm, Is.EqualTo(Math.Max(model, fast.AverageWpm)).Within(1e-9),
                    "the chart prints the model's target at the clock");
                Assert.That(fast.TargetWpm, Is.Not.EqualTo(raw.TargetWpm * 1.5).Within(1e-6),
                    $"which is NOT the rate-1 target times the rate "
                    + $"(model {fast.TargetWpm:F3} at DT against {raw.TargetWpm * 1.5:F3} multiplied): "
                    + "the reading duration shrank too");
            });
        }

        /// <summary>
        /// THE TWO SURFACES PRINT ONE NUMBER. The chart reads its target from the profile; the title
        /// wedge's row reads it through its own RateAdjusted hook. Both come through
        /// <see cref="LyricPaceStatistics.Compute"/>, so a DT or HT toggle cannot show the player two
        /// different targets for the same map.
        /// </summary>
        [Test]
        public void TheChartAndTheTitleWedgePrintTheSameTargetAtEveryClock()
        {
            TypeBeatBeatmap beatmap = map();

            BeatmapStatistic row = beatmap.GetStatistics().Single(s => s.Name == "Target WPM");

            Assert.That(row.RateAdjusted, Is.Not.Null, "the row has to be rate-adjustable at all");

            foreach (double rate in new[] { 1d, 1.5, 0.75, 1.35 })
            {
                double chartTarget = beatmap.GetTypingPace(rate)!.TargetWpm;

                Assert.That(row.RateAdjusted!(rate).Content, Is.EqualTo(chartTarget.ToString("0")),
                    $"the wedge and the chart disagree at {rate}x");
            }
        }

        [TestCase(BeatmapLanguage.Japanese, "shi", "し", 2, false, 1)]
        [TestCase(BeatmapLanguage.Japanese, "shi", "し", 2, true, 1.5)]
        [TestCase(BeatmapLanguage.German, "strasse", "straße", 6, false, 0.75)]
        [TestCase(BeatmapLanguage.French, "ee!", "éê！", 4, true, 1.5)]
        public void PolyglotPaceCountsRequiredInputsAcrossTheChartAndRows(BeatmapLanguage language, string romaji,
                                                                 string original, int inputs, bool literate, double rate)
        {
            var beatmap = new TypeBeatBeatmap();
            beatmap.Metadata.Language = language;
            string text = string.Concat(Enumerable.Repeat(romaji, 20));
            string script = string.Concat(Enumerable.Repeat(original, 20));
            addLine(beatmap, 0, 0, 6000, new List<TimedUnit>
            {
                new TimedUnit { Text = text, Original = script, StartTime = 0, EndTime = 6000 },
            });
            var reference = new TypeBeatBeatmap();
            addLine(reference, 0, 0, 6000, new List<TimedUnit>
            {
                new TimedUnit { Text = new string('a', inputs * 20), StartTime = 0, EndTime = 6000 },
            });
            if (literate)
                new TypeBeatModLiterate().ApplyToBeatmap(beatmap);
            new TypeBeatModPolyglot().ApplyToBeatmap(beatmap);

            var expected = reference.GetTypingPace(rate)!;
            var actual = beatmap.GetTypingPace(rate)!;
            Assert.Multiple(() =>
            {
                Assert.That(actual.PeakWpm, Is.EqualTo(expected.PeakWpm).Within(1e-9));
                Assert.That(actual.AverageWpm, Is.EqualTo(expected.AverageWpm).Within(1e-9));
                Assert.That(actual.TargetWpm, Is.EqualTo(expected.TargetWpm).Within(1e-9));
                Assert.That(beatmap.HitObjects.Single().Line.RawText, Is.EqualTo(text));
                Assert.That(beatmap.HitObjects.Single().Line.Units.Single().Original, Is.EqualTo(script));
            });
            var rows = beatmap.GetStatistics().ToDictionary(row => row.Name.ToString());
            Assert.That(rows["Average WPM"].RateAdjusted!(rate).Content, Is.EqualTo(actual.AverageWpm.ToString("0")));
            Assert.That(rows["Target WPM"].RateAdjusted!(rate).Content, Is.EqualTo(actual.TargetWpm.ToString("0")));
        }

        [TestCase(BeatmapLanguage.Chinese, "中文")]
        [TestCase(BeatmapLanguage.Korean, "한국")]
        [TestCase(BeatmapLanguage.Japanese, null)]
        public void UnsupportedOrMissingOriginalsKeepTheirAuthoredPace(BeatmapLanguage language, string? original)
        {
            var beatmap = map();
            beatmap.Metadata.Language = language;
            foreach (var hit in beatmap.HitObjects)
                hit.Line = new LyricLine
                {
                    RawText = hit.Line.RawText, Original = original, Units = hit.Line.Units,
                    StartTime = hit.Line.StartTime, EndTime = hit.Line.EndTime, SingEndTime = hit.Line.SingEndTime,
                };
            var expected = beatmap.GetTypingPace()!;
            new TypeBeatModPolyglot().ApplyToBeatmap(beatmap);
            var actual = beatmap.GetTypingPace()!;
            Assert.That((actual.PeakWpm, actual.TargetWpm, actual.AverageWpm),
                Is.EqualTo((expected.PeakWpm, expected.TargetWpm, expected.AverageWpm)));
        }

        private static TypeBeatBeatmap map()
        {
            var beatmap = new TypeBeatBeatmap();

            for (int i = 0; i < 12; i++)
            {
                double at = i * 2000;
                var units = new List<TimedUnit>();

                for (int w = 0; w < 4; w++)
                    units.Add(new TimedUnit { Text = "flame", StartTime = at + w * 400, EndTime = at + (w + 1) * 400 });

                addLine(beatmap, i, at, 1800, units);
            }

            // One short, very dense burst at the end, which is the material the TARGET is about: it is
            // the map's hardest window, and its duration is short enough that the capability curve's
            // conversion at that duration is what the figure turns on - the whole point of recomputing
            // the target at the clock rather than scaling it.
            var burst = new List<TimedUnit>();

            for (int w = 0; w < 20; w++)
                burst.Add(new TimedUnit { Text = "flame", StartTime = 24000 + w * 50, EndTime = 24000 + (w + 1) * 50 });

            addLine(beatmap, 12, 24000, 1000, burst);

            return beatmap;
        }

        private static void addLine(TypeBeatBeatmap beatmap, int index, double at, double span, List<TimedUnit> units)
        {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = at,
                    Granularity = TimingGranularity.Line,
                    LineIndex = index,
                    Line = new LyricLine
                    {
                        RawText = string.Join(' ', units.Select(u => u.Text)),
                        StartTime = at,
                        EndTime = at + span,
                        SingEndTime = at + span,
                        Units = units,
                    },
                });
        }
    }
}
