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

        // A pool of varied real-ish words, all five characters long, so a fixture's cell count is a
        // number this file can state rather than read back off the thing under test.
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

        /// <summary>
        /// The cells <see cref="LyricDifficulty"/> counts on a <see cref="buildMap"/> fixture: five
        /// per pool word plus the SPACE after every word but each line's last (backlog 269).
        /// </summary>
        private static int buildMapCells(int lineCount, int wordsPerLine) => lineCount * (wordsPerLine * 5 + wordsPerLine - 1);

        /// <summary>
        /// The shared ANCHOR, and the one fixture whose expectation is not this repo's own opinion:
        /// every digit below was produced by the prototype the model is a port of
        /// (docs/sr-feats-model.js in the parent superrepo, run over the same four words as
        /// <c>W</c> rows), and the two agree to the last bit rather than to a tolerance. The web
        /// port's LyricPaceTest pins the same number, so the three implementations are held
        /// together here.
        ///
        /// <para>Four words over 4.6 seconds, which is deliberately more than the 1.36 second
        /// smallest window: see <see cref="AMapShorterThanTheSmallestWindowRatesItsLengthAlone"/>
        /// for what happens under that.</para>
        /// </summary>
        private static LyricLine[] anchorMap() => new[]
        {
            line(0, 2600, ("hello", 0, 600), ("brave", 700, 1300)),
            line(2600, 5200, ("world", 2600, 3400), ("again", 3600, 4600)),
        };

        private const double anchor_stars = 2.0640903577664327;

        [Test]
        public void MatchesTheReferenceModel()
        {
            Assert.That(LyricDifficulty.Compute(anchorMap()), Is.EqualTo(anchor_stars));
        }

        [Test]
        public void EmptyMapIsZero()
        {
            Assert.AreEqual(0, LyricDifficulty.Compute(Array.Empty<LyricLine>()));
        }

        /// <summary>
        /// THE SHORT-MAP RULE, stated rather than discovered. Windows are scheduled in REAL seconds
        /// and are deliberately never clamped down to the map, because a window's ratio only means
        /// anything against S(t) at the window's own duration. A map whose whole sung timeline is
        /// shorter than the smallest scheduled window therefore has no window that fits, finds no
        /// feat, and rates its length term alone, which for anything that short is zero.
        ///
        /// <para>"cat cat" over 800 ms was this suite's anchor for six backlog items, which is why
        /// it is still here: it now rates exactly nothing, and stretching the same two words over
        /// three seconds is all it takes to make it rate something.</para>
        /// </summary>
        [Test]
        public void AMapShorterThanTheSmallestWindowRatesItsLengthAlone()
        {
            var tooShort = new[] { line(0, 800, ("cat", 0, 400), ("cat", 400, 800)) };
            var longEnough = new[] { line(0, 3000, ("cat", 0, 1500), ("cat", 1500, 3000)) };

            Assert.Multiple(() =>
            {
                Assert.That(LyricDifficulty.Compute(tooShort), Is.Zero, "0.8 s of singing fits no window at all");
                Assert.That(LyricDifficulty.Compute(longEnough), Is.GreaterThan(0), "3 s of the same two words does");
            });
        }

        /// <summary>
        /// AN INTER-WORD SPACE IS A CELL (backlog 269), and the space belongs to the LINE rather
        /// than to the pair of words: a word whose successor is on the same line carries the
        /// spacebar press after it, and a word ending its line does not, because what follows it is
        /// a line break. So the same two words at the same two times rate differently depending on
        /// whether the author put them on one line or two, and that is correct: on two lines the
        /// player really does type one keystroke fewer.
        /// </summary>
        [Test]
        public void AnInterWordSpaceIsACellAndBelongsToItsLine()
        {
            var oneLine = new[] { line(0, 2000, ("aaa", 0, 1000), ("bbb", 1000, 2000)) };
            var twoLines = new[]
            {
                line(0, 1000, ("aaa", 0, 1000)),
                line(1000, 2000, ("bbb", 1000, 2000)),
            };

            Assert.Multiple(() =>
            {
                Assert.That(LyricDifficulty.Compute(oneLine), Is.EqualTo(0.8078544371659612), "7 cells: aaa + space + bbb");
                Assert.That(LyricDifficulty.Compute(twoLines), Is.EqualTo(0.6609718122266955), "6 cells: no space over a line break");
            });
        }

        [Test]
        public void RateAdjustRaisesDifficulty()
        {
            var map = buildMap(lineCount: 12, wordsPerLine: 4, lineMs: 2400);

            double halfTime = LyricDifficulty.Compute(map, 0.75);
            double noMod = LyricDifficulty.Compute(map, 1.0);
            double doubleTime = LyricDifficulty.Compute(map, 1.5);

            // Faster clock -> the same cells inside a shorter window -> a higher ratio against the
            // same S(t); slower clock -> lower.
            Assert.Less(halfTime, noMod);
            Assert.Less(noMod, doubleTime);
        }

        /// <summary>
        /// The DT/HT TRIPLE, pinned exactly rather than by inequality, because these three numbers
        /// are what the server stores as <c>difficulty_rating</c>, <c>sr_dt</c> and <c>sr_ht</c> and
        /// what PerformancePoints prices a rate play from. The same triple is pinned in the web
        /// port's LyricPaceTest.
        /// </summary>
        [Test]
        public void TheRateTripleIsPinned()
        {
            var map = buildMap(lineCount: 12, wordsPerLine: 6, lineMs: 1800);

            Assert.Multiple(() =>
            {
                Assert.That(LyricDifficulty.Compute(map, 0.75), Is.EqualTo(5.627915297368787), "sr_ht");
                Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(7.215163474421059), "difficulty_rating");
                Assert.That(LyricDifficulty.Compute(map, 1.50), Is.EqualTo(8.810646556914051), "sr_dt");
            });
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
            // rating in the ranked pool reaches 10 except the hardest published difficulty, while
            // sr_dt passes it routinely).
            var map = buildMap(lineCount: 40, wordsPerLine: 6, lineMs: 2000);

            double noMod = LyricDifficulty.Compute(map);
            double doubleTime = LyricDifficulty.Compute(map, 1.50);

            // Both figures carry the backlog-152 length bonus, which is the SAME on both rates,
            // since the bonus reads the cell count and a clock change adds no cells.
            Assert.That(noMod, Is.EqualTo(7.744395928445708).Within(1e-9));
            Assert.That(doubleTime, Is.EqualTo(10.630203384913813).Within(1e-9), "under the old ceiling this read exactly 10.00");
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
            // ~4 words / 2.4 s line, 40 lines: about 230 CPM sustained for a minute and a half,
            // which is fast but not superhuman, so it has to land in the middle of the scale.
            var map = buildMap(lineCount: 40, wordsPerLine: 4, lineMs: 2400);
            double sr = LyricDifficulty.Compute(map);

            TestContext.WriteLine($"40-line map -> {sr:0.00} stars");
            Assert.That(sr, Is.InRange(2.0, 6.5));
        }

        /// <summary>
        /// A CUT VERSION CAN NEVER OUTRATE THE FULL VERSION IT WAS CUT FROM, which is the property
        /// the feats model was chosen for: every feat is scored against HUMAN CAPABILITY rather
        /// than against the map's own peak, so keeping a difficulty's hardest chorus and dropping
        /// everything after it can only remove feats from the sum, never rescale the ones left.
        ///
        /// <para>Three versions of one map, sharing an identical hardest chorus. The CUT is that
        /// chorus alone; one full version carries an easy tail after it and the other a tail as
        /// dense as the chorus itself (an Insane keeping backing-vocal lines a Hard drops). All
        /// three are pinned exactly, because the ORDER is the claim and a tolerance would let a
        /// future change reorder them inside it.</para>
        /// </summary>
        [Test]
        public void ACutVersionNeverOutratesTheFullVersionItWasCutFrom()
        {
            var peakChorus = buildMap(lineCount: 4, wordsPerLine: 4, lineMs: 1200);
            double peakEndMs = 4 * 1200;

            var cut = peakChorus;
            var easyTail = peakChorus.Concat(buildMap(lineCount: 10, wordsPerLine: 2, lineMs: 2400, startAt: peakEndMs)).ToArray();
            var hardTail = peakChorus.Concat(buildMap(lineCount: 10, wordsPerLine: 4, lineMs: 1200, startAt: peakEndMs)).ToArray();

            double cutSr = LyricDifficulty.Compute(cut);
            double easySr = LyricDifficulty.Compute(easyTail);
            double hardSr = LyricDifficulty.Compute(hardTail);

            TestContext.WriteLine($"cut {cutSr:0.000}; full with an easy tail {easySr:0.000}; full with a dense tail {hardSr:0.000}");

            Assert.Multiple(() =>
            {
                Assert.That(cutSr, Is.EqualTo(5.053073444109227));
                Assert.That(easySr, Is.EqualTo(5.606153773636355));
                Assert.That(hardSr, Is.EqualTo(6.938059060030686));

                // The two claims the numbers above encode, restated so a failure says which broke.
                Assert.That(easySr, Is.GreaterThan(cutSr), "the cut cannot outrate what it was cut from");
                Assert.That(hardSr - easySr, Is.GreaterThan(0.15), "and the extra feats a dense tail adds have to show");
            });
        }

        #region The length bonus (backlog 152)

        /// <summary>
        /// The additive per-decade length bonus, stated as its own quantity. Every word the fixture
        /// builder emits is a 5-character pool word and every line's words but its last carry a
        /// space, so the cell count is exactly <see cref="buildMapCells"/> and the bonus is a number
        /// this test can write out rather than read back off the thing under test. The other half of
        /// each expectation is the FEATS rating, measured by setting <c>length_stars</c> to 0.
        /// </summary>
        [TestCase(40, 8, 1200, 15.291281961350359)] // 1880 cells: 40 lines of 8 words plus 7 spaces
        [TestCase(40, 4, 2400, 4.023130071607224)] // 920 cells
        public void TheLengthBonusIsAddedFlatOnTopOfTheFeatsRating(int lineCount, int wordsPerLine, double lineMs, double featsOnly)
        {
            var map = buildMap(lineCount, wordsPerLine, lineMs);
            int cells = buildMapCells(lineCount, wordsPerLine);

            double bonus = 0.12 * Math.Log10(cells / 100.0);

            Assert.That(bonus, Is.GreaterThan(0), "the fixture has to be over the pivot for this to test anything");
            Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(featsOnly + bonus).Within(1e-5));
        }

        /// <summary>
        /// AND IT IS EXACTLY ZERO BELOW 100 CELLS, which is what the <c>max(0, .)</c> clamp is for
        /// and is not a rounding claim: the raw term is NEGATIVE under the pivot, so without the
        /// clamp every short fixture would LOSE stars. The two cases are a fixture under the pivot
        /// and one exactly on it, where <c>log10(1)</c> is zero outright. The at-pivot fixture is
        /// one word per line, so it carries no inter-word spaces and its 20 five-letter words are
        /// exactly 100 cells.
        /// </summary>
        [TestCase(2, 6, 1800, 70, 4.846615926359888)] // under the pivot: the raw term is negative
        [TestCase(20, 1, 600, 100, 2.4622788302413965)] // AT the pivot: log10(1) is exactly 0
        public void TheLengthBonusIsExactlyNothingAtOrBelowTheHundredCellPivot(int lineCount, int wordsPerLine, double lineMs, int cells, double featsOnly)
        {
            var map = buildMap(lineCount, wordsPerLine, lineMs);

            double raw = 0.12 * Math.Log10(cells / 100.0);
            double rated = LyricDifficulty.Compute(map);

            TestContext.WriteLine($"{cells} cells -> {rated:0.000000} (raw term {raw:0.000000}, clamped away)");

            Assert.Multiple(() =>
            {
                Assert.That(buildMapCells(lineCount, wordsPerLine), Is.EqualTo(cells), "the stated cell count is the fixture's");
                Assert.That(raw, Is.LessThanOrEqualTo(0), "the clamp cannot be tested where the raw term is positive");
                Assert.That(rated, Is.EqualTo(featsOnly).Within(1e-5));
            });
        }

        #endregion

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
                line(2200, 5000, ("Typing", 2200, 3000), ("is", 3000, 3400), ("a", 3400, 3700), ("rhythm;", 3700, 4300), ("not", 4300, 4700), ("a", 4700, 4850), ("race.", 4850, 5000)),
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
        /// that is written down so the next reader does not have to rediscover it. Under the feats
        /// model the reason is that the two changes act on DIFFERENT AXES: the rate compresses the
        /// timeline, which moves which windows fit and which bins a greedy feat consumes, while
        /// Literate adds cells to the words already there. Neither is a scalar on the other.
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

        #region Freestyle slots, priced at a quarter (backlog 211)

        private const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

        /// <summary>
        /// A map of UNIFORM words: every token gets the same span and the same step from the last,
        /// laid end to end and cut into lines of <paramref name="wordsPerLine"/>. Uniform is what
        /// makes the fixtures below exact: two maps built this way differ in NOTHING but what their
        /// tokens weigh, since the timeline they occupy is identical.
        /// </summary>
        private static LyricLine[] uniformMap(string[] tokens, int wordsPerLine, double stepMs, double spanMs)
        {
            var lines = new List<LyricLine>();
            double t = 0;

            for (int i = 0; i < tokens.Length; i += wordsPerLine)
            {
                int count = Math.Min(wordsPerLine, tokens.Length - i);
                var units = new (string, double, double)[count];

                for (int w = 0; w < count; w++)
                {
                    double ws = t + w * stepMs;
                    units[w] = (tokens[i + w], ws, ws + spanMs);
                }

                lines.Add(line(t, t + count * stepMs, units));
                t += count * stepMs;
            }

            return lines.ToArray();
        }

        /// <summary>
        /// <paramref name="count"/> tokens built from <paramref name="shape"/>, which is handed the
        /// word's index and takes its letters from <see cref="alphabet"/>.
        /// </summary>
        private static string[] tokens(int count, Func<int, string> shape) => Enumerable.Range(0, count).Select(shape).ToArray();

        private static string letters(int i, int n)
        {
            var sb = new System.Text.StringBuilder(n);

            for (int k = 0; k < n; k++)
                sb.Append(alphabet[(i + k) % alphabet.Length]);

            return sb.ToString();
        }

        private const char marker = Typeability.FREESTYLE_MARKER;

        /// <summary>
        /// THE BROAD REGRESSION PIN. Every value here is an exact double rather than a tolerance,
        /// so any change to the model at all has to come through this test and be argued for. It
        /// also carries the backlog 211 claim it was written for: none of these fixtures holds a
        /// single freestyle marker, and the freestyle weight is a cell COUNT, so all of them are
        /// bit-identical whatever that weight is set to.
        /// </summary>
        [Test]
        public void AMapWithNoFreestyleSlotsRatesBitIdenticallyToBeforeTheyWerePriced()
        {
            var anchor = anchorMap();
            var big = buildMap(lineCount: 40, wordsPerLine: 8, lineMs: 1200);
            var realistic = buildMap(lineCount: 40, wordsPerLine: 4, lineMs: 2400);
            var mid = buildMap(lineCount: 12, wordsPerLine: 6, lineMs: 1800);
            var punctuated = new[]
            {
                line(0, 2000, ("Hello,", 0, 700), ("bad-cat!", 700, 1400), ("sat...", 1400, 2000)),
                line(2200, 5000, ("Typing", 2200, 3000), ("is", 3000, 3400), ("a", 3400, 3700), ("rhythm;", 3700, 4300), ("not", 4300, 4700), ("a", 4700, 4850), ("race.", 4850, 5000)),
            };

            Assert.Multiple(() =>
            {
                Assert.That(LyricDifficulty.Compute(anchor), Is.EqualTo(anchor_stars), "the shared reference anchor");
                Assert.That(LyricDifficulty.Compute(big), Is.EqualTo(15.444180903262001));
                Assert.That(LyricDifficulty.Compute(big, 1.50), Is.EqualTo(22.44830032037408));
                Assert.That(LyricDifficulty.Compute(realistic), Is.EqualTo(4.138784610888691));
                Assert.That(LyricDifficulty.Compute(mid, 0.75), Is.EqualTo(5.627915297368787));
                Assert.That(LyricDifficulty.Compute(mid), Is.EqualTo(7.215163474421059));
                Assert.That(LyricDifficulty.Compute(mid, 1.50), Is.EqualTo(8.810646556914051));
                Assert.That(LyricDifficulty.Compute(mid, 1, literate: true), Is.EqualTo(7.215163474421059));
                Assert.That(LyricDifficulty.Compute(punctuated), Is.EqualTo(3.370730938568178));
                Assert.That(LyricDifficulty.Compute(punctuated, 1, literate: true), Is.EqualTo(3.8136239497474924));
                Assert.That(LyricDifficulty.Compute(punctuated, 1.50, literate: true), Is.EqualTo(3.9224730590455015));
            });
        }

        /// <summary>
        /// THE PRICE, stated as an exact identity rather than as an inequality: FOUR freestyle slots
        /// weigh exactly ONE ordinary cell, so a map of "a&amp;&amp;&amp;&amp;," words must rate
        /// BIT-identically to the same map written "ab," (one fixed key plus four quarters against
        /// two fixed keys, or, under Literate, two cells plus four quarters against three).
        ///
        /// <para>Everything else about the pair is held equal BY CONSTRUCTION, which is what lets
        /// this be an equality: the two maps occupy the same timeline word for word, they are cut
        /// into lines at the same places (so they carry the same inter-word spaces), and the 60
        /// words put both over the 100-cell length pivot (170 priced cells plain, 230 under
        /// Literate, spaces included) so the length accumulator has to count the quarter as well or
        /// the bonuses differ.</para>
        ///
        /// <para>Two spacings, because the model reads a word's SPAN as well as its onset: LOOSE
        /// (400 ms step, 350 ms span) leaves a gap between words, TIGHT (80 ms step, 60 ms span)
        /// puts several words inside a single 50 ms timeline bin, which is where the uniform spread
        /// and the partial-bin proration actually do something.</para>
        /// </summary>
        [TestCase(400, 350, false, TestName = "AFreestyleSlotIsExactlyAQuarterCell(loose, plain)")]
        [TestCase(400, 350, true, TestName = "AFreestyleSlotIsExactlyAQuarterCell(loose, literate)")]
        [TestCase(80, 60, false, TestName = "AFreestyleSlotIsExactlyAQuarterCell(tight bins, plain)")]
        [TestCase(80, 60, true, TestName = "AFreestyleSlotIsExactlyAQuarterCell(tight bins, literate)")]
        public void FourFreestyleSlotsWeighExactlyOneCell(double stepMs, double spanMs, bool literate)
        {
            // "a&&&&," : one fixed key (two under Literate, the mark) plus four quarter-cells.
            var free = uniformMap(tokens(60, i => letters(i, 1) + new string(marker, 4) + ","), wordsPerLine: 6, stepMs, spanMs);
            // "ab," : the same weight written entirely in fixed keys.
            var full = uniformMap(tokens(60, i => letters(i, 2) + ","), wordsPerLine: 6, stepMs, spanMs);

            double freeSr = LyricDifficulty.Compute(free, 1, literate);
            double fullSr = LyricDifficulty.Compute(full, 1, literate);

            TestContext.WriteLine($"step {stepMs} literate {literate}: freestyle {freeSr:0.000000}, all-fixed twin {fullSr:0.000000}");

            Assert.That(freeSr, Is.EqualTo(fullSr));
        }

        /// <summary>
        /// A quarter is BETWEEN the two prices it could have had, which is the whole decision: the
        /// slots used to be worth nothing (a freestyle section was an accuracy and combo farm the
        /// rating could not see) and they are not worth a whole cell either, since there is no letter
        /// to find. The "excluded" map here is not an approximation of the old behaviour, it IS the
        /// old number: the pre-211 code stripped every marker before measuring anything, so a map
        /// with the markers deleted computed exactly what the marker map computed.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void FreestyleRatesAboveTheOldFreePriceAndBelowAFullCell(bool literate)
        {
            var excluded = uniformMap(tokens(60, i => letters(i, 1) + ","), wordsPerLine: 6, stepMs: 400, spanMs: 350);
            var freestyle = uniformMap(tokens(60, i => letters(i, 1) + new string(marker, 4) + ","), wordsPerLine: 6, stepMs: 400, spanMs: 350);
            var fixedKeys = uniformMap(tokens(60, i => letters(i, 5) + ","), wordsPerLine: 6, stepMs: 400, spanMs: 350);

            double excludedSr = LyricDifficulty.Compute(excluded, 1, literate);
            double freestyleSr = LyricDifficulty.Compute(freestyle, 1, literate);
            double fixedSr = LyricDifficulty.Compute(fixedKeys, 1, literate);

            TestContext.WriteLine($"literate {literate}: excluded (pre-211) {excludedSr:0.000}, quartered {freestyleSr:0.000}, all fixed keys {fixedSr:0.000}");

            Assert.Multiple(() =>
            {
                Assert.That(freestyleSr, Is.GreaterThan(excludedSr), "pricing the slots has to raise the rating");
                Assert.That(freestyleSr, Is.LessThan(fixedSr), "a slot is not a letter");
            });
        }

        /// <summary>
        /// The length bonus reads the SAME quarter, stated on its own because it is the one place
        /// the weight is a map-wide accumulator rather than a per-word cost. The fixture's 60 words
        /// carry one fixed key and four slots each, and its 10 lines carry 5 inter-word spaces
        /// apiece, so its priced cell count is <c>60 * (1 + 4/4) + 10 * 5 = 170</c>, over the
        /// 100-cell pivot; count a slot as a whole cell and it would be 350, count it as nothing and
        /// it would be 110.
        /// </summary>
        [Test]
        public void TheLengthBonusCountsAFreestyleSlotAsAQuarterCell()
        {
            var free = uniformMap(tokens(60, i => letters(i, 1) + new string(marker, 4) + ","), wordsPerLine: 6, stepMs: 400, spanMs: 350);

            // The FEATS rating alone, i.e. what this fixture rates with length_stars set to 0
            // (measured that way, exactly as the backlog-152 cases above were).
            const double feats_only = 2.659249929780881;
            double bonus = 0.12 * Math.Log10(170 / 100.0);

            Assert.That(bonus, Is.GreaterThan(0), "the fixture has to clear the pivot for this to test anything");
            Assert.That(LyricDifficulty.Compute(free), Is.EqualTo(feats_only + bonus).Within(1e-5));
        }

        /// <summary>
        /// WHERE the markers sit inside a word cannot matter, which is the observable consequence of
        /// keeping them out of the stream TEXT and carrying them as a count: the word's cells are
        /// spread uniformly across its sung span whatever order they were authored in.
        /// </summary>
        [Test]
        public void WhereTheMarkersSitInsideAWordDoesNotMove()
        {
            var trailing = uniformMap(tokens(60, i => letters(i, 2) + new string(marker, 2)), wordsPerLine: 6, stepMs: 400, spanMs: 350);
            var interleaved = uniformMap(tokens(60, i => letters(i, 1) + marker + letters(i + 1, 1) + marker), wordsPerLine: 6, stepMs: 400, spanMs: 350);
            var leading = uniformMap(tokens(60, i => new string(marker, 2) + letters(i, 2)), wordsPerLine: 6, stepMs: 400, spanMs: 350);

            Assert.Multiple(() =>
            {
                Assert.That(LyricDifficulty.Compute(interleaved), Is.EqualTo(LyricDifficulty.Compute(trailing)));
                Assert.That(LyricDifficulty.Compute(leading), Is.EqualTo(LyricDifficulty.Compute(trailing)));
            });
        }

        /// <summary>
        /// A word of NOTHING BUT slots is a word. It used to be dropped from the map outright (its
        /// stream was empty, so it never became a word at all), which is how a whole mashable
        /// freestyle section could rate exactly 0.00: this fixture is that section, and it now rates
        /// exactly what the same map of one-key words rates, four slots to the cell.
        /// </summary>
        [Test]
        public void AWordOfNothingButFreestyleSlotsIsStillAWord()
        {
            var mashed = uniformMap(tokens(60, _ => new string(marker, 4)), wordsPerLine: 6, stepMs: 400, spanMs: 350);
            var oneKeyWords = uniformMap(tokens(60, _ => "a"), wordsPerLine: 6, stepMs: 400, spanMs: 350);

            double mashedSr = LyricDifficulty.Compute(mashed);

            TestContext.WriteLine($"all-freestyle map -> {mashedSr:0.000} (it rated exactly 0.00 before backlog 211)");

            Assert.Multiple(() =>
            {
                Assert.That(mashedSr, Is.GreaterThan(0), "before 211 this map had no words in it at all");
                Assert.That(mashedSr, Is.EqualTo(LyricDifficulty.Compute(oneKeyWords)));
            });
        }

        #endregion
    }
}
