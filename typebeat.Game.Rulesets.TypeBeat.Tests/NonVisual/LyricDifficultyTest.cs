// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class LyricDifficultyTest
    {
        private static LyricLine line(double start, double end, params (string text, double s, double e)[] units) => new LyricLine
        {
            RawText = string.Join(" ", units.Select(u => u.text)),
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = units.Select(u => new TimedUnit { Text = u.text, StartTime = u.s, EndTime = u.e }).ToArray(),
        };

        // A pool of varied real-ish words so generated maps don't saturate the repetition factor.
        private static readonly string[] pool = { "flame", "river", "cider", "amber", "otter", "nudge", "vivid", "query", "zebra", "month", "proxy", "blitz" };

        private static LyricLine[] buildMap(int lineCount, int wordsPerLine, double lineMs, double startAt = 0)
        {
            var lines = new List<LyricLine>();
            double t = startAt;
            int wordIndex = 0;

            for (int l = 0; l < lineCount; l++)
            {
                double wordMs = lineMs / wordsPerLine;
                var units = new (string, double, double)[wordsPerLine];

                for (int w = 0; w < wordsPerLine; w++)
                {
                    double ws = t + w * wordMs;
                    units[w] = (pool[wordIndex++ % pool.Length], ws, ws + wordMs);
                }

                lines.Add(line(t, t + lineMs, units));
                t += lineMs;
            }

            return lines.ToArray();
        }

        [Test]
        public void MatchesHandComputedRating()
        {
            // "cat cat" -> 0.45 stars. Shared anchor with the web port's LyricPaceTest, locking
            // the two ports to the same per-word strain sum.
            double sr = LyricDifficulty.Compute(new[] { line(0, 800, ("cat", 0, 400), ("cat", 400, 800)) });

            Assert.AreEqual(0.45, sr, 0.01);
        }

        [Test]
        public void EmptyMapIsZero()
        {
            Assert.AreEqual(0, LyricDifficulty.Compute(Array.Empty<LyricLine>()));
        }

        [Test]
        public void RateAdjustRaisesDifficulty()
        {
            var map = buildMap(lineCount: 12, wordsPerLine: 4, lineMs: 2400);

            double halfTime = LyricDifficulty.Compute(map, 0.75);
            double noMod = LyricDifficulty.Compute(map, 1.0);
            double doubleTime = LyricDifficulty.Compute(map, 1.5);

            // Faster clock -> shorter windows -> harder; slower clock -> easier.
            Assert.Less(halfTime, noMod);
            Assert.Less(noMod, doubleTime);
        }

        [Test]
        public void AddingContentNeverLowersRating()
        {
            var baseMap = buildMap(lineCount: 8, wordsPerLine: 4, lineMs: 2400);
            // Same-pace continuation appended contiguously after the base map.
            var extended = baseMap.Concat(buildMap(lineCount: 8, wordsPerLine: 4, lineMs: 2400, startAt: 8 * 2400)).ToArray();

            double baseSr = LyricDifficulty.Compute(baseMap);
            double extendedSr = LyricDifficulty.Compute(extended);

            // The defining property: a superset can never rate below its subset.
            Assert.GreaterOrEqual(extendedSr, baseSr);
            // Length counts, but only logarithmically — doubling must not double the rating.
            Assert.Less(extendedSr, baseSr * 2);
        }

        [Test]
        public void SpikeBeatsFiller()
        {
            // A short dense burst (fast words) rates above a long even stretch of the same words.
            var spike = buildMap(lineCount: 6, wordsPerLine: 6, lineMs: 1200); // ~fast
            var filler = buildMap(lineCount: 24, wordsPerLine: 3, lineMs: 3000); // long, easy

            Assert.Greater(LyricDifficulty.Compute(spike), LyricDifficulty.Compute(filler));
        }

        [Test]
        public void RealisticMapLandsInASaneBand()
        {
            // ~100 WPM (4 words / 2.4 s line), 40 lines. Perfectly uniform (no rhythm variation
            // or pressure spikes), so it sits a little below real ~100 WPM maps, which rate ~3.5-4.
            var map = buildMap(lineCount: 40, wordsPerLine: 4, lineMs: 2400);
            double sr = LyricDifficulty.Compute(map);

            TestContext.WriteLine($"~100 WPM / 40-line map -> {sr:0.00} stars");
            Assert.That(sr, Is.InRange(2.0, 6.5));
        }

        [Test]
        public void SustainedDifficultyOutweighsAMatchingPeak()
        {
            // Two maps share an identical single hardest chorus (same peak strain), but one keeps
            // going with a dense a cappella section afterwards ("Insane" keeping the backing-vocal
            // lines a "Hard" diff would drop) while the other cuts to something easy. A bucket/
            // single-peak formula rates these nearly equal since D_max is identical; summing over
            // every word must rate the sustained one clearly harder, since the extra section is
            // real additional difficulty, not filler.
            var peakChorus = buildMap(lineCount: 4, wordsPerLine: 4, lineMs: 1200); // ~a hard chorus
            double peakEndMs = 4 * 1200;

            var easyTail = buildMap(lineCount: 10, wordsPerLine: 2, lineMs: 2400, startAt: peakEndMs);
            // Same density as the chorus — like an Insane diff keeping backing-vocal lines a Hard
            // diff drops, so the ending stays just as dense as the peak instead of going quiet.
            var hardTail = buildMap(lineCount: 10, wordsPerLine: 4, lineMs: 1200, startAt: peakEndMs);

            var easierVersion = peakChorus.Concat(easyTail).ToArray();
            var harderVersion = peakChorus.Concat(hardTail).ToArray();

            double easierSr = LyricDifficulty.Compute(easierVersion);
            double harderSr = LyricDifficulty.Compute(harderVersion);

            TestContext.WriteLine($"matching-peak, easy tail -> {easierSr:0.00}; matching-peak, hard tail -> {harderSr:0.00}");

            // Same peak, but the sustained-hard version must clearly separate from the easy one —
            // this is exactly what a single-bucket/D_max-only formula cannot see.
            Assert.That(harderSr - easierSr, Is.GreaterThan(0.15));
        }
    }
}
