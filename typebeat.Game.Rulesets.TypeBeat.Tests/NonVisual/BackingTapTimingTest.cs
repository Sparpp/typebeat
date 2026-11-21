// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>Tap timing on the backing track (backlog 501): what a tap, a hold and finishing early commit.</summary>
    [TestFixture]
    public class BackingTapTimingTest
    {
        /// <summary>A line "one two" sung 1000..2000 (100ms a character), window 1000..5000, carrying <paramref name="backing"/>.</summary>
        private static LyricLine line(params BackingVocal[] backing) => new LyricLine
        {
            RawText = "one two",
            StartTime = 1000,
            EndTime = 5000,
            SingEndTime = 2000,
            Units = new[]
            {
                new TimedUnit { Text = "one", StartTime = 1000, EndTime = 1300, Source = TimingSource.Explicit },
                new TimedUnit { Text = "two", StartTime = 1300, EndTime = 1600, Source = TimingSource.Explicit },
            },
            BackingVocals = backing,
        };

        private static IReadOnlyList<LyricLine> build(LyricLine[] lines, double[] taps, double?[]? releases = null)
        {
            var queue = BackingTapTiming.BuildQueue(lines, 0, lines.Length - 1);
            return BackingTapTiming.Build(lines, queue, taps, releases);
        }

        [Test]
        public void TestTapsSetStartsAndShortTapsRunToTheNextWordAtMost()
        {
            var lines = new[] { line(new BackingVocal("oh", 1000, 1100), new BackingVocal("yeah", 1100, 1200)) };

            var words = build(lines, new[] { 2000.0, 2150 })[0].BackingVocals;

            // "oh" would take 200ms at the line's pace but "yeah" comes first; "yeah" takes its 400ms.
            Assert.That(words.Select(w => (w.StartTime, w.EndTime)), Is.EqualTo(new[] { (2000.0, 2150.0), (2150.0, 2550.0) }));
        }

        [Test]
        public void TestAHeldTapLastsUntilTheRelease()
        {
            var lines = new[] { line(new BackingVocal("oh", 1000, 1100)) };

            var word = build(lines, new[] { 2000.0 }, new double?[] { 3200 })[0].BackingVocals.Single();

            Assert.That((word.StartTime, word.EndTime), Is.EqualTo((2000.0, 3200.0)));
        }

        [Test]
        public void TestPiecesOfOneWordStayJoined()
        {
            var lines = new[] { line(new BackingVocal("h", 1000, 1100), new BackingVocal("a", 1100, 1200, Joined: true)) };

            var words = build(lines, new[] { 2000.0, 2300 }, new double?[] { 2250, 2900 })[0].BackingVocals;

            Assert.That(words.Select(w => (w.StartTime, w.EndTime, w.Joined)),
                Is.EqualTo(new[] { (2000.0, 2300.0, false), (2300.0, 2900.0, true) }));
        }

        [Test]
        public void TestTypedWordsAndUntouchedLinesStayAsTheyWere()
        {
            var lines = new[] { line(new BackingVocal("oh", 1000, 1100)), line() };

            var built = build(lines, new[] { 2000.0 });

            Assert.That(built[0].Units, Is.EqualTo(lines[0].Units));
            Assert.That((built[0].StartTime, built[0].EndTime), Is.EqualTo((1000.0, 5000.0)));
            Assert.That(built[1], Is.SameAs(lines[1]));
        }

        [Test]
        public void TestATapOutsideTheLineLandsOnItsEdge()
        {
            var lines = new[] { line(new BackingVocal("oh", 1000, 1100)) };

            var word = build(lines, new[] { 6000.0 })[0].BackingVocals.Single();

            Assert.That((word.StartTime, word.EndTime), Is.EqualTo((5000.0, 5000.0)));
        }

        [Test]
        public void TestFinishingEarlyMovesOnlyTheWordsTheTapsOvertook()
        {
            var lines = new[] { line(new BackingVocal("a", 1000, 1100), new BackingVocal("b", 1200, 1300), new BackingVocal("c", 4000, 4100)) };

            // One tap: "b" is left behind it, so it moves on after it (one default word later),
            // keeping its length; "c" still starts after the tap and stays.
            var words = build(lines, new[] { 2000.0 }).Single().BackingVocals;

            Assert.That(words.Select(w => (w.Text, w.StartTime, w.EndTime)),
                Is.EqualTo(new[] { ("a", 2000.0, 2100.0), ("b", 2400.0, 2500.0), ("c", 4000.0, 4100.0) }));
        }
    }
}
