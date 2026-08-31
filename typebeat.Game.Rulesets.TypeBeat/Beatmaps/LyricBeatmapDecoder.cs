// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using osu.Framework.Logging;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Storyboards;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Decoder for the "type!beat file format" .osu variant (M6 format skeleton):
    /// [General]/[Metadata]/[Difficulty]/[TimingPoints] are handled by the inherited legacy
    /// parsing; a [Lyrics] section carries one compact JSON object per line: an optional
    /// header object (version/song_end_ms/granularity, no "text" key) followed by one
    /// timing.json v2 line object per lyric line, parsed by the regression-anchored
    /// <see cref="TimingJsonLoader"/> logic straight into <see cref="TypeBeatHitObject"/>s.
    ///
    /// <para>The magic line's VERSION is read (<see cref="LyricFormatVersion"/>) and decides one
    /// thing, backlog 255: whether a bracket in a stored [Lyrics] line is a backing vocal to strip
    /// (v1, and anything unversioned or unparseable) or a literal lyric mark to keep (v2 and up).
    /// See <see cref="LyricOsuFormat.FORMAT_VERSION"/> for why the version is a sound discriminator
    /// rather than a guess.</para>
    ///
    /// Registration (<see cref="Register"/>) is invoked from <see cref="TypeBeatRuleset"/>'s
    /// static constructor, which runs when RulesetStore instantiates the ruleset at startup,
    /// before both the import path (BeatmapImporter) and the load path (WorkingBeatmapCache)
    /// can ever request a decoder. Tests call it directly.
    /// </summary>
    public class LyricBeatmapDecoder : LegacyBeatmapDecoder
    {
        /// <summary>
        /// First line of the file, up to but not including the version number. Matched as a PREFIX
        /// by the decoder registry, so every version of the format routes here and the number after
        /// it is read by <see cref="ParseFormatVersion"/>.
        /// </summary>
        public const string MAGIC = @"type!beat file format v";

        /// <summary>
        /// The version a file with no readable number is treated as. It is the ORIGINAL format, so
        /// an unparseable magic line falls back to the historical reading (brackets stripped)
        /// rather than to the current one, which is the direction that cannot invent lyric content
        /// for a map that never had it.
        /// </summary>
        public const int FALLBACK_FORMAT_VERSION = 1;

        /// <summary>
        /// The first format version whose [Lyrics] brackets are LITERAL lyric marks rather than
        /// backing-vocal spans to strip (backlog 255). Below it the decode strips, at or above it
        /// the decode preserves.
        /// </summary>
        public const int LITERAL_BRACKETS_FROM_VERSION = 2;

        /// <summary>
        /// The version number off the magic line, or <see cref="FALLBACK_FORMAT_VERSION"/> when
        /// there is none to read. Only the digits immediately after <see cref="MAGIC"/> are taken,
        /// so trailing whitespace or anything else on the line is ignored rather than fatal.
        /// </summary>
        public static int ParseFormatVersion(string magicLine)
        {
            if (string.IsNullOrEmpty(magicLine) || !magicLine.StartsWith(MAGIC, StringComparison.InvariantCulture))
                return FALLBACK_FORMAT_VERSION;

            int end = MAGIC.Length;

            while (end < magicLine.Length && char.IsAsciiDigit(magicLine[end]))
                end++;

            return int.TryParse(magicLine.AsSpan(MAGIC.Length, end - MAGIC.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out int version)
                ? version
                : FALLBACK_FORMAT_VERSION;
        }

        private static bool registered;

        public static new void Register()
        {
            if (registered)
                return;

            registered = true;

            // The registry hands the factory the file's own first line, which is where the format
            // version lives; the storyboard decoder below has no use for it.
            AddDecoder<Beatmap>(MAGIC, magicLine => new LyricBeatmapDecoder(ParseFormatVersion(magicLine)));

            // The same .osu file is also decoded for storyboards (WorkingBeatmap.Storyboard);
            // without a magic match that path throws. type!beat files with an imported background
            // video DO carry an [Events] "Video,..." line, which the stock storyboard decoder turns
            // into a StoryboardVideo the Player renders behind the ruleset (see TypeBeatPlayfield).
            AddDecoder<Storyboard>(MAGIC, _ => new LegacyStoryboardDecoder());
        }

        private readonly List<TimingJsonLoader.RawLine> rawLines = new List<TimingJsonLoader.RawLine>();

        private double? songEndMs;
        private double? beatdropMs;
        private TimingGranularity? headerGranularity;

        /// <summary>
        /// The type!beat format version of the file being decoded. Deliberately NOT the base
        /// decoder's <c>FormatVersion</c>, which is the LEGACY osu version driving the inherited
        /// [General]/[Metadata]/[TimingPoints] parsing and must stay at <c>LATEST_VERSION</c>.
        /// </summary>
        public int LyricFormatVersion { get; }

        public LyricBeatmapDecoder()
            : this(LyricOsuFormat.FORMAT_VERSION)
        {
        }

        public LyricBeatmapDecoder(int lyricFormatVersion)
            : base(LATEST_VERSION)
        {
            LyricFormatVersion = lyricFormatVersion;
        }

        protected override void ParseStreamInto(LineBufferedReader stream, bool isPrimaryStream, Beatmap beatmap)
        {
            base.ParseStreamInto(stream, isPrimaryStream, beatmap);

            if (isPrimaryStream)
                finalise(beatmap);
        }

        protected override void ParseLine(Beatmap beatmap, Section section, string line, bool isPrimaryStream)
        {
            if (section == Section.Lyrics)
            {
                parseLyricLine(line);
                return;
            }

            base.ParseLine(beatmap, section, line, isPrimaryStream);
        }

        private void parseLyricLine(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return;

                if (!root.TryGetProperty("text", out _))
                {
                    // Header object: {"version":2,"song_end_ms":...,"beatdrop_ms":...,"granularity":"Word"}.
                    if (root.TryGetProperty("song_end_ms", out JsonElement songEnd) && songEnd.ValueKind == JsonValueKind.Number)
                        songEndMs = songEnd.GetDouble();

                    if (root.TryGetProperty("beatdrop_ms", out JsonElement beatdrop) && beatdrop.ValueKind == JsonValueKind.Number)
                        beatdropMs = beatdrop.GetDouble();

                    if (root.TryGetProperty("granularity", out JsonElement gran) && gran.ValueKind == JsonValueKind.String
                        && Enum.TryParse<TimingGranularity>(gran.GetString(), true, out var parsed))
                    {
                        headerGranularity = parsed;
                    }

                    return;
                }

                // THE VERSION GATE (backlog 255). From v2 on, a bracket in a stored [Lyrics] line is
                // a literal lyric mark and stays; the strip lives on the import side of the seam
                // (see TryParseRawLine). A v1 file predates that, and no v1 write path could store a
                // literal bracket, so its brackets ARE backing vocals and are stripped exactly as
                // they always were: an existing map decodes byte-identically to before.
                if (TimingJsonLoader.TryParseRawLine(root, out var rawLine,
                        stripBackingVocals: LyricFormatVersion < LITERAL_BRACKETS_FROM_VERSION))
                {
                    rawLines.Add(rawLine);
                }
            }
            catch (JsonException e)
            {
                Logger.Log($"Failed to parse [Lyrics] line \"{line}\": {e.Message}");
            }
        }

        private void finalise(Beatmap beatmap)
        {
            // The typebeat ruleset owns every map in this format, regardless of any Mode: line.
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;

            beatmap.IntroBeatdropTime = beatdropMs;

            if (rawLines.Count == 0)
                return;

            var lines = TimingJsonLoader.BuildLines(rawLines, songEndMs);

            TimingGranularity granularity = headerGranularity
                                            ?? (anyExplicitWords() ? TimingGranularity.Word : TimingGranularity.Line);

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = granularity,
                });
            }

            bool anyExplicitWords()
            {
                foreach (var raw in rawLines)
                {
                    if (raw.Words.Count > 0)
                        return true;
                }

                return false;
            }
        }
    }
}
