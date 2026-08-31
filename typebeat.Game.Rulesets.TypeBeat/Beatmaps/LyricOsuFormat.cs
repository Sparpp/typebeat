// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text;
using System.Text.Json;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Writes the "type!beat file format" .osu variant from a lyriclab timing.json (v2).
    /// The single source of truth for the [Lyrics] serialization, used by the map-conversion
    /// tool (tools/TypeBeatOszConverter) and the decoder round-trip tests, so what the tool
    /// produces is exactly what <see cref="LyricBeatmapDecoder"/> parses.
    ///
    /// <para><b>Format versions.</b> The magic line's number is a real discriminator since
    /// backlog 255, not decoration (see <see cref="FORMAT_VERSION"/>): v1 files decode their
    /// [Lyrics] text with the backing-vocal strip, v2 files keep a literal bracket. Every writer
    /// here emits the current version, so a file only ever carries v1 if it was written before
    /// that change.</para>
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
        /// The format version every writer here stamps on the magic line, and the one thing that
        /// tells a reader how to read a BRACKET in a stored [Lyrics] line (backlog 255).
        ///
        /// <list type="bullet">
        /// <item><b>v1</b>: brackets are BACKING VOCALS. No write path that produced a v1 file could
        /// store a literal bracket, because every one of them ran
        /// <see cref="Typeability.StripBackingVocals"/> before the text was written or on the way
        /// back out, so a '(' in a v1 file is a backing vocal by construction and the decoder still
        /// strips it. That makes the version a perfect discriminator with no ambiguity: an existing
        /// map decodes exactly as it always has, and not one byte of it moves.</item>
        /// <item><b>v2</b>: brackets are LITERAL lyric marks, ordinary punctuation like a comma.
        /// The strip now happens only where a foreign lyrics file is INGESTED
        /// (<see cref="LrcParser"/>, <see cref="TimingJsonLoader.TryParse"/> and the .osu writer's
        /// own sweep in <c>LyricMapImporter.StripBackingVocalLines</c>), so a v2 file is already
        /// bracket-free unless a mapper typed the brackets in on purpose.</item>
        /// </list>
        ///
        /// <para>A v1 map re-saved from the editor comes back out as v2, and correctly so: its
        /// brackets were stripped when it was decoded, so the text being written carries none.</para>
        /// </summary>
        public const int FORMAT_VERSION = 2;

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
        /// <param name="videoFilename">Optional background video file name (emitted as a legacy [Events] video).</param>
        /// <param name="videoOffsetMs">The video event's own start time: the song position (ms) at
        /// which the video's first frame plays, so POSITIVE starts the video later than the song and
        /// negative starts it earlier. Whole milliseconds, because the decode side int-parses this
        /// field (<see cref="typebeat.Game.Beatmaps.Formats.Parsing.ParseInt"/>) and a throwing line
        /// is swallowed, which would silently drop the video element instead of failing loudly.
        /// 0 (the default, and every map nobody has re-synced) writes exactly the <c>Video,0,"file"</c>
        /// line this format has always written: same reason the Language line is conditional, an
        /// encoding that moved would re-hash installed maps through
        /// <c>TypeBeatRuleset.NativeEncodingsEquivalentForStatus</c>.</param>
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
                                         string? backgroundFilename = null, string? videoFilename = null, int videoOffsetMs = 0,
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

            sb.AppendLine($"{LyricBeatmapDecoder.MAGIC}{FORMAT_VERSION.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
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

                // The second field is the video's start time against the song (its offset), which is
                // what the mapper sets in song setup. An unset offset is 0, so a map nobody has
                // re-synced still writes the exact `Video,0,"file"` line it always has.
                if (!string.IsNullOrEmpty(videoFilename))
                    sb.AppendLine($"Video,{videoOffsetMs.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"{videoFilename}\"");
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

        private static readonly System.Text.RegularExpressions.Regex video_offset_field =
            new System.Text.RegularExpressions.Regex(@"^Video,-?[0-9]+,",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>
        /// Normalises the [Events] video event's OFFSET (its start time, the second field) to 0, so
        /// two encodings that differ only by how the background video is synced to the song compare
        /// equal. Same purpose as <see cref="StripBeatdrop"/>: the offset moves a decorative clip
        /// and touches neither gameplay nor scoring (the server excludes the video from its gameplay
        /// fingerprint outright), so re-syncing a video must not demote a ranked map.
        ///
        /// <para>Only the offset is normalised, never the line: a different video FILE, or a video
        /// added or removed, is a real content change and still compares as one. Anchored to the
        /// start of a line so a lyric that happens to contain "Video,12," is left alone.</para>
        /// </summary>
        public static string StripVideoOffset(string osu) => video_offset_field.Replace(osu, "Video,0,");

        private static readonly System.Text.RegularExpressions.Regex format_version_field =
            new System.Text.RegularExpressions.Regex(@"^" + System.Text.RegularExpressions.Regex.Escape(LyricBeatmapDecoder.MAGIC) + "[0-9]+",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Normalises the magic line's FORMAT VERSION away, so two encodings that differ only by it
        /// compare equal. Same purpose as <see cref="StripBeatdrop"/> and
        /// <see cref="StripVideoOffset"/>: without it, backlog 255's v1 to v2 bump would demote
        /// every ranked map to locally-modified the first time its author opened the editor and
        /// saved, since the writer's version moved under them and nothing else had to change.
        ///
        /// <para>It is safe precisely because the version says how to READ brackets and a v1 map
        /// carrying any has already lost them at decode: a save that actually changes the lyric
        /// changes the [Lyrics] lines too, and those still compare. Anchored to the start of the
        /// file, so only the magic line is touched.</para>
        /// </summary>
        public static string StripFormatVersion(string osu) => format_version_field.Replace(osu, LyricBeatmapDecoder.MAGIC);
    }
}
