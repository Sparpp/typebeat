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
        /// The shared ANCHOR, and the one fixture whose expectation is not this repo's own opinion:
        /// every digit below was produced by the prototype the model is a port of
        /// (docs/sr-envelope-model.js in the parent superrepo, run over the same four words as
        /// <c>W</c> rows), and the two agree to the last bit rather than to a tolerance. The web
        /// port's LyricPaceTest pins the same number, so the three implementations are held
        /// together here. The digit string moved once, when the anchor went 10.6 to 12.0: the model
        /// is LINEAR in the anchor, so this is the prototype's own value times 12 / 10.6 and the
        /// prototype's <c>anchor</c> has to be moved with it for the two to keep agreeing bit for
        /// bit.
        ///
        /// <para>Four words over 4.6 seconds, which is deliberately more than the 1.36 second
        /// smallest window: see <see cref="AMapShorterThanTheSmallestWindowRatesExactlyZero"/>
        /// for what happens under that.</para>
        /// </summary>
        private static LyricLine[] anchorMap() => new[]
        {
            line(0, 2600, ("hello", 0, 600), ("brave", 700, 1300)),
            line(2600, 5200, ("world", 2600, 3400), ("again", 3600, 4600)),
        };

        private const double anchor_stars = 1.9987321307058443;

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
        /// shorter than the smallest scheduled window therefore has no window that fits, so its peak
        /// ratio is 0 and so is the range that ratio scales.
        ///
        /// <para>EXACTLY ZERO since backlog 273, where before it rated its length term alone. The
        /// length term is gone, so there is nothing left for such a map to rate, and Is.Zero here is
        /// now a statement about the model rather than about a term being small.</para>
        ///
        /// <para>"cat cat" over 800 ms was this suite's anchor for six backlog items, which is why
        /// it is still here: it rates exactly nothing, and stretching the same two words over three
        /// seconds is all it takes to make it rate something.</para>
        /// </summary>
        [Test]
        public void AMapShorterThanTheSmallestWindowRatesExactlyZero()
        {
            var tooShort = new[] { line(0, 800, ("cat", 0, 400), ("cat", 400, 800)) };
            var longEnough = new[] { line(0, 3000, ("cat", 0, 1500), ("cat", 1500, 3000)) };

            Assert.Multiple(() =>
            {
                Assert.That(LyricDifficulty.Compute(tooShort), Is.Zero, "0.8 s of singing fits no window at all");
                Assert.That(LyricDifficulty.Compute(longEnough), Is.EqualTo(0.6680255352545187), "3 s of the same two words does");
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
                Assert.That(LyricDifficulty.Compute(oneLine), Is.EqualTo(0.9185219527454119), "7 cells: aaa + space + bbb");
                Assert.That(LyricDifficulty.Compute(twoLines), Is.EqualTo(0.7512636532216476), "6 cells: no space over a line break");
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
                Assert.That(LyricDifficulty.Compute(map, 0.75), Is.EqualTo(6.260654550067575), "sr_ht");
                Assert.That(LyricDifficulty.Compute(map), Is.EqualTo(8.16426556177434), "difficulty_rating");
                Assert.That(LyricDifficulty.Compute(map, 1.50), Is.EqualTo(11.762970854950098), "sr_dt");
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

            Assert.That(noMod, Is.EqualTo(8.908767792639306).Within(1e-9));
            Assert.That(doubleTime, Is.EqualTo(13.185566464522914).Within(1e-9), "under the old ceiling this read exactly 10.00");
        }

        [Test]
        public void AddingContentNeverLowersRating()
        {
            var baseMap = buildMap(lineCount: 8, wordsPerLine: 4, lineMs: 2400);
            // Same-pace continuation appended contiguously after the base map.
            var extended = baseMap.Concat(buildMap(lineCount: 8, wordsPerLine: 4, lineMs: 2400, startAt: 8 * 2400)).ToArray();

            double baseSr = LyricDifficulty.Compute(baseMap);
            double extendedSr = LyricDifficulty.Compute(extended);

            // The defining property, and under the envelope (backlog 273) it is structural rather
            // than incidental: N is a sum of non-negative per-bin terms and the peak is a maximum,
            // so appending anything can only raise both, never lower either.
            Assert.GreaterOrEqual(extendedSr, baseSr);
            // Length counts, but only through the characters it adds and saturating; doubling the
            // map must not double the rating.
            Assert.Less(extendedSr, baseSr * 2);
        }

        /// <summary>
        /// A LONG INSTRUMENTAL GAP, and the one fixture in this file whose exact value pins the
        /// <c>min(1, env/ratio_0)</c> CLAMP. Twenty seconds of dense singing, forty-five seconds of
        /// nothing, twenty seconds of easy singing.
        ///
        /// <para>WHY THE CLAMP EXISTS AND WHY IT NEEDS A FIXTURE. The peak ratio is computed the
        /// prototype's way, as cells over five over minutes and THEN over S(t), while the envelope
        /// divides the same cells by the pre-multiplied denominator in one step. Those two agree to
        /// within a bit, not always exactly, so at the peak's own bins <c>env/ratio_0</c> can come
        /// out at 1.0000000000000002 and, raised to the eighth, would carry that error into N. It
        /// really happens: over the twelve reference packages the clamp fires about two thousand
        /// times, always by exactly one unit in the last place. Most fixtures then round back to the
        /// same double anyway, which is why the clamp can be deleted and leave the rest of this file
        /// green; the gap shape does not, so this pin is where a deletion lands.</para>
        ///
        /// <para>A gap is also what makes the shape reachable: the dense half and the easy half are
        /// far enough apart that a long window covering both is worse than a short one covering the
        /// dense half alone, so the peak sits at a short duration and the long windows genuinely
        /// contribute a lower envelope instead of everything saturating together.</para>
        /// </summary>
        [Test]
        public void AMapWithALongInstrumentalGapIsRatedOnItsSingingAlone()
        {
            var gapped = buildMap(lineCount: 10, wordsPerLine: 6, lineMs: 2000)
                .Concat(buildMap(lineCount: 10, wordsPerLine: 2, lineMs: 2000, startAt: 65000)).ToArray();

            var denseHalf = buildMap(lineCount: 10, wordsPerLine: 6, lineMs: 2000);

            Assert.Multiple(() =>
            {
                Assert.That(LyricDifficulty.Compute(gapped), Is.EqualTo(7.212309813869222));
                Assert.That(LyricDifficulty.Compute(gapped, 1.50), Is.EqualTo(10.259613445167089), "sr_dt");

                // The 45 seconds of silence cost nothing and the easy tail after it earns a little,
                // which together is the claim that the gap is not averaged into the rating.
                Assert.That(LyricDifficulty.Compute(gapped), Is.GreaterThan(LyricDifficulty.Compute(denseHalf)));
            });
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
        /// A CUT VERSION CAN NEVER OUTRATE THE FULL VERSION IT WAS CUT FROM. The peak is scored
        /// against HUMAN CAPABILITY rather than against the map's own peak, so the cut and the full
        /// version share a RANGE, and inside that shared range the full version has strictly more
        /// characters to fill it with. Keeping a difficulty's hardest chorus and dropping everything
        /// after it can only remove characters from N, never rescale the range.
        ///
        /// <para>Three versions of one map, sharing an identical hardest chorus. The CUT is that
        /// chorus alone; one full version carries an easy tail after it and the other a tail as
        /// dense as the chorus itself (an Insane keeping backing-vocal lines a Hard drops). All
        /// three are pinned exactly, because the ORDER is the claim and a tolerance would let a
        /// future change reorder them inside it.</para>
        ///
        /// <para>NOTE HOW LITTLE THE EASY TAIL IS WORTH under the envelope (backlog 273): about
        /// 0.016 of a star, where the feats model paid it 0.55. That is the model working as
        /// designed, not a weakened claim. Those bins sit far below the chorus, so their d^8 weight
        /// is nearly nothing, and "there is more of it" is deliberately a soft signal now that the
        /// flat length term is gone. The DENSE tail, whose characters really are near the peak, is
        /// still worth well over a star, and the gap between the two tails is what the second
        /// assertion pins.</para>
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
                Assert.That(cutSr, Is.EqualTo(6.040220880568299));
                Assert.That(easySr, Is.EqualTo(6.056395565022444));
                Assert.That(hardSr, Is.EqualTo(7.621171412439358));

                // The two claims the numbers above encode, restated so a failure says which broke.
                Assert.That(easySr, Is.GreaterThan(cutSr), "the cut cannot outrate what it was cut from");
                Assert.That(hardSr - easySr, Is.GreaterThan(1.0), "and the characters a dense tail puts near the peak have to show");
            });
        }

        #region The envelope: how a map fills the range its hardest window opens (backlog 273)

        /// <summary>
        /// The WPM the fastest humans sustain for <paramref name="seconds"/>, restated here rather
        /// than read off the model, so the fixtures below can be built to a stated RATIO of it.
        /// </summary>
        private static double capability(double seconds) => 220 + 221 * Math.Pow(1.35 / seconds, 0.35);

        /// <summary>
        /// How many four-character words (five cells each, counting the space after them) it takes
        /// to run at <paramref name="ratio"/> of record pace for <paramref name="seconds"/>:
        /// <c>ratio * S(t) * 5 * (t/60)</c> cells, over five cells to the word.
        /// </summary>
        private static int wordsFor(double seconds, double ratio) => (int)Math.Round(ratio * capability(seconds) * (seconds / 60));

        /// <summary>One LINE of <paramref name="words"/> identical four-character words, evenly filling its span.</summary>
        private static LyricLine section(double startMs, double seconds, int words)
        {
            double step = seconds * 1000.0 / words;
            var units = new (string, double, double)[words];

            for (int i = 0; i < words; i++)
                units[i] = ("abcd", startMs + i * step, startMs + (i + 1) * step);

            return line(startMs, startMs + seconds * 1000, units);
        }

        /// <summary>The same builder at an EASY 60 WPM, i.e. one five-cell word a second.</summary>
        private static LyricLine easySection(double startMs, double seconds)
            => section(startMs, seconds, Math.Max(1, (int)Math.Round(60.0 * seconds / 60)));

        /// <summary><paramref name="n"/> bursts of 1.5 s at ratio 0.75, spaced evenly through an easy 120 s map.</summary>
        private static LyricLine[] burstMap(int n)
        {
            var lines = new List<LyricLine>();
            double t = 0;
            double easy = (120.0 - n * 1.5) / n;

            for (int k = 0; k < n; k++)
            {
                lines.Add(easySection(t * 1000, easy));
                t += easy;
                lines.Add(section(t * 1000, 1.5, wordsFor(1.5, 0.75)));
                t += 1.5;
            }

            return lines.ToArray();
        }

        /// <summary>
        /// THE HEADLINE PROPERTY OF THE ENVELOPE, and the reason the feats model was replaced. Four
        /// maps whose HARDEST STRETCH runs at the same 0.75 of record pace, so all four open the
        /// same range; what separates them is how many characters sit near that peak.
        ///
        /// <para>The feats model had the eight bursts AHEAD of the sustain, because eight
        /// non-overlapping windows filled eight decaying slots while one long sustain filled one.
        /// That was the defect: how a difficulty happens to be CHOPPED UP is not a difficulty. The
        /// envelope counts characters, so a 120 second sustain (which fills 98% of its range) beats
        /// four 15 second sections, which beat eight 1.5 second bursts, which beat one.</para>
        ///
        /// <para>Pinned exactly, and the ORDER is restated underneath so a failure says whether the
        /// arithmetic moved or the claim broke. The numbers are this port's own measurement, not the
        /// backlog item's: the item quotes 7.771 / 7.758 / 6.701 / 5.944 from the SR sandbox over
        /// its own per-word extract, and the fixtures here are built from the capability curve
        /// directly, which lands them a few hundredths away with the ordering intact. Those quoted
        /// figures are at the ORIGINAL 10.6 anchor; at the 12.0 anchor pinned below they read
        /// 8.797 / 8.782 / 7.586 / 6.729, since the model is linear in the anchor.</para>
        /// </summary>
        [Test]
        public void ASustainedStretchBeatsTheSameDifficultyChoppedIntoBursts()
        {
            var sustain = new[] { section(0, 120, wordsFor(120, 0.75)) };

            var fourSections = new List<LyricLine>();
            double t = 0;

            for (int k = 0; k < 4; k++)
            {
                fourSections.Add(easySection(t * 1000, 15));
                t += 15;
                fourSections.Add(section(t * 1000, 15, wordsFor(15, 0.75)));
                t += 15;
            }

            double sustainSr = LyricDifficulty.Compute(sustain);
            double fourSr = LyricDifficulty.Compute(fourSections.ToArray());
            double eightSr = LyricDifficulty.Compute(burstMap(8));
            double oneSr = LyricDifficulty.Compute(burstMap(1));

            TestContext.WriteLine($"sustain {sustainSr:0.000}; four 15 s sections {fourSr:0.000}; eight bursts {eightSr:0.000}; one burst {oneSr:0.000}");

            Assert.Multiple(() =>
            {
                Assert.That(sustainSr, Is.EqualTo(8.798896190030623));
                Assert.That(fourSr, Is.EqualTo(8.779855661548071));
                Assert.That(eightSr, Is.EqualTo(7.528093274681277));
                Assert.That(oneSr, Is.EqualTo(6.65179181801228));

                Assert.That(sustainSr, Is.GreaterThan(fourSr), "a sustain beats the same pace split into four");
                Assert.That(fourSr, Is.GreaterThan(eightSr), "which beats the same pace split into eight bursts");
                Assert.That(eightSr, Is.GreaterThan(oneSr), "and eight bursts beat one");

                // Non-vacuity: all four really do share a peak, so the ordering above is the FILL
                // talking and not four different ranges. Their floors agree to a few tenths of a
                // percent (the bin grid rounds each window's start differently), which is what
                // "the same 0.75" survives as once the timeline is discretised.
                Assert.That(oneSr / sustainSr, Is.GreaterThan(1 / 1.3333 - 0.01),
                    "one burst cannot fall below the shared floor, which is 1/(1 + envelope_range) of a filled range");
            });
        }

        /// <summary>
        /// LENGTH IS NOW A SOFT SIGNAL AND NOTHING ELSE. Backlog 152's flat
        /// <c>0.12 * log10(cells/100)</c> is deleted (backlog 273), so padding a map counts only
        /// through the characters the padding adds, weighted by how near the peak they sit. Sixty
        /// seconds of 60 WPM singing bolted onto an already saturated 123 second sustain is worth
        /// about a twelfth of one percent, where the old term paid it a flat 0.02 of a star on top.
        ///
        /// <para>POSITIVE is the claim, not merely non-negative: there is deliberately NO CUTOFF
        /// under the per-character weight, so easy padding is worth a little rather than exactly
        /// nothing. Delete the <c>d^envelope_power</c> term's tail (say by cutting off under 0.5)
        /// and this equality fails on the second digit.</para>
        /// </summary>
        [Test]
        public void PaddingAHardMapWithEasySingingAddsALittleAndNotNothing()
        {
            var sustain = section(0, 123, wordsFor(123, 0.75));
            var padded = new[] { sustain, easySection(123000, 60) };

            double bare = LyricDifficulty.Compute(new[] { sustain });
            double withPadding = LyricDifficulty.Compute(padded);

            TestContext.WriteLine($"123 s sustain {bare:0.000000}; padded with 60 s at 60 WPM {withPadding:0.000000} ({(withPadding / bare - 1) * 100:0.0000}%)");

            Assert.Multiple(() =>
            {
                Assert.That(bare, Is.EqualTo(8.942163764721732));
                Assert.That(withPadding, Is.EqualTo(8.949815951568478));

                Assert.That(withPadding, Is.GreaterThan(bare), "easy padding is worth a little, never nothing");
                Assert.That(withPadding / bare - 1, Is.EqualTo(0.000856).Within(5e-6), "and a little means under a tenth of a percent");
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
        /// that is written down so the next reader does not have to rediscover it. Under the
        /// envelope model the reason is that the two changes act on DIFFERENT AXES: the rate
        /// compresses the timeline, which moves which windows fit at all and therefore which window
        /// is the peak, while Literate adds cells to the words already there, which moves the
        /// per-bin density and so the whole envelope relative to that peak. Stars are the PRODUCT of
        /// those two (a peak-scaled range times a fill), so neither is a scalar on the other.
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
                Assert.That(LyricDifficulty.Compute(big), Is.EqualTo(19.695650220525547));
                Assert.That(LyricDifficulty.Compute(big, 1.50), Is.EqualTo(28.553224233675905));
                Assert.That(LyricDifficulty.Compute(realistic), Is.EqualTo(4.896180402042062));
                Assert.That(LyricDifficulty.Compute(mid, 0.75), Is.EqualTo(6.260654550067575));
                Assert.That(LyricDifficulty.Compute(mid), Is.EqualTo(8.16426556177434));
                Assert.That(LyricDifficulty.Compute(mid, 1.50), Is.EqualTo(11.762970854950098));
                Assert.That(LyricDifficulty.Compute(mid, 1, literate: true), Is.EqualTo(8.16426556177434));
                Assert.That(LyricDifficulty.Compute(punctuated), Is.EqualTo(3.177354181493089));
                Assert.That(LyricDifficulty.Compute(punctuated, 1, literate: true), Is.EqualTo(3.5620837044806435));
                Assert.That(LyricDifficulty.Compute(punctuated, 1.50, literate: true), Is.EqualTo(4.583755790239398));
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
        /// two timelines hold different densities, which moves both the peak and the envelope.</para>
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
        /// THE FREESTYLE MAP'S OWN EXACT PIN, which the equalities either side of it cannot give:
        /// both of those would still hold if BOTH maps moved together to some other weight. The
        /// fixture's 60 words carry one fixed key and four slots each, and its 10 lines carry 5
        /// inter-word spaces apiece, so its priced cell count is <c>60 * (1 + 4/4) + 10 * 5 = 170</c>;
        /// count a slot as a whole cell and it would be 350, count it as nothing and it would be 110,
        /// and both of those move the timeline densities this number is computed from.
        /// </summary>
        [Test]
        public void AFreestyleMapRatesItsQuarteredDensity()
        {
            var free = uniformMap(tokens(60, i => letters(i, 1) + new string(marker, 4) + ","), wordsPerLine: 6, stepMs: 400, spanMs: 350);

            Assert.That(LyricDifficulty.Compute(free), Is.EqualTo(2.7463413849647913));
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
