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
    public class LyricPaceStatisticsTest
    {
        /// <summary>
        /// A line whose word span runs from its start to its vocal end, which is its boundary end
        /// unless <paramref name="singEnd"/> says otherwise.
        ///
        /// <para><paramref name="units"/> overrides the spans when a fixture needs more than one — a
        /// pause INSIDE a line is what the break threshold reads, and a single span covering the whole
        /// window cannot express one.</para>
        /// </summary>
        private static LyricLine makeLine(string text, double start, double end, double? singEnd = null, params (double Start, double End)[] units)
        {
            double vocalEnd = singEnd ?? end;

            IReadOnlyList<TimedUnit> built = units.Length > 0
                ? units.Select(u => new TimedUnit { Text = text, StartTime = u.Start, EndTime = u.End }).ToArray()
                : new[] { new TimedUnit { Text = text, StartTime = start, EndTime = vocalEnd } };

            return new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = vocalEnd,
                Units = built,
            };
        }

        [Test]
        public void ComputesBoundaryWindowPace()
        {
            // "ab cd": 2 words, 5 typeable cells (a, b, space, c, d). Boundary window
            // = EndTime - StartTime = 3000 ms, regardless of where the unit targets sit.
            // CPM = 5 / (3000ms / 60000) = 100; WPM = CPM / 5 = 20.
            //
            // The real-word convention this replaced would have said 2 / 0.05 = 40 WPM. The line
            // averages 5/2 = 2.5 cells per word, half the 5 the unit assumes, so the new figure is
            // half the old one: exactly the proportionality AverageCharsPerWord now advertises.
            var pace = LyricPaceStatistics.Compute(new[] { makeLine("ab cd", 1000, 4000) });

            Assert.AreEqual(5, pace.TypeableCellCount);
            Assert.AreEqual(2, pace.WordCount);
            Assert.AreEqual(20.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(100.0, pace.AverageCpm, 1e-9);
            // One line, so the whole-map rate and the line mean are the same number by construction.
            Assert.AreEqual(pace.AverageWpm, pace.LineAverageWpm, 1e-12);
            Assert.AreEqual(pace.AverageCpm, pace.LineAverageCpm, 1e-12);
            Assert.AreEqual(2.5, pace.AverageCharsPerWord, 1e-9);
        }

        [Test]
        public void WholeMapAverageWeightsLinesByTimeWhileTheLineMeanDoesNot()
        {
            // Line 1: "ab cd" over 3000 ms -> 100 CPM / 20 WPM, 5 cells.
            // Line 2: "ab cd" over 1500 ms -> 200 CPM / 40 WPM, 5 cells.
            //
            //   whole map = 10 cells / (3000 + 1500 ms) = 10 / 0.075 = 133.333 CPM = 26.667 WPM
            //   line mean = (100 + 200) / 2            =                150     CPM = 30      WPM
            //
            // The two are different numbers on purpose: the whole-map rate is what the map asks per
            // minute of singing, the line mean is what its average LINE asks. A short fast line moves
            // the second far more than the first.
            var pace = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 1000, 4000),
                makeLine("ab cd", 4000, 5500),
            });

            Assert.AreEqual(10, pace.TypeableCellCount);
            Assert.AreEqual(4, pace.WordCount);
            Assert.AreEqual(10.0 / (4500 / 60000.0), pace.AverageCpm, 1e-9);
            Assert.AreEqual(10.0 / (4500 / 60000.0) / LyricPaceStatistics.CHARS_PER_WORD, pace.AverageWpm, 1e-9);
            Assert.AreEqual(150.0, pace.LineAverageCpm, 1e-9);
            Assert.AreEqual(30.0, pace.LineAverageWpm, 1e-9);
            Assert.AreEqual(2.5, pace.AverageCharsPerWord, 1e-9);
        }

        [Test]
        public void MinimumWindowGuardsDegenerateBoundaries()
        {
            // A 100 ms boundary window clamps to the 500 ms floor:
            // 5 cells / (500ms / 60000) = 600 CPM; WPM = 600 / 5 = 120.
            var pace = LyricPaceStatistics.Compute(new[] { makeLine("abcde", 1000, 1100) });

            Assert.AreEqual(120.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(600.0, pace.AverageCpm, 1e-9);
            // The same floor guards the vocal window the whole-map rate divides by.
            Assert.AreEqual(600.0, pace.LineAverageCpm, 1e-9);
            Assert.AreEqual(120.0, pace.LineAverageWpm, 1e-9);
        }

        /// <summary>
        /// THE POINT OF THE WHOLE-MAP RATE: a break in the song is not typing time.
        ///
        /// <para>A line's <see cref="LyricLine.EndTime"/> is the NEXT line's start, so a map with a long
        /// instrumental after a line hands that pause to the line's own boundary window. The per-line
        /// mean then charges the player for it, one pause at a time; the whole-map rate sums
        /// <see cref="LyricLine.SingEndTime"/> − <see cref="LyricLine.StartTime"/> instead and never
        /// sees it.</para>
        /// </summary>
        [Test]
        public void TheWholeMapAverageLeavesTheSongsBreaksOutOfTheDenominator()
        {
            // Two 5-cell lines, each sung for 4 s but bounded for 20 s (a 16 s instrumental after each):
            //   whole map = 10 cells / (4000 + 4000 ms) = 10 / 0.1333 =  75 CPM = 15 WPM
            //   line mean = each line 5 cells / 20 s     =             15 CPM =  3 WPM
            var pace = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 0, 20000, singEnd: 4000),
                makeLine("ab cd", 20000, 40000, singEnd: 24000),
            });

            Assert.AreEqual(10, pace.TypeableCellCount);
            Assert.AreEqual(75.0, pace.AverageCpm, 1e-9);
            Assert.AreEqual(15.0, pace.AverageWpm, 1e-9);

            // The figure it replaced reads five times slower on the same map, because every one of those
            // 16-second silences is sitting inside a line's own vote.
            Assert.AreEqual(15.0, pace.LineAverageCpm, 1e-9);
            Assert.AreEqual(3.0, pace.LineAverageWpm, 1e-9);

            // "ab cd" is five cells over two words, so the map types 2.5 cells per word; the two
            // averages differ ONLY by where the windows stop, which is the point of the fixture.
            Assert.AreEqual(2.5, pace.AverageCharsPerWord, 1e-9);
        }

        /// <summary>
        /// THE THRESHOLD, and the difference between it being a threshold and it being a trim: a
        /// pause counts as singing time up to <c>break_min_ms</c> and is dropped WHOLE beyond it.
        ///
        /// <para>Every fixture below is the same five cells over the same 10 s boundary window, so
        /// the line mean is pinned at 30 CPM throughout and only the whole-map average moves. The
        /// CPM figures encode the charged window directly, because 5 cells * 60000 / CPM is the
        /// number of milliseconds that went into the denominator: 4000 -> 75, 5000 -> 60,
        /// 6000 -> 50.</para>
        /// </summary>
        [Test]
        public void APauseCountsUpToTheBreakThresholdAndIsDroppedWholeBeyondIt()
        {
            // A BREATH of exactly 1000 ms between the two 2 s spans COUNTS — the comparison is
            // inclusive, so the widest pause the constant allows is not itself a break. The 5 s
            // tail after the last span is wider than the constant and is dropped whole.
            var breath = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 0, 10000, singEnd: 5000, (0, 2000), (3000, 5000)),
            });

            Assert.AreEqual(60.0, breath.AverageCpm, 1e-9);
            Assert.AreEqual(12.0, breath.AverageWpm, 1e-9);

            // A BREAK of 1001 ms is over the line and is dropped WHOLE rather than trimmed to the
            // constant: a trim would have charged the extra millisecond's worth and read 5000 ms
            // (60 CPM) here, so 75 is the number that says "dropped".
            var gone = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 0, 10000, singEnd: 5001, (0, 2000), (3001, 5001)),
            });

            Assert.AreEqual(75.0, gone.AverageCpm, 1e-9);

            // The same rule reads the TAIL: a 1000 ms one after the final span counts, and a
            // 1001 ms one does not.
            var shortTail = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 0, 6000, singEnd: 5000, (0, 2000), (3000, 5000)),
            });

            var longTail = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 0, 6001, singEnd: 5000, (0, 2000), (3000, 5000)),
            });

            Assert.AreEqual(50.0, shortTail.AverageCpm, 1e-9);
            Assert.AreEqual(60.0, longTail.AverageCpm, 1e-9);

            // The threshold is the whole-map figure's alone. The line mean reads the BOUNDARY
            // window and never looks inside it, so the two 10 s fixtures above disagree on the
            // whole-map rate, 60 against 75, while reading the SAME line mean. (The two tail
            // fixtures are shorter than the 10 s window, so their line means differ for that
            // reason instead — nothing about the threshold.)
            foreach (var pace in new[] { breath, gone })
            {
                Assert.AreEqual(30.0, pace.LineAverageCpm, 1e-9);
                Assert.AreEqual(6.0, pace.LineAverageWpm, 1e-9);
            }

            // Every fixture here is the same five cells, so no window shape can move that.
            foreach (var pace in new[] { breath, gone, shortTail, longTail })
            {
                Assert.AreEqual(5, pace.TypeableCellCount);
            }
        }

        /// <summary>
        /// A freestyle slot is an AUTHORING MARK, not typing, so no pace figure charges for it —
        /// the same rule the estimator gives punctuation. The engine's own readouts do count the
        /// press (see the class remarks), and that divergence is deliberate: those answer how fast
        /// the player typed, these answer how fast the map asked to be typed.
        /// </summary>
        [Test]
        public void FreestyleSlotsAreNotCountedByAnyPaceFigure()
        {
            // "a&b c" is two words and FOUR cells: a, b, the inter-word space and c. Counting the
            // marker would read five.
            var marked = LyricPaceStatistics.Compute(new[] { makeLine("a&b c", 0, 60000) });

            Assert.AreEqual(4, marked.TypeableCellCount);
            Assert.AreEqual(2, marked.WordCount);
            Assert.AreEqual(4.0, marked.AverageCpm, 1e-9);
            Assert.AreEqual(2.0, marked.AverageCharsPerWord, 1e-9);

            // Dropping the marker from the text moves no figure at all, so the exclusion cannot
            // have shifted any map that carries none.
            var plain = LyricPaceStatistics.Compute(new[] { makeLine("ab c", 0, 60000) });

            Assert.AreEqual(plain.TypeableCellCount, marked.TypeableCellCount);
            Assert.AreEqual(plain.AverageCpm, marked.AverageCpm, 1e-12);
            Assert.AreEqual(plain.LineAverageCpm, marked.LineAverageCpm, 1e-12);

            // The target is no longer asserted equal here: it is read off the shipped (chunked)
            // axis, where an any-key slot is a cell like any other, so a marker that leaves the
            // averages untouched can still move it by a few percent.

            // A line of nothing BUT freestyle slots asks for no typing, so it is not a counted
            // line and cannot vote in either average. The target is no longer asserted here: it
            // used to read the model's hardest-window figure, which the shipped (chunked) axis
            // does not have, so the old equality held only because the character floor had
            // already priced both maps at zero.
            var withOnly = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 1000, 4000),
                makeLine("&&&&", 4000, 6000),
            });

            var withoutLine = LyricPaceStatistics.Compute(new[] { makeLine("ab cd", 1000, 4000) });

            Assert.AreEqual(withoutLine.TypeableCellCount, withOnly.TypeableCellCount);
            Assert.AreEqual(withoutLine.WordCount, withOnly.WordCount);
            Assert.AreEqual(withoutLine.AverageCpm, withOnly.AverageCpm, 1e-12);
            Assert.AreEqual(withoutLine.LineAverageCpm, withOnly.LineAverageCpm, 1e-12);
        }

        /// <summary>
        /// The one identity the typing-test convention rests on, and the reason the metric counts
        /// inter-word spaces: a line whose average word is exactly 5 CELLS long has the same WPM
        /// under the typing-test convention (cells/5) as under the real-word one (words), so the
        /// convention is a reweighting around 5, not an arbitrary rescaling, and
        /// AverageCharsPerWord is precisely the old CPM:WPM ratio made visible.
        ///
        /// <para>Stated at LINE granularity, and the fixture gives every line an average of exactly
        /// 5 rather than only the map total: the identity is per line, so a map that averages 5
        /// overall while its individual lines do not would not satisfy it line by line.</para>
        ///
        /// <para>Both map figures are pinned below them, and they now DIFFER: the whole-map rate
        /// weights the 1500 ms line twice as heavily as the 3000 ms one, while the line mean gives
        /// them one vote each. Nothing about the conversion from cells to words changed; what
        /// changed is which lines the average is over.</para>
        /// </summary>
        [Test]
        public void FiveCellWordsMakeTheNewWpmEqualTheOldOne()
        {
            // Line 1, "abcd efghi" over 3000 ms: 2 words, 4 + 1 + 5 = 10 cells, 10/2 = 5 exactly.
            //   old WPM = 2 words / 0.05 min           = 40
            //   CPM     = 10 cells / 0.05 min          = 200
            //   new WPM = 200 / 5                      = 40   (equal)
            //
            // Line 2, "abcd efgh ijkl mnopq" over 1500 ms: 4 words, 17 chars + 3 spaces = 20 cells,
            // 20/4 = 5 exactly.
            //   old WPM = 4 words / 0.025 min          = 160
            //   CPM     = 20 cells / 0.025 min         = 800
            //   new WPM = 800 / 5                      = 160  (equal)
            //
            // Map: mean CPM = (200 + 800) / 2 = 500, mean WPM = (40 + 160) / 2 = 100 = 500 / 5,
            // and chars/word = (10 + 20) / (2 + 4) = 30 / 6 = 5.
            var lines = new[]
            {
                makeLine("abcd efghi", 1000, 4000),
                makeLine("abcd efgh ijkl mnopq", 4000, 5500),
            };

            var pace = LyricPaceStatistics.Compute(lines);

            Assert.AreEqual(30, pace.TypeableCellCount);
            Assert.AreEqual(6, pace.WordCount);
            Assert.AreEqual(5.0, pace.AverageCharsPerWord, 1e-9);
            // whole map: 30 cells / 4500 ms = 400 CPM = 80 WPM.
            Assert.AreEqual(400.0, pace.AverageCpm, 1e-9);
            Assert.AreEqual(80.0, pace.AverageWpm, 1e-9);
            // line mean: (200 + 800) / 2 = 500 CPM = 100 WPM.
            Assert.AreEqual(500.0, pace.LineAverageCpm, 1e-9);
            Assert.AreEqual(100.0, pace.LineAverageWpm, 1e-9);

            // And the real-word convention, recomputed here from the same windows, agrees LINE BY
            // LINE with the cells/5 one — which is the identity above, and the reason the two
            // cells-per-word fixtures were chosen at exactly 5.
            double lineOneRealWordWpm = 2 / (3000 / 60000.0);
            double lineTwoRealWordWpm = 4 / (1500 / 60000.0);

            Assert.AreEqual((lineOneRealWordWpm + lineTwoRealWordWpm) / 2, pace.LineAverageWpm, 1e-9);
        }

        [Test]
        public void WpmIsCpmOverFiveWhateverTheWordLength()
        {
            // The identity above is conditional on 5-cell words; THIS one is unconditional, which is
            // the point of deriving AverageWpm from AverageCpm instead of summing it separately.
            // Lines chosen to average nothing like 5: 5/2 = 2.5 and 18/2 = 9.0 cells per word,
            // 23/4 = 5.75 over the map.
            var pace = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 1000, 4000),
                makeLine("abcdefgh ijklmnopq", 4000, 9000),
            });

            Assert.AreEqual(pace.AverageCpm / 5.0, pace.AverageWpm, 1e-12);
            Assert.AreNotEqual(5.0, pace.AverageCharsPerWord);
        }

        [Test]
        public void CharsPerWordCountsInterWordSpaces()
        {
            // "ab cd ef": 3 words, 6 chars + 2 spaces = 8 cells, so 8/3 and not 6/3. Spaces are in
            // because the 5 in "5 chars = 1 word" counts them: they are keystrokes like any other,
            // and leaving them out here would put the two metrics in different units and break the
            // identity pinned above.
            var pace = LyricPaceStatistics.Compute(new[] { makeLine("ab cd ef", 1000, 4000) });

            Assert.AreEqual(8, pace.TypeableCellCount);
            Assert.AreEqual(3, pace.WordCount);
            Assert.AreEqual(8 / 3.0, pace.AverageCharsPerWord, 1e-9);
        }

        /// <summary>
        /// <paramref name="windowsMs"/> lines of "a b c", one per boundary window given. The line
        /// holds exactly 5 cells (three tokens, three chars, two inter-word spaces), so its rate is
        /// 5 * 60000 / window CPM and the whole distribution is hand-computable. Line times are laid
        /// out end to end with a 500 ms rest between them, which nothing here reads: a per-line mean
        /// cannot see the gaps.
        ///
        /// <para>THREE tokens rather than the single "abcde" this used to write, so the fixture reads
        /// as ordinary lyric text; cell for cell it is the same 5, so every pinned CPM and WPM below
        /// is the number it was before the token count changed. Five cells is well under the peak
        /// character floor the target needs, which is why these lines exercise the two AVERAGES and
        /// are paired with <see cref="denseLine"/> wherever a target is wanted.</para>
        /// </summary>
        private static LyricLine[] linesAtWindows(params double[] windowsMs) => linesAtWindowsOf("a b c", windowsMs);

        /// <summary><see cref="linesAtWindows"/> with the line text chosen.</summary>
        private static LyricLine[] linesAtWindowsOf(string text, params double[] windowsMs)
        {
            var lines = new LyricLine[windowsMs.Length];
            double at = 1000;

            for (int i = 0; i < windowsMs.Length; i++)
            {
                lines[i] = makeLine(text, at, at + windowsMs[i]);
                at += windowsMs[i] + 500;
            }

            return lines;
        }

        /// <summary>
        /// The map's six windows, chosen so every per-line rate is a round CPM:
        ///
        /// <list type="bullet">
        /// <item>500 ms -> 600 CPM (120 WPM)</item>
        /// <item>600 ms -> 500 CPM (100 WPM)</item>
        /// <item>750 ms -> 400 CPM (80 WPM)</item>
        /// <item>1000 ms -> 300 CPM (60 WPM)</item>
        /// <item>1500 ms -> 200 CPM (40 WPM)</item>
        /// <item>3000 ms -> 100 CPM (20 WPM)</item>
        /// </list>
        /// </summary>
        private static readonly double[] six_windows = { 500, 600, 750, 1000, 1500, 3000 };

        /// <summary>
        /// A line dense enough to be a candidate peak: eight words and eight cells over
        /// <paramref name="lineMs"/>, which is what the fixtures below need. The five-cell
        /// <see cref="linesAtWindows"/> lines are useful for the averages but too thin to clear the
        /// model's character floor, so they report no target at all.
        /// </summary>
        private static LyricLine denseLine(string text, double start, double lineMs)
            => makeLine(text, start, start + lineMs);

        private const string dense_text = "a b c d e f g h";

        /// <summary>
        /// Twelve dense lines laid end to end, with the first <paramref name="fastLines"/> of them
        /// run at <paramref name="fastMs"/> and the rest at the pace that fills the same total
        /// duration. Both arms therefore hold the same cells over the same length; only the
        /// DISTRIBUTION differs, which is exactly what a peak figure has to be able to see.
        /// </summary>
        private static LyricLine[] twoPaceMap(int fastLines, double fastMs, double totalMs)
        {
            const int lines = 12;
            double slowMs = (totalMs - fastLines * fastMs) / (lines - fastLines);
            var result = new LyricLine[lines];
            double at = 1000;

            for (int i = 0; i < lines; i++)
            {
                double ms = i < fastLines ? fastMs : slowMs;
                result[i] = denseLine(dense_text, at, ms);
                at += ms;
            }

            return result;
        }

        /// <summary>
        /// THE TARGET is the map's hardest window BY RAW SPEED re-expressed at
        /// <see cref="LyricDifficulty.TargetWindowSeconds"/>, which is the figure the Star Rating
        /// Sandbox prints beside every map and the one song select renders. This file does not
        /// re-derive it: the statistic hands the lines to the difficulty model and publishes what
        /// comes back, so the number the player sees and the number the model computed cannot drift
        /// apart. That equality IS the contract, and it is asserted on every shape below.
        /// </summary>
        [Test]
        public void TargetWpmIsTheModelsOwnSpeedWindowFigure()
        {
            foreach (var lines in new[]
            {
                twoPaceMap(6, 1000, 21000),
                twoPaceMap(0, 0, 21000),
                linesAtWindows(six_windows),
                linesAtWindows(1000, 1000, 1000, 1000, 1000),
            })
            {
                var pace = LyricPaceStatistics.Compute(lines);

                double model = LyricDifficulty
                    .ComputeDetail(lines, 1, false, LyricDifficulty.EnduranceAxis.Envelope)
                    .TargetWpm;

                // The model's own figure, FLOORED at the whole-map average: see
                // TargetWpmIsFlooredAtTheWholeMapAverage for why the two can invert.
                Assert.AreEqual(Math.Max(model, pace.AverageWpm), pace.TargetWpm, 1e-12,
                    "the strip has to publish the model's own figure, floored at the average");
            }
        }

        /// <summary>
        /// THE FLOOR ITSELF. Target WPM is the pace of the map's hardest window and the average is
        /// the pace of the whole song, so the target is normally the higher of the two; on a map
        /// whose hardest stretch is SLOWER than its relentless average they invert and the figure
        /// presented as the map's ask sits under what the map already demands everywhere. Nothing
        /// may be published below the map's own average pace, so the reported target is floored
        /// there - which is a presentation rule, not a rating one.
        /// </summary>
        [Test]
        public void TargetWpmIsFlooredAtTheWholeMapAverage()
        {
            foreach (var lines in new[]
            {
                twoPaceMap(6, 1000, 21000),
                twoPaceMap(0, 0, 21000),
                linesAtWindows(six_windows),
                linesAtWindows(1000, 1000, 1000, 1000, 1000),
                linesAtWindowsOf("a b c", 500, 600, 750, 1000, 1500, 3000),
            })
            {
                var pace = LyricPaceStatistics.Compute(lines);

                Assert.That(pace.TargetWpm, Is.GreaterThanOrEqualTo(pace.AverageWpm),
                    "the published target may never sit under the map's whole-map average");
            }
        }

        /// <summary>
        /// AND IT IS A PEAK: the two arms hold the same cells over the same length, and the one
        /// that concentrates them into a fast half reads a substantially higher target. The
        /// averages are the control - a per-minute rate cannot tell the two shapes apart, which is
        /// the whole reason the figure is read off a window rather than off the map.
        /// </summary>
        [Test]
        public void TargetWpmRisesWithThePeakWhileTheAveragesDoNot()
        {
            var peaked = LyricPaceStatistics.Compute(twoPaceMap(6, 1000, 21000));
            var flat = LyricPaceStatistics.Compute(twoPaceMap(0, 0, 21000));

            Assert.Greater(peaked.TargetWpm, 0, "the fast half is dense enough to clear the floor");
            Assert.Greater(flat.TargetWpm, 0);
            Assert.Greater(peaked.TargetWpm, flat.TargetWpm, "concentrating the same cells has to raise the target");

            Assert.AreEqual(peaked.AverageWpm, flat.AverageWpm, 1e-9, "the whole-map rate cannot see the shape");

            // The per-line mean DOES move - it gives each line one vote, so a map cut into half-length
            // and double-length lines averages differently from one cut into twelve equal ones. It is
            // pinned here as the contrast: the figure that answers "what pace is this map typed at"
            // has to be read off a window, not off either average.
            Assert.AreNotEqual(peaked.LineAverageWpm, flat.LineAverageWpm);
        }

        /// <summary>
        /// The target is a SPEED figure and nothing else: it is read off the authored density with
        /// the typability multiplier and the rhythm bonus left out of both the window choice and the
        /// conversion, so it does not move when either experiment does. The other two figures are
        /// spelled out beside it so the fixture says what it is not as well as what it is.
        /// </summary>
        [Test]
        public void TargetWpmIsNotAnAverageAndIgnoresTheExperiments()
        {
            var lines = twoPaceMap(6, 1000, 21000);
            var pace = LyricPaceStatistics.Compute(lines);
            var model = LyricDifficulty.ComputeDetail(lines, 1, false, LyricDifficulty.EnduranceAxis.Envelope);

            Assert.AreNotEqual(pace.AverageWpm, pace.TargetWpm);
            Assert.AreNotEqual(pace.LineAverageWpm, pace.TargetWpm);

            // Same map, same figure, whatever the rhythm arm and the typability strength are: the
            // model's own target is computed before either reaches the rating. On this fixture the
            // peak is well clear of the average, so the floor does not engage and this is the model's
            // figure untouched.
            Assert.Greater(model.TargetWpm, pace.AverageWpm, "this fixture is the case where the peak IS the higher figure");
            Assert.AreEqual(model.TargetWpm, pace.TargetWpm, 1e-12);
            Assert.Less(model.TargetWpm, 300, "a sanity bound: this fixture is not a 300 WPM map");
        }
        [Test]
        public void EmptyMapIsZero()
        {
            var pace = LyricPaceStatistics.Compute(Array.Empty<LyricLine>());

            Assert.AreEqual(0, pace.TypeableCellCount);
            Assert.AreEqual(0, pace.WordCount);
            Assert.AreEqual(0, pace.AverageWpm);
            Assert.AreEqual(0, pace.AverageCpm);
            Assert.AreEqual(0, pace.LineAverageWpm);
            Assert.AreEqual(0, pace.LineAverageCpm);

            // No counted line, so no fastest fifth of one either: 0, on the same rule.
            Assert.AreEqual(0, pace.TargetWpm);

            // No words to divide by: 0 rather than a NaN that would render as "NaN" in the wedge.
            Assert.AreEqual(0, pace.AverageCharsPerWord);
        }
    }
}
