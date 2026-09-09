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
        public void SingleThirtyCharacterWordIsHandComputable()
        {
            // One 30-char word over [0, 3000]: k = 30, so typeable char j targets
            // 0 + j*3000/30 = 100j. That is exactly 30 cells at 0, 100, ... 2900, with no
            // inter-word space cell (there is only one token), so the map holds one window.
            //
            //   spanMs = 2900 - 0 = 2900
            //   cpm    = 29 / (2900/60000) = 29 * 60000 / 2900   = 600
            //   wpm    = 600 / 5                                 = 120
            //
            // The 30-character word does NOT make this window worth one word: under the typing-test
            // convention a word is 5 keystrokes whatever the text says, so the map's own word length
            // is reported separately (LyricPaceStatistics.AverageCharsPerWord) instead of being
            // baked into the WPM. The real-word convention this replaced said 29/30 of a word over
            // the same span, i.e. 20 WPM, a number the HUD could never have shown.
            var curve = LyricWpmCurve.Compute(new[] { singleWordLine(new string('a', 30), 0, 3000) });

            Assert.IsFalse(curve.IsEmpty);
            Assert.AreEqual(600.0, curve.PeakCpm, 1e-9);
            Assert.AreEqual(120.0, curve.PeakWpm, 1e-9);
            Assert.AreEqual(0, curve.StartTime, 1e-9);
            Assert.AreEqual(2900, curve.EndTime, 1e-9);

            // The single window starts at the map's first cell, so it lands in bucket 0.
            Assert.AreEqual(LyricWpmCurve.DEFAULT_CURVE_POINTS, curve.Curve.Count);
            Assert.AreEqual(120.0, curve.Curve[0], 1e-9);
            Assert.AreEqual(0.0, curve.Curve.Skip(1).Max(), 1e-9);
        }

        [Test]
        public void InterWordSpaceCellsHalveTheWindowSpan()
        {
            // 30 one-char words 100 ms apart. The flattening interleaves an inter-word space cell at
            // each unit end, so the map holds 30 chars + 29 spaces = 59 cells whose targets run
            // 0, 100, 100, 200, 200, ... 2800, 2900, 2900: cell i is char m at 100m for even i = 2m,
            // and the space after word m at 100(m+1) for odd i = 2m+1.
            //
            //   even-start window: span 1500 ms, cpm 29*60000/1500 = 1160,       wpm 232
            //   odd-start  window: span 1400 ms, cpm 29*60000/1400 = 1242.857..., wpm 248.571...
            //
            // The odd-start window wins BOTH now. Under the old real-word convention the even and
            // odd windows tied on WPM (15 whole words vs 14 words and 2 halves) while the odd one
            // won on CPM, so the two peaks sat in different windows and had to be maximised
            // independently. Every cell being worth 1/5 of a word removes that possibility: the
            // peaks are proportional, and the assertion below is the whole point of this fixture.
            var curve = LyricWpmCurve.Compute(new[] { evenWordsLine(30, 0, 100) });

            Assert.AreEqual(1740000.0 / 1400.0, curve.PeakCpm, 1e-9);
            Assert.AreEqual(1740000.0 / 1400.0 / 5.0, curve.PeakWpm, 1e-9);
            Assert.AreEqual(248.571428571428, curve.PeakWpm, 1e-9);
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

            // Holds for every map, whatever its word lengths, which is what makes the in-game HUD's
            // rolling counter and this map figure the same quantity: both average over 30 presses,
            // both divide characters by 5 (Gameplay/TypingEngine.cs, LiveRollingWpm).
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
            // 10 one-char words = 10 chars + 9 spaces = 19 cells, under the 30-cell window.
            var curve = LyricWpmCurve.Compute(new[] { evenWordsLine(10, 0, 100) });

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
