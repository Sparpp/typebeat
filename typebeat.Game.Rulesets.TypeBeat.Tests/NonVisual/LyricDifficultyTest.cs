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
        /// <summary>
        /// The SHIPPED rating (the chunked axis, see <see cref="LyricDifficulty.Live"/>), exactly as
        /// every caller reads it, shipped typability included. The exact pins below are the same
        /// numbers for the current client formula, including the negative-z whole-map bonus. The
        /// mirrored web formula must use these same pins when its parity fixtures are updated.
        ///
        /// <para>These fixtures used to read the ENVELOPE model with typability off, the sandbox
        /// reading that model was ported from. That model is gone (it last fed only Target WPM), so the
        /// fixtures whose claim was a property of the envelope itself went with it, and the ones whose
        /// claim is about a map went to the axis that ships.</para>
        /// </summary>
        private static double Shipped(IEnumerable<LyricLine> lines, double rate = 1, bool literate = false)
            => LyricDifficulty.Compute(lines, rate, literate);

        /// <summary>
        /// The LIVE axis (the chunked one) with typability switched off, which is what a fixture about
        /// the CELL WEIGHT has to read. Typability is a property of a map's SPELLING, and the game now
        /// scores a lyric line's spelling in-client, so two maps that differ only in how a word is
        /// written rate differently on purpose; the freestyle weight is a count of cells, which no
        /// spelling can move, so these fixtures take the rating with every line read as unscored.
        /// </summary>
        private static double WithoutTypability(IEnumerable<LyricLine> lines, bool literate = false)
            => LyricDifficulty.RateChunked(lines.ToList(), 1, literate, LyricDifficulty.NoScores, ChunkedEndurance.Live).Report.Stars;

        /// <summary>
        /// The map's weighted CELL MASS as the model builds it: every typeable character, every
        /// inter-word space, and every freestyle slot at its quarter. This is the reading a fixture
        /// about the SLOT WEIGHT has to pin - the model's own cell currency - because on the live
        /// (chunked) axis two maps whose cells weigh the same can still rate differently: the axis
        /// prices how many of those cells are TYPEABLE and how finely they are subdivided, and a
        /// freestyle slot is a keypress with no key to find.
        /// </summary>
        private static double CellMass(IEnumerable<LyricLine> lines)
            => LyricDifficulty.ComputeDetail(lines.ToList()).Cells;

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

        [Test]
        public void EmptyMapIsZero()
        {
            Assert.AreEqual(0, Shipped(Array.Empty<LyricLine>()));
        }

        /// <summary>
        /// AN INTER-WORD SPACE IS A CELL (backlog 269), and the space belongs to the LINE rather
        /// than to the pair of words: a word whose successor is on the same line carries the
        /// spacebar press after it, and a word ending its line does not, because what follows it is
        /// a line break. So the same two words at the same two times rate differently depending on
        /// whether the author put them on one line or two, and that is correct: on two lines the
        /// player really does type one keystroke fewer.
        ///
        /// <para>FOUR WORDS RATHER THAN TWO, so the two layouts are clear to compare: one line of four
        /// carries 3 spaces over the same timeline that four lines of one carry none, which is the
        /// whole of the difference between the two ratings below.</para>
        /// </summary>
        [Test]
        public void AnInterWordSpaceIsACellAndBelongsToItsLine()
        {
            var oneLine = new[] { line(0, 4000, ("flame", 0, 1000), ("river", 1000, 2000), ("cider", 2000, 3000), ("amber", 3000, 4000)) };
            var twoLines = new[]
            {
                line(0, 1000, ("flame", 0, 1000)),
                line(1000, 2000, ("river", 1000, 2000)),
                line(2000, 3000, ("cider", 2000, 3000)),
                line(3000, 4000, ("amber", 3000, 4000)),
            };

            Assert.Multiple(() =>
            {
                Assert.That(Shipped(oneLine), Is.EqualTo(2.1078517699642552), "23 cells: four words + the 3 spaces between them");
                Assert.That(Shipped(twoLines), Is.EqualTo(1.7962369444673221), "20 cells: no space over a line break");
            });
        }

        [Test]
        public void RateAdjustRaisesDifficulty()
        {
            var map = buildMap(lineCount: 12, wordsPerLine: 4, lineMs: 2400);

            double halfTime = Shipped(map, 0.75);
            double noMod = Shipped(map, 1.0);
            double doubleTime = Shipped(map, 1.5);

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
                Assert.That(Shipped(map, 0.75), Is.EqualTo(6.8512192811076789), "sr_ht");
                Assert.That(Shipped(map), Is.EqualTo(8.6903668295584655), "difficulty_rating");
                Assert.That(Shipped(map, 1.50), Is.EqualTo(12.738265542265243), "sr_dt");
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

            double noMod = Shipped(map);
            double doubleTime = Shipped(map, 1.50);

            Assert.That(noMod, Is.EqualTo(9.7738950146424912).Within(1e-9));
            Assert.That(doubleTime, Is.EqualTo(14.110578133445392).Within(1e-9), "under the old ceiling this read exactly 10.00");
        }

        [Test]
        public void AddingContentNeverLowersRating()
        {
            var baseMap = buildMap(lineCount: 8, wordsPerLine: 4, lineMs: 2400);
            // Same-pace continuation appended contiguously after the base map.
            var extended = baseMap.Concat(buildMap(lineCount: 8, wordsPerLine: 4, lineMs: 2400, startAt: 8 * 2400)).ToArray();

            double baseSr = Shipped(baseMap);
            double extendedSr = Shipped(extended);

            // The defining property: the same pace for twice as long is never easier.
            Assert.GreaterOrEqual(extendedSr, baseSr);
            // Length counts, but only through the characters it adds and saturating; doubling the
            // map must not double the rating.
            Assert.Less(extendedSr, baseSr * 2);
        }

        [Test]
        public void SpikeBeatsFiller()
        {
            // A short dense burst (fast words) rates above a long even stretch of the same words.
            var spike = buildMap(lineCount: 6, wordsPerLine: 6, lineMs: 1200); // ~fast
            var filler = buildMap(lineCount: 24, wordsPerLine: 3, lineMs: 3000); // long, easy

            Assert.Greater(Shipped(spike), Shipped(filler));
        }

        [Test]
        public void RealisticMapLandsInASaneBand()
        {
            // ~4 words / 2.4 s line, 40 lines: about 230 CPM sustained for a minute and a half,
            // which is fast but not superhuman, so it has to land in the middle of the scale.
            var map = buildMap(lineCount: 40, wordsPerLine: 4, lineMs: 2400);
            double sr = Shipped(map);

            TestContext.WriteLine($"40-line map -> {sr:0.00} stars");
            Assert.That(sr, Is.InRange(2.0, 6.5));
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
                line(2200, 5000, ("Typing", 2200, 3000), ("is", 3000, 3400), ("a", 3400, 3700), ("rhythm;", 3700, 4300), ("not", 4300, 4700), ("a", 4700, 4850), ("race.", 4850, 5000)),
            };

            double plain = Shipped(map);
            double literate = Shipped(map, 1, literate: true);

            TestContext.WriteLine($"plain -> {plain:0.0000}; literate -> {literate:0.0000}");

            Assert.That(literate, Is.Not.EqualTo(plain).Within(1e-9));
        }

        /// <summary>
        /// TYPABILITY IS READ ON THE TEXT THE LINE IS TYPED AS, so the mod changes which score
        /// applies and not only how many cells there are. The index charges capitals for Shift and
        /// marks as keystrokes of their own, so the authored line and its default stream are
        /// different sentences to it with different z; scoring the authored text while charging
        /// the cells of the stripped one, which is what this model used to do, prices a line
        /// nobody types. The fixture is a line of the bundled catalogue and both of its forms are
        /// in the shipped table.
        /// </summary>
        [Test]
        public void TypabilityReadsTheTypedStream()
        {
            const string authored = "Now I'm Mister Charisma, fucking Pablo Escobar";
            string played = Typeability.ToDefaultStream(authored);

            Assert.That(played, Is.EqualTo("now im mister charisma fucking pablo escobar"));
            Assert.That(TypabilityIndex.TryScore(authored, out TypabilityIndex.Score authoredScore), Is.True, "the authored form has to be in the table");
            Assert.That(TypabilityIndex.TryScore(played, out TypabilityIndex.Score playedScore), Is.True, "and so does the played form");
            Assert.That(Math.Abs(authoredScore.Z - playedScore.Z), Is.GreaterThan(.2), "the fixture has to score the two forms apart");

            var map = new[]
            {
                line(0, 4100, ("Now", 0, 460), ("I'm", 460, 900), ("Mister", 900, 1500), ("Charisma,", 1500, 2100),
                    ("fucking", 2100, 2700), ("Pablo", 2700, 3300), ("Escobar", 3300, 4100)),
            };

            // The one fixture in this file that reads typability back, through the shipped score source.
            var plain = LyricDifficulty.ComputeDetail(map, 1, false);
            var literate = LyricDifficulty.ComputeDetail(map, 1, true);

            TestContext.WriteLine($"plain z {plain.TypabilityMeanZ:0.0000} (played {playedScore.Z:0.0000}); " +
                                  $"literate z {literate.TypabilityMeanZ:0.0000} (authored {authoredScore.Z:0.0000})");

            Assert.That(plain.TypabilityMeanZ, Is.EqualTo(playedScore.Z).Within(1e-9),
                "without the mod the line is scored as the stripped stream");
            Assert.That(literate.TypabilityMeanZ, Is.EqualTo(authoredScore.Z).Within(1e-9),
                "and with it as the authored text");
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
                    Assert.That(Shipped(map, rate, literate: true),
                        Is.EqualTo(Shipped(map, rate)), $"rate {rate}");
                }
            });
        }

        /// <summary>
        /// Literate does not compose with the rate by any constant, which is why the server stores
        /// the CROSS PRODUCT of the two (029_literate_stars.sql) instead of deriving three of the
        /// six ratings from the other three. The obvious saving is
        /// <c>sr_literate_dt = sr_literate · (sr_dt/sr_base)</c>; it is wrong, and this is where
        /// that is written down so the next reader does not have to rediscover it. The two changes
        /// act on DIFFERENT THINGS: the rate compresses the timeline, which moves every window's
        /// length against the capability curve, while Literate adds cells (and Shift presses) to the
        /// words already there, which moves each window's density, so neither is a scalar on the
        /// other.
        /// </summary>
        [Test]
        public void TheLiterateRatingIsNotTheRateRatingTimesAConstant()
        {
            var map = new[]
            {
                line(0, 2000, ("Hello,", 0, 700), ("bad-cat!", 700, 1400), ("sat...", 1400, 2000)),
                line(2200, 5000, ("Typing", 2200, 3000), ("is", 3000, 3400), ("a", 3400, 3700), ("rhythm;", 3700, 4300), ("not", 4300, 4700), ("a", 4700, 4850), ("race.", 4850, 5000)),
            };

            double plainBase = Shipped(map);
            double plainDt = Shipped(map, 1.50);
            double literateBase = Shipped(map, 1, literate: true);
            double literateDt = Shipped(map, 1.50, literate: true);

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
            var big = buildMap(lineCount: 40, wordsPerLine: 8, lineMs: 1200);
            var realistic = buildMap(lineCount: 40, wordsPerLine: 4, lineMs: 2400);
            var mid = buildMap(lineCount: 12, wordsPerLine: 6, lineMs: 1800);

            Assert.Multiple(() =>
            {
                Assert.That(Shipped(big), Is.EqualTo(20.989384416358078));
                Assert.That(Shipped(big, 1.50), Is.EqualTo(30.813485280756332));
                Assert.That(Shipped(realistic), Is.EqualTo(5.1418377347840236));
                Assert.That(Shipped(mid, 0.75), Is.EqualTo(6.8512192811076789));
                Assert.That(Shipped(mid), Is.EqualTo(8.6903668295584655));
                Assert.That(Shipped(mid, 1.50), Is.EqualTo(12.738265542265243));
                // No mark and no capital in the pool, so the Literate stream is the same stream.
                Assert.That(Shipped(mid, 1, literate: true), Is.EqualTo(8.6903668295584655));
            });
        }

        /// <summary>
        /// THE PRICE, stated as an exact identity rather than as an inequality: FOUR freestyle slots
        /// weigh exactly ONE ordinary cell, so a map of "a&amp;&amp;&amp;&amp;," words must rate
        /// BIT-identically to the same map written "ab," (one fixed key plus four quarters against
        /// two fixed keys, or, under Literate, two cells plus four quarters against three).
        ///
        /// <para>Everything else about the pair is held equal BY CONSTRUCTION, which is what lets
        /// this be an equality: the two maps occupy the same timeline word for word and they are cut
        /// into lines at the same places, so they carry the same inter-word spaces and the same 170
        /// priced cells plain (230 under Literate). Price a slot at anything but a quarter and the
        /// two timelines hold different densities.</para>
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

            double freeCells = CellMass(free);
            double fullCells = CellMass(full);

            TestContext.WriteLine($"step {stepMs} literate {literate}: freestyle {freeCells:0.000000} cells, all-fixed twin {fullCells:0.000000}");

            Assert.That(freeCells, Is.EqualTo(fullCells), "four quarter-cells have to weigh exactly one whole one");
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

            double excludedSr = Shipped(excluded, 1, literate);
            double freestyleSr = Shipped(freestyle, 1, literate);
            double fixedSr = Shipped(fixedKeys, 1, literate);

            TestContext.WriteLine($"literate {literate}: excluded (pre-211) {excludedSr:0.000}, quartered {freestyleSr:0.000}, all fixed keys {fixedSr:0.000}");

            Assert.Multiple(() =>
            {
                Assert.That(freestyleSr, Is.GreaterThan(excludedSr), "pricing the slots has to raise the rating");
                Assert.That(freestyleSr, Is.LessThan(fixedSr), "a slot is not a letter");
            });
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
                Assert.That(WithoutTypability(interleaved), Is.EqualTo(WithoutTypability(trailing)));
                Assert.That(WithoutTypability(leading), Is.EqualTo(WithoutTypability(trailing)));
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

            double mashedCells = CellMass(mashed);

            TestContext.WriteLine($"all-freestyle map -> {mashedCells:0.000} cells (it had none at all before backlog 211)");

            Assert.Multiple(() =>
            {
                Assert.That(mashedCells, Is.GreaterThan(0), "before 211 this map had no words in it at all");
                Assert.That(mashedCells, Is.EqualTo(CellMass(oneKeyWords)), "a mashable word weighs what the same run of fixed keys weighs");
            });
        }

        #endregion
    }
}
