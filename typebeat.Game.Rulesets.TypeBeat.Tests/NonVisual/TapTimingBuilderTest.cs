// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The pure heart of tap timing: taps in, a whole new sheet out, one shot, no mutation.
    /// Everything the editor's "Time" button can produce is decided here, so this fixture pins the
    /// rules the mapper will feel: what a tap means, where word ends come from, what happens on a
    /// completely untimed sheet, when only a middle section is retimed, when the taps collide with
    /// content outside the pass, and when the mapper finishes early.
    /// </summary>
    [TestFixture]
    public class TapTimingBuilderTest
    {
        private const double tail = TypeBeatEditorOperations.LAST_LINE_TAIL_MS;

        private static LyricLine line(string text, double start, double end, double singEnd, params (double s, double e)[] words)
        {
            string[] tokens = text.Split(' ');

            return new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = words.Select((w, i) => new TimedUnit
                {
                    Text = tokens[i],
                    StartTime = w.s,
                    EndTime = w.e,
                    Source = TimingSource.Explicit,
                }).ToArray(),
            };
        }

        /// <summary>Copy of <paramref name="source"/> with word <paramref name="unitIndex"/> subdivided at <paramref name="boundaries"/>.</summary>
        private static LyricLine subdivide(LyricLine source, int unitIndex, params double[] boundaries)
        {
            var units = source.Units.Select((u, i) => i != unitIndex
                ? u
                : new TimedUnit
                {
                    Text = u.Text,
                    StartTime = u.StartTime,
                    EndTime = u.EndTime,
                    Source = u.Source,
                    Confidence = u.Confidence,
                    SyllableBoundaries = boundaries,
                }).ToArray();

            return new LyricLine
            {
                RawText = source.RawText,
                StartTime = source.StartTime,
                EndTime = source.EndTime,
                SingEndTime = source.SingEndTime,
                Units = units,
                SealGraceMs = source.SealGraceMs,
                Estimated = source.Estimated,
            };
        }

        /// <summary>
        /// The user's own example: "remember me", where remember carries two subdivisions and so
        /// asks for THREE taps, making four for the line. Followed by an ordinary undivided line.
        /// </summary>
        private static List<LyricLine> subdividedSheet() => new List<LyricLine>
        {
            subdivide(line("remember me", 1000, 3000, 2800, (1000, 1800), (1900, 2800)), 0, 1300, 1550),
            line("hold on", 3000, 6000, 5500, (3000, 4200), (4300, 5500)),
        };

        /// <summary>Three fully timed lines of two words each: [1000..3000], [3000..6000], [6000..8000].</summary>
        private static List<LyricLine> timedSheet() => new List<LyricLine>
        {
            line("alpha beta", 1000, 3000, 2800, (1000, 1800), (1900, 2800)),
            line("gamma delta", 3000, 6000, 5500, (3000, 4200), (4300, 5500)),
            line("eta theta", 6000, 8000, 7800, (6000, 6800), (6900, 7800)),
        };

        /// <summary>
        /// A freshly pasted sheet: real words, timing that means nothing (everything crammed into
        /// the first couple of seconds, as a text-only import produces).
        /// </summary>
        private static List<LyricLine> untimedSheet() => new List<LyricLine>
        {
            line("one two", 0, 1000, 900, (0, 450), (450, 900)),
            line("three four", 1000, 2000, 1900, (1000, 1450), (1450, 1900)),
        };

        [Test]
        public void TestUntimedSheetTimedEndToEnd()
        {
            var lines = untimedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines);

            Assert.That(queue, Is.EqualTo(new[]
            {
                new TapTarget(0, 0), new TapTarget(0, 1), new TapTarget(1, 0), new TapTarget(1, 1),
            }));

            var built = TapTimingBuilder.Build(lines, queue, new[] { 5000d, 5500, 6200, 6800 });

            // Each tap is a word START; inside a line a word runs until the next one.
            Assert.That(built[0].Units.Select(u => (u.StartTime, u.EndTime)),
                Is.EqualTo(new[] { (5000d, 5500d), (5500d, 6000d) }));

            // The line's LAST word takes its default width instead of running to the next line:
            // "one" (3 chars) was given 500ms, so 166.67ms a character, and "two" is also 3 chars,
            // so the 200ms gap before the next line survives instead of being sung through.
            Assert.That(built[0].SingEndTime, Is.EqualTo(6000));

            // Boundary invariant: EndTime_i == StartTime_(i+1).
            Assert.That(built[0].StartTime, Is.EqualTo(5000));
            Assert.That(built[0].EndTime, Is.EqualTo(6200));
            Assert.That(built[1].StartTime, Is.EqualTo(6200));

            Assert.That(built[1].Units.Select(u => (u.StartTime, u.EndTime)),
                Is.EqualTo(new[] { (6200d, 6800d), (6800d, 7280d) }));

            // The very last word has no next tap, so it takes its DEFAULT WIDTH at the line's own
            // measured rate: "three" (5 chars) was given 600ms, so 120ms a character, and "four"
            // (4 chars) is 480ms. A flat mean would have given the shorter word the longer one's
            // 600ms; the width scales with what the player has to type.
            Assert.That(built[1].SingEndTime, Is.EqualTo(7280));
            Assert.That(built[1].EndTime, Is.EqualTo(7280 + tail));

            Assert.That(built.SelectMany(l => l.Units).All(u => u.Source == TimingSource.Explicit), Is.True,
                "tapped words are hand timing");
            Assert.That(built.All(l => !l.Estimated), Is.True, "a fully tapped sheet is not estimated");

            // Purity: the input sheet is untouched.
            Assert.That(lines[0].StartTime, Is.EqualTo(0));
            Assert.That(lines[0].Units[0].StartTime, Is.EqualTo(0));
        }

        [Test]
        public void TestNoTapsChangesNothing()
        {
            var lines = timedSheet();
            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines), System.Array.Empty<double>());

            Assert.That(built.Select(l => (l.StartTime, l.EndTime, l.SingEndTime)),
                Is.EqualTo(lines.Select(l => (l.StartTime, l.EndTime, l.SingEndTime))));
        }

        [Test]
        public void TestMiddleSectionBetweenTimedNeighbours()
        {
            var lines = timedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines, 1, 0, 1, 1);

            var built = TapTimingBuilder.Build(lines, queue, new[] { 3500d, 4200 });

            // The section moves...
            Assert.That(built[1].StartTime, Is.EqualTo(3500));
            Assert.That(built[1].Units.Select(u => (u.StartTime, u.EndTime)),
                Is.EqualTo(new[] { (3500d, 4200d), (4200d, 4900d) }));
            Assert.That(built[1].SingEndTime, Is.EqualTo(4900));

            // ...the line BEFORE it keeps its own timing, only its window end follows the boundary...
            Assert.That(built[0].StartTime, Is.EqualTo(1000));
            Assert.That(built[0].Units.Select(u => (u.StartTime, u.EndTime)),
                Is.EqualTo(new[] { (1000d, 1800d), (1900d, 2800d) }));
            Assert.That(built[0].EndTime, Is.EqualTo(3500));

            // ...and the line AFTER it is left exactly where it was (no collision to resolve).
            Assert.That(built[2].StartTime, Is.EqualTo(6000));
            Assert.That(built[2].EndTime, Is.EqualTo(8000));
            Assert.That(built[2].Units.Select(u => (u.StartTime, u.EndTime)),
                Is.EqualTo(new[] { (6000d, 6800d), (6900d, 7800d) }));
            Assert.That(built[1].EndTime, Is.EqualTo(6000));
        }

        [Test]
        public void TestTapsCollidingWithLaterContentPushItOut()
        {
            var lines = timedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines, 1, 0, 1, 1);

            // The second tap lands past where line 3 currently starts.
            var built = TapTimingBuilder.Build(lines, queue, new[] { 3500d, 6500 });

            Assert.That(built[1].Units[1].StartTime, Is.EqualTo(6500), "the tap wins");

            // Line 3 is pushed just far enough to stay ordered AND to leave the tapped last word
            // ("delta", 5 chars) its guaranteed default width of 5 * DEFAULT_CHAR_MS = 400ms, and
            // nothing more. The guarantee is deliberately the flat default and not this line's own
            // 3000ms-a-word cadence: the shove lands on content the mapper never tapped, so it is
            // bounded by the word's length rather than by how slowly they were tapping.
            Assert.That(built[1].Units[1].EndTime, Is.EqualTo(6900), "the last word is not a 30ms sliver");
            Assert.That(built[2].StartTime, Is.EqualTo(6900));
            Assert.That(built[2].Units[0].StartTime, Is.EqualTo(6900));
            Assert.That(built[2].Units[1].StartTime, Is.EqualTo(6930), "ordering only, no retiming");

            // Boundary invariant survives the push.
            Assert.That(built[1].EndTime, Is.EqualTo(built[2].StartTime));
        }

        [Test]
        public void TestTapsBeforeEarlierContentAreClampedNotReordered()
        {
            var lines = timedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines, 1, 0, 1, 1);

            // Tapping the middle section earlier than the previous line's last word: content before
            // the pass is never moved, so the taps clamp against it and stay ordered.
            var built = TapTimingBuilder.Build(lines, queue, new[] { 500d, 800 });

            Assert.That(built[0].StartTime, Is.EqualTo(1000), "the earlier line does not move");
            Assert.That(built[1].StartTime, Is.GreaterThanOrEqualTo(1900 + TypeBeatEditorOperations.MIN_SPAN_MS));
            Assert.That(built[1].Units[0].StartTime, Is.LessThan(built[1].Units[1].StartTime));
            assertMonotonic(built);
        }

        [Test]
        public void TestFinishingEarlyOnAnUntimedSheetPacesTheRemainder()
        {
            var lines = untimedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines);

            // Only the first line was tapped, then Finish.
            var built = TapTimingBuilder.Build(lines, queue, new[] { 5000d, 5500 });

            Assert.That(built[0].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 5000d, 5500d }));
            Assert.That(built[0].Units.All(u => u.Source == TimingSource.Explicit), Is.True);
            Assert.That(built[0].Estimated, Is.False);

            // The untapped remainder had no usable timing left (it sat before the last tap), so it is
            // paced on at the mean tapped word duration and the line says it is estimated.
            Assert.That(built[1].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 6000d, 6500d }));
            Assert.That(built[1].Units.All(u => u.Source == TimingSource.Interpolated), Is.True);
            Assert.That(built[1].Estimated, Is.True);
            assertMonotonic(built);
        }

        [Test]
        public void TestFinishingEarlyKeepsStillValidExistingTiming()
        {
            var lines = timedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines);

            // One tap, right at the top of the song. Everything after it still sits later than the
            // tap, so it is left exactly as it was rather than being paced over.
            var built = TapTimingBuilder.Build(lines, queue, new[] { 1100d });

            Assert.That(built[0].Units[0].StartTime, Is.EqualTo(1100));
            Assert.That(built[0].Units[1].StartTime, Is.EqualTo(1900));
            Assert.That(built[1].StartTime, Is.EqualTo(3000));
            Assert.That(built[2].StartTime, Is.EqualTo(6000));
            Assert.That(built.All(l => !l.Estimated), Is.True, "nothing had to be guessed");
        }

        [Test]
        public void TestWordRunInsideOneLine()
        {
            var lines = timedSheet();

            // Only the second word of line 2 was selected.
            var queue = TapTimingBuilder.BuildQueue(lines, 1, 1, 1, 1);
            Assert.That(queue, Is.EqualTo(new[] { new TapTarget(1, 1) }));

            var built = TapTimingBuilder.Build(lines, queue, new[] { 4600d });

            Assert.That(built[1].StartTime, Is.EqualTo(3000), "the line boundary is not part of the pass");
            Assert.That(built[1].Units[0].StartTime, Is.EqualTo(3000), "the untouched word keeps its timing");
            Assert.That(built[1].Units[1].StartTime, Is.EqualTo(4600));

            // The single tapped word is the last of its line, so it takes the default word duration.
            Assert.That(built[1].Units[1].EndTime, Is.EqualTo(4600 + TapTimingBuilder.DEFAULT_WORD_MS));
            Assert.That(built[1].Units[1].Source, Is.EqualTo(TimingSource.Explicit));
            Assert.That(built[1].Units[0].Source, Is.EqualTo(TimingSource.Explicit));
        }

        [Test]
        public void TestLastWordWidthScalesWithItsCharacterCount()
        {
            // Two lines tapped at the SAME cadence (200ms per two-character word, so 100ms a
            // character), differing only in how long their last word is.
            var lines = new List<LyricLine>
            {
                line("aa bb cccc", 0, 1000, 900, (0, 300), (300, 600), (600, 900)),
                line("aa bb cc", 1000, 2000, 1900, (1000, 1300), (1300, 1600), (1600, 1900)),
            };

            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines),
                new[] { 1000d, 1200, 1400, 5000, 5200, 5400 });

            // Inside a line a word still runs to the next word, unchanged.
            Assert.That(built[0].Units.Select(u => (u.StartTime, u.EndTime)).Take(2),
                Is.EqualTo(new[] { (1000d, 1200d), (1200d, 1400d) }));

            // The last word has no next word of its own line, so it takes the line's rate times its
            // own character count: 4 chars -> 400ms, 2 chars -> 200ms, at the same 100ms a char.
            Assert.That(built[0].Units[^1].EndTime - built[0].Units[^1].StartTime, Is.EqualTo(400));
            Assert.That(built[1].Units[^1].EndTime - built[1].Units[^1].StartTime, Is.EqualTo(200));

            // Backlog 246: the line's sung end is its last word's end, so it follows the widening.
            Assert.That(built[0].SingEndTime, Is.EqualTo(1800));
            Assert.That(built[1].SingEndTime, Is.EqualTo(5600));
            Assert.That(built[1].EndTime, Is.EqualTo(5600 + tail));
            assertMonotonic(built);
        }

        [Test]
        public void TestLastWordFallsBackToTheDefaultCharRate()
        {
            // Nothing to measure a cadence from: one tap, on the last word of its line, with the
            // word before it untouched. The width is then the flat per-character default.
            var lines = new List<LyricLine>
            {
                line("alpha go", 1000, 3000, 2800, (1000, 1800), (1900, 2800)),
                line("omega", 3000, 5000, 4800, (3000, 4800)),
            };

            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines, 0, 1, 0, 1), new[] { 2000d });

            // "go" is TWO characters, so the old flat DEFAULT_WORD_MS would have been 2.5x too wide.
            Assert.That(TapTimingBuilder.DEFAULT_WORD_MS, Is.EqualTo(5 * TapTimingBuilder.DEFAULT_CHAR_MS),
                "the flat default is the per-char default at the mean English word length");
            Assert.That(built[0].Units[1].EndTime, Is.EqualTo(2000 + 2 * TapTimingBuilder.DEFAULT_CHAR_MS));
            Assert.That(built[0].SingEndTime, Is.EqualTo(2000 + 2 * TapTimingBuilder.DEFAULT_CHAR_MS));

            // The default is small enough that the line behind it never has to move for it.
            Assert.That(built[1].StartTime, Is.EqualTo(3000));
            assertMonotonic(built);
        }

        [Test]
        public void TestLastWordIsNotSquashedByUntimedContentBehindIt()
        {
            // The reported bug: ONE line of a freshly pasted sheet is tap-timed. Everything behind
            // the pass still carries the import's meaningless times, so the ordering pass used to
            // drop the next line MIN_SPAN_MS after the final tap and leave the last word a 30ms
            // sliver. It now clears the word's default width first.
            var lines = untimedSheet();
            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines, 0, 0, 0, 1), new[] { 5000d, 5500 });

            double width = built[0].Units[1].EndTime - built[0].Units[1].StartTime;

            Assert.That(width, Is.EqualTo(3 * TapTimingBuilder.DEFAULT_CHAR_MS), "\"two\" is three characters");
            Assert.That(width, Is.GreaterThan(TypeBeatEditorOperations.MIN_SPAN_MS));
            Assert.That(built[0].SingEndTime, Is.EqualTo(built[0].Units[1].EndTime));

            // The untimed line behind it is only ORDERED, never retimed: it lands exactly where the
            // room ran out and keeps its own shape from there.
            Assert.That(built[1].StartTime, Is.EqualTo(5500 + 3 * TapTimingBuilder.DEFAULT_CHAR_MS));
            assertMonotonic(built);
        }

        [Test]
        public void TestLastWordNeverPushesOverTheNextTap()
        {
            // The mapper tapped the next line's first word 100ms after the line's last word: that is
            // what they sang, so the word is 100ms and nothing moves. The width is a default for
            // when there is nothing better, never an override of a tap.
            var lines = untimedSheet();
            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines), new[] { 5000d, 5500, 5600, 6200 });

            Assert.That(built[0].Units[1].EndTime, Is.EqualTo(5600));
            Assert.That(built[1].StartTime, Is.EqualTo(5600));
            Assert.That(built[1].Units[0].StartTime, Is.EqualTo(5600));
            assertMonotonic(built);
        }

        [Test]
        public void TestRetimedWordKeepsSubdivisionsOnlyWhenTapsCoveredThem()
        {
            var lines = timedSheet();

            // Give the first word of line 2 a subdivision, as the editor's D key would.
            var withBoundary = lines[1].Units.ToArray();
            withBoundary[0] = new TimedUnit
            {
                Text = withBoundary[0].Text,
                StartTime = withBoundary[0].StartTime,
                EndTime = withBoundary[0].EndTime,
                Source = TimingSource.Explicit,
                SyllableBoundaries = new[] { 3600d },
            };
            lines[1] = new LyricLine
            {
                RawText = lines[1].RawText,
                StartTime = lines[1].StartTime,
                EndTime = lines[1].EndTime,
                SingEndTime = lines[1].SingEndTime,
                Units = withBoundary,
            };

            // The subdivided word now asks for TWO taps, so this queue is three slots, not two.
            var queue = TapTimingBuilder.BuildQueue(lines, 1, 0, 1, 1);
            Assert.That(queue, Is.EqualTo(new[] { new TapTarget(1, 0, 0), new TapTarget(1, 0, 1), new TapTarget(1, 1) }));

            // BOTH of the word's syllables were tapped, so the taps SET the subdivision: the old
            // 3600 mark is replaced by the 4200 the mapper actually sang.
            var covered = TapTimingBuilder.Build(lines, queue, new[] { 3500d, 4200 });
            Assert.That(covered[1].Units[0].StartTime, Is.EqualTo(3500));
            Assert.That(covered[1].Units[0].SyllableBoundaries, Is.EqualTo(new[] { 4200d }));

            // The mapper finished after the word's FIRST syllable: the word moved wholesale but its
            // second syllable never got a time, so the old mark is meaningless and goes.
            var partial = TapTimingBuilder.Build(lines, queue, new[] { 3500d });
            Assert.That(partial[1].Units[0].StartTime, Is.EqualTo(3500));
            Assert.That(partial[1].Units[0].SyllableBoundaries, Is.Empty, "the second syllable was never tapped");
            Assert.That(partial[1].Units[1].StartTime, Is.EqualTo(4300), "the untapped word kept its still-valid time");

            // A word the pass never touched keeps everything about it.
            var untouched = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines, 0, 0, 0, 1), new[] { 1000d, 1900 });
            Assert.That(untouched[1].Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3600d }));
        }

        [Test]
        public void TestSubdividedWordsExpandIntoOneTapPerSyllable()
        {
            var lines = subdividedSheet();

            // "remember me" with remember split three ways is FOUR taps, exactly as the mapper
            // sings it; the undivided line behind it is still one tap per word.
            Assert.That(TapTimingBuilder.BuildQueue(lines), Is.EqualTo(new[]
            {
                new TapTarget(0, 0, 0), new TapTarget(0, 0, 1), new TapTarget(0, 0, 2), new TapTarget(0, 1),
                new TapTarget(1, 0), new TapTarget(1, 1),
            }));

            Assert.That(TapTimingBuilder.SyllableCount(lines[0].Units[0]), Is.EqualTo(3));
            Assert.That(TapTimingBuilder.SyllableCount(lines[0].Units[1]), Is.EqualTo(1));
        }

        [Test]
        public void TestSyllableTapsSetTheWordsSubdivisionTimes()
        {
            var lines = subdividedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines, 0, 0, 0, 1);

            // remember start / -emb / -er, then "me". The old boundaries (1300, 1550) are replaced
            // outright by what was tapped, so the caret sweeps at the speed the mapper sang.
            var built = TapTimingBuilder.Build(lines, queue, new[] { 1000d, 1250, 1600, 2000 });

            Assert.That(built[0].Units[0].StartTime, Is.EqualTo(1000));
            Assert.That(built[0].Units[0].EndTime, Is.EqualTo(2000), "the word runs to the next word's start");

            // Taps comfortably inside the word pass through untouched: segments of 250, 350 and
            // 400ms, which is what save/decode must reproduce exactly.
            Assert.That(built[0].Units[0].SyllableBoundaries, Is.EqualTo(new[] { 1250d, 1600d }));

            // The undivided word alongside it takes no boundaries at all.
            Assert.That(built[0].Units[1].StartTime, Is.EqualTo(2000));
            Assert.That(built[0].Units[1].SyllableBoundaries, Is.Empty);

            // Nothing outside the pass moved.
            Assert.That(built[1].Units[0].StartTime, Is.EqualTo(3000));
            assertMonotonic(built);
        }

        [Test]
        public void TestPacedWordDropsTheSubdivisionsItsTapsNeverCovered()
        {
            var lines = new List<LyricLine>
            {
                line("one two", 0, 1000, 900, (0, 450), (450, 900)),
                subdivide(line("remember me", 1000, 2000, 1900, (1000, 1450), (1450, 1900)), 0, 1150, 1300),
            };

            // Only the first line was tapped; the rest is paced on and never got a syllable time.
            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines), new[] { 5000d, 5400 });

            Assert.That(built[1].Units[0].StartTime, Is.EqualTo(5800), "paced on at the mean tapped word duration");
            Assert.That(built[1].Units[0].SyllableBoundaries, Is.Empty, "a paced word's old sub-word marks mean nothing");
            Assert.That(built[1].Units[0].Source, Is.EqualTo(TimingSource.Interpolated));
            Assert.That(built[1].Estimated, Is.True);
        }

        [Test]
        public void TestPacingMeasuresWordsNotSyllables()
        {
            var lines = subdividedSheet();

            // Four taps for line 1, but only two of them are WORD starts (4000 and 4500), so the
            // untapped tail is paced at 500ms per word. Measuring the mean over all four taps would
            // give 167ms and cram the rest of the sheet into a third of the time.
            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines), new[] { 4000d, 4100, 4200, 4500 });

            Assert.That(built[1].Units[0].StartTime, Is.EqualTo(5000));
            Assert.That(built[1].Units[1].StartTime, Is.EqualTo(5500));
        }

        [Test]
        public void TestSyllableTapsAreFittedInsideTheFinalWordSpan()
        {
            var lines = subdividedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines, 0, 0, 0, 1);

            // Defensive: syllable taps handed in past the word's own end (a caller that did not
            // order them) still come out strictly inside it, ascending and spaced, because that is
            // what TimedUnit.SyllableBoundaries promises and what the encoder round-trips.
            var built = TapTimingBuilder.Build(lines, queue, new[] { 1000d, 1700, 1750, 1100 });

            var unit = built[0].Units[0];
            Assert.That(unit.StartTime, Is.EqualTo(1000));
            Assert.That(unit.EndTime, Is.EqualTo(1100));
            Assert.That(unit.SyllableBoundaries, Is.EqualTo(new[] { 1060d, 1080d }));

            foreach (double boundary in unit.SyllableBoundaries)
            {
                Assert.That(boundary, Is.GreaterThan(unit.StartTime));
                Assert.That(boundary, Is.LessThan(unit.EndTime));
            }
        }

        [Test]
        public void TestWordLeftTooNarrowForItsSyllablesKeepsNone()
        {
            var lines = subdividedSheet();
            var queue = TapTimingBuilder.BuildQueue(lines, 0, 0, 0, 1);

            // Every tap crammed into 15ms: after collision clamping the word is MIN_SPAN_MS wide,
            // which cannot hold three syllables of MIN_SYLLABLE_MS each, so it holds none.
            var built = TapTimingBuilder.Build(lines, queue, new[] { 1000d, 1005, 1010, 1015 });

            Assert.That(built[0].Units[0].EndTime - built[0].Units[0].StartTime,
                Is.EqualTo(TypeBeatEditorOperations.MIN_SPAN_MS));
            Assert.That(built[0].Units[0].SyllableBoundaries, Is.Empty, "no room for three syllables");
            assertMonotonic(built);
        }

        [Test]
        public void TestSyllableTextMatchesTheEngineCharSplit()
        {
            // The chips the recording surface shows are the exact char runs TypingLine judges by:
            // k typeable chars spread evenly across the segments in index space.
            Assert.That(TapTimingBuilder.SyllableTextOf("remember", 0, 3), Is.EqualTo("rem"));
            Assert.That(TapTimingBuilder.SyllableTextOf("remember", 1, 3), Is.EqualTo("emb"));
            Assert.That(TapTimingBuilder.SyllableTextOf("remember", 2, 3), Is.EqualTo("er"));

            // An undivided word is its whole self.
            Assert.That(TapTimingBuilder.SyllableTextOf("me", 0, 1), Is.EqualTo("me"));

            // Punctuation rides with the typeable char before it, and the split is always lossless:
            // the chips spell the word back exactly, however many ways it is cut.
            foreach (string word in new[] { "remember", "hey!", "don't", "a", "rhythm", "fire" })
            {
                for (int count = 1; count <= 4; count++)
                {
                    string joined = string.Concat(Enumerable.Range(0, count)
                                                             .Select(i => TapTimingBuilder.SyllableTextOf(word, i, count)));
                    Assert.That(joined, Is.EqualTo(word), $"{word} split {count} ways");
                }
            }
        }

        [Test]
        public void TestTapsOutOfOrderAreClampedNotAccepted()
        {
            var lines = untimedSheet();

            // The builder is defensive: a caller handing it non-monotonic times still gets an
            // ordered, non-degenerate sheet rather than an inverted one.
            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines), new[] { 5000d, 4000, 4500, 9000 });

            assertMonotonic(built);
            Assert.That(built[0].Units[0].StartTime, Is.EqualTo(5000));
        }

        [Test]
        public void TestEmptySheetAndEmptyQueue()
        {
            Assert.That(TapTimingBuilder.Build(new List<LyricLine>(), new List<TapTarget>(), new[] { 1000d }), Is.Empty);

            var lines = timedSheet();
            var built = TapTimingBuilder.Build(lines, new List<TapTarget>(), new[] { 1000d });
            Assert.That(built.Select(l => l.StartTime), Is.EqualTo(lines.Select(l => l.StartTime)));
        }

        [Test]
        public void TestBuildQueueSpansCtrlPickedGaps()
        {
            var lines = timedSheet();

            // Lines 1 and 3 picked with ctrl: the queue covers the contiguous span between them,
            // because taps are continuous in time and cannot skip the middle.
            var queue = TapTimingBuilder.BuildQueue(lines, 0, 0, 2, 1);
            Assert.That(queue, Has.Count.EqualTo(6));
            Assert.That(queue[2], Is.EqualTo(new TapTarget(1, 0)));
        }

        /// <summary>Every line and word start is ordered, and no line or word is inverted.</summary>
        private static void assertMonotonic(IReadOnlyList<LyricLine> built)
        {
            double previous = double.NegativeInfinity;

            for (int i = 0; i < built.Count; i++)
            {
                var l = built[i];

                Assert.That(l.StartTime, Is.GreaterThan(previous), $"line {i} start out of order");
                Assert.That(l.SingEndTime, Is.GreaterThanOrEqualTo(l.StartTime), $"line {i} sung end inverted");
                Assert.That(l.EndTime, Is.GreaterThanOrEqualTo(l.SingEndTime), $"line {i} window end inverted");

                if (i + 1 < built.Count)
                    Assert.That(l.EndTime, Is.EqualTo(built[i + 1].StartTime), $"boundary invariant broken after line {i}");

                double cursor = l.StartTime;

                foreach (var u in l.Units)
                {
                    Assert.That(u.StartTime, Is.GreaterThanOrEqualTo(cursor), $"line {i} words out of order");
                    Assert.That(u.EndTime, Is.GreaterThanOrEqualTo(u.StartTime), $"line {i} word inverted");
                    Assert.That(u.EndTime, Is.LessThanOrEqualTo(l.EndTime), $"line {i} word spills past the line");
                    cursor = u.EndTime;
                }

                previous = l.StartTime;
            }
        }
    }
}
