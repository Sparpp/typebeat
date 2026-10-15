// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Typing pace ACROSS a lyric map, as a perfect player would experience it: every typeable cell
    /// of the map is laid out on the beatmap timeline in typing order, a rolling window of at least
    /// <see cref="WINDOW_SECONDS"/> seconds and <see cref="MIN_WINDOW_CELLS"/> cells is swept over it.
    /// Eligible windows determine the peak WPM and CPM. The graph samples pace throughout the map,
    /// including ordinary sections whose windows do not reach the peak's character floor.
    ///
    /// Kept byte-for-byte in step with the website's port
    /// (typebeat-web: Typebeat.Web.Packages.Lyrics.LyricWpmCurve) so the in-game and the on-site
    /// pace figures for a map always agree. Any change here must be mirrored there. For that reason
    /// this file depends on nothing but <see cref="LyricLine"/>, <see cref="TimedUnit"/> and
    /// <see cref="Typeability"/>: no engine types, no gameplay types, no framework types.
    /// </summary>
    public readonly struct LyricWpmCurve
    {
        /// <summary>
        /// The graph's shortest window and smoothing threshold. The graph keeps its own 16-cell
        /// smoothing rule; the star-rating model scores sparse windows without a character cutoff.
        /// </summary>
        public const double WINDOW_SECONDS = 1.5;
        public const int MIN_WINDOW_CELLS = 16;

        /// <summary>Curve resolution used by song select (one point per graph bar).</summary>
        public const int DEFAULT_CURVE_POINTS = 100;

        /// <summary>
        /// Typeable cells per word, the typing-test convention, and the same 5 as
        /// <c>TypingEngine.LiveRollingWpm</c> and <c>LyricPaceStatistics.CHARS_PER_WORD</c>.
        /// Restated here rather than referenced so this file keeps depending on nothing but
        /// <see cref="LyricLine"/>, <see cref="TimedUnit"/> and <see cref="Typeability"/> and stays
        /// mirrorable; the three must agree.
        /// </summary>
        public const double CHARS_PER_WORD = 5.0;

        private readonly double[]? curve;

        /// <summary>
        /// Highest WPM in the shortest window satisfying the shipped SR axis's minimum time and
        /// character count, in the typing-test unit of <see cref="CHARS_PER_WORD"/> cells per word.
        /// This is exactly <see cref="PeakCpm"/> / 5.
        /// </summary>
        public double PeakWpm { get; }

        /// <summary>
        /// Highest CPM of any window. Every cell is worth exactly 1/5 of a word, so CPM and WPM
        /// peak in the same window.
        /// </summary>
        public double PeakCpm { get; }

        /// <summary>Target time of the first typeable cell of the map; 0 when empty.</summary>
        public double StartTime { get; }

        /// <summary>Target time of the last typeable cell of the map; 0 when empty.</summary>
        public double EndTime { get; }

        /// <summary>
        /// Raw (UNNORMALISED) WPM at evenly spaced points across [<see cref="StartTime"/>,
        /// <see cref="EndTime"/>]. Each bucket reads a centered time window without the peak's
        /// character floor, so slower passages have a pace too. Eligible peak windows also mark
        /// their starting bucket, keeping the curve's maximum equal to <see cref="PeakWpm"/>.
        /// Empty for a degenerate map.
        /// </summary>
        public IReadOnlyList<double> Curve => curve ?? Array.Empty<double>();

        /// <summary>True when the map carried too little to measure (see <see cref="Compute"/>).</summary>
        public bool IsEmpty => curve == null || curve.Length == 0;

        private LyricWpmCurve(double[]? curve, double peakWpm, double peakCpm, double startTime, double endTime)
        {
            this.curve = curve;
            PeakWpm = peakWpm;
            PeakCpm = peakCpm;
            StartTime = startTime;
            EndTime = endTime;
        }

        // Flat word ramps deliberately ignore authored syllables, as the published curve always
        // has. Keep each letter's slot, interpolate punctuation, then project the whole line: a
        // surviving hyphen becomes a space at that slot, and collapsed/edge breaks add no cells.
        internal static IReadOnlyList<double> DefaultTargets(LyricLine line)
        {
            string raw = line.RawText;
            var rawTargets = new double?[raw.Length];
            string[] tokens = raw.Split(' ');
            int position = 0;

            for (int m = 0; m < tokens.Length; m++)
            {
                string token = tokens[m];
                TimedUnit? unit = line.Units.Count > 0 ? line.Units[Math.Min(m, line.Units.Count - 1)] : null;
                double start = unit?.StartTime ?? line.StartTime;
                double end = unit?.EndTime ?? line.SingEndTime;
                int count = token.Count(Typeability.IsCell);
                int cell = 0;

                foreach (char ch in token)
                {
                    if (Typeability.IsCell(ch))
                        rawTargets[position] = start + (double)cell++ * (end - start) / count;

                    position++;
                }

                if (m < tokens.Length - 1)
                    rawTargets[position++] = end;
            }

            for (int i = 0; i < rawTargets.Length; i++)
            {
                if (rawTargets[i].HasValue)
                    continue;

                int end = i;

                while (end + 1 < rawTargets.Length && !rawTargets[end + 1].HasValue)
                    end++;

                double? before = i > 0 ? rawTargets[i - 1] : null;
                double? after = end + 1 < rawTargets.Length ? rawTargets[end + 1] : null;
                int count = end - i + 1;

                for (int offset = 0; offset < count; offset++)
                    rawTargets[i + offset] = before is double p
                        ? (after is double q ? p + (offset + 1) * (q - p) / (count + 1) : p)
                        : after ?? line.StartTime;

                i = end;
            }

            for (int i = 1; i < rawTargets.Length; i++)
                rawTargets[i] = Math.Max(rawTargets[i]!.Value, rawTargets[i - 1]!.Value);

            var text = new StringBuilder(raw.Length);
            var sources = new List<int>(raw.Length);
            Typeability.ProjectDefault(raw, text, sources);
            var targets = new List<double>(sources.Count);

            for (int i = 0; i < sources.Count; i++)
            {
                if (Typeability.IsCell(text[i]))
                    targets.Add(rawTargets[sources[i]]!.Value);
            }

            return targets;
        }

        /// <summary>
        /// Sweeps the shortest eligible SR-sized window over <paramref name="lines"/> and returns
        /// the peaks plus a <paramref name="points"/>-point WPM curve at <paramref name="rate"/>.
        ///
        /// <para>Degenerate input (no lines, fewer than <see cref="MIN_WINDOW_CELLS"/> cells in total, a
        /// zero-length map span, a non-positive <paramref name="points"/>) returns an empty, all-zero
        /// result rather than throwing or dividing by zero.</para>
        /// </summary>
        public static LyricWpmCurve Compute(IEnumerable<LyricLine> lines, int points = DEFAULT_CURVE_POINTS, double rate = 1)
        {
            var lineList = lines.ToList();

            // Cell target times in typing order. That is the whole state this needs: every cell is
            // worth 1/CHARS_PER_WORD of a word, so counting cells IS counting words and the
            // per-cell fractional word shares this used to carry alongside (1/k for each of a
            // token's k chars, 0 for an inter-word space) were deleted with the real-word
            // convention that needed them.
            var targets = new List<double>();

            for (int li = 0; li < lineList.Count; li++)
            {
                var line = lineList[li];
                targets.AddRange(DefaultTargets(line));
            }

            int cellCount = targets.Count;

            if (points <= 0 || rate <= 0 || cellCount < MIN_WINDOW_CELLS)
                return new LyricWpmCurve(null, 0, 0, 0, 0);

            double first = targets[0];
            double last = targets[cellCount - 1];
            double mapSpanMs = last - first;

            if (mapSpanMs <= 0)
                return new LyricWpmCurve(null, 0, 0, 0, 0);

            // On sparse maps the character floor makes the shortest eligible window longer than
            // 1.5 seconds. Find the shortest span carrying 16 cells, then enforce the time floor.
            double shortestCellsMs = double.PositiveInfinity;

            for (int i = 0; i + MIN_WINDOW_CELLS <= cellCount; i++)
            {
                double span = targets[i + MIN_WINDOW_CELLS - 1] - targets[i];

                if (span >= 0)
                    shortestCellsMs = Math.Min(shortestCellsMs, span);
            }

            double windowMs = Math.Max(WINDOW_SECONDS * 1000 * rate, shortestCellsMs);
            double[] result = new double[points];
            double peakWpm = 0;
            double peakCpm = 0;
            int end = 0;

            for (int i = 0; i + MIN_WINDOW_CELLS <= cellCount; i++)
            {
                if (end < i)
                    end = i;

                while (end < cellCount && targets[end] <= targets[i] + windowMs)
                    end++;

                int cells = end - i;

                if (cells < MIN_WINDOW_CELLS)
                    continue;

                double cpm = cells * 60000.0 * rate / windowMs;
                double wpm = cpm / CHARS_PER_WORD;

                if (wpm > peakWpm)
                    peakWpm = wpm;

                if (cpm > peakCpm)
                    peakCpm = cpm;

                int bucket = (int)((targets[i] - first) / mapSpanMs * points);

                if (bucket < 0)
                    bucket = 0;

                if (bucket >= points)
                    bucket = points - 1;

                if (wpm > result[bucket])
                    result[bucket] = wpm;
            }

            // The peak needs 16 cells, but the graph should show the pace of every typed passage.
            // Sampling each bar's own centered window also fills bars with no cell exactly at their
            // timestamp. The peak sweep above remains in the curve, so its maximum stays PeakWpm.
            int lower = 0;
            int upper = 0;

            for (int bucket = 0; bucket < points; bucket++)
            {
                double center = first + (bucket + 0.5) * mapSpanMs / points;
                double start = center - windowMs / 2;
                double finish = center + windowMs / 2;

                while (lower < cellCount && targets[lower] < start)
                    lower++;

                if (upper < lower)
                    upper = lower;

                while (upper < cellCount && targets[upper] <= finish)
                    upper++;

                double wpm = (upper - lower) * 60000.0 * rate / windowMs / CHARS_PER_WORD;

                if (wpm > result[bucket])
                    result[bucket] = wpm;
            }

            return new LyricWpmCurve(result, peakWpm, peakCpm, first, last);
        }
    }
}
