// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Boundary-window typing-pace statistics for a lyric map. A line's typing window is the
    /// time between its start and end boundaries, the time a player actually gets, however
    /// leniently or strictly the map is bounded, not the perfect-play playhead. Counts are
    /// real: WPM uses the line's actual word count, CPM its actual typeable cell count
    /// (chars + inter-word spaces); there is no "1 word = 5 chars" estimate, so the CPM:WPM
    /// ratio reflects the map's true word length. Per-line rates are averaged unweighted
    /// across lines, so instrumental gaps between lines never dilute the pace.
    /// </summary>
    public readonly struct LyricPaceStatistics
    {
        /// <summary>Mean of per-line (words / boundary window) rates.</summary>
        public double AverageWpm { get; init; }

        /// <summary>Mean of per-line (typeable cells / boundary window) rates.</summary>
        public double AverageCpm { get; init; }

        /// <summary>Total typeable cells (chars + inter-word spaces) across all lines.</summary>
        public int TypeableCellCount { get; init; }

        /// <summary>Total words (tokens containing at least one typeable char) across all lines.</summary>
        public int WordCount { get; init; }

        /// <summary>Guards degenerate data (zero/near-zero windows) from exploding the rate.</summary>
        private const double min_line_window_ms = 500;

        public static LyricPaceStatistics Compute(IEnumerable<LyricLine> lines)
        {
            int totalCells = 0;
            int totalWords = 0;
            int lineCount = 0;
            double wpmSum = 0;
            double cpmSum = 0;

            foreach (var line in lines)
            {
                // Cell arithmetic mirrors TypingLine.FromLyricLine: every typeable char is a
                // cell, plus one typeable space cell per token gap.
                string[] tokens = line.RawText.Split(' ');

                int cells = tokens.Length - 1;
                int words = 0;

                foreach (string token in tokens)
                {
                    int typeable = 0;

                    foreach (char ch in token)
                    {
                        if (Typeability.IsTypeable(ch))
                            typeable++;
                    }

                    cells += typeable;

                    if (typeable > 0)
                        words++;
                }

                if (cells <= 0)
                    continue;

                double windowMinutes = Math.Max(line.EndTime - line.StartTime, min_line_window_ms) / 60000.0;

                wpmSum += words / windowMinutes;
                cpmSum += cells / windowMinutes;
                totalCells += cells;
                totalWords += words;
                lineCount++;
            }

            if (lineCount == 0)
                return default;

            return new LyricPaceStatistics
            {
                AverageWpm = wpmSum / lineCount,
                AverageCpm = cpmSum / lineCount,
                TypeableCellCount = totalCells,
                WordCount = totalWords,
            };
        }
    }
}
