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
    public class LyricWpmCurveTest
    {
        /// <summary>A single-token line whose one unit spans the whole line.</summary>
        private static LyricLine singleWordLine(string text, double start, double end) => new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = end } },
        };

        /// <summary>
        /// <paramref name="count"/> one-character words, word m sung over
        /// [start + m*step, start + (m+1)*step].
        /// </summary>
        private static LyricLine evenWordsLine(int count, double start, double step)
        {
            var units = new List<TimedUnit>();

            for (int m = 0; m < count; m++)
                units.Add(new TimedUnit { Text = "a", StartTime = start + m * step, EndTime = start + (m + 1) * step });

            return new LyricLine
            {
                RawText = string.Join(' ', Enumerable.Repeat("a", count)),
                StartTime = start,
                EndTime = start + count * step,
                SingEndTime = start + count * step,
                Units = units,
            };
        }

        [Test]
        public void SingleThirtyCharacterWordUsesShortestEligibleTimeWindow()
        {
            // 30 cells at 100 ms intervals. A 1.5-second window starting at the first cell
            // contains 16 cells, meeting the SR character floor: 16 / 1.5 * 60 = 640 CPM.
            var curve = LyricWpmCurve.Compute(new[] { singleWordLine(new string('a', 30), 0, 3000) });

            Assert.IsFalse(curve.IsEmpty);
            Assert.AreEqual(640.0, curve.PeakCpm, 1e-9);
            Assert.AreEqual(128.0, curve.PeakWpm, 1e-9);
            Assert.AreEqual(0, curve.StartTime, 1e-9);
            Assert.AreEqual(2900, curve.EndTime, 1e-9);
            Assert.AreEqual(LyricWpmCurve.DEFAULT_CURVE_POINTS, curve.Curve.Count);
            Assert.AreEqual(128.0, curve.Curve[0], 1e-9);
            Assert.AreEqual(curve.PeakWpm, curve.Curve.Max(), 1e-9);
        }

        [Test]
        public void InterWordSpaceCellsCountInTimeWindow()
        {
            // 30 one-char words 100 ms apart contain 59 typing cells, including spaces.
            // A 1.5-second inclusive window beginning at 100 ms contains 32 cells.
            var curve = LyricWpmCurve.Compute(new[] { evenWordsLine(30, 0, 100) });

            Assert.AreEqual(1280.0, curve.PeakCpm, 1e-9);
            Assert.AreEqual(256.0, curve.PeakWpm, 1e-9);
            Assert.AreEqual(0, curve.StartTime, 1e-9);
            Assert.AreEqual(2900, curve.EndTime, 1e-9);
        }

        [Test]
        public void PeakWpmIsPeakCpmOverFive()
        {
            // The curve carries its own copy of the 5 so that it stays mirrorable into the server
            // with no dependencies (see the constant's doc), which means nothing but this stops the
            // copy drifting from the map averages' one. The third copy, TypingEngine's literal in
            // LiveWpm/LiveRollingWpm, is not reachable from here without standing up a whole run.
            Assert.AreEqual(LyricPaceStatistics.CHARS_PER_WORD, LyricWpmCurve.CHARS_PER_WORD);

            // Holds for every map, regardless of word length or eligible window duration.
            foreach (var curve in new[]
                     {
                         LyricWpmCurve.Compute(new[] { evenWordsLine(40, 0, 100) }),
                         LyricWpmCurve.Compute(new[] { singleWordLine(new string('a', 60), 0, 3000) }),
                         LyricWpmCurve.Compute(new[] { evenWordsLine(40, 0, 200), evenWordsLine(40, 8000, 60) }),
                     })
            {
                Assert.IsFalse(curve.IsEmpty);
                Assert.AreEqual(curve.PeakCpm / 5.0, curve.PeakWpm, 1e-9);
            }
        }

        [Test]
        public void PeakIsTheMaximumOfTheCurve()
        {
            // Every window starts at some cell time inside [StartTime, EndTime], so every window
            // lands in some bucket and the curve's maximum is exactly the peak WPM.
            var curve = LyricWpmCurve.Compute(new[]
            {
                evenWordsLine(40, 0, 200),
                evenWordsLine(40, 8000, 60),
            });

            Assert.IsFalse(curve.IsEmpty);
            Assert.AreEqual(curve.PeakWpm, curve.Curve.Max(), 1e-9);

            // The second line is typed more than three times as fast as the first, and buckets are
            // laid out on map time, so the peak has to sit in the back half of the curve.
            int peakBucket = curve.Curve.Select((v, i) => (v, i)).OrderByDescending(x => x.v).First().i;
            Assert.Greater(peakBucket, curve.Curve.Count / 2);
        }

        [Test]
        public void CurvePointCountIsRespected()
        {
            var curve = LyricWpmCurve.Compute(new[] { evenWordsLine(40, 0, 100) }, 8);

            Assert.AreEqual(8, curve.Curve.Count);
            Assert.AreEqual(curve.PeakWpm, curve.Curve.Max(), 1e-9);
        }

        [Test]
        public void MapShorterThanTheWindowIsEmpty()
        {
            // Eight one-char words = eight chars plus seven spaces, below the 16-cell floor.
            var curve = LyricWpmCurve.Compute(new[] { evenWordsLine(8, 0, 100) });

            Assert.IsTrue(curve.IsEmpty);
            Assert.AreEqual(0, curve.Curve.Count);
            Assert.AreEqual(0, curve.PeakWpm);
            Assert.AreEqual(0, curve.PeakCpm);
        }

        [Test]
        public void EmptyMapIsZero()
        {
            var curve = LyricWpmCurve.Compute(Array.Empty<LyricLine>());

            Assert.IsTrue(curve.IsEmpty);
            Assert.AreEqual(0, curve.PeakWpm);
            Assert.AreEqual(0, curve.PeakCpm);
            Assert.AreEqual(0, curve.StartTime);
            Assert.AreEqual(0, curve.EndTime);
        }

        [Test]
        public void ZeroSpanMapIsEmpty()
        {
            // Every cell of a zero-length unit targets the same instant: no span anywhere, so there
            // is nothing to divide by and nothing to report.
            var curve = LyricWpmCurve.Compute(new[] { singleWordLine(new string('a', 40), 1000, 1000) });

            Assert.IsTrue(curve.IsEmpty);
            Assert.AreEqual(0, curve.PeakWpm);
            Assert.AreEqual(0, curve.PeakCpm);
        }

        [Test]
        public void NonPositivePointCountIsEmpty()
        {
            var curve = LyricWpmCurve.Compute(new[] { evenWordsLine(40, 0, 100) }, 0);

            Assert.IsTrue(curve.IsEmpty);
        }

        [Test]
        public void PunctuationTakesNoCell()
        {
            // Punctuation is not IsCell, so "abc," and "abc" flatten to the same cells at the same
            // times: adding marks must not move the curve.
            var plain = LyricWpmCurve.Compute(new[] { evenWordsLine(40, 0, 100) });

            var units = new List<TimedUnit>();

            for (int m = 0; m < 40; m++)
                units.Add(new TimedUnit { Text = "a", StartTime = m * 100, EndTime = (m + 1) * 100 });

            var punctuated = LyricWpmCurve.Compute(new[]
            {
                new LyricLine
                {
                    RawText = string.Join(' ', Enumerable.Repeat("a,", 40)),
                    StartTime = 0,
                    EndTime = 4000,
                    SingEndTime = 4000,
                    Units = units,
                },
            });

            Assert.AreEqual(plain.PeakWpm, punctuated.PeakWpm, 1e-9);
            Assert.AreEqual(plain.PeakCpm, punctuated.PeakCpm, 1e-9);
            Assert.AreEqual(plain.Curve, punctuated.Curve);
        }
    }
}
