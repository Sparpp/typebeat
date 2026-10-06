// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.Linq;

namespace typebeat.Game.Overlays.BeatmapSet
{
    /// <summary>
    /// The set overlay's typing stats as text and graph data: BPM, word and character counts, chars per word, the
    /// peak / target / average WPM line and the pace curve. The server leaves any of these null on a row its pace
    /// backfills have not reached yet; each then reads <see cref="MISSING"/> (or nothing, for the chars/word tooltip
    /// and the graph) rather than a fabricated 0. Formats match the website's set page and song select.
    /// </summary>
    public static class TypingStatsFormat
    {
        public const string MISSING = "-";

        /// <summary>"120 bpm": the set's BPM, <c>0.##</c> as lazer showed it; 0 is the server's "unknown" ("- bpm").</summary>
        public static string Bpm(double bpm)
            => $@"{(bpm > 0 ? bpm.ToString(@"0.##", CultureInfo.InvariantCulture) : MISSING)} bpm";

        /// <summary>"1,903 chars", or "- chars" when the count is unknown.</summary>
        public static string Count(int? value, string noun)
            => $@"{(value is int v ? v.ToString(@"N0", CultureInfo.InvariantCulture) : MISSING)} {noun}";

        /// <summary>
        /// Typeable characters per word, one decimal (the shipped maps sit between 4.1 and 4.6, so a whole number would
        /// say nothing). Empty when either count is missing or there are no words.
        /// </summary>
        public static string CharsPerWord(int? words, int? chars)
            => words is int w && w > 0 && chars is int c
                ? $@"{((double)c / w).ToString(@"0.0", CultureInfo.InvariantCulture)} chars/word"
                : string.Empty;

        /// <summary>"Peak 95 · Target 70 · Avg 62": hardest first, whole numbers, as on the website.</summary>
        public static string PaceLine(double? peak, double? target, double? average)
            => $@"Peak {wpm(peak)} · Target {wpm(target)} · Avg {wpm(average)}";

        /// <summary>
        /// The raw WPM curve scaled so its tallest point is 1, for <see cref="Beatmaps.Drawables.PaceGraph"/>. Null when
        /// there is nothing to draw (no curve, or no point above 0). Non-finite points (NaN, infinity) read as 0.
        /// Scaled by the array's own maximum rather than <c>peak_wpm</c>, which is the same number by construction but can be null.
        /// </summary>
        public static float[]? NormalisedCurve(float[]? curve)
        {
            if (curve is not { Length: > 0 })
                return null;

            float max = curve.Where(float.IsFinite).DefaultIfEmpty(0).Max();

            if (max <= 0)
                return null;

            return curve.Select(v => float.IsFinite(v) ? Math.Max(0, v) / max : 0).ToArray();
        }

        private static string wpm(double? value) => value is double v ? v.ToString(@"0", CultureInfo.InvariantCulture) : MISSING;
    }
}
