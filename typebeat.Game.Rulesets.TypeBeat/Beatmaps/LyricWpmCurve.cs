// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Typing pace ACROSS a lyric map, as a perfect player would experience it: every typeable cell
    /// of the map is laid out on the beatmap timeline in typing order, a rolling window of
    /// <see cref="WINDOW_CELLS"/> consecutive cells is swept over that sequence, and each window
    /// yields one WPM and one CPM. What comes out is the peak of each (independently) plus a
    /// downsampled curve of the WPM over map time, which song select draws as a bar graph.
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
        /// Cells per rolling window. This is <c>TypingEngine.rolling_wpm_window</c>
        /// (Gameplay/TypingEngine.cs:47-48), the window the HUD's live WPM counter averages over,
        /// and that is the whole reason the number is 30: the curve is meant to show the same
        /// "how fast are you typing RIGHT NOW" quantity the player watches during a run, measured
        /// on the map instead of on a performance.
        /// </summary>
        public const int WINDOW_CELLS = 30;

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
        /// Highest WPM of any window, in the typing-test unit of <see cref="CHARS_PER_WORD"/> cells
        /// to the word, hence exactly <see cref="PeakCpm"/> / 5. This class used to count REAL
        /// fractional words here, which made the peak a number the in-game counter could never have
        /// shown; it now IS that number, the highest reading a perfect player would have seen on the
        /// HUD's rolling counter (which averages over the same <see cref="WINDOW_CELLS"/> presses
        /// and divides by the same 5).
        /// </summary>
        public double PeakWpm { get; }

        /// <summary>
        /// Highest CPM of any window. Under the old real-word convention this was maximised
        /// INDEPENDENTLY of <see cref="PeakWpm"/>, since a burst of long words peaked the CPM and a
        /// burst of short ones the WPM. With every cell now worth exactly 1/5 of a word the two are
        /// proportional, so they always peak in the same window and the independent maximisation has
        /// nothing left to find. Both are still reported: one is the unit players compare across
        /// typing tests, the other the raw keystroke rate.
        /// </summary>
        public double PeakCpm { get; }

        /// <summary>Target time of the first typeable cell of the map; 0 when empty.</summary>
        public double StartTime { get; }

        /// <summary>Target time of the last typeable cell of the map; 0 when empty.</summary>
        public double EndTime { get; }

        /// <summary>
        /// Raw (UNNORMALISED) WPM at evenly spaced points across [<see cref="StartTime"/>,
        /// <see cref="EndTime"/>]. Bucket b takes the maximum WPM over the windows whose FIRST cell
        /// falls in it, and 0 where no window starts in it. Scaling this for display is the caller's
        /// job. Empty for a degenerate map.
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

        /// <summary>
        /// Sweeps the rolling window over <paramref name="lines"/> and returns the peaks plus a
        /// <paramref name="points"/>-point WPM curve.
        ///
        /// <para>Degenerate input (no lines, fewer than <see cref="WINDOW_CELLS"/> cells in total, a
        /// zero-length map span, a non-positive <paramref name="points"/>) returns an empty, all-zero
        /// result rather than throwing or dividing by zero.</para>
        /// </summary>
        public static LyricWpmCurve Compute(IEnumerable<LyricLine> lines, int points = DEFAULT_CURVE_POINTS)
        {
            var lineList = lines as IReadOnlyList<LyricLine> ?? lines.ToList();

            // Cell target times in typing order. That is the whole state this needs: every cell is
            // worth 1/CHARS_PER_WORD of a word, so counting cells IS counting words and the
            // per-cell fractional word shares this used to carry alongside (1/k for each of a
            // token's k chars, 0 for an inter-word space) were deleted with the real-word
            // convention that needed them.
            var targets = new List<double>();

            for (int li = 0; li < lineList.Count; li++)
            {
                var line = lineList[li];
                int lineFirstCell = targets.Count;

                // The FLAT form of TypingLine.FromLyricLine (Gameplay/TypingLine.cs:259-321): tokens
                // are whitespace-delimited, token m reads Units[min(m, Units.Count - 1)] (the line's
                // own boundaries when it has no units at all), typeable char j of the k in a token
                // targets unitStart + j*(unitEnd - unitStart)/k so the first char sits AT the unit
                // start, and every token but the last is followed by an inter-word SPACE cell at the
                // unit's end.
                //
                // SYLLABLE BOUNDARIES ARE DELIBERATELY IGNORED here even though TypingLine honours
                // them (syllableCharTarget): the server's TimedUnit carries Text/StartTime/EndTime
                // only, so reading them would make this file impossible to mirror. The effect is
                // confined to where chars sit WITHIN a single word, which a 30-cell window barely
                // resolves anyway.
                string[] tokens = line.RawText.Split(' ');

                for (int m = 0; m < tokens.Length; m++)
                {
                    string token = tokens[m];

                    TimedUnit? unit = line.Units.Count > 0 ? line.Units[Math.Min(m, line.Units.Count - 1)] : null;

                    double unitStart = unit?.StartTime ?? line.StartTime;
                    double unitEnd = unit?.EndTime ?? line.SingEndTime;

                    // k = typeable cells in this token (freestyle slots included: they cost a keypress).
                    int k = 0;

                    foreach (char ch in token)
                    {
                        if (Typeability.IsCell(ch))
                            k++;
                    }

                    int j = 0;

                    foreach (char ch in token)
                    {
                        if (!Typeability.IsCell(ch))
                            continue;

                        targets.Add(unitStart + (double)j * (unitEnd - unitStart) / k);
                        j++;
                    }

                    if (m < tokens.Length - 1)
                    {
                        // Inter-word space cell: a keypress, so it bounds a gap and counts towards
                        // both rates. It used to count towards CPM only, being no part of any word;
                        // under the typing-test convention it is a fifth of a word like every other
                        // keystroke, which is exactly why AverageCharsPerWord counts spaces too.
                        targets.Add(unitEnd);
                    }
                }

                // TypingLine's non-decreasing guard (TypingLine.cs:359-364), applied per line just as
                // it is there, so inverted source data cannot hand us a negative window span.
                for (int i = lineFirstCell + 1; i < targets.Count; i++)
                {
                    if (targets[i] < targets[i - 1])
                        targets[i] = targets[i - 1];
                }
            }

            int cellCount = targets.Count;

            if (points <= 0 || cellCount < WINDOW_CELLS)
                return new LyricWpmCurve(null, 0, 0, 0, 0);

            double first = targets[0];
            double last = targets[cellCount - 1];
            double mapSpanMs = last - first;

            if (mapSpanMs <= 0)
                return new LyricWpmCurve(null, 0, 0, 0, 0);

            double[] result = new double[points];
            double peakWpm = 0;
            double peakCpm = 0;

            for (int i = 0; i + WINDOW_CELLS <= cellCount; i++)
            {
                double spanMs = targets[i + WINDOW_CELLS - 1] - targets[i];

                if (spanMs <= 0)
                    continue;

                double minutes = spanMs / 60000.0;

                // WINDOW_CELLS - 1, not WINDOW_CELLS: n presses bound n-1 inter-key gaps, so the span
                // covers (n-1) cells' worth of typing. This is the live counter's own correction
                // (TypingEngine.cs:185-188), kept here so the two readouts agree in scale.
                double cpm = (WINDOW_CELLS - 1) / minutes;

                // The same 29 cells, in words: 29/5 of one. Both peaks are still tracked separately
                // even though wpm is now a fixed multiple of cpm, so that the invariant
                // PeakWpm == PeakCpm / CHARS_PER_WORD is a consequence of the loop rather than an
                // assumption written into it.
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

            return new LyricWpmCurve(result, peakWpm, peakCpm, first, last);
        }
    }
}
