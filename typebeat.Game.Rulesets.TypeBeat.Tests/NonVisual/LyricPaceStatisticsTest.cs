// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class LyricPaceStatisticsTest
    {
        private static LyricLine makeLine(string text, double start, double end) => new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = end,
            SingEndTime = end,
            Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = end } },
        };

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
            Assert.AreEqual(2.5, pace.AverageCharsPerWord, 1e-9);
        }

        [Test]
        public void AveragesPerLineRatesUnweighted()
        {
            // Line 1: "ab cd" over 3000 ms -> 100 CPM / 20 WPM.
            // Line 2: "ab cd" over 1500 ms -> 200 CPM / 40 WPM.
            // Map = unweighted mean of per-line rates: 150 CPM / 30 WPM.
            var pace = LyricPaceStatistics.Compute(new[]
            {
                makeLine("ab cd", 1000, 4000),
                makeLine("ab cd", 4000, 5500),
            });

            Assert.AreEqual(10, pace.TypeableCellCount);
            Assert.AreEqual(4, pace.WordCount);
            Assert.AreEqual(30.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(150.0, pace.AverageCpm, 1e-9);
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
        }

        /// <summary>
        /// The one identity the whole convention change rests on, and the reason the new metric
        /// counts inter-word spaces: a line whose average word is exactly 5 CELLS long has the same
        /// WPM under the typing-test convention (cells/5) as under the real-word one (words), so
        /// the change is a reweighting around 5, not an arbitrary rescaling, and
        /// AverageCharsPerWord is precisely the old CPM:WPM ratio made visible.
        ///
        /// <para>Stated at LINE granularity, and the fixture gives every line an average of exactly
        /// 5 rather than only the map total: WPM and CPM are unweighted means of per-line rates, so
        /// a map that averages 5 overall while its individual lines do not would not satisfy the
        /// identity line by line.</para>
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
            Assert.AreEqual(500.0, pace.AverageCpm, 1e-9);
            Assert.AreEqual(100.0, pace.AverageWpm, 1e-9);

            // And the old convention, recomputed here from the same boundary windows, agrees:
            // mean of (words / minutes) over the two lines.
            double oldConventionWpm = (2 / (3000 / 60000.0) + 4 / (1500 / 60000.0)) / 2;

            Assert.AreEqual(oldConventionWpm, pace.AverageWpm, 1e-9);
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
        /// <para>THREE tokens rather than the single "abcde" this used to write, so every line clears
        /// the target's three-word eligibility floor and the fixtures below exercise the SELECTION
        /// rather than its all-short fallback. Cell for cell it is the same 5, so every pinned CPM
        /// and WPM below is the number it was before the floor existed. <see cref="short_line"/> and
        /// <see cref="one_word_line"/> are the ineligible counterparts at identical rates.</para>
        /// </summary>
        private static LyricLine[] linesAtWindows(params double[] windowsMs) => linesAtWindowsOf("a b c", windowsMs);

        /// <summary><see cref="linesAtWindows"/> with the line text chosen, for the eligibility fixtures.</summary>
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
        /// A TWO-word line of the same 5 cells ("ab" + space + "cd"), so it runs at exactly the rate
        /// a <see cref="linesAtWindows"/> line of the same window runs at while being INELIGIBLE for
        /// the target pool. Every fixture below that wants to show the floor doing something pairs
        /// this against "a b c" over the same windows.
        /// </summary>
        private const string short_line = "ab cd";

        /// <summary>A ONE-word line of the same 5 cells, for the all-short fallback.</summary>
        private const string one_word_line = "abcde";

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

        [Test]
        public void TargetWpmIsTheMeanOfTheFastestFifthOfTheLines()
        {
            // Six lines at 600, 500, 400, 300, 200 and 100 CPM (see six_windows).
            //
            //   average = (600 + 500 + 400 + 300 + 200 + 100) / 6 = 2100 / 6 = 350 CPM = 70 WPM
            //   target  = the fastest ceil(0.20 * 6) = 2 of them, (600 + 500) / 2 = 550 CPM = 110 WPM
            //   fastest single line                              = 600 CPM              = 120 WPM
            //
            // Three DIFFERENT numbers, which is the point of the fixture: an implementation that
            // returned the map average, or the one fastest line, under the name TargetWpm would
            // pass a fixture where any two of them coincided.
            var pace = LyricPaceStatistics.Compute(linesAtWindows(six_windows));

            Assert.AreEqual(70.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(110.0, pace.TargetWpm, 1e-9);

            Assert.AreNotEqual(pace.TargetWpm, pace.AverageWpm);
            Assert.AreNotEqual(pace.TargetWpm, 120.0);
        }

        [Test]
        public void TargetLineCountRoundsTheFifthUp()
        {
            // The count is ceil(0.20 * lineCount), and this is where it steps. Five lines take ONE
            // line (0.20 * 5 = 1.0 exactly), six take TWO (1.2 rounds up), which is why adding a
            // SLOWER sixth line LOWERS the target: the selection widened to two lines and the
            // second-fastest is below the fastest. That is the statistic working, not a defect.
            var five = LyricPaceStatistics.Compute(linesAtWindows(500, 600, 750, 1000, 1500));
            var six = LyricPaceStatistics.Compute(linesAtWindows(six_windows));

            // Five: target = 600 CPM = 120 WPM, average = 2000 / 5 = 400 CPM = 80 WPM.
            Assert.AreEqual(120.0, five.TargetWpm, 1e-9);
            Assert.AreEqual(80.0, five.AverageWpm, 1e-9);

            Assert.AreEqual(110.0, six.TargetWpm, 1e-9);

            // And below the step the fifth rounds up to the whole of the one selected line: every
            // map from one line to four selects exactly its fastest, never an empty slice.
            for (int n = 1; n <= 4; n++)
            {
                var pace = LyricPaceStatistics.Compute(linesAtWindows(six_windows.Take(n).ToArray()));

                Assert.AreEqual(120.0, pace.TargetWpm, 1e-9, $"{n} line(s)");
            }
        }

        /// <summary>
        /// The pairing the two figures are READ as, and since backlog 274 a property of the fixture
        /// rather than of the arithmetic. While the pool was every counted line a mean over the top
        /// fifth could not sit below the mean over all of them, so this held unconditionally; the
        /// three-word eligibility floor makes the pool a SUBSET, and a fast enough ineligible line
        /// now raises the average without being able to raise the target
        /// (<see cref="AFastTwoWordBurstCannotDefineTheTarget"/> is that map, and it is the pin that
        /// says so out loud). What survives, and what these fixtures hold, is the ordinary case:
        /// where the map's fastest lines clear the floor, the target still sits above the average,
        /// and equality is still exactly the map on which every counted line runs at one rate.
        /// </summary>
        [Test]
        public void TargetIsNeverBelowTheAverageWhenTheFastestLinesAreEligible()
        {
            // Both arms are pinned rather than only the interesting one. Every line of both maps is
            // three words, so the pool is the whole map and the old guarantee applies as it stood.
            //
            // STRICT on a mixed map: 110 against 70 above.
            var mixed = LyricPaceStatistics.Compute(linesAtWindows(six_windows));

            Assert.Greater(mixed.TargetWpm, mixed.AverageWpm);

            // EQUAL on a uniform one, which is the only shape that reaches equality: five lines all
            // at 1000 ms = 300 CPM, so both selections average 300 CPM = 60 WPM.
            var uniform = LyricPaceStatistics.Compute(linesAtWindows(1000, 1000, 1000, 1000, 1000));

            Assert.AreEqual(60.0, uniform.AverageWpm, 1e-9);
            Assert.AreEqual(60.0, uniform.TargetWpm, 1e-9);
            Assert.AreEqual(uniform.AverageWpm, uniform.TargetWpm, 1e-12);
        }

        [Test]
        public void TargetSkipsTheSameLinesTheAverageSkips()
        {
            // The selection pool is EXACTLY the set of lines the average counts. A line with no
            // typeable cell at all ("..." projects to nothing) is skipped by both, so it can neither
            // enter the fastest fifth as a phantom 0 nor widen the count that decides how many
            // lines the fifth is.
            var withEmpty = LyricPaceStatistics.Compute(new[]
            {
                makeLine("a b c", 1000, 1500),
                makeLine("...", 2000, 2100),
                makeLine("a b c", 3000, 4000),
                makeLine("...", 5000, 5100),
                makeLine("a b c", 6000, 7000),
            });

            var withoutEmpty = LyricPaceStatistics.Compute(linesAtWindows(500, 1000, 1000));

            Assert.AreEqual(withoutEmpty.AverageWpm, withEmpty.AverageWpm, 1e-12);
            Assert.AreEqual(withoutEmpty.TargetWpm, withEmpty.TargetWpm, 1e-12);

            // Three counted lines: ceil(0.6) = 1, so the target is the 500 ms line alone at 600 CPM.
            Assert.AreEqual(120.0, withEmpty.TargetWpm, 1e-9);
        }

        /// <summary>
        /// THE FEATURE (backlog 274), and the fixture that shows what it is for. A map of four
        /// ordinary three-word lines with six two-word interjections cut through it: the
        /// interjections are over in half a second each, so they read as the fastest lines on the
        /// map by a distance, and before the floor they WERE the map's target.
        /// </summary>
        [Test]
        public void AFastTwoWordBurstCannotDefineTheTarget()
        {
            // Four eligible lines (three words, 5 cells) at 1000, 1500, 3000 and 3000 ms
            //   -> 300, 200, 100 and 100 CPM
            // Six ineligible bursts (two words, the same 5 cells) at 500 ms -> 600 CPM each.
            //
            //   average  = (300 + 200 + 100 + 100 + 6 * 600) / 10 = 4300 / 10 = 430 CPM = 86 WPM
            //   target   = the fastest ceil(0.20 * 4) = 1 ELIGIBLE line, 300 CPM             = 60 WPM
            //   pre-274  = the fastest ceil(0.20 * 10) = 2 of ALL ten, (600 + 600) / 2
            //                                                        = 600 CPM              = 120 WPM
            //
            // So the floor HALVES this map's target, which is the whole point: 120 WPM was the pace
            // of a two-word shout, and nothing on the map asks a player to hold it.
            var lines = linesAtWindows(1000, 1500, 3000, 3000)
                        .Concat(linesAtWindowsOf(short_line, 500, 500, 500, 500, 500, 500))
                        .ToArray();

            var pace = LyricPaceStatistics.Compute(lines);

            Assert.AreEqual(86.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(60.0, pace.TargetWpm, 1e-9);

            // The pre-274 answer, named rather than implied: revert the floor and this reads 120.
            Assert.AreNotEqual(120.0, pace.TargetWpm);

            // AND THE 272 INVARIANT IS GONE. The bursts are counted by the average and refused by
            // the pool, so here the target sits BELOW the average rather than above it. That is not
            // a defect: the average is diluted upward by lines nobody sustains, and the target is
            // the pace of the map's real lines.
            Assert.Less(pace.TargetWpm, pace.AverageWpm);
        }

        /// <summary>
        /// THE FLOOR ITSELF, at the boundary: three words in, two words out. Two lines at the same
        /// 5 cells, so the only thing separating them is where their spaces are.
        /// </summary>
        [Test]
        public void ThreeWordsAreEligibleAndTwoAreNot()
        {
            // "ab cd" over 500 ms  -> 600 CPM = 120 WPM, two words, REFUSED
            // "a b c" over 1000 ms -> 300 CPM =  60 WPM, three words, SELECTED
            //
            //   average = (600 + 300) / 2 = 450 CPM = 90 WPM
            //   target  = the fastest ceil(0.20 * 1) = 1 eligible line, 300 CPM = 60 WPM
            //
            // At a floor of TWO both lines are eligible and the target reads 120 (the fastest of the
            // two); at a floor of FOUR neither is, the fallback takes every line and the target
            // reads 120 again. So this one number pins the three from both sides.
            var pace = LyricPaceStatistics.Compute(new[]
            {
                makeLine(short_line, 1000, 1500),
                makeLine("a b c", 2000, 3000),
            });

            Assert.AreEqual(90.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(60.0, pace.TargetWpm, 1e-9);
        }

        /// <summary>
        /// THE FALLBACK (backlog 274): a map on which NOTHING clears the floor keeps a target, by
        /// selecting from all of its counted lines exactly as it did before the floor existed.
        /// Filtering to an empty pool would leave such a map with no target at all, and the rule for
        /// a 0 (and so for the server's NULL target_wpm) stays what it was, a map with no COUNTED
        /// line rather than one with no eligible line.
        /// </summary>
        [Test]
        public void AMapOfNothingButShortLinesFallsBackToEveryLine()
        {
            // The same six rates three ways: as three-word lines (the pool is the whole map), as
            // two-word lines and as one-word lines (the pool is empty and the fallback is the whole
            // map). All three read the 110 WPM TargetWpmIsTheMeanOfTheFastestFifthOfTheLines pins,
            // so the fallback really is the pre-274 arithmetic and not an approximation of it.
            var eligible = LyricPaceStatistics.Compute(linesAtWindows(six_windows));
            var twoWord = LyricPaceStatistics.Compute(linesAtWindowsOf(short_line, six_windows));
            var oneWord = LyricPaceStatistics.Compute(linesAtWindowsOf(one_word_line, six_windows));

            Assert.AreEqual(110.0, eligible.TargetWpm, 1e-9);
            Assert.AreEqual(110.0, twoWord.TargetWpm, 1e-9);
            Assert.AreEqual(110.0, oneWord.TargetWpm, 1e-9);

            // The rates are identical too, which is what makes the equality above mean anything: all
            // three texts are 5 cells over the same windows.
            Assert.AreEqual(70.0, twoWord.AverageWpm, 1e-9);
            Assert.AreEqual(70.0, oneWord.AverageWpm, 1e-9);
        }

        [Test]
        public void EmptyMapIsZero()
        {
            var pace = LyricPaceStatistics.Compute(Array.Empty<LyricLine>());

            Assert.AreEqual(0, pace.TypeableCellCount);
            Assert.AreEqual(0, pace.WordCount);
            Assert.AreEqual(0, pace.AverageWpm);
            Assert.AreEqual(0, pace.AverageCpm);

            // No counted line, so no fastest fifth of one either: 0, on the same rule.
            Assert.AreEqual(0, pace.TargetWpm);

            // No words to divide by: 0 rather than a NaN that would render as "NaN" in the wedge.
            Assert.AreEqual(0, pace.AverageCharsPerWord);
        }
    }
}
