// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>
    /// Generates a perfect play: every typeable cell's expected character pressed exactly at its
    /// target time (delta 0 = Perfect), using the same <see cref="TypingLine.FromLyricLine"/>
    /// flattening the engine itself is built from, so the frames line up with the engine's cells by
    /// construction. Case is emitted exactly as authored, which stays perfect under the Literate mod
    /// and is folded away otherwise.
    ///
    /// Times are clamped into each line's typeable window (never before its activation, never past
    /// its seal deadline) and kept monotonic, then rounded to integral milliseconds like recorded
    /// input so the frames survive .osr encoding untouched.
    /// </summary>
    public class TypeBeatAutoGenerator : AutoGenerator<TypeBeatReplayFrame>
    {
        /// <summary>Safety margin kept before a line's force-seal deadline for clamped presses.</summary>
        private const double seal_margin_ms = 10;

        public TypeBeatAutoGenerator(IBeatmap beatmap)
            : base(beatmap)
        {
        }

        protected override void GenerateFrames()
        {
            // Mirror DrawableTypeBeatRuleset.createEngine: engine line order is LineIndex order.
            var lineObjects = Beatmap.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

            if (lineObjects.Count == 0)
                return;

            TimingGranularity granularity = lineObjects[0].Granularity;

            double lastTime = double.NegativeInfinity;

            foreach (var lineObject in lineObjects)
            {
                var line = TypingLine.FromLyricLine(lineObject.Line, granularity);

                // The line is typeable in [ActivationTime, EndTime + SealGraceMs); keep a margin
                // before the deadline so a boundary-pinned target is still pressed while typeable.
                double windowStart = line.ActivationTime;
                double windowEnd = Math.Max(windowStart, line.EndTime + line.SealGraceMs - seal_margin_ms);

                foreach (var cell in line.Cells)
                {
                    if (!cell.IsTypeable)
                        continue;

                    double time = Math.Clamp(cell.TargetTime, windowStart, windowEnd);

                    // Monotonic, integral (matches live capture; lossless in .osr).
                    time = Math.Round(Math.Max(time, lastTime));
                    lastTime = time;

                    // A freestyle cell accepts any key, so the perfect play presses a fixed letter
                    // rather than the authoring marker; the frame stays inside the a-z surface the
                    // .osr mapping is pinned on, and the marker never reaches the display as a
                    // "typed" char.
                    Frames.Add(new TypeBeatReplayFrame(time, cell.IsFreestyle ? Typeability.FREESTYLE_AUTO_CHAR : cell.Expected));
                }
            }
        }
    }
}
