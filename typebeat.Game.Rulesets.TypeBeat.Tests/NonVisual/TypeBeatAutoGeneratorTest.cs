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
        /// <summary>
        /// Drives an engine with a generated replay exactly the way the playfield's engine ticker
        /// does (Update to the frame's timestamp, then the keystroke), asserting every single press
        /// is ACCEPTED. A press the engine answers with false is not a rejection, it is a press that
        /// never happened (no active line, or a line already complete), and it silently shifts every
        /// later press on that line onto the wrong cell.
        /// </summary>
        private static TypingEngine playPerfectly(TypeBeatBeatmap map)
        {
            var replay = new TypeBeatAutoGenerator(map).Generate();
            var engine = new TypingEngine(lyricBeatmap(map));

            foreach (var frame in replay.Frames.Cast<TypeBeatReplayFrame>())
            {
                engine.Update(frame.Time);
                Assert.IsTrue(engine.ProcessKey(frame.Character, frame.Time),
                    $"'{frame.Character}' @ {frame.Time} must reach a live cell (line {engine.ActiveLineIndex}, caret {engine.CaretIndex})");
                Assert.AreEqual(0, engine.ConsecutiveWrongKeys, $"'{frame.Character}' @ {frame.Time} must not be rejected");
            }

            engine.Update(1_000_000);

            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(1.0, engine.LiveAccuracy, "no wrong keys");

            foreach (var l in engine.Lines)
            {
                foreach (var cell in l.Cells)
                {
                    if (cell.IsTypeable)
                        Assert.AreEqual(CellState.Correct, cell.State, $"cell '{cell.Expected}' @ {cell.TargetTime} in \"{l.DisplayText}\"");
                }
            }

            return engine;
        }

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
            var engine = playPerfectly(map);

            foreach (var l in engine.Lines)
            {
                foreach (var cell in l.Cells)
                {
                    if (cell.IsTypeable)
                        Assert.AreEqual(0, cell.JudgedDelta!.Value, 1.0, "pressed at target (within rounding)");
                }
            }
        }

        /// <summary>
        /// Backlog 51 regression, distilled from "Busta Rhymes Goes To The Wii Shop Channel".
        ///
        /// <para>Real maps carry FRACTIONAL times, and the decoder makes line windows contiguous
        /// (a line's EndTime IS the next line's StartTime), so the next line's first cell target is
        /// routinely that exact fractional boundary. Rounding the frame to integral milliseconds
        /// used to round it DOWN, landing the press a fraction of a millisecond BEFORE the previous
        /// line's seal deadline: the previous line was still the active one, it was already fully
        /// typed, and a complete line makes ProcessKey inert. The press vanished, and every later
        /// press on the new line landed one cell early.</para>
        ///
        /// <para>An all-freestyle line hides that drift completely (every key matches a freestyle
        /// cell) until the first SPACE cell, at which point autoplay's space lands on a freestyle
        /// cell (which rejects space, backlog 50) and its letters land on the space cell, rejected
        /// again and again: 13 consecutive rejections fail the play outright.</para>
        /// </summary>
        [Test]
        public void FractionalLineBoundaryDoesNotSwallowTheFirstPress()
        {
            // The boundary the previous line seals on, and the next line's first cell target.
            const double boundary = 1000.3856;

            var map = beatmap(
                line("ab", 0, boundary, 900, unit("ab", 100, 900)),
                line("&&& &&&", boundary, 4000, 3000,
                    unit("&&&", boundary, 2000),
                    unit("&&&", 2000, 3000)));

            var frames = new TypeBeatAutoGenerator(map).Generate().Frames.Cast<TypeBeatReplayFrame>().ToList();

            // The first press of the second line must be at or after the boundary, never rounded
            // back across it (1001, not 1000).
            var firstOnSecondLine = frames.First(f => f.Time >= 1000);
            Assert.GreaterOrEqual(firstOnSecondLine.Time, boundary, "a press must never precede its line's activation");

            playPerfectly(map);
        }

        /// <summary>
        /// The same swallow with the drift left visible: a fractional boundary in front of an
        /// ordinary lyric line. Pre-fix the first press was eaten and every later press landed on
        /// the previous cell, so the line's last cell was never typed and sealed as a miss.
        /// </summary>
        [Test]
        public void FractionalLineBoundaryKeepsOrdinaryLinesInStep()
        {
            const double boundary = 2000.25;

            var map = beatmap(
                line("hi", 0, boundary, 1800, unit("hi", 100, 1800)),
                line("cd ef", boundary, 6000, 5000,
                    unit("cd", boundary, 3500),
                    unit("ef", 3500, 5000)));

            playPerfectly(map);
        }
    }
}
