// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The lyric editor's clipboard payloads, serialized as JSON into the editor's string clipboard
    /// (<c>EditorClipboard.Content</c>), discriminated by <c>type</c> so paste can dispatch:
    ///
    ///  - <see cref="LineTimingsPayload"/>: one entry per copied line, each holding its units'
    ///    offsets and sung-end RELATIVE TO THE LINE START. Pasting rebases the pattern onto each
    ///    target line's own start; line boundaries are never moved (so no cascade through the
    ///    shared-boundary chain), which is exactly the repeated-chorus workflow: stamp the line
    ///    starts by ear, then paste chorus #1's internal timing onto #2/#3.
    ///  - <see cref="UnitTimingsPayload"/>: the selected word units' offsets relative to the FIRST
    ///    selected unit's start. Pasting anchors the pattern at a target word's current start.
    ///
    /// <para>A line payload carries a word's SUB-WORD timing too (subdivision boundaries, the
    /// authored char split, the authored rests), because a chorus line's internal rhythm is the
    /// whole point of the gesture: see <see cref="TypeBeatEditorOperations.PasteLineTimings"/> for
    /// the transfer policy. A UNIT payload deliberately does not, and the same method's remarks say
    /// why. Every one of those fields is OPTIONAL, so a payload written before they existed still
    /// parses and simply carries no sub-word timing.</para>
    ///
    /// <para>TEXT (backlog 339). From <see cref="TEXT_VERSION"/> a LINE payload also carries the
    /// line's words, the author's form per word in <see cref="UnitSpan.Text"/>, so the default paste
    /// can put the copied line down AS IS (<see cref="TypeBeatEditorOperations.PasteLine"/>). The
    /// type discriminator is unchanged and every new field is optional, so the two builds exchange
    /// clipboards: an old build reads a new payload as timings (it ignores the fields it does not
    /// know), and a new build reads an old payload as timing-only under both paste gestures
    /// (<see cref="LineTimingsPayload.CarriesText"/> is false for it). The timing-only paste
    /// (<see cref="TypeBeatEditorOperations.PasteLineTimings"/>) never reads the text.</para>
    ///
    /// <para>The per-word <c>text</c> lives on <see cref="UnitSpan"/>, beside the char-indexed fields
    /// it spells out, so a UNIT payload can carry it the same way (backlog 343) without a second
    /// shape. <see cref="UnitSpan.Chars"/> stays: it is the gate the timing-only paste still uses.</para>
    /// </summary>
    public static class LyricTimingClipboard
    {
        private const string line_type = "typebeat-line-timings";
        private const string unit_type = "typebeat-unit-timings";

        /// <summary>
        /// The <see cref="LineTimingsPayload.Version"/> from which a line payload carries its words'
        /// text. A payload without the field reads as 0 (the timing-only payload every earlier build
        /// wrote).
        /// </summary>
        public const int TEXT_VERSION = 2;

        /// <summary>One authored rest inside a word: its [start, end] as offsets from the payload's
        /// reference point, plus the character index it sits after (see <see cref="WordPause"/>).</summary>
        public class RestSpan
        {
            [JsonProperty("start")]
            public double Start;

            [JsonProperty("end")]
            public double End;

            [JsonProperty("split_char")]
            public int SplitChar;
        }

        /// <summary>One unit's [start, end] as offsets from the payload's reference point.</summary>
        public class UnitSpan
        {
            [JsonProperty("start")]
            public double Start;

            [JsonProperty("end")]
            public double End;

            /// <summary>
            /// The source word's character count, or 0 when the payload predates the field. The gate
            /// on every CHAR-INDEXED field below: same count means the cut lands on the same
            /// character of the repeated word, so it can travel.
            /// </summary>
            [JsonProperty("chars")]
            public int Chars;

            /// <summary>
            /// The source word's text in the AUTHOR'S form (<see cref="TimedUnit.Text"/>, the token of
            /// <see cref="LyricLine.RawText"/> the map stores, freestyle markers included), or null on
            /// a payload that carries no text: every payload written before backlog 339, and (until
            /// backlog 343) every UNIT payload. Set by <see cref="TypeBeatEditorOperations.CopyLineTimings"/>.
            ///
            /// <para>The word's ORIGINAL-script spelling (backlog 330) travels beside it in
            /// <see cref="Original"/>, so a romanised word and its source script move together.</para>
            /// </summary>
            [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)]
            public string? Text;

            /// <summary>
            /// The source word's <see cref="TimedUnit.Original"/> (backlog 330), or null when it has
            /// none (and on every payload written before the field). Optional like every field here,
            /// so an older build reads a payload carrying it and simply drops it. Only the text paste
            /// (<see cref="TypeBeatEditorOperations.PasteLine"/>) reads it; a timing paste keeps the
            /// target words' own originals, as it keeps their own text.
            /// </summary>
            [JsonProperty("original", NullValueHandling = NullValueHandling.Ignore)]
            public string? Original;

            /// <summary>
            /// <see cref="TimedUnit.SyllableBoundaries"/> as offsets from the payload's reference
            /// point, or null for an undivided word (and for any payload predating the field).
            /// </summary>
            [JsonProperty("syllables", NullValueHandling = NullValueHandling.Ignore)]
            public List<double>? Boundaries;

            /// <summary>
            /// <see cref="TimedUnit.SyllableSplits"/> verbatim (char indices into the SOURCE word),
            /// or null when the source word's split was derived rather than authored.
            /// </summary>
            [JsonProperty("split_chars", NullValueHandling = NullValueHandling.Ignore)]
            public List<int>? Splits;

            /// <summary><see cref="TimedUnit.Pauses"/>, or null for a word with no authored rest.</summary>
            [JsonProperty("rests", NullValueHandling = NullValueHandling.Ignore)]
            public List<RestSpan>? Rests;
        }

        /// <summary>One line's internal timing, all offsets relative to the line's StartTime.</summary>
        public class LineTimings
        {
            [JsonProperty("sing_end")]
            public double SingEndOffset;

            [JsonProperty("units")]
            public List<UnitSpan> Units = new List<UnitSpan>();

            /// <summary>The source line's <see cref="LyricLine.SealGraceMs"/>, or null on a payload predating the field.</summary>
            [JsonProperty("seal_grace", NullValueHandling = NullValueHandling.Ignore)]
            public double? SealGraceMs;

            /// <summary>The source line's <see cref="LyricLine.Estimated"/>, or null on a payload predating the field.</summary>
            [JsonProperty("estimated", NullValueHandling = NullValueHandling.Ignore)]
            public bool? Estimated;

            /// <summary>
            /// The source line's <see cref="Objects.TypeBeatHitObject.Granularity"/>, or null on a payload
            /// predating the field. A LINE-granularity source holds interpolated words, and the text
            /// paste keeps them interpolated rather than promoting the target map on their account.
            /// </summary>
            [JsonProperty("granularity", NullValueHandling = NullValueHandling.Ignore)]
            public TimingGranularity? Granularity;

            /// <summary>
            /// The source line's own <see cref="LyricLine.Original"/> (backlog 330), or null. Read by
            /// the text paste alone. The line's unromanised words do not travel: they have no text to
            /// paste, and the original here still spells them out.
            /// </summary>
            [JsonProperty("original", NullValueHandling = NullValueHandling.Ignore)]
            public string? Original;

            /// <summary>
            /// Whether this line spells out every word: at least one word, and each one's
            /// <see cref="UnitSpan.Text"/> a single non-empty token.
            /// </summary>
            [JsonIgnore]
            public bool HasText => Units.Count > 0 && Units.All(u => !string.IsNullOrEmpty(u.Text) && !u.Text.Contains(' '));

            /// <summary>The line as the map stores it (the words joined by single spaces), or null without <see cref="HasText"/>.</summary>
            [JsonIgnore]
            public string? RawText => HasText ? string.Join(' ', Units.Select(u => u.Text)) : null;
        }

        public class LineTimingsPayload
        {
            [JsonProperty("type")]
            public string Type = line_type;

            /// <summary>
            /// 0 (absent) for the timing-only payload; <see cref="TEXT_VERSION"/> once the lines carry
            /// their words. The marker, together with every line actually spelling its words out, is
            /// what the default paste dispatches on.
            /// </summary>
            [JsonProperty("version", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public int Version;

            [JsonProperty("lines")]
            public List<LineTimings> Lines = new List<LineTimings>();

            /// <summary>
            /// Whether the default paste can put these lines down AS IS (words and timing together):
            /// the payload is at least <see cref="TEXT_VERSION"/> and every line has its text. Anything
            /// else pastes timing-only, whichever gesture asked.
            /// </summary>
            [JsonIgnore]
            public bool CarriesText => Version >= TEXT_VERSION && Lines.Count > 0 && Lines.All(l => l.HasText);

            /// <summary>
            /// The copied lyrics as plain text, one line per copied line, for the OS clipboard (so a
            /// copied line pastes into a text editor). Null when the payload carries no text.
            /// </summary>
            public string? PlainText() => CarriesText ? string.Join('\n', Lines.Select(l => l.RawText)) : null;
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
        /// malformed content. Never throws; the clipboard can hold arbitrary text.
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
