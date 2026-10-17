// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Direct pins on the countable stream the Fletcher rush cap measures in (CountableStream). The cap's
// own behaviour is held through played runs in FletcherEngineTest and RushCapCostsAccuracyTest.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class CountableStreamTest
    {
        private static LyricLine line(string text, double start, double end)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = end,
                Units = text.Split(' ').Select((w, i) => new TimedUnit { Text = w, StartTime = start + i * 500, EndTime = start + (i + 1) * 500 }).ToArray(),
            };

        private static TypingEngine engine() => new TypingEngine(new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "Test", Title = "Song", FolderPath = @"X:\nowhere", AudioFileName = "a.mp3" },
            Lines = new[] { line("ab cd", 1000, 3000), line("ef", 3000, 5000) },
            Granularity = TimingGranularity.Word,
        });

        [Test]
        public void ASpaceIsNotCountableAndPositionsRunAcrossLines()
        {
            var stream = new CountableStream(engine().Lines);

            Assert.Multiple(() =>
            {
                Assert.That(stream.PositionAt(0, 0), Is.Zero);
                Assert.That(stream.PositionAt(0, 2), Is.EqualTo(2), "a, b; the space after them counts nothing");
                Assert.That(stream.PositionAt(0, 3), Is.EqualTo(2));
                Assert.That(stream.PositionAt(0, 5), Is.EqualTo(4), "past the end of the line");
                Assert.That(stream.PositionAt(0, 99), Is.EqualTo(4), "clamped to the line");
                Assert.That(stream.PositionAt(1, 0), Is.EqualTo(4), "the next line starts where this one ended");
                Assert.That(stream.PositionAt(1, -1), Is.EqualTo(4), "clamped at the head as well");
            });
        }

        [Test]
        public void ThePlayheadCountsEveryCountableTargetAtOrBeforeItsTime()
        {
            var lines = engine().Lines;
            var stream = new CountableStream(lines);
            var targets = lines.SelectMany(l => l.Cells).Where(c => c.IsCountable).Select(c => c.TargetTime).OrderBy(t => t).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(stream.PlayheadPosition(double.NegativeInfinity), Is.Zero);
                Assert.That(stream.PlayheadPosition(targets[0]), Is.EqualTo(1), "a target exactly on the time is reached");
                Assert.That(stream.PlayheadPosition(targets[^1]), Is.EqualTo(targets.Length));
                Assert.That(stream.PlayheadPosition(double.PositiveInfinity), Is.EqualTo(targets.Length));
            });
        }

        [Test]
        public void TheEngineReadsTheSameStream()
        {
            var e = engine();
            var stream = new CountableStream(e.Lines);

            foreach (double time in new[] { 0.0, 1200, 2600, 4000, 9000 })
                Assert.That(e.PlayheadCountablePosition(time), Is.EqualTo(stream.PlayheadPosition(time)), $"at {time}");

            Assert.That(e.CaretCountablePosition, Is.Zero, "no line is active");
        }
    }
}
