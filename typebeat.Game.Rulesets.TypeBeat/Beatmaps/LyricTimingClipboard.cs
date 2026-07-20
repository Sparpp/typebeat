// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The lyric editor's clipboard payloads — TIMING patterns only, never text. Serialized as
    /// JSON into the editor's string clipboard (<c>EditorClipboard.Content</c>), discriminated by
    /// <c>type</c> so paste can dispatch:
    ///
    ///  - <see cref="LineTimingsPayload"/>: one entry per copied line, each holding its units'
    ///    offsets and sung-end RELATIVE TO THE LINE START. Pasting rebases the pattern onto each
    ///    target line's own start — line boundaries are never moved (so no cascade through the
    ///    shared-boundary chain), which is exactly the repeated-chorus workflow: stamp the line
    ///    starts by ear, then paste chorus #1's internal timing onto #2/#3.
    ///  - <see cref="UnitTimingsPayload"/>: the selected word units' offsets relative to the FIRST
    ///    selected unit's start. Pasting anchors the pattern at a target word's current start.
    /// </summary>
    public static class LyricTimingClipboard
    {
        private const string line_type = "typebeat-line-timings";
        private const string unit_type = "typebeat-unit-timings";

        /// <summary>One unit's [start, end] as offsets from the payload's reference point.</summary>
        public class UnitSpan
        {
            [JsonProperty("start")]
            public double Start;

            [JsonProperty("end")]
            public double End;
        }

        /// <summary>One line's internal timing, all offsets relative to the line's StartTime.</summary>
        public class LineTimings
        {
            [JsonProperty("sing_end")]
            public double SingEndOffset;

            [JsonProperty("units")]
            public List<UnitSpan> Units = new List<UnitSpan>();
        }

        public class LineTimingsPayload
        {
            [JsonProperty("type")]
            public string Type = line_type;

            [JsonProperty("lines")]
            public List<LineTimings> Lines = new List<LineTimings>();
        }

        public class UnitTimingsPayload
        {
            [JsonProperty("type")]
            public string Type = unit_type;

            [JsonProperty("units")]
            public List<UnitSpan> Units = new List<UnitSpan>();
        }

        public static string Serialize(LineTimingsPayload payload) => JsonConvert.SerializeObject(payload);

        public static string Serialize(UnitTimingsPayload payload) => JsonConvert.SerializeObject(payload);

        /// <summary>
        /// Parses clipboard content into whichever payload it holds, or (null, null) for foreign /
        /// malformed content. Never throws — the clipboard can hold arbitrary text.
        /// </summary>
        public static (LineTimingsPayload? lines, UnitTimingsPayload? units) TryParse(string? content)
        {
            if (string.IsNullOrEmpty(content) || content[0] != '{')
                return (null, null);

            try
            {
                var probe = JsonConvert.DeserializeAnonymousType(content, new { type = string.Empty });

                switch (probe?.type)
                {
                    case line_type:
                        var lines = JsonConvert.DeserializeObject<LineTimingsPayload>(content);
                        return lines?.Lines.Count > 0 ? (lines, null) : (null, null);

                    case unit_type:
                        var units = JsonConvert.DeserializeObject<UnitTimingsPayload>(content);
                        return units?.Units.Count > 0 ? (null, units) : (null, null);

                    default:
                        return (null, null);
                }
            }
            catch (Exception)
            {
                return (null, null);
            }
        }
    }
}
