// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Storyboards;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Serialises an editor <see cref="IBeatmap"/> of <see cref="TypeBeatHitObject"/>s back into
    /// the "type!beat file format v1" (.osu with a [Lyrics] section). The editor's model is the
    /// authority — the emitted timing.json is rebuilt from the current <see cref="Beatmaps.LyricLine"/>s
    /// (start_ms, end_ms == SingEndTime, and word units), then run through the same
    /// <see cref="LyricOsuFormat"/> writer the import path uses so the output round-trips through
    /// <see cref="LyricBeatmapDecoder"/> byte-for-byte identically.
    /// </summary>
    public static class TypeBeatBeatmapEncoder
    {
        public static void Encode(IBeatmap beatmap, TextWriter writer)
            => Encode(beatmap, null, writer);

        public static void Encode(IBeatmap beatmap, Storyboard? storyboard, TextWriter writer)
        {
            var lines = beatmap.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();
            var metadata = beatmap.BeatmapInfo.Metadata;

            string timingJson = buildTimingJson(lines);

            string osu = LyricOsuFormat.GenerateOsu(
                artist: metadata.Artist,
                title: metadata.Title,
                audioFilename: metadata.AudioFile,
                creator: metadata.Author.Username,
                timingJsonText: timingJson,
                previewTime: metadata.PreviewTime,
                audioLeadIn: beatmap.AudioLeadIn,
                beatdropMs: beatmap.IntroBeatdropTime,
                backgroundFilename: metadata.BackgroundFile,
                videoFilename: storyboard?.PrimaryVideo?.Path,
                beatmapId: beatmap.BeatmapInfo.OnlineID,
                beatmapSetId: beatmap.BeatmapInfo.BeatmapSet?.OnlineID ?? -1,
                difficultyName: beatmap.BeatmapInfo.DifficultyName);

            writer.Write(osu);
        }

        private static string buildTimingJson(IReadOnlyList<TypeBeatHitObject> lines)
        {
            // Word/Syllable granularity carries explicit per-word units; Line granularity has no
            // authored word timing (units are interpolated on load), so omit words[] to keep the
            // decoded granularity Line.
            bool wordGranularity = lines.Count > 0 && lines[0].Granularity != TimingGranularity.Line;

            // song_end_ms clamps the last line's derived EndTime on decode; emit the current value
            // so the last line's window survives the round-trip.
            double songEndMs = lines.Count > 0 ? lines[^1].Line.EndTime : 0;

            using var ms = new MemoryStream();

            using (var json = new Utf8JsonWriter(ms))
            {
                json.WriteStartObject();
                json.WriteNumber("version", TimingJsonLoader.SUPPORTED_VERSION);
                json.WriteNumber("song_end_ms", songEndMs);
                json.WriteStartArray("lines");

                foreach (var h in lines)
                {
                    var line = h.Line;

                    json.WriteStartObject();
                    json.WriteString("text", line.RawText);
                    json.WriteNumber("start_ms", line.StartTime);
                    json.WriteNumber("end_ms", line.SingEndTime);

                    if (line.Estimated)
                        json.WriteBoolean("estimated", true);

                    // The derived-from-overrun grace can't be recomputed from clamped unit times,
                    // so persist it explicitly (the decoder honours it over the derivation).
                    if (line.SealGraceMs > 0)
                        json.WriteNumber("seal_grace_ms", line.SealGraceMs);

                    if (wordGranularity)
                    {
                        json.WriteStartArray("words");

                        foreach (var unit in line.Units)
                        {
                            json.WriteStartObject();
                            json.WriteString("text", unit.Text);
                            json.WriteNumber("start_ms", unit.StartTime);
                            json.WriteNumber("end_ms", unit.EndTime);
                            json.WriteNumber("score", unit.Confidence);
                            json.WriteEndObject();
                        }

                        json.WriteEndArray();
                    }

                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
