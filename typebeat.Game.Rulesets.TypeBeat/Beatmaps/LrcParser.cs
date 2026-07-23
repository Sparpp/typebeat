// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Beatmaps/LrcParser.cs (regression-anchored).
// Only the namespace changed.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Pure static LRC parser; the fallback path for maps without a timing.json.
    /// Handles [mm:ss.xx]/[mm:ss.xxx] tags, multiple leading tags (duplicate the line),
    /// [offset:] shifting, a trailing bare terminator timestamp, the vocal-density cap,
    /// and char-weighted word-unit interpolation.
    /// </summary>
    public static class LrcParser
    {
        public const double MAX_MS_PER_TYPEABLE_CHAR = 350;
        public const double DEFAULT_LAST_LINE_DURATION_MS = 5000;

        public static IReadOnlyList<LyricLine> Parse(string lrcContent)
        {
            var result = new List<LyricLine>();
            if (string.IsNullOrEmpty(lrcContent))
                return result;

            // Strip a leading BOM if present.
            if (lrcContent[0] == '﻿')
                lrcContent = lrcContent.Substring(1);

            string[] rawLines = lrcContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // First pass: resolve a single [offset:] value (last occurrence wins).
            double offset = 0;

            foreach (string raw in rawLines)
            {
                if (tryReadOffset(raw, out double parsed))
                    offset = parsed;
            }

            // Second pass: collect every timestamped entry (empty text allowed; those are
            // pure boundary/terminator markers).
            var entries = new List<(double Time, string Text)>();

            foreach (string raw in rawLines)
            {
                extractEntries(raw, offset, entries);
            }

            if (entries.Count == 0)
                return result;

            // Stable sort by time so duplicated leading tags keep insertion order at ties.
            entries.Sort((a, b) => a.Time.CompareTo(b.Time));

            // Indices of entries that carry real (non-empty normalized) text.
            var emitted = new List<int>();

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Text.Length > 0)
                    emitted.Add(i);
            }

            for (int e = 0; e < emitted.Count; e++)
            {
                int idx = emitted[e];
                double start = entries[idx].Time;
                string text = entries[idx].Text;
                int typeable = Typeability.TypeableCount(text);

                double end;

                if (e < emitted.Count - 1)
                {
                    // Non-last line: hard seal at the next emitted line's start.
                    end = entries[emitted[e + 1]].Time;
                }
                else
                {
                    // Last emitted line: use a trailing terminator timestamp if one exists
                    // after it, otherwise fall back to a bounded default duration.
                    double? terminator = null;

                    for (int j = idx + 1; j < entries.Count; j++)
                    {
                        if (entries[j].Time > start)
                        {
                            terminator = entries[j].Time;
                            break;
                        }
                    }

                    end = terminator ?? start + Math.Min(DEFAULT_LAST_LINE_DURATION_MS, MAX_MS_PER_TYPEABLE_CHAR * typeable);
                }

                if (end < start)
                    end = start;

                double singEnd = start + Math.Min(end - start, MAX_MS_PER_TYPEABLE_CHAR * typeable);

                result.Add(new LyricLine
                {
                    RawText = text,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = singEnd,
                    Units = InterpolateUnits(text, start, singEnd)
                });
            }

            return result;
        }

        /// <summary>Parses "mm:ss.xx" and "mm:ss.xxx" (also tolerates "m:ss.x").</summary>
        public static bool TryParseTimestamp(string token, out double milliseconds)
        {
            milliseconds = 0;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            token = token.Trim();

            int colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1)
                return false;

            string minutesPart = token.Substring(0, colon);
            string rest = token.Substring(colon + 1);

            if (!int.TryParse(minutesPart, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
                return false;

            int dot = rest.IndexOf('.');
            string secondsPart;
            string fractionPart;

            if (dot < 0)
            {
                secondsPart = rest;
                fractionPart = string.Empty;
            }
            else
            {
                secondsPart = rest.Substring(0, dot);
                fractionPart = rest.Substring(dot + 1);
            }

            if (!int.TryParse(secondsPart, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                return false;

            double fractionMs = 0;

            if (fractionPart.Length > 0)
            {
                if (!int.TryParse(fractionPart, NumberStyles.None, CultureInfo.InvariantCulture, out int frac))
                    return false;

                // ".48" -> 480ms, ".395" -> 395ms; scale by the number of fractional digits.
                fractionMs = frac / Math.Pow(10, fractionPart.Length) * 1000.0;
            }

            milliseconds = minutes * 60000.0 + seconds * 1000.0 + fractionMs;
            return true;
        }

        /// <summary>
        /// Distributes [start, end] over the whitespace tokens of <paramref name="normalizedText"/>,
        /// weighting each token by (typeableCount + 1). Source = Interpolated.
        /// Shared by the LRC path and TimingJsonLoader's per-line fallback.
        /// </summary>
        internal static IReadOnlyList<TimedUnit> InterpolateUnits(string normalizedText, double start, double end)
        {
            var units = new List<TimedUnit>();
            if (string.IsNullOrEmpty(normalizedText))
                return units;

            string[] tokens = normalizedText.Split(' ');

            double totalWeight = 0;
            double[] weights = new double[tokens.Length];

            for (int i = 0; i < tokens.Length; i++)
            {
                weights[i] = Typeability.TypeableCount(tokens[i]) + 1;
                totalWeight += weights[i];
            }

            if (totalWeight <= 0)
                totalWeight = tokens.Length;

            double span = end - start;
            double cumulative = 0;

            for (int i = 0; i < tokens.Length; i++)
            {
                double unitStart = start + span * (cumulative / totalWeight);
                cumulative += weights[i];
                double unitEnd = start + span * (cumulative / totalWeight);

                units.Add(new TimedUnit
                {
                    Text = tokens[i],
                    StartTime = unitStart,
                    EndTime = unitEnd,
                    Source = TimingSource.Interpolated
                });
            }

            return units;
        }

        private static bool tryReadOffset(string rawLine, out double offset)
        {
            offset = 0;
            if (string.IsNullOrEmpty(rawLine))
                return false;

            int idx = 0;
            while (idx < rawLine.Length && char.IsWhiteSpace(rawLine[idx]))
                idx++;

            while (idx < rawLine.Length && rawLine[idx] == '[')
            {
                int close = rawLine.IndexOf(']', idx);
                if (close < 0)
                    return false;

                string inner = rawLine.Substring(idx + 1, close - idx - 1);
                idx = close + 1;

                int c = inner.IndexOf(':');

                if (c > 0 && inner.Substring(0, c).Trim().Equals("offset", StringComparison.OrdinalIgnoreCase))
                {
                    string value = inner.Substring(c + 1).Trim();

                    // Accept a leading '+' which int.TryParse(NumberStyles.Integer) already allows.
                    if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double parsed))
                    {
                        offset = parsed;
                        return true;
                    }
                }
            }

            return false;
        }

        private static void extractEntries(string rawLine, double offset, List<(double Time, string Text)> entries)
        {
            if (string.IsNullOrEmpty(rawLine))
                return;

            int idx = 0;
            while (idx < rawLine.Length && char.IsWhiteSpace(rawLine[idx]))
                idx++;

            var times = new List<double>();

            while (idx < rawLine.Length && rawLine[idx] == '[')
            {
                int close = rawLine.IndexOf(']', idx);
                if (close < 0)
                    break;

                string inner = rawLine.Substring(idx + 1, close - idx - 1);
                idx = close + 1;

                if (TryParseTimestamp(inner, out double ms))
                    times.Add(ms - offset);
                // Metadata tags ([ti:], [ar:], [offset:], [Lyrics], ...) are silently skipped.
            }

            if (times.Count == 0)
                return; // Non-timestamped line, skipped entirely.

            string rawText = rawLine.Substring(idx);
            string text = Typeability.Normalize(Typeability.StripBackingVocals(rawText));

            // A backing-vocal-only line (all bracketed) vanishes entirely; it must NOT linger
            // as an empty entry, or it would masquerade as a boundary/terminator marker. Genuine
            // bare-timestamp terminators had no text to begin with and pass through unchanged.
            if (text.Length == 0 && Typeability.Normalize(rawText).Length > 0)
                return;

            foreach (double t in times)
                entries.Add((t, text));
        }
    }
}
