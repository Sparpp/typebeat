// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Perfect-play typing-pace statistics for a lyric map, matching the engine's WPM
    /// definition (gross WPM over active line time; 1 word = 5 typed cells, spaces included).
    /// Under perfect play a line is active from its StartTime until its last typeable cell's
    /// TargetTime, so the pace is total typeable cells over the sum of those windows —
    /// instrumental gaps between lines do not dilute it.
    /// </summary>
    public readonly struct LyricPaceStatistics
    {
        /// <summary>Gross words per minute under perfect play.</summary>
        public double AverageWpm { get; init; }

        /// <summary>Gross characters per minute under perfect play (5 chars = 1 word).</summary>
        public double AverageCpm => AverageWpm * 5;

        /// <summary>Total typeable cells (chars + inter-word spaces) across all lines.</summary>
        public int TypeableCellCount { get; init; }

        /// <summary>Guards degenerate data (all targets at the line start) from exploding the rate.</summary>
        private const double min_line_active_ms = 500;

        public static LyricPaceStatistics Compute(IEnumerable<LyricLine> lines, TimingGranularity granularity)
        {
            int cells = 0;
            double activeMs = 0;

            foreach (var line in lines)
            {
                var typingLine = TypingLine.FromLyricLine(line, granularity);

                if (typingLine.TypeableCount == 0)
                    continue;

                double lastTarget = typingLine.StartTime;

                foreach (var cell in typingLine.Cells)
                {
                    if (cell.IsTypeable)
                        lastTarget = Math.Max(lastTarget, cell.TargetTime);
                }

                cells += typingLine.TypeableCount;
                activeMs += Math.Max(lastTarget - typingLine.StartTime, min_line_active_ms);
            }

            if (cells == 0 || activeMs <= 0)
                return default;

            double cpm = cells / (activeMs / 60000.0);

            return new LyricPaceStatistics
            {
                AverageWpm = cpm / 5.0,
                TypeableCellCount = cells,
            };
        }
    }
}
