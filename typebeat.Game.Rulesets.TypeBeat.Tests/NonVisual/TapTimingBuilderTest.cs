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

            // The line's LAST word is capped at the line's own mean word duration (500ms here), so
            // the 200ms gap before the next line survives instead of being sung through.
            Assert.That(built[0].SingEndTime, Is.EqualTo(6000));

            // Boundary invariant: EndTime_i == StartTime_(i+1).
            Assert.That(built[0].StartTime, Is.EqualTo(5000));
            Assert.That(built[0].EndTime, Is.EqualTo(6200));
            Assert.That(built[1].StartTime, Is.EqualTo(6200));

            Assert.That(built[1].Units.Select(u => (u.StartTime, u.EndTime)),
                Is.EqualTo(new[] { (6200d, 6800d), (6800d, 7400d) }));

            // The very last word has no next tap: it takes the line's mean word duration (600ms).
            Assert.That(built[1].SingEndTime, Is.EqualTo(7400));
            Assert.That(built[1].EndTime, Is.EqualTo(7400 + tail));

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

            // Line 3 is pushed just far enough to stay ordered, and nothing more: its second word,
            // which never collided, does not move at all.
            Assert.That(built[2].StartTime, Is.EqualTo(6500 + TypeBeatEditorOperations.MIN_SPAN_MS));
            Assert.That(built[2].Units[0].StartTime, Is.EqualTo(6500 + TypeBeatEditorOperations.MIN_SPAN_MS));
            Assert.That(built[2].Units[1].StartTime, Is.EqualTo(6900));

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
        public void TestRetimedWordsLoseSyllableSubdivisions()
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

            var built = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines, 1, 0, 1, 1), new[] { 3500d, 4200 });

            Assert.That(built[1].Units[0].SyllableBoundaries, Is.Empty, "the word moved wholesale");

            // A word the pass never touched keeps everything about it.
            var untouched = TapTimingBuilder.Build(lines, TapTimingBuilder.BuildQueue(lines, 0, 0, 0, 1), new[] { 1000d, 1900 });
            Assert.That(untouched[1].Units[0].SyllableBoundaries, Is.EqualTo(new[] { 3600d }));
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
