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
            // "cat cat" -> 0.79 stars. Shared anchor with the web port's LyricPaceTest, locking
            // the two ports to the same per-word strain sum (density + endurance).
            double sr = LyricDifficulty.Compute(new[] { line(0, 800, ("cat", 0, 400), ("cat", 400, 800)) });

            Assert.AreEqual(0.79, sr, 0.01);
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
        public void RateAdjustedRatingIsNotTruncatedAtTheTop()
        {
            // backlog 118. Compute used to end in a flat clamp to 10 stars, chosen to keep a star
            // BADGE sane, and it truncated the rate-adjusted ratings with it. That mattered because
            // the same pass produces sr_dt, the 1.50x rating PerformancePoints prices a Double Time
            // play from. The asymmetry is the point: this shape stays clear of 10 at 1.00x and
            // passes it at 1.50x, so the old ceiling cut one of the two numbers whose RATIO decides
            // what the rate is charged for, which is the same shape the real catalogue has (no base
            // rating has ever reached 10; sr_dt reached it on 3 of the 5 real reference maps).
            var map = buildMap(lineCount: 40, wordsPerLine: 8, lineMs: 1200);

            double noMod = LyricDifficulty.Compute(map);
            double doubleTime = LyricDifficulty.Compute(map, 1.50);

            Assert.That(noMod, Is.EqualTo(6.1622).Within(0.001));
            Assert.That(doubleTime, Is.EqualTo(10.5567).Within(0.001), "under the old ceiling this read exactly 10.00");
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
            // Length counts, but only logarithmically; doubling must not double the rating.
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
            // or pressure spikes), so it sits well below real ~100 WPM maps, which rate ~6 and up.
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
            // Same density as the chorus, like an Insane diff keeping backing-vocal lines a Hard
            // diff drops, so the ending stays just as dense as the peak instead of going quiet.
            var hardTail = buildMap(lineCount: 10, wordsPerLine: 4, lineMs: 1200, startAt: peakEndMs);

            var easierVersion = peakChorus.Concat(easyTail).ToArray();
            var harderVersion = peakChorus.Concat(hardTail).ToArray();

            double easierSr = LyricDifficulty.Compute(easierVersion);
            double harderSr = LyricDifficulty.Compute(harderVersion);

            TestContext.WriteLine($"matching-peak, easy tail -> {easierSr:0.00}; matching-peak, hard tail -> {harderSr:0.00}");

            // Same peak, but the sustained-hard version must clearly separate from the easy one;
            // this is exactly what a single-bucket/D_max-only formula cannot see.
            Assert.That(harderSr - easierSr, Is.GreaterThan(0.15));
        }

        #region The Literate stream (backlog 144)

        /// <summary>
        /// The whole reason the Literate mod is priced through this rating rather than by a flat
        /// multiplier: it makes every supported punctuation mark a typed cell of its own, so the
        /// map really is a different map and rates as one.
        /// </summary>
        [Test]
        public void LiterateRatesThePunctuatedStream()
        {
            var map = new[]
            {
                line(0, 2000, ("Hello,", 0, 700), ("bad-cat!", 700, 1400), ("sat...", 1400, 2000)),
            };

            double plain = LyricDifficulty.Compute(map);
            double literate = LyricDifficulty.Compute(map, 1, literate: true);

            TestContext.WriteLine($"plain -> {plain:0.0000}; literate -> {literate:0.0000}");

            Assert.That(literate, Is.Not.EqualTo(plain).Within(1e-9));
        }

        /// <summary>
        /// AND IT IS EXACTLY A NO-OP WITHOUT MARKS OR CAPITALS, which is the other half of the
        /// claim and the one that keeps every stored rating where it is: the default stream of a
        /// mark-free, lower-case line is that line, so the Literate pass over such a map must be
        /// BIT-identical rather than merely close. Every map authored before punctuation existed
        /// had its marks stripped on the way in, so this is the ordinary case.
        /// </summary>
        [Test]
        public void LiterateIsBitIdenticalOnAMapWithNoMarksAndNoCapitals()
        {
            var map = buildMap(lineCount: 12, wordsPerLine: 6, lineMs: 1800);

            Assert.Multiple(() =>
            {
                foreach (double rate in new[] { 0.75, 1.00, 1.50 })
                {
                    Assert.That(LyricDifficulty.Compute(map, rate, literate: true),
                        Is.EqualTo(LyricDifficulty.Compute(map, rate)), $"rate {rate}");
                }
            });
        }

        /// <summary>
        /// Literate does not compose with the rate by any constant, which is why the server stores
        /// the CROSS PRODUCT of the two (029_literate_stars.sql) instead of deriving three of the
        /// six ratings from the other three. The obvious saving is
        /// <c>sr_literate_dt = sr_literate · (sr_dt/sr_base)</c>; it is wrong, and this is where
        /// that is written down so the next reader does not have to rediscover it.
        /// </summary>
        [Test]
        public void TheLiterateRatingIsNotTheRateRatingTimesAConstant()
        {
            var map = new[]
            {
                line(0, 2000, ("Hello,", 0, 700), ("bad-cat!", 700, 1400), ("sat...", 1400, 2000)),
                line(2200, 5000, ("Typing", 2200, 3000), ("is", 3000, 3400), ("a", 3400, 3700), ("rhythm;", 3700, 4300), ("not", 4300, 4700), ("a", 4700, 4850), ("race.", 4850, 5000)),
            };

            double plainBase = LyricDifficulty.Compute(map);
            double plainDt = LyricDifficulty.Compute(map, 1.50);
            double literateBase = LyricDifficulty.Compute(map, 1, literate: true);
            double literateDt = LyricDifficulty.Compute(map, 1.50, literate: true);

            double predicted = literateBase * (plainDt / plainBase);

            TestContext.WriteLine($"actual literate DT {literateDt:0.0000}; predicted {predicted:0.0000} " +
                                  $"({(predicted / literateDt - 1) * 100:0.000}% out)");

            Assert.That(literateDt, Is.Not.EqualTo(predicted).Within(1e-9));
        }

        #endregion
    }
}
