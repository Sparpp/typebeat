// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The autoplay generator must emit exactly one frame per typeable cell, in order, at (rounded)
    /// target times, and driving a <see cref="TypingEngine"/> with those frames (via the same
    /// Update-then-key call sequence playback uses) must produce a perfect play: every typeable
    /// cell Correct, no misses, 100% accuracy.
    /// </summary>
    [TestFixture]
    public class TypeBeatAutoGeneratorTest
    {
        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static TypeBeatBeatmap beatmap(params LyricLine[] lines)
        {
            var map = new TypeBeatBeatmap();

            for (int i = 0; i < lines.Length; i++)
            {
                map.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = TimingGranularity.Word,
                });
            }

            return map;
        }

        private static LyricBeatmap lyricBeatmap(TypeBeatBeatmap map) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "T", Title = "S", FolderPath = @"X:\n", AudioFileName = "a.mp3" },
            Lines = map.HitObjects.Select(h => h.Line).ToList(),
            Granularity = TimingGranularity.Word,
        };

        private static TypeBeatBeatmap createTwoLineMap() => beatmap(
            line("ab cd!", 0, 4000, 3500, unit("ab", 500, 1500), unit("cd!", 2000, 3500)),
            line("Ef 9", 4000, 8000, 7500, unit("Ef", 4500, 6000), unit("9", 6500, 7500)));

        [Test]
        public void GeneratesOneFramePerTypeableCellAtTargetTimes()
        {
            var map = createTwoLineMap();
            var replay = new TypeBeatAutoGenerator(map).Generate();

            var frames = replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

            // Typeable surface: letters/digits/spaces; '!' is punctuation (auto-skipped, no frame).
            Assert.AreEqual("ab cd" + "Ef 9", string.Concat(frames.Select(f => f.Character)));

            var engineLines = map.HitObjects.Select(h => TypingLine.FromLyricLine(h.Line, TimingGranularity.Word)).ToList();

            double[] expectedTargets = engineLines
                                       .SelectMany(l => l.Cells)
                                       .Where(c => c.IsTypeable)
                                       .Select(c => c.TargetTime)
                                       .ToArray();

            Assert.AreEqual(expectedTargets.Length, frames.Count);

            for (int i = 0; i < frames.Count; i++)
            {
                Assert.AreEqual(System.Math.Round(expectedTargets[i]), frames[i].Time, $"frame {i} time");
                Assert.AreEqual(frames[i].Time, System.Math.Round(frames[i].Time), $"frame {i} must have an integral time");
            }

            for (int i = 1; i < frames.Count; i++)
                Assert.LessOrEqual(frames[i - 1].Time, frames[i].Time, "frame times must be monotonic");
        }

        [Test]
        public void GeneratedReplayPlaysPerfectly()
        {
            var map = createTwoLineMap();
            var replay = new TypeBeatAutoGenerator(map).Generate();
            var engine = new TypingEngine(lyricBeatmap(map));

            // Same call sequence the playfield's replay feeder makes.
            foreach (var frame in replay.Frames.Cast<TypeBeatReplayFrame>())
            {
                engine.Update(frame.Time);
                Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time), $"'{frame.Character}' @ {frame.Time} must be accepted");
            }

            engine.Update(100000);

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(1.0, engine.LiveAccuracy, "no wrong keys");

            foreach (var l in engine.Lines)
            {
                foreach (var cell in l.Cells)
                {
                    if (cell.IsTypeable)
                    {
                        Assert.AreEqual(CellState.Correct, cell.State);
                        Assert.AreEqual(0, cell.JudgedDelta!.Value, 1.0, "pressed at target (within rounding)");
                    }
                }
            }
        }
    }
}
