// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The recording half of tap timing: a queue of word slots and a plain list of song times.
    /// The whole point of keeping it this dumb is that transport control falls out for free, so
    /// this fixture pins tapping, the double-fire guard, undo, and what a backward seek does.
    /// </summary>
    [TestFixture]
    public class TapTimingSessionTest
    {
        private static List<LyricLine> sheet() => new List<LyricLine>
        {
            makeLine("one two", 0, 1000, 900),
            makeLine("three four", 1000, 2000, 1900),
        };

        private static LyricLine makeLine(string text, double start, double end, double singEnd)
        {
            string[] tokens = text.Split(' ');
            double step = (singEnd - start) / tokens.Length;

            return new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = tokens.Select((t, i) => new TimedUnit
                {
                    Text = t,
                    StartTime = start + step * i,
                    EndTime = start + step * (i + 1),
                }).ToArray(),
            };
        }

        private static TapTimingSession session()
        {
            var lines = sheet();
            return new TapTimingSession(lines, TapTimingBuilder.BuildQueue(lines));
        }

        [Test]
        public void TestTapsAppendInQueueOrder()
        {
            var s = session();

            Assert.That(s.Queue, Has.Count.EqualTo(4));
            Assert.That(s.WordAt(0), Is.EqualTo("one"));
            Assert.That(s.WordAt(2), Is.EqualTo("three"));
            Assert.That(s.StartsLine(0), Is.True);
            Assert.That(s.StartsLine(1), Is.False);
            Assert.That(s.StartsLine(2), Is.True, "word 3 opens the second line");

            Assert.That(s.Tap(5000), Is.True);
            Assert.That(s.Tap(5500), Is.True);

            Assert.That(s.Taps, Is.EqualTo(new[] { 5000d, 5500d }));
            Assert.That(s.TappedCount, Is.EqualTo(2));
            Assert.That(s.NextTarget, Is.EqualTo(new TapTarget(1, 0)));
            Assert.That(s.QueueComplete, Is.False);
        }

        [Test]
        public void TestQueueExhaustionRefusesFurtherTaps()
        {
            var s = session();

            foreach (double t in new[] { 1000d, 2000, 3000, 4000 })
                Assert.That(s.Tap(t), Is.True);

            Assert.That(s.QueueComplete, Is.True);
            Assert.That(s.NextTarget, Is.Null);
            Assert.That(s.Tap(5000), Is.False, "there is no fifth word to time");
            Assert.That(s.Taps, Has.Count.EqualTo(4));
        }

        [Test]
        public void TestDoubleFireIsIgnored()
        {
            var s = session();

            Assert.That(s.Tap(5000), Is.True);
            Assert.That(s.Tap(5000 + TapTimingSession.MIN_TAP_GAP_MS / 2), Is.False, "a key bounce is not a word");
            Assert.That(s.Tap(5000 + TapTimingSession.MIN_TAP_GAP_MS), Is.True);
            Assert.That(s.Taps, Has.Count.EqualTo(2));
        }

        [Test]
        public void TestUndoLastTap()
        {
            var s = session();

            s.Tap(1000);
            s.Tap(2000);

            Assert.That(s.UndoLastTap(), Is.True);
            Assert.That(s.Taps, Is.EqualTo(new[] { 1000d }));
            Assert.That(s.UndoLastTap(), Is.True);
            Assert.That(s.UndoLastTap(), Is.False);
            Assert.That(s.TappedCount, Is.Zero);
        }

        [Test]
        public void TestSeekingBackDropsTheTapsAfterTheSeekPoint()
        {
            var s = session();

            s.Tap(1000);
            s.Tap(2000);
            s.Tap(3000);

            // Rewind to 1500: everything recorded after that point is undone, and the queue rewinds
            // with it, so resuming just carries on from word 2.
            Assert.That(s.TruncateFrom(1500), Is.True);
            Assert.That(s.Taps, Is.EqualTo(new[] { 1000d }));
            Assert.That(s.NextTarget, Is.EqualTo(new TapTarget(0, 1)));

            // Seeking forward drops nothing.
            Assert.That(s.TruncateFrom(9000), Is.False);
            Assert.That(s.Taps, Has.Count.EqualTo(1));

            // Rewinding past everything empties the pass.
            Assert.That(s.TruncateFrom(0), Is.True);
            Assert.That(s.TappedCount, Is.Zero);
        }

        [Test]
        public void TestNegativeTapTimesAreClampedToZero()
        {
            var s = session();

            Assert.That(s.Tap(-500), Is.True);
            Assert.That(s.Taps[0], Is.Zero);
        }

        [Test]
        public void TestBuildCommitMatchesTheBuilder()
        {
            var lines = sheet();
            var queue = TapTimingBuilder.BuildQueue(lines);
            var s = new TapTimingSession(lines, queue);

            s.Tap(5000);
            s.Tap(5500);

            var expected = TapTimingBuilder.Build(lines, queue, s.Taps);
            var actual = s.BuildCommit();

            Assert.That(actual.Select(l => (l.StartTime, l.EndTime, l.SingEndTime)),
                Is.EqualTo(expected.Select(l => (l.StartTime, l.EndTime, l.SingEndTime))));
        }
    }
}
