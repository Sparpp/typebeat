// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported from type!beat TypeBeat.Game/Beatmaps/TimingJsonLoader.cs (regression-anchored).
// Logic preserved exactly; restructured only to expose two seams the fork's
// LyricBeatmapDecoder reuses: TryParseRawLine (one timing.json line object -> RawLine)
// and BuildLines (RawLine list -> LyricLines). TryLoad behaviour is unchanged.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Loads a lyriclab timing.json (version 2) into <see cref="LyricLine"/>s with Explicit
    /// word-level <see cref="TimedUnit"/>s. Never throws: any whole-file problem returns false
    /// with an empty list so the caller can fall back to the LRC path.
    /// </summary>
    public static class TimingJsonLoader
    {
        public const int SUPPORTED_VERSION = 2;
        public const double LAST_LINE_TAIL_MS = 3000;

        /// <summary>Cap on the per-line seal grace granted for vocals overrunning the line boundary.</summary>
        public const double MAX_SEAL_GRACE_MS = 700;

        // NOTE on engine.offset_ms: the producer (typebeat-lyriclab align_lyrics.py) bakes the
        // offset into every start_ms/end_ms it writes (frame_ms() adds it before serialization),
        // so engine.offset_ms is a RECORD of what was applied, not a pending correction.
        // Deliberately not applied here; subtracting it would double-count the shift.

        public static bool TryLoad(string timingJsonPath, out IReadOnlyList<LyricLine> lines)
        {
            lines = Array.Empty<LyricLine>();

            if (string.IsNullOrEmpty(timingJsonPath) || !File.Exists(timingJsonPath))
                return false;

            return TryParse(File.ReadAllText(timingJsonPath), out lines);
        }

        /// <summary>
        /// In-memory equivalent of <see cref="TryLoad"/>: parses timing.json (v2) text directly.
        /// Used by the editor's align-in-place import, which holds the aligner output as a string.
        /// </summary>
        public static bool TryParse(string json, out IReadOnlyList<LyricLine> lines)
        {
            lines = Array.Empty<LyricLine>();

            try
            {
                if (string.IsNullOrEmpty(json))
                    return false;

                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                if (!root.TryGetProperty("version", out JsonElement versionElement)
                    || !tryGetInt(versionElement, out int version)
                    || version != SUPPORTED_VERSION)
                {
                    return false;
                }

                double? songEndMs = null;

                if (root.TryGetProperty("song_end_ms", out JsonElement songEndElement)
                    && tryGetDouble(songEndElement, out double songEndValue))
                {
                    songEndMs = songEndValue;
                }

                if (!root.TryGetProperty("lines", out JsonElement linesElement)
                    || linesElement.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                // Collect usable raw line data first (skip empty-normalized text lines).
                var raw = new List<RawLine>();

                foreach (JsonElement lineElement in linesElement.EnumerateArray())
                {
                    if (TryParseRawLine(lineElement, out var rawLine))
                        raw.Add(rawLine);
                }

                if (raw.Count == 0)
                    return false;

                lines = BuildLines(raw, songEndMs);
                return true;
            }
            catch
            {
                lines = Array.Empty<LyricLine>();
                return false;
            }
        }

        /// <summary>
        /// Parses one timing.json "lines[]" element into a <see cref="RawLine"/>.
        /// False for non-objects, missing text/start_ms, and lines whose text normalizes to
        /// empty (whole-line bracketed backing vocals, dropped so the previous line extends
        /// over their span, which also dissolves the overlapping-lines case at its source).
        /// A partial strip changes the token count, so the words[] alignment in
        /// <see cref="BuildLines"/> falls back to interpolation for that line, which is acceptable.
        /// Besides the aligner's own fields, two type!beat editor extensions are honoured here:
        /// <c>seal_grace_ms</c> and <c>freestyle</c> (both documented at their read sites below).
        /// </summary>
        public static bool TryParseRawLine(JsonElement lineElement, out RawLine rawLine)
        {
            rawLine = default;

            if (lineElement.ValueKind != JsonValueKind.Object)
                return false;

            if (!lineElement.TryGetProperty("text", out JsonElement textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            // Opt-in freestyle authoring (type!beat extension, written by the editor's encoder):
            // "freestyle": true declares that the ampersands in this line's text are FREESTYLE
            // CELL markers rather than lyric punctuation. Without the flag the text normalizes
            // exactly as it always has (ampersands stripped), so every map produced before this
            // feature, and every aligner line whose lyrics genuinely contain "&", decodes unchanged.
            bool freestyle = lineElement.TryGetProperty("freestyle", out JsonElement freestyleElement)
                             && freestyleElement.ValueKind == JsonValueKind.True;

            string normalized = Typeability.Normalize(Typeability.StripBackingVocals(textElement.GetString() ?? string.Empty), keepFreestyleMarkers: freestyle);
            if (normalized.Length == 0)
                return false;

            if (!lineElement.TryGetProperty("start_ms", out JsonElement startElement)
                || !tryGetDouble(startElement, out double startMs))
            {
                return false;
            }

            double endMs = startMs;

            if (lineElement.TryGetProperty("end_ms", out JsonElement endElement)
                && tryGetDouble(endElement, out double parsedEnd))
            {
                endMs = parsedEnd;
            }

            bool estimated = lineElement.TryGetProperty("estimated", out JsonElement estimatedElement)
                             && estimatedElement.ValueKind == JsonValueKind.True;

            var words = new List<(string Text, double Start, double End, double Score, List<double> Syllables)>();

            if (lineElement.TryGetProperty("words", out JsonElement wordsElement)
                && wordsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement wordElement in wordsElement.EnumerateArray())
                {
                    if (wordElement.ValueKind != JsonValueKind.Object)
                        continue;

                    string wordText = wordElement.TryGetProperty("text", out JsonElement wt) && wt.ValueKind == JsonValueKind.String
                        ? wt.GetString() ?? string.Empty
                        : string.Empty;

                    double ws = wordElement.TryGetProperty("start_ms", out JsonElement wsEl) && tryGetDouble(wsEl, out double wsv) ? wsv : startMs;
                    double we = wordElement.TryGetProperty("end_ms", out JsonElement weEl) && tryGetDouble(weEl, out double wev) ? wev : ws;

                    // Missing score = trusted (1); only genuinely low-margin words widen windows.
                    double score = wordElement.TryGetProperty("score", out JsonElement scEl) && tryGetDouble(scEl, out double scv) ? scv : 1;

                    // Optional syllable subdivisions: each syllable's start_ms strictly inside the
                    // word becomes an internal boundary (the first syllable starts at the word start,
                    // so it contributes no boundary). Both the aligner and the editor emit these.
                    var syllables = new List<double>();

                    if (wordElement.TryGetProperty("syllables", out JsonElement sylsEl) && sylsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement sylEl in sylsEl.EnumerateArray())
                        {
                            if (sylEl.ValueKind == JsonValueKind.Object
                                && sylEl.TryGetProperty("start_ms", out JsonElement sylStart)
                                && tryGetDouble(sylStart, out double sylMs)
                                && sylMs > ws && sylMs < we)
                            {
                                syllables.Add(sylMs);
                            }
                        }
                    }

                    words.Add((wordText, ws, we, score, syllables));
                }
            }

            // Optional explicit seal grace, written by the editor's encoder so the grace derived
            // from raw (pre-clamp) word overruns survives a save/reload round-trip, where the
            // re-emitted word times are clamped and the overrun can no longer be recomputed.
            double? sealGraceMs = null;

            if (lineElement.TryGetProperty("seal_grace_ms", out JsonElement sealGraceElement)
                && tryGetDouble(sealGraceElement, out double parsedSealGrace))
            {
                sealGraceMs = parsedSealGrace;
            }

            rawLine = new RawLine(normalized, startMs, endMs, estimated, words, sealGraceMs);
            return true;
        }

        /// <summary>
        /// Resolves per-line End/SingEnd/SealGrace and word units for a full ordered set of
        /// <see cref="RawLine"/>s (a non-last line's hard seal is the next line's start).
        /// </summary>
        public static IReadOnlyList<LyricLine> BuildLines(IReadOnlyList<RawLine> raw, double? songEndMs)
        {
            var result = new List<LyricLine>(raw.Count);

            for (int i = 0; i < raw.Count; i++)
            {
                RawLine line = raw[i];
                double start = line.StartMs;

                double end;

                if (i < raw.Count - 1)
                {
                    end = raw[i + 1].StartMs;
                }
                else
                {
                    double tailEnd = line.EndMs + LAST_LINE_TAIL_MS;
                    end = songEndMs.HasValue ? Math.Min(songEndMs.Value, tailEnd) : tailEnd;
                }

                if (end < start)
                    end = start;

                double singEnd = Math.Clamp(line.EndMs, start, end);

                // Vocals genuinely overrunning the line boundary (backing vocals overlapping the
                // next line) get a bounded seal grace so their clamped tail cells stay hittable.
                double rawWordsEnd = start;

                foreach (var w in line.Words)
                    rawWordsEnd = Math.Max(rawWordsEnd, Math.Max(w.Start, w.End));

                // An explicit seal grace (editor round-trip) wins over the derived overrun.
                double sealGrace = line.SealGraceMs is double explicitGrace
                    ? Math.Clamp(explicitGrace, 0, MAX_SEAL_GRACE_MS)
                    : Math.Min(Math.Max(0, rawWordsEnd - end), MAX_SEAL_GRACE_MS);

                string[] tokens = line.Text.Split(' ');
                IReadOnlyList<TimedUnit> units;

                if (tokens.Length == line.Words.Count && tokens.Length > 0)
                {
                    units = buildExplicitUnits(tokens, line.Words, start, end);
                }
                else
                {
                    // Per-line fallback: char-weighted interpolation across [start, singEnd].
                    units = LrcParser.InterpolateUnits(line.Text, start, singEnd);
                }

                result.Add(new LyricLine
                {
                    RawText = line.Text,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = singEnd,
                    Units = units,
                    SealGraceMs = sealGrace,
                    Estimated = line.Estimated
                });
            }

            return result;
        }

        private static IReadOnlyList<TimedUnit> buildExplicitUnits(
            string[] tokens,
            List<(string Text, double Start, double End, double Score, List<double> Syllables)> words,
            double lineStart,
            double lineEnd)
        {
            var units = new List<TimedUnit>(tokens.Length);
            double prevEnd = lineStart;

            for (int m = 0; m < tokens.Length; m++)
            {
                double ws = Math.Clamp(words[m].Start, lineStart, lineEnd);
                double we = Math.Clamp(words[m].End, ws, lineEnd);

                // Enforce non-decreasing across units.
                if (ws < prevEnd)
                    ws = prevEnd;
                if (we < ws)
                    we = ws;

                // Keep only subdivisions that stayed strictly inside the (possibly clamped) word.
                var syllables = words[m].Syllables.Where(b => b > ws && b < we).Distinct().OrderBy(b => b).ToArray();

                units.Add(new TimedUnit
                {
                    Text = tokens[m],
                    StartTime = ws,
                    EndTime = we,
                    Source = TimingSource.Explicit,
                    Confidence = Math.Clamp(words[m].Score, 0, 1),
                    SyllableBoundaries = syllables.Length == 0 ? System.Array.Empty<double>() : syllables,
                });

                prevEnd = we;
            }

            return units;
        }

        private static bool tryGetInt(JsonElement element, out int value)
        {
            value = 0;

            if (element.ValueKind != JsonValueKind.Number)
                return false;

            if (element.TryGetInt32(out value))
                return true;

            // JSON doesn't distinguish 2 from 2.0; accept whole-number float tokens too.
            if (element.TryGetDouble(out double d) && d == Math.Floor(d) && d >= int.MinValue && d <= int.MaxValue)
            {
                value = (int)d;
                return true;
            }

            return false;
        }

        private static bool tryGetDouble(JsonElement element, out double value)
        {
            value = 0;
            if (element.ValueKind == JsonValueKind.Number)
                return element.TryGetDouble(out value);

            return false;
        }

        public readonly record struct RawLine(
            string Text,
            double StartMs,
            double EndMs,
            bool Estimated,
            List<(string Text, double Start, double End, double Score, List<double> Syllables)> Words,
            double? SealGraceMs = null);
    }
}
