// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Import;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// THE IMPORT-TIME SYLLABIFICATION PASS (backlog 363). The engine no longer syllabifies at
    /// gameplay: a word the map leaves unsubdivided plays as ONE syllable group over its unit. So a
    /// freshly imported map is syllabified ONCE, here, with the C# <see cref="Syllabifier"/>, and the
    /// result is written into the map as ordinary AUTHORED subdivisions the mapper can see, drag and
    /// delete in the editor like any other.
    ///
    /// <para>Called from the import sinks only: the LRC and TTML synthesis
    /// (<c>LyricMapImporter.SynthesizeTimingJsonFromLrc</c> / <c>SynthesizeTimingJsonFromTtml</c>), the
    /// aligner's document (<see cref="ApplyToTimingJson"/>) and the editor's own TTML import. NEVER on
    /// a load path, and never on an .osz/.osu package import or an online download, which carry a
    /// map exactly as its mapper saved it: a stored map is not rewritten (writing subdivisions into
    /// its bytes would change its gameplay fingerprint and demote a ranked set on re-upload).</para>
    ///
    /// <para>THE RULE. A unit with no syllable boundary, no pause, a syllabifiable token
    /// (<see cref="Syllabifier.IsSyllabifiable"/>; a stylised spelling stays one ungrouped word), a
    /// span longer than zero and a NATURAL split (<see cref="Syllabifier.SplitPoints"/>) into two or
    /// more syllables gets, for each natural segment start <c>c_s</c> in CELL space
    /// (<see cref="SyllableSegments.CellCuts"/> under <see cref="Typeability.IsCell"/>, of the word's
    /// <c>k</c> typeable cells), the boundary <c>unitStart + c_s * (unitEnd - unitStart) / k</c>, in
    /// exactly the operand order of the flat ramp (<c>TypingLine.syllableCharTarget</c> with no
    /// boundaries), AND the split itself as its authored <see cref="TimedUnit.SyllableSplits"/>. That
    /// reproduces the groups the gameplay-time syllabifier used to make bit for bit, the cut cells'
    /// targets bit for bit and every interior target to within floating-point rounding.</para>
    ///
    /// <para>The split is ALWAYS written, even though it equals what
    /// <see cref="SyllableSegments.Derived"/> would pick: without it the boundaries spread the cells
    /// EVENLY by index across the segments, so "apple" (2|3 over 1000 ms, boundary at 400) would put
    /// its targets at 0/160/320/520/760 instead of 0/200/400/600/800, with cell 2 80 ms before its own
    /// group opens, which is the very mismatch an authored split exists to rule out.</para>
    ///
    /// <para>A word the source already subdivided (an aligner's syllables, a pipe in an LRC line) or
    /// paused is left exactly as it is.</para>
    /// </summary>
    public static class ImportSyllables
    {
        /// <summary>
        /// The natural subdivision of one unsubdivided, unpaused word over [<paramref name="start"/>,
        /// <paramref name="end"/>], or null when the rule above gives it none.
        /// </summary>
        public static (double[] Boundaries, int[] Splits)? NaturalSubdivision(string token, double start, double end)
        {
            if (string.IsNullOrEmpty(token) || !(end > start) || !Syllabifier.IsSyllabifiable(token))
                return null;

            var splits = Syllabifier.SplitPoints(token);

            if (splits.Count == 0 || !SyllableSegments.IsAuthoredValid(token, splits.Count + 1, splits))
                return null;

            int[] cuts = SyllableSegments.CellCuts(token, splits, Typeability.IsCell);
            int k = cuts[^1];

            if (k <= 0)
                return null;

            double[] boundaries = new double[splits.Count];

            for (int s = 1; s <= splits.Count; s++)
            {
                // The flat ramp's own arithmetic, so the boundary IS the cut cell's old target.
                double boundary = start + (double)cuts[s] * (end - start) / k;

                // A segment owning no cell, or a cut the arithmetic cannot place strictly inside the
                // word, has no boundary to give: the word keeps playing as one group.
                if (!(boundary > start) || !(boundary < end) || (s > 1 && !(boundary > boundaries[s - 2])))
                    return null;

                boundaries[s - 1] = boundary;
            }

            return (boundaries, splits.ToArray());
        }

        /// <summary><paramref name="unit"/> with its natural subdivision written in, or the unit itself when it takes none.</summary>
        public static TimedUnit Apply(TimedUnit unit, string? language = null)
        {
            if (unit.SyllableBoundaries.Count > 0 || unit.Pauses.Count > 0)
                return unit;

            var subdivision = NaturalSubdivision(unit.Text, unit.StartTime, unit.EndTime);

            if (subdivision == null)
                return unit;

            var (boundaries, splits) = subdivision.Value;

            var generated = new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = unit.Source,
                Confidence = unit.Confidence,
                SyllableBoundaries = boundaries,
                SyllableSplits = splits,
                Pauses = unit.Pauses,
                Original = unit.Original,
            };
            return OriginalLyricSubdivisions.AlignAutomaticCuts(generated, language ?? LyricOriginals.DetectLanguage(new[] { unit.Original }));
        }

        /// <summary>
        /// <paramref name="lines"/> with every word the rule applies to subdivided (see the class
        /// summary). A line none of whose words changes is returned as the same object.
        /// </summary>
        public static IReadOnlyList<LyricLine> Apply(IReadOnlyList<LyricLine> lines, string? language = null)
        {
            var result = new LyricLine[lines.Count];

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                string? lineLanguage = language ?? LyricOriginals.DetectLanguage(new[] { line.Original }.Concat(line.Units.Select(u => u.Original)));
                var originals = !string.IsNullOrWhiteSpace(line.Original) && line.Units.Any(u => u.Original == null)
                    ? OriginalLyricGenerator.MatchWords(line, line.Original, BeatmapLanguageExtensions.FromCanonicalName(lineLanguage))
                    : new string?[line.Units.Count];
                var units = line.Units.Select((u, index) => Apply(u.Original == null && originals[index] != null
                    ? LrcParser.WithOriginal(u, originals[index]) : u, lineLanguage)).ToArray();

                result[i] = units.Where((u, m) => !ReferenceEquals(u, line.Units[m])).Any()
                    ? new LyricLine
                    {
                        RawText = line.RawText,
                        StartTime = line.StartTime,
                        EndTime = line.EndTime,
                        SingEndTime = line.SingEndTime,
                        Units = units,
                        SealGraceMs = line.SealGraceMs,
                        Estimated = line.Estimated,
                        Original = line.Original,
                        UnromanisedWords = line.UnromanisedWords,
                    }
                    : line;
            }

            return result;
        }

        /// <summary>
        /// The same pass over an ALIGNER-produced timing.json, which arrives already written and
        /// carries fields no writer here round-trips, so it is edited in place: every
        /// <c>words[]</c> entry with typed text, no syllable boundary strictly inside its span (an
        /// aligner writes a monosyllable as one syllable object, which loads as no boundary) and no
        /// pause gets <c>syllables[]</c> and <c>split_chars</c> from <see cref="NaturalSubdivision"/>.
        /// A line whose text carries the editor's '|' marks is left alone, since those name the
        /// mapper's own cuts. A document with nothing to change, or one that does not parse, is
        /// returned VERBATIM.
        /// </summary>
        public static string ApplyToTimingJson(string timingJson, string? language = null)
        {
            if (string.IsNullOrEmpty(timingJson))
                return timingJson;

            try
            {
                if (JsonNode.Parse(timingJson) is not JsonObject root || root["lines"] is not JsonArray lines)
                    return timingJson;

                bool changed = false;

                foreach (JsonNode? node in lines)
                {
                    if (node is not JsonObject line || line["words"] is not JsonArray words)
                        continue;

                    if (line["text"] is JsonValue lineText && lineText.TryGetValue(out string? text) && SplitMarkers.Carries(text))
                        continue;

                    var lineOriginals = new string?[words.Count];
                    if (line["original"] is JsonValue captionValue && captionValue.TryGetValue(out string? caption) && !string.IsNullOrWhiteSpace(caption))
                    {
                        var units = words.OfType<JsonObject>().Select(w => new TimedUnit
                        {
                            Text = w["text"]?.GetValue<string>() ?? "",
                            StartTime = tryNumber(w["start_ms"], out double a) ? a : 0,
                            EndTime = tryNumber(w["end_ms"], out double z) ? z : 0,
                        }).ToArray();
                        if (units.Length == words.Count && units.Length > 0)
                        {
                            var source = new LyricLine
                            {
                                RawText = string.Join(' ', units.Select(u => u.Text)), Units = units,
                                Original = caption, StartTime = units[0].StartTime, EndTime = units[^1].EndTime, SingEndTime = units[^1].EndTime,
                            };
                            lineOriginals = OriginalLyricGenerator.MatchWords(source, caption,
                                BeatmapLanguageExtensions.FromCanonicalName(language ?? LyricOriginals.DetectLanguage(new[] { caption })));
                        }
                    }
                    for (int wordIndex = 0; wordIndex < words.Count; wordIndex++)
                    {
                        JsonNode? wordNode = words[wordIndex];
                        if (wordNode is not JsonObject word
                            || word["text"] is not JsonValue textValue || !textValue.TryGetValue(out string? token) || string.IsNullOrEmpty(token)
                            || !tryNumber(word["start_ms"], out double start) || !tryNumber(word["end_ms"], out double end)
                            || word["pauses"] != null || word["pause"] != null
                            || hasInteriorSyllable(word["syllables"], start, end))
                        {
                            continue;
                        }

                        var subdivision = NaturalSubdivision(token, start, end);

                        if (subdivision == null)
                            continue;

                        var (boundaries, splits) = subdivision.Value;
                        string? original = word["original"] is JsonValue originalValue && originalValue.TryGetValue(out string? value) ? value : lineOriginals[wordIndex];
                        if (!string.IsNullOrWhiteSpace(original))
                        {
                            var aligned = OriginalLyricSubdivisions.AlignAutomaticCuts(new TimedUnit
                            {
                                Text = token, Original = original, StartTime = start, EndTime = end,
                                SyllableBoundaries = boundaries, SyllableSplits = splits,
                            }, language ?? LyricOriginals.DetectLanguage(new[] { original }));
                            boundaries = aligned.SyllableBoundaries.ToArray();
                            splits = aligned.SyllableSplits.ToArray();
                            if (boundaries.Length == 0)
                                continue;
                        }

                        var segmentTexts = SyllableSegments.SegmentTexts(token, splits);
                        var syllables = new JsonArray();

                        for (int s = 0; s <= boundaries.Length; s++)
                        {
                            syllables.Add(new JsonObject
                            {
                                ["text"] = s < segmentTexts.Count ? segmentTexts[s] : string.Empty,
                                ["start_ms"] = s == 0 ? start : boundaries[s - 1],
                                ["end_ms"] = s == boundaries.Length ? end : boundaries[s],
                            });
                        }

                        var splitChars = new JsonArray();

                        foreach (int split in splits)
                            splitChars.Add(split);

                        word["syllables"] = syllables;
                        word["split_chars"] = splitChars;
                        changed = true;
                    }
                }

                return changed ? root.ToJsonString() : timingJson;
            }
            catch (Exception)
            {
                return timingJson;
            }
        }

        private static bool hasInteriorSyllable(JsonNode? syllables, double start, double end)
        {
            if (syllables is not JsonArray array)
                return false;

            foreach (JsonNode? node in array)
            {
                if (node is JsonObject syllable && tryNumber(syllable["start_ms"], out double at) && at > start && at < end)
                    return true;
            }

            return false;
        }

        private static bool tryNumber(JsonNode? node, out double value)
        {
            value = 0;
            return node is JsonValue v && v.TryGetValue(out value);
        }
    }
}
