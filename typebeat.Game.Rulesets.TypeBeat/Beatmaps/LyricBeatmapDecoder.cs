// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
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
    /// Decoder for the "type!beat file format v1" .osu variant (M6 format skeleton):
    /// [General]/[Metadata]/[Difficulty]/[TimingPoints] are handled by the inherited legacy
    /// parsing; a [Lyrics] section carries one compact JSON object per line — an optional
    /// header object (version/song_end_ms/granularity, no "text" key) followed by one
    /// timing.json v2 line object per lyric line, parsed by the regression-anchored
    /// <see cref="TimingJsonLoader"/> logic straight into <see cref="TypeBeatHitObject"/>s.
    ///
    /// Registration (<see cref="Register"/>) is invoked from <see cref="TypeBeatRuleset"/>'s
    /// static constructor, which runs when RulesetStore instantiates the ruleset at startup —
    /// before both the import path (BeatmapImporter) and the load path (WorkingBeatmapCache)
    /// can ever request a decoder. Tests call it directly.
    /// </summary>
    public class LyricBeatmapDecoder : LegacyBeatmapDecoder
    {
        /// <summary>First line of the file; the version suffix is currently informational.</summary>
        public const string MAGIC = @"type!beat file format v";

        private static bool registered;

        public static new void Register()
        {
            if (registered)
                return;

            registered = true;

            AddDecoder<Beatmap>(MAGIC, _ => new LyricBeatmapDecoder());

            // The same .osu file is also decoded for storyboards (WorkingBeatmap.Storyboard);
            // without a magic match that path throws. type!beat files carry no [Events], so a
            // stock storyboard decoder yields an empty storyboard.
            AddDecoder<Storyboard>(MAGIC, _ => new LegacyStoryboardDecoder());
        }

        private readonly List<TimingJsonLoader.RawLine> rawLines = new List<TimingJsonLoader.RawLine>();

        private double? songEndMs;
        private double? beatdropMs;
        private TimingGranularity? headerGranularity;

        public LyricBeatmapDecoder()
            : base(LATEST_VERSION)
        {
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

                if (TimingJsonLoader.TryParseRawLine(root, out var rawLine))
                    rawLines.Add(rawLine);
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
