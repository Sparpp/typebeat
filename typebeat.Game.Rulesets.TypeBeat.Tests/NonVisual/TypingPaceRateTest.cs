// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// THE PACE CHART'S FIGURES FOLLOW THE CLOCK, and they do not all follow it the same way.
    ///
    /// <para>Two of the three are plain rates: the peak and the average are WPM - cells over time - and
    /// a speed-changing mod plays the same cells in less time, so they scale with the clock exactly. The
    /// curve's window is a run of CELLS rather than a span of seconds, so that holds for the peak and
    /// for every sample of the graph, which is also why the graph's normalised shape is identical at
    /// any rate.</para>
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
        /// The two plain rates: DT asks for 1.5x, HT for 0.75x, and the graph's shape does not move
        /// because the chart is normalised by the peak that scaled with it.
        /// </summary>
        [Test]
        public void ThePeakAndAverageScaleWithTheClock()
        {
            TypeBeatBeatmap beatmap = map();
            TypingPaceProfile raw = beatmap.GetTypingPace()!;
            TypingPaceProfile fast = beatmap.GetTypingPace(1.5)!;
            TypingPaceProfile slow = beatmap.GetTypingPace(0.75)!;

            Assert.Multiple(() =>
            {
                Assert.That(raw.PeakWpm, Is.GreaterThan(0), "not vacuous: the fixture has a peak");
                Assert.That(raw.AverageWpm, Is.GreaterThan(0));
                Assert.That(fast.PeakWpm, Is.EqualTo(raw.PeakWpm * 1.5).Within(1e-9), "the peak is cells over time");
                Assert.That(fast.AverageWpm, Is.EqualTo(raw.AverageWpm * 1.5).Within(1e-9), "and so is the average");
                Assert.That(slow.AverageWpm, Is.EqualTo(raw.AverageWpm * 0.75).Within(1e-9), "down as well as up");

                Assert.That(fast.WpmCurve.Select(w => w / fast.PeakWpm),
                    Is.EqualTo(raw.WpmCurve.Select(w => w / raw.PeakWpm)).Within(1e-12),
                    "the chart's normalised shape is the same at any clock");
            });
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
        /// <see cref="LyricPaceStatistics.TargetWpmAt"/>, so a DT or HT toggle cannot show the player two
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
