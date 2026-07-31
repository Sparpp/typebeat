// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text;
using System.Text.Json;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Writes the "type!beat file format v1" .osu variant from a lyriclab timing.json (v2).
    /// The single source of truth for the [Lyrics] serialization, used by the map-conversion
    /// tool (tools/TypeBeatOszConverter) and the decoder round-trip tests, so what the tool
    /// produces is exactly what <see cref="LyricBeatmapDecoder"/> parses.
    ///
    /// <para><b>type!beat line-object extensions</b> (beyond the aligner's own schema; both are
    /// optional, and a line without them decodes exactly as it always has):</para>
    /// <list type="bullet">
    /// <item><c>"seal_grace_ms"</c>: extra typeable time past the line boundary, persisted because
    /// it cannot be re-derived from clamped word times.</item>
    /// <item><c>"freestyle": true</c>: the ampersands in this line's <c>text</c> are FREESTYLE CELL
    /// markers (<see cref="Typeability.FREESTYLE_MARKER"/>), cells the player may satisfy with any
    /// key but space. The flag is what makes the marker unambiguous: without it an ampersand is ordinary
    /// untypeable lyric punctuation and is stripped on decode, so lyrics that merely contain "&amp;"
    /// (and every map written before the feature existed) are unaffected.</item>
    /// </list>
    /// </summary>
    public static class LyricOsuFormat
    {
        /// <summary>
        /// Generates the full .osu text. Each timing.json line object is re-emitted compact
        /// (one per line, full fidelity including fields the decoder ignores) so the .osu
        /// remains the map's provenance-complete lyric source.
        /// </summary>
        /// <param name="artist">Romanised [Metadata] Artist.</param>
        /// <param name="title">Romanised [Metadata] Title.</param>
        /// <param name="audioFilename">Audio file name as stored in the beatmap set.</param>
        /// <param name="creator">Creator metadata tag.</param>
        /// <param name="timingJsonText">Source lyriclab timing.json (version 2).</param>
        /// <param name="previewTime">Menu preview start (ms); -1 for none (osu default).</param>
        /// <param name="audioLeadIn">Silent lead-in before the map starts (ms).</param>
        /// <param name="beatdropMs">Optional intro beatdrop timestamp (ms); null when unset.</param>
        /// <param name="backgroundFilename">Optional background image file name (emitted as a legacy [Events] background).</param>
        /// <param name="videoFilename">Optional background video file name (emitted as a legacy [Events] video, offset 0).</param>
        /// <param name="beatmapId">Server-side beatmap ID; omitted from [Metadata] unless positive.</param>
        /// <param name="beatmapSetId">Server-side beatmap set ID; omitted from [Metadata] unless positive.</param>
        /// <param name="difficultyName">Difficulty name (the [Metadata] Version), so a set can hold
        /// several difficulties without their identities colliding; defaults to "type!beat".</param>
        /// <param name="tags">Space-separated [Metadata] Tags, exactly what the author set in the
        /// editor (empty when unset; there are no default tags). Flows to the website's
        /// beatmapsets.tags on submission.</param>
        /// <param name="titleUnicode">Original (non-romanised) [Metadata] TitleUnicode; falls back
        /// to <paramref name="title"/> when unset, so a map without a separate original never
        /// writes a blank TitleUnicode line.</param>
        /// <param name="artistUnicode">Original (non-romanised) [Metadata] ArtistUnicode; falls back
        /// to <paramref name="artist"/> when unset.</param>
        /// <param name="language">Canonical lowercase song language
        /// (<see cref="typebeat.Game.Beatmaps.BeatmapLanguageExtensions.ToCanonicalName"/>), chosen by
        /// the mapper in song setup. Null/empty (an unspecified map) writes NO Language line at all,
        /// which is what keeps every pre-task-58 map's encoding byte-identical, so adding this field
        /// cannot demote a ranked map to locally-modified. Flows to the website's
        /// beatmapsets.language on submission.</param>
        /// <exception cref="ArgumentException">When the timing.json is not a supported v2 document.</exception>
        public static string GenerateOsu(string artist, string title, string audioFilename, string creator, string timingJsonText,
                                         double previewTime = -1, double audioLeadIn = 0, double? beatdropMs = null,
                                         string? backgroundFilename = null, string? videoFilename = null,
                                         int beatmapId = -1, int beatmapSetId = -1, string difficultyName = "type!beat",
                                         string tags = "", string? titleUnicode = null, string? artistUnicode = null,
                                         string? language = null)
        {
            using var doc = JsonDocument.Parse(timingJsonText);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || (int)version.GetDouble() != TimingJsonLoader.SUPPORTED_VERSION)
            {
                throw new ArgumentException("Not a supported timing.json v2 document.", nameof(timingJsonText));
            }

            if (!root.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("timing.json has no lines[] array.", nameof(timingJsonText));

            double? songEndMs = null;
            if (root.TryGetProperty("song_end_ms", out JsonElement songEnd) && songEnd.ValueKind == JsonValueKind.Number)
                songEndMs = songEnd.GetDouble();

            bool anyWords = false;
            bool anySyllables = false;

            foreach (JsonElement line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object
                    || !line.TryGetProperty("words", out JsonElement words)
                    || words.ValueKind != JsonValueKind.Array || words.GetArrayLength() == 0)
                {
                    continue;
                }

                anyWords = true;

                foreach (JsonElement word in words.EnumerateArray())
                {
                    if (word.ValueKind == JsonValueKind.Object
                        && word.TryGetProperty("syllables", out JsonElement syls)
                        && syls.ValueKind == JsonValueKind.Array && syls.GetArrayLength() > 1)
                    {
                        anySyllables = true;
                        break;
                    }
                }

                if (anySyllables)
                    break;
            }

            var sb = new StringBuilder();

            sb.AppendLine("type!beat file format v1");
            sb.AppendLine();
            sb.AppendLine("[General]");
            sb.AppendLine($"AudioFilename: {audioFilename}");
            sb.AppendLine($"AudioLeadIn: {formatMs(audioLeadIn)}");
            sb.AppendLine($"PreviewTime: {formatMs(previewTime)}");
            sb.AppendLine("Countdown: 0");
            sb.AppendLine("SampleSet: None");
            sb.AppendLine();
            sb.AppendLine("[Metadata]");
            sb.AppendLine($"Title:{title}");
            sb.AppendLine($"TitleUnicode:{(string.IsNullOrEmpty(titleUnicode) ? title : titleUnicode)}");
            sb.AppendLine($"Artist:{artist}");
            sb.AppendLine($"ArtistUnicode:{(string.IsNullOrEmpty(artistUnicode) ? artist : artistUnicode)}");
            sb.AppendLine($"Creator:{creator}");
            sb.AppendLine($"Version:{(string.IsNullOrWhiteSpace(difficultyName) ? "type!beat" : difficultyName)}");
            // Legacy [Metadata] Tags is a single line; strip stray newlines defensively. No default
            // tags; an unset field writes an empty Tags line.
            string sanitizedTags = (tags ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            sb.AppendLine($"Tags:{sanitizedTags}");

            // Song language (task 58). Emitted ONLY when the mapper has chosen one, so a map that
            // has not been through the new setup field encodes exactly as it did before this key
            // existed (TypeBeatRuleset.NativeEncodingsEquivalentForStatus compares encodings, and
            // an unconditional line would re-hash every map in every install).
            if (!string.IsNullOrWhiteSpace(language))
                sb.AppendLine($"Language:{language.Trim()}");

            // Online IDs are stamped on submission; the server validates the embedded IDs
            // against the set being uploaded, and the inherited legacy [Metadata] parsing
            // reads them back on decode.
            if (beatmapId > 0)
                sb.AppendLine($"BeatmapID:{beatmapId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (beatmapSetId > 0)
                sb.AppendLine($"BeatmapSetID:{beatmapSetId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine("[Difficulty]");
            sb.AppendLine("HPDrainRate:5");
            sb.AppendLine("CircleSize:5");
            sb.AppendLine("OverallDifficulty:5");
            sb.AppendLine("ApproachRate:5");
            sb.AppendLine("SliderMultiplier:1.4");
            sb.AppendLine("SliderTickRate:1");

            // Legacy [Events] syntax so the inherited legacy beatmap/storyboard parsing picks the
            // background and video up on decode without any custom handling.
            if (!string.IsNullOrEmpty(backgroundFilename) || !string.IsNullOrEmpty(videoFilename))
            {
                sb.AppendLine();
                sb.AppendLine("[Events]");
                sb.AppendLine("//Background and Video events");

                if (!string.IsNullOrEmpty(backgroundFilename))
                    sb.AppendLine($"0,0,\"{backgroundFilename}\",0,0");

                if (!string.IsNullOrEmpty(videoFilename))
                    sb.AppendLine($"Video,0,\"{videoFilename}\"");
            }

            sb.AppendLine();
            sb.AppendLine("[TimingPoints]");
            sb.AppendLine("0,500,4,2,0,100,1,0");
            sb.AppendLine();
            sb.AppendLine("[Lyrics]");

            // Header object first (no "text" key): version / song_end_ms / granularity.
            var header = new StringBuilder();
            header.Append($"{{\"version\":{TimingJsonLoader.SUPPORTED_VERSION}");
            if (songEndMs is double end)
                header.Append($",\"song_end_ms\":{end.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (beatdropMs is double drop)
                header.Append($",\"beatdrop_ms\":{drop.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            header.Append($",\"granularity\":\"{(anySyllables ? TimingGranularity.Syllable : anyWords ? TimingGranularity.Word : TimingGranularity.Line)}\"}}");
            sb.AppendLine(header.ToString());

            foreach (JsonElement line in lines.EnumerateArray())
            {
                // Re-serializing a JsonElement emits compact single-line JSON.
                sb.AppendLine(JsonSerializer.Serialize(line));
            }

            return sb.ToString();
        }

        /// <summary>Legacy [General] timing values are whole milliseconds; round and force invariant.</summary>
        private static string formatMs(double ms)
            => ((long)Math.Round(ms)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static readonly System.Text.RegularExpressions.Regex beatdrop_field =
            new System.Text.RegularExpressions.Regex(",?\"beatdrop_ms\":[-0-9.eE+]+", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Removes the menu-only <c>beatdrop_ms</c> field (in the [Lyrics] header) from a type!beat
        /// .osu, so two encodings that differ only by the beatdrop compare equal. Used to keep a
        /// beatdrop-only editor save from demoting a ranked map's online status. The encoder always
        /// writes the field after another header key, so the leading comma is what's stripped.
        /// </summary>
        public static string StripBeatdrop(string osu) => beatdrop_field.Replace(osu, string.Empty);
    }
}
