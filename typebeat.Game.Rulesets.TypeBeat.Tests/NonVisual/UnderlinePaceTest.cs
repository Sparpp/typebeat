// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 228: the UNDERLINE PACE HUE. The sung-sweep rail is cut into one band per WORD and each
// band is tinted by how fast the playhead crosses it relative to the REST OF THE MAP: neutral
// across the middle half of the distribution, red towards the map's fastest, green towards its
// slowest. Display only, so nothing here is about a judgement, a score, a replay or the wire.
//
// The three rules are pure functions (UnderlinePace.SegmentLine / RanksOf / ColourForRank), which is
// what makes them pinnable without a drawable, exactly as ComputeSpaceErrorDots and
// CorrectCharColour are.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class UnderlinePaceTest
    {
        #region Cell / line builders

        private static TypingCell cell(char expected, bool typeable, double targetTime)
            => new TypingCell(expected, typeable, targetTime, TimingGranularity.Word);

        private static TypingCell letter(char expected, double targetTime) => cell(expected, true, targetTime);

        /// <summary>The inter-word gap: a TYPEABLE space cell, which is what makes it a boundary.</summary>
        private static TypingCell gap(double targetTime) => cell(' ', true, targetTime);

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        /// <summary>A two-word line "aa bb" sung over [<paramref name="start"/>,
        /// <paramref name="start"/> + 2 * <paramref name="wordMs"/>], each word taking
        /// <paramref name="wordMs"/>. Both of its word segments therefore have the same speed,
        /// 2 countable cells over <paramref name="wordMs"/>.</summary>
        private static TypingLine evenLine(double start, double wordMs)
        {
            double singEnd = start + 2 * wordMs;

            return TypingLine.FromLyricLine(new LyricLine
            {
                RawText = "aa bb",
                StartTime = start,
                EndTime = singEnd + 1000,
                SingEndTime = singEnd,
                Units = new[] { unit("aa", start, start + wordMs), unit("bb", start + wordMs, singEnd) },
            }, TimingGranularity.Word);
        }

        /// <summary>
        /// The MAP the distribution tests read: five two-word lines, one much slower than the rest
        /// (a breathy line), three typical, one much faster. Ten segments in total, in three tie
        /// groups of 2 / 6 / 2.
        /// </summary>
        private static TypingLine[] mixedMap() => new[]
        {
            evenLine(0, 10000),      // slow:    2 cells / 10000 ms = 0.0002
            evenLine(30000, 2000),   // typical: 2 / 2000 = 0.001
            evenLine(40000, 2000),
            evenLine(50000, 2000),
            evenLine(60000, 200),    // fast:    2 / 200 = 0.01
        };

        #endregion

        #region Colour measurement (sRGB IEC 61966-2-1 / WCAG 2.x)

        private static double toLinear(double channel)
            => channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

        private static double luminance(Color4 c)
            => 0.2126 * toLinear(c.R) + 0.7152 * toLinear(c.G) + 0.0722 * toLinear(c.B);

        private static double contrast(Color4 a, Color4 b)
        {
            double la = luminance(a);
            double lb = luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        #endregion

        #region The colour ramp

        [Test]
        public void TheNeutralBandIsExactlyThePreTaskRail()
        {
            // The single flat track this feature replaced was SungAccent at 20%. Half of every map
            // renders in the neutral band, and a uniformly paced map renders entirely in it, so this
            // equality is what makes the feature invisible where it has nothing to say.
            Assert.That(UnderlinePace.NeutralColour, Is.EqualTo(new Color4(
                TypeBeatStyle.SungAccent.R, TypeBeatStyle.SungAccent.G, TypeBeatStyle.SungAccent.B, 0.20f)));

            Assert.That(UnderlinePace.NEUTRAL_ALPHA, Is.EqualTo(0.20f));
        }

        [Test]
        public void TheWholeNeutralBufferIsNeutralInclusiveOfBothEdges()
        {
            // Exactly the specified rule: 25th to 75th percentile triggers no hue AT ALL. The two
            // edges are asserted first and by name, because an exclusive comparison at either end is
            // the easy way to get this subtly wrong.
            Assert.That(UnderlinePace.ColourForRank(UnderlinePace.NEUTRAL_LO_RANK), Is.EqualTo(UnderlinePace.NeutralColour));
            Assert.That(UnderlinePace.ColourForRank(UnderlinePace.NEUTRAL_HI_RANK), Is.EqualTo(UnderlinePace.NeutralColour));

            for (int step = 25; step <= 75; step++)
            {
                Assert.That(UnderlinePace.ColourForRank(step / 100.0), Is.EqualTo(UnderlinePace.NeutralColour),
                    $"rank {step / 100.0} must take no hue");
            }

            // And nothing just outside it is neutral, or the buffer would silently be wider than the
            // quartiles the rule is stated in.
            Assert.That(UnderlinePace.ColourForRank(0.2499), Is.Not.EqualTo(UnderlinePace.NeutralColour));
            Assert.That(UnderlinePace.ColourForRank(0.7501), Is.Not.EqualTo(UnderlinePace.NeutralColour));
        }

        [Test]
        public void BothEndsAreExactlyTheirAnchorColours()
        {
            // Exactness at the ends is a contract for the same reason CorrectCharColour's is: a
            // componentwise lerp at t = 1 is only float-approximately the end colour.
            Assert.That(UnderlinePace.ColourForRank(1), Is.EqualTo(new Color4(
                TypeBeatStyle.ErrorChar.R, TypeBeatStyle.ErrorChar.G, TypeBeatStyle.ErrorChar.B, UnderlinePace.HUED_ALPHA)));

            Assert.That(UnderlinePace.ColourForRank(0), Is.EqualTo(new Color4(
                TypeBeatStyle.PaceSlowAccent.R, TypeBeatStyle.PaceSlowAccent.G, TypeBeatStyle.PaceSlowAccent.B, UnderlinePace.HUED_ALPHA)));
        }

        [Test]
        public void TheFastRampClimbsMonotonicallyFromTheBufferToTheReddestEnd()
        {
            var previous = UnderlinePace.ColourForRank(UnderlinePace.NEUTRAL_HI_RANK);

            for (int step = 1; step <= 25; step++)
            {
                var current = UnderlinePace.ColourForRank(UnderlinePace.NEUTRAL_HI_RANK + step / 100.0);

                Assert.That(current.R, Is.GreaterThan(previous.R), $"red at +{step}");
                Assert.That(current.G, Is.LessThan(previous.G), $"green at +{step}");
                Assert.That(current.B, Is.LessThan(previous.B), $"blue at +{step}");
                Assert.That(current.A, Is.GreaterThan(previous.A), $"alpha at +{step}");

                previous = current;
            }

            Assert.That(previous, Is.EqualTo(UnderlinePace.ColourForRank(1)));
        }

        [Test]
        public void TheSlowRampClimbsMonotonicallyFromTheBufferToTheGreenestEnd()
        {
            var previous = UnderlinePace.ColourForRank(UnderlinePace.NEUTRAL_LO_RANK);

            for (int step = 1; step <= 25; step++)
            {
                var current = UnderlinePace.ColourForRank(UnderlinePace.NEUTRAL_LO_RANK - step / 100.0);

                Assert.That(current.G, Is.GreaterThan(previous.G), $"green at -{step}");
                Assert.That(current.B, Is.LessThan(previous.B), $"blue at -{step}");
                Assert.That(current.R, Is.LessThan(previous.R), $"red at -{step}");
                Assert.That(current.A, Is.GreaterThan(previous.A), $"alpha at -{step}");

                previous = current;
            }

            Assert.That(previous, Is.EqualTo(UnderlinePace.ColourForRank(0)));
        }

        [Test]
        public void TheHuesStaySubtleEnoughForGreyToRemainTheDefault()
        {
            // Every band this rule can produce sits inside the rail's own weight class: the neutral
            // alpha at one end, a third at the other, and nothing beyond either.
            for (int step = 0; step <= 100; step++)
            {
                float alpha = UnderlinePace.ColourForRank(step / 100.0).A;

                Assert.That(alpha, Is.GreaterThanOrEqualTo(UnderlinePace.NEUTRAL_ALPHA).And.LessThanOrEqualTo(UnderlinePace.HUED_ALPHA),
                    $"rank {step / 100.0} left the rail's alpha regime");
            }

            Assert.That(UnderlinePace.HUED_ALPHA, Is.GreaterThan(UnderlinePace.NEUTRAL_ALPHA).And.LessThan(0.5f),
                "the loudest band must still be a faint rail, not a highlight");
        }

        [Test]
        public void TheSlowAnchorKeepsTheRailsWeightWhileClearingTheFastAnchor()
        {
            // The green must be a HUE rotation of the rail, not a brighter rail: a luminance step
            // would make a slow passage read as a louder underline rather than a greener one.
            Assert.That(contrast(TypeBeatStyle.PaceSlowAccent, TypeBeatStyle.SungAccent), Is.LessThan(1.2),
                "the slow anchor must sit at effectively the rail's own luminance");

            // Separation from the rail is carried by HUE instead, and by a large step of it: the
            // blue channel is more than halved while the green rises, which is what turns the rail's
            // cyan into an unambiguous green rather than a slightly different blue.
            Assert.That(TypeBeatStyle.PaceSlowAccent.B, Is.LessThan(TypeBeatStyle.SungAccent.B * 0.6f));
            Assert.That(TypeBeatStyle.PaceSlowAccent.G, Is.GreaterThan(TypeBeatStyle.SungAccent.G));
            Assert.That(TypeBeatStyle.PaceSlowAccent.G, Is.GreaterThan(TypeBeatStyle.PaceSlowAccent.B));

            // And the two ENDS of the ramp can never be confused with one another.
            Assert.That(contrast(TypeBeatStyle.PaceSlowAccent, TypeBeatStyle.ErrorChar), Is.GreaterThan(2.0),
                "the slow and fast anchors must be plainly separable");
            Assert.That(TypeBeatStyle.ErrorChar.R, Is.GreaterThan(TypeBeatStyle.ErrorChar.G));
            Assert.That(TypeBeatStyle.PaceSlowAccent.G, Is.GreaterThan(TypeBeatStyle.PaceSlowAccent.R));
        }

        [Test]
        public void OutOfRangeAndNanClamp()
        {
            Assert.That(UnderlinePace.ColourForRank(-0.5), Is.EqualTo(UnderlinePace.ColourForRank(0)));
            Assert.That(UnderlinePace.ColourForRank(double.NegativeInfinity), Is.EqualTo(UnderlinePace.ColourForRank(0)));
            Assert.That(UnderlinePace.ColourForRank(1.5), Is.EqualTo(UnderlinePace.ColourForRank(1)));
            Assert.That(UnderlinePace.ColourForRank(double.PositiveInfinity), Is.EqualTo(UnderlinePace.ColourForRank(1)));

            // NaN fails every comparison, so Math.Clamp alone would pass it into the interpolation
            // and out as a NaN colour. It falls to the middle of the neutral buffer instead.
            Assert.That(UnderlinePace.ColourForRank(double.NaN), Is.EqualTo(UnderlinePace.NeutralColour));
        }

        #endregion

        #region Percentile ranks

        [Test]
        public void AnEmptyOrSingleSegmentDistributionIsNeutral()
        {
            Assert.That(UnderlinePace.RanksOf(Array.Empty<double>()), Is.Empty);

            double[] one = UnderlinePace.RanksOf(new[] { 0.004 });

            Assert.That(one, Has.Length.EqualTo(1));
            Assert.That(one[0], Is.EqualTo(0.5));
            Assert.That(UnderlinePace.ColourForRank(one[0]), Is.EqualTo(UnderlinePace.NeutralColour));
        }

        [Test]
        public void EveryEqualSpeedSharesOneRankSoAUniformMapIsFullyNeutral()
        {
            // The line-granularity case, and the reason the rank is a MID-rank rather than a sorted
            // position: spreading equal speeds from 0 to 1 by the accident of their order would hue
            // a map that has nothing to say.
            double[] ranks = UnderlinePace.RanksOf(Enumerable.Repeat(0.003, 7).ToArray());

            Assert.That(ranks, Is.All.EqualTo(0.5));
            Assert.That(ranks.Select(UnderlinePace.ColourForRank), Is.All.EqualTo(UnderlinePace.NeutralColour));
        }

        [Test]
        public void RanksAreTheMidRankOfEachTieGroup()
        {
            // Cross-check, worked by hand: three tie groups of 2 / 6 / 2 over ten segments give
            // (0 + 2) / 2 / 10 = 0.1, (2 + 8) / 2 / 10 = 0.5 and (8 + 10) / 2 / 10 = 0.9.
            var speeds = new List<double>();
            speeds.AddRange(Enumerable.Repeat(0.0002, 2));
            speeds.AddRange(Enumerable.Repeat(0.001, 6));
            speeds.AddRange(Enumerable.Repeat(0.01, 2));

            double[] ranks = UnderlinePace.RanksOf(speeds);

            Assert.That(ranks.Take(2), Is.All.EqualTo(0.1));
            Assert.That(ranks.Skip(2).Take(6), Is.All.EqualTo(0.5));
            Assert.That(ranks.Skip(8), Is.All.EqualTo(0.9));

            // Input order must not matter, only value.
            double[] shuffled = UnderlinePace.RanksOf(new[] { 0.01, 0.0002, 0.001, 0.001, 0.001, 0.01, 0.001, 0.0002, 0.001, 0.001 });

            Assert.That(shuffled[0], Is.EqualTo(0.9));
            Assert.That(shuffled[1], Is.EqualTo(0.1));
            Assert.That(shuffled[2], Is.EqualTo(0.5));
        }

        [Test]
        public void TheRampIsLinearInRankAndNotInSpeed()
        {
            // THE decision the spec singles out. One absurd burst must not compress everything
            // else's colour, so replacing the top speed with a thousand times itself changes no
            // other segment's colour at all.
            var ordinary = new[] { 0.001, 0.002, 0.003, 0.004, 0.005, 0.006, 0.007, 0.008 };
            var absurd = ordinary.ToArray();
            absurd[7] = 12.0;

            Assert.That(UnderlinePace.RanksOf(absurd), Is.EqualTo(UnderlinePace.RanksOf(ordinary)));

            // The complement: a segment nowhere near the middle of the SPEED range is still neutral
            // when it is in the middle of the RANK order. 2 sits 1% of the way from 1 to 100.
            double[] ranks = UnderlinePace.RanksOf(new[] { 1.0, 2.0, 100.0 });

            Assert.That(ranks[1], Is.EqualTo(0.5));
            Assert.That(UnderlinePace.ColourForRank(ranks[1]), Is.EqualTo(UnderlinePace.NeutralColour));

            // And the ramp really is linear in rank: equal rank steps are equal ramp steps. Asserted
            // on the ALPHA, which this file lifts linearly itself; the hue walks the framework's
            // colour ramp, which interpolates in LINEAR light, so its sRGB channels are deliberately
            // not linear in t (the same is true of the sync tint, see SyncTintTest).
            float a = UnderlinePace.ColourForRank(0.80).A;
            float b = UnderlinePace.ColourForRank(0.85).A;
            float c = UnderlinePace.ColourForRank(0.90).A;

            Assert.That(b - a, Is.EqualTo(c - b).Within(1e-6f));
            Assert.That(b - a, Is.GreaterThan(0));
        }

        #endregion

        #region Word segmentation

        private static void assertRanges(IReadOnlyList<PaceSegment> segments, params (int start, int end)[] expected)
        {
            Assert.That(segments.Count, Is.EqualTo(expected.Length), "segment count");

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That((segments[i].StartCell, segments[i].EndCellExclusive), Is.EqualTo(expected[i]),
                    $"segment {i}");
            }
        }

        /// <summary>"ab cd": the gap belongs to the word BEFORE it, so the first segment runs a-b-gap
        /// and the second starts at 'c'.</summary>
        [Test]
        public void TheTrailingGapBelongsToTheWordItCloses()
        {
            var cells = new[] { letter('a', 0), letter('b', 100), gap(200), letter('c', 1200), letter('d', 1300) };

            assertRanges(UnderlinePace.SegmentLine(cells, 1400), (0, 3), (3, 5));
        }

        [Test]
        public void ALineWithNoGapIsOneSegmentAndAnEmptyLineIsNone()
        {
            assertRanges(UnderlinePace.SegmentLine(new[] { letter('a', 0), letter('b', 500) }, 1000), (0, 2));
            Assert.That(UnderlinePace.SegmentLine(Array.Empty<TypingCell>(), 1000), Is.Empty);
        }

        [Test]
        public void ALeadingGapIsItsOwnSegmentAndATrailingOneStaysInTheLastWord()
        {
            // A gap first: it closes an empty word, so it is a segment of its own and 'a' opens the
            // next one. Nothing is dropped, which is what keeps the bands tiling the line.
            assertRanges(UnderlinePace.SegmentLine(new[] { gap(0), letter('a', 100), letter('b', 200) }, 400), (0, 1), (1, 3));

            // A gap LAST opens no segment, so the line does not gain an empty band past its end.
            assertRanges(UnderlinePace.SegmentLine(new[] { letter('a', 0), letter('b', 100), gap(200) }, 400), (0, 3));
        }

        [Test]
        public void SegmentsAlwaysTileTheWholeLine()
        {
            var lines = new[]
            {
                new[] { letter('a', 0), gap(50), letter('b', 100), gap(150), letter('c', 200) },
                new[] { gap(0), gap(10), letter('a', 20) },
                new[] { letter('a', 0) },
            };

            foreach (var cells in lines)
            {
                var segments = UnderlinePace.SegmentLine(cells, 1000);

                Assert.That(segments[0].StartCell, Is.EqualTo(0), "no hole at the line start");
                Assert.That(segments[^1].EndCellExclusive, Is.EqualTo(cells.Length), "no hole at the line end");

                for (int i = 1; i < segments.Length; i++)
                    Assert.That(segments[i].StartCell, Is.EqualTo(segments[i - 1].EndCellExclusive), "no hole in the middle");
            }
        }

        /// <summary>
        /// The metric itself: COUNTABLE cells over the segment's full span INCLUDING the breath that
        /// follows it, which is what makes a long gap read as slow.
        /// </summary>
        [Test]
        public void TheSpanIncludesTheBreathSoALongGapReadsAsSlow()
        {
            // "ab cd" with a whole second of silence after "ab": a0 b100 _200 c1200 d1300, sung
            // through to 1400. Cross-checks, worked by hand:
            //   segment 0 = cells [0, 3), 2 countable, span 1200 - 0    -> 2 / 1200
            //   segment 1 = cells [3, 5), 2 countable, span 1400 - 1200 -> 2 / 200
            var breathy = UnderlinePace.SegmentLine(
                new[] { letter('a', 0), letter('b', 100), gap(200), letter('c', 1200), letter('d', 1300) }, 1400);

            Assert.That(breathy[0].Speed, Is.EqualTo(2 / 1200.0).Within(1e-12));
            Assert.That(breathy[1].Speed, Is.EqualTo(2 / 200.0).Within(1e-12));

            // The same word with no breath after it is six times faster, and the only thing that
            // moved is the gap's own time: the character count is identical.
            var tight = UnderlinePace.SegmentLine(
                new[] { letter('a', 0), letter('b', 100), gap(150), letter('c', 200), letter('d', 300) }, 400);

            Assert.That(tight[0].Speed, Is.EqualTo(2 / 200.0).Within(1e-12));
            Assert.That(tight[0].Speed, Is.GreaterThan(breathy[0].Speed));
        }

        [Test]
        public void OnlyCountableCellsAreCountedThoughEveryCellsTimeIsSpanned()
        {
            // The gap is typeable and spends time, but it is not a countable character, so "ab cd"
            // prices its first word at 2 characters and not 3.
            var segments = UnderlinePace.SegmentLine(
                new[] { letter('a', 0), letter('b', 100), gap(200), letter('c', 400), letter('d', 500) }, 600);

            Assert.That(segments[0].Speed, Is.EqualTo(2 / 400.0).Within(1e-12));

            // Punctuation the Literate stream keeps is not typeable at all, so it is not counted
            // either, and it does not open a segment.
            var punctuated = UnderlinePace.SegmentLine(
                new[] { letter('a', 0), cell(',', false, 100), gap(200), letter('c', 400) }, 600);

            assertRanges(punctuated, (0, 3), (3, 4));
            Assert.That(punctuated[0].Speed, Is.EqualTo(1 / 400.0).Within(1e-12));
        }

        [Test]
        public void ADegenerateSpanFallsToTheFloorRatherThanDividingByZero()
        {
            // Every target collapsed onto one instant (a hand-edited timing file, a zero-length
            // line): the speed must be finite and deterministic, not infinite.
            var collapsed = UnderlinePace.SegmentLine(
                new[] { letter('a', 1000), letter('b', 1000), gap(1000), letter('c', 1000) }, 1000);

            Assert.That(collapsed[0].Speed, Is.EqualTo(2 / UnderlinePace.MIN_SEGMENT_SPAN_MS).Within(1e-12));
            Assert.That(collapsed[1].Speed, Is.EqualTo(1 / UnderlinePace.MIN_SEGMENT_SPAN_MS).Within(1e-12));
            Assert.That(collapsed.All(s => double.IsFinite(s.Speed)));

            // A BACKWARDS span (a sing end before the line's own last character) takes the same
            // floor rather than a negative speed, which would sort below every honest segment.
            var backwards = UnderlinePace.SegmentLine(new[] { letter('a', 5000), letter('b', 6000) }, 0);

            Assert.That(backwards[0].Speed, Is.EqualTo(2 / UnderlinePace.MIN_SEGMENT_SPAN_MS).Within(1e-12));

            // And a NaN target, which no comparison would catch, lands on the floor too.
            var nan = UnderlinePace.SegmentLine(new[] { letter('a', double.NaN), letter('b', 100) }, 200);

            Assert.That(double.IsFinite(nan[0].Speed));
        }

        /// <summary>
        /// "ab cd" with word blocks ab [1000, 2000] and cd [2000, 3400], carrying a sung-end flag
        /// (end_ms, what the editor's blue marker drags) chosen INDEPENDENTLY of them. Freshly
        /// parsed data always has the two equal, so only an editor drag produces this.
        /// </summary>
        private static TypingLine flagLine(double singEnd) => TypingLine.FromLyricLine(new LyricLine
        {
            RawText = "ab cd",
            StartTime = 1000,
            EndTime = 9000,
            SingEndTime = singEnd,
            Units = new[] { unit("ab", 1000, 2000), unit("cd", 2000, 3400) },
        }, TimingGranularity.Word);

        /// <summary>
        /// Backlog 245: the last word is priced by its OWN duration, not by the line's sung-end
        /// flag. Every other segment closes on the next segment's first vocal target, a time inside
        /// the next word block, so pricing the last one by a flag the mapper drags independently
        /// made it the one band whose hue moved without any word moving.
        /// </summary>
        [Test]
        public void DraggingTheSungEndFlagDoesNotRepriceTheLastWord()
        {
            // The flag a parser would emit (cd's own end), one dragged well PAST it, one dragged
            // well BEFORE it but still after cd's last character (2700), so the "never before the
            // last target" guard is not what is holding the line.
            var lines = new[] { flagLine(3400), flagLine(6000), flagLine(2900) };

            foreach (var line in lines)
            {
                Assert.That(UnderlinePace.SungEndOf(line), Is.EqualTo(3400), "closed on cd's own end");

                var segments = UnderlinePace.SegmentLine(line);
                assertRanges(segments, (0, 3), (3, 5));

                // Hand-worked. Segment 0 is "ab" plus its gap, 2 countable over [1000, 2000), the
                // interior control: it was always bounded by its own block and still is.
                Assert.That(segments[0].Speed, Is.EqualTo(2 / 1000.0).Within(1e-12));

                // Segment 1 is "cd", 2 countable over [2000, 3400), the word's own 1400ms.
                Assert.That(segments[1].Speed, Is.EqualTo(2 / 1400.0).Within(1e-12));
            }

            // And the RANK inside a whole map's percentiles, which is what actually picks the hue:
            // twelve segments, the ten of mixedMap plus this line's two. Sorted, 0.0002 x2,
            // 0.001 x6, 2/1400, 2/1000, 0.01 x2, so the last word's mid-rank is (8 + 9)/2/12 and
            // the interior control's is (9 + 10)/2/12. Neither is the neutral 0.5, so the
            // invariance below is not vacuous.
            foreach (var line in lines)
            {
                var map = mixedMap().Append(line).ToArray();
                double[] ranks = UnderlinePace.RanksOf(map.SelectMany(UnderlinePace.SegmentLine).Select(s => s.Speed).ToArray());

                Assert.That(ranks, Has.Length.EqualTo(12));
                Assert.That(ranks[11], Is.EqualTo(8.5 / 12).Within(1e-12), "the last word's rank");
                Assert.That(ranks[10], Is.EqualTo(9.5 / 12).Within(1e-12), "the interior control's rank");

                Assert.That(UnderlinePace.BuildBands(map)[5], Is.EqualTo(UnderlinePace.BuildBands(mixedMap().Append(lines[0]).ToArray())[5]),
                    "the bands the display renders");
            }
        }

        [Test]
        public void ALineIsClosedAtItsOwnSungEndNeverBeforeItsLastCharacter()
        {
            var line = evenLine(0, 2000);

            // Degenerate on purpose (the fixture's flag equals its last unit's end, as a parser's
            // always does): DraggingTheSungEndFlagDoesNotRepriceTheLastWord is the case that can
            // tell the flag and the last word's own end apart.
            Assert.That(UnderlinePace.SungEndOf(line), Is.EqualTo(line.SingEndTime));

            // A line whose declared sing end sits before its last target is closed at that target
            // instead, matching the last anchor of TypingLine's own sung polyline.
            var late = TypingLine.FromLyricLine(new LyricLine
            {
                RawText = "ab",
                StartTime = 0,
                EndTime = 9000,
                SingEndTime = 100,
                Units = new[] { unit("ab", 0, 4000) },
            }, TimingGranularity.Word);

            Assert.That(UnderlinePace.SungEndOf(late), Is.GreaterThanOrEqualTo(late.Cells[^1].TargetTime));
        }

        #endregion

        #region The distribution is the whole map's, not one line's

        [Test]
        public void TheMapsFixtureHasThePaceItClaimsTo()
        {
            // Grounding for the two tests below, so a failure there is about the RULE rather than
            // about the fixture drifting: 2 slow segments, 6 typical, 2 fast.
            double[] speeds = mixedMap().SelectMany(UnderlinePace.SegmentLine).Select(s => s.Speed).ToArray();

            Assert.That(speeds, Has.Length.EqualTo(10));
            Assert.That(speeds.Take(2), Is.All.EqualTo(2 / 10000.0).Within(1e-12));
            Assert.That(speeds.Skip(2).Take(6), Is.All.EqualTo(2 / 2000.0).Within(1e-12));
            Assert.That(speeds.Skip(8), Is.All.EqualTo(2 / 200.0).Within(1e-12));
        }

        /// <summary>
        /// THE DECIDED DISTRIBUTION BASIS. A line that is uniformly fast is fast RELATIVE TO THE MAP
        /// and must glow red; per-line percentiles were rejected precisely because every segment of
        /// such a line is typical of it, so it would grey out.
        /// </summary>
        [Test]
        public void AUniformlyFastLineStillRedsBecauseThePercentilesAreTheMapsOwn()
        {
            var lines = mixedMap();
            var bands = UnderlinePace.BuildBands(lines);

            // The fast line: both of its bands are on the red side of the buffer.
            foreach (var band in bands[4])
            {
                Assert.That(band.Colour.R, Is.GreaterThan(UnderlinePace.NeutralColour.R), "the fast line must red");
                Assert.That(band.Colour.B, Is.LessThan(UnderlinePace.NeutralColour.B));
            }

            // The breathy line: both of its bands are on the green side.
            foreach (var band in bands[0])
            {
                Assert.That(band.Colour.G, Is.GreaterThan(UnderlinePace.NeutralColour.G), "the slow line must green");
                Assert.That(band.Colour.B, Is.LessThan(UnderlinePace.NeutralColour.B));
            }

            // The typical middle: untouched, exactly the rail that shipped before this feature.
            foreach (var line in new[] { 1, 2, 3 })
                Assert.That(bands[line].Select(b => b.Colour), Is.All.EqualTo(UnderlinePace.NeutralColour));

            // And the counterfactual, which is what makes the choice load-bearing rather than
            // incidental: ranked WITHIN its own line, the fast line's segments are all equal, so
            // every one of them would land at 0.5 and take no hue at all.
            double[] perLine = UnderlinePace.RanksOf(UnderlinePace.SegmentLine(lines[4]).Select(s => s.Speed).ToArray());

            Assert.That(perLine, Is.All.EqualTo(0.5));
            Assert.That(perLine.Select(UnderlinePace.ColourForRank), Is.All.EqualTo(UnderlinePace.NeutralColour),
                "per-line percentiles would grey out the very line this feature exists to mark");
        }

        [Test]
        public void BandsCoverEveryLineWithTheSegmentsTheyWereBuiltFrom()
        {
            var lines = mixedMap();
            var bands = UnderlinePace.BuildBands(lines);

            Assert.That(bands, Has.Length.EqualTo(lines.Length));

            for (int k = 0; k < lines.Length; k++)
            {
                var segments = UnderlinePace.SegmentLine(lines[k]);

                Assert.That(bands[k], Has.Length.EqualTo(segments.Length));
                Assert.That(bands[k][0].StartCell, Is.EqualTo(0));
                Assert.That(bands[k][^1].EndCellExclusive, Is.EqualTo(lines[k].Cells.Count));

                for (int j = 0; j < segments.Length; j++)
                {
                    Assert.That(bands[k][j].StartCell, Is.EqualTo(segments[j].StartCell));
                    Assert.That(bands[k][j].EndCellExclusive, Is.EqualTo(segments[j].EndCellExclusive));
                }
            }
        }

        #endregion

        #region Rate invariance

        /// <summary>
        /// The structural half, and the real proof: a rate mod has no seam through which it could
        /// rewrite a <see cref="TypingCell.TargetTime"/>. It touches the CLOCK and the window ladder
        /// and nothing else, so if one ever grew a beatmap-applying interface this fails loudly
        /// rather than the hues quietly diverging between a modded and an unmodded view of one map.
        /// </summary>
        [Test]
        public void NoRateModHasASeamThatCouldMoveACharacterTarget()
        {
            var rateMods = new TypeBeatRuleset().AllMods.OfType<ModRateAdjust>().ToList();

            Assert.That(rateMods, Has.Count.EqualTo(3), "Double Time, Nightcore and Half Time");

            foreach (var mod in rateMods)
            {
                Assert.That(mod, Is.Not.InstanceOf<IApplicableToBeatmap>(), $"{mod.Acronym}");
                Assert.That(mod, Is.Not.InstanceOf<IApplicableAfterBeatmapConversion>(), $"{mod.Acronym}");
                Assert.That(mod, Is.Not.InstanceOf<IApplicableToHitObject>(), $"{mod.Acronym}");
            }
        }

        /// <summary>
        /// The behavioural half: applying what a rate mod actually does to this ruleset (multiply
        /// <see cref="TypingEngine.WindowScale"/>, which is the whole of
        /// <c>TypeBeatModDoubleTime.ApplyToDrawableRuleset</c>) leaves every target, every segment
        /// speed and therefore every band colour byte-identical.
        /// </summary>
        [Test]
        public void RateModsLeaveEveryBandColourByteIdentical()
        {
            var beatmap = new LyricBeatmap
            {
                Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Pace", FolderPath = string.Empty, AudioFileName = "a.mp3" },
                Lines = new[]
                {
                    lyric("aa bb", 0, 20000),
                    lyric("aa bb", 30000, 34000),
                    lyric("aa bb", 40000, 44000),
                    lyric("aa bb", 50000, 54000),
                    lyric("aa bb", 60000, 60400),
                },
                Granularity = TimingGranularity.Word,
            };

            var engine = new TypingEngine(beatmap);

            double[] targets = engine.Lines.SelectMany(l => l.Cells).Select(c => c.TargetTime).ToArray();
            var plain = UnderlinePace.BuildBands(engine.Lines);

            foreach (var mod in new ModRateAdjust[] { new TypeBeatModDoubleTime(), new TypeBeatModHalfTime(), new TypeBeatModNightcore() })
            {
                mod.SpeedChange.Value = mod is TypeBeatModHalfTime ? 0.5 : 2.0;
                engine.WindowScale = mod.SpeedChange.Value;

                Assert.That(engine.Lines.SelectMany(l => l.Cells).Select(c => c.TargetTime).ToArray(), Is.EqualTo(targets),
                    $"{mod.Acronym} moved a character target");

                var modded = UnderlinePace.BuildBands(engine.Lines);

                for (int k = 0; k < plain.Length; k++)
                    Assert.That(modded[k], Is.EqualTo(plain[k]), $"{mod.Acronym} changed line {k}'s hues");
            }

            // Sanity: the fixture really does hue, so the equalities above are not vacuous.
            Assert.That(plain[4].Select(b => b.Colour), Is.All.Not.EqualTo(UnderlinePace.NeutralColour));
        }

        private static LyricLine lyric(string text, double start, double singEnd)
        {
            double mid = (start + singEnd) / 2;

            return new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = singEnd + 1000,
                SingEndTime = singEnd,
                Units = new[] { unit("aa", start, mid), unit("bb", mid, singEnd) },
            };
        }

        #endregion
    }
}
