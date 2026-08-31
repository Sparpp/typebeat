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
    /// the "type!beat file format" (.osu with a [Lyrics] section, currently v2). The editor's model is the
    /// authority; the emitted timing.json is rebuilt from the current <see cref="Beatmaps.LyricLine"/>s
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
                // A video element's StartTime IS its offset against the song. Rounded to whole
                // milliseconds because the format's field is int-parsed on decode; without this the
                // offset a mapper sets in song setup would be dropped on every save.
                videoOffsetMs: (int)System.Math.Round(storyboard?.PrimaryVideo?.StartTime ?? 0),
                beatmapId: beatmap.BeatmapInfo.OnlineID,
                beatmapSetId: beatmap.BeatmapInfo.BeatmapSet?.OnlineID ?? -1,
                difficultyName: beatmap.BeatmapInfo.DifficultyName,
                tags: metadata.Tags,
                titleUnicode: metadata.TitleUnicode,
                artistUnicode: metadata.ArtistUnicode,
                language: metadata.Language.ToCanonicalName());

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

                    // Freestyle authoring: the markers live in the text itself, but the decoder
                    // only reads them as markers when the line opts in, so a stored ampersand can
                    // never turn an old (non-editor) map's lyrics into freestyle cells. Written
                    // only for lines that actually carry one.
                    if (line.RawText.IndexOf(Typeability.FREESTYLE_MARKER) >= 0)
                        json.WriteBoolean("freestyle", true);

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

                            if (unit.SyllableBoundaries.Count > 0)
                            {
                                json.WriteStartArray("syllables");
                                writeSyllables(json, unit);
                                json.WriteEndArray();
                            }

                            // The AUTHORED character split (backlog 181), written ADDITIVELY beside
                            // syllables[] and only when the mapper actually authored one: a word
                            // left on the derived split persists exactly as it always did, so no
                            // existing map's bytes move.
                            if (unit.SyllableSplits.Count > 0)
                            {
                                json.WriteStartArray("split_chars");

                                foreach (int split in unit.SyllableSplits)
                                    json.WriteNumberValue(split);

                                json.WriteEndArray();
                            }

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

        /// <summary>
        /// Emits a word's syllable segments (lyriclab <c>{text,start_ms,end_ms}</c> shape) from its
        /// subdivision boundaries: N boundaries → N+1 segments spanning [start, b1, …, end]. The
        /// word's characters are split across the segments as evenly as possible so each syllable
        /// carries some text; only the segment TIMES round-trip (the loader derives boundaries from
        /// each syllable's start_ms), so the text split is just a sensible default.
        ///
        /// <para>A word carrying an AUTHORED split (backlog 181) prints THAT split instead, so the
        /// saved JSON reads "ap"/"ple" rather than the even halves. Cosmetic either way: the loader
        /// reads the split back from <c>split_chars</c> and never from these strings, which is what
        /// keeps every pre-181 map (whose texts are the even default) splitting exactly as before.
        /// The even default is likewise left untouched for a derived word, so no existing map's
        /// bytes move.</para>
        /// </summary>
        private static void writeSyllables(Utf8JsonWriter json, TimedUnit unit)
        {
            var edges = new List<double> { unit.StartTime };
            edges.AddRange(unit.SyllableBoundaries);
            edges.Add(unit.EndTime);

            string text = unit.Text;
            int segments = edges.Count - 1;

            IReadOnlyList<string>? authored = Gameplay.SyllableSegments.IsAuthoredValid(text, segments, unit.SyllableSplits)
                ? Gameplay.SyllableSegments.SegmentTexts(text, unit.SyllableSplits)
                : null;

            for (int i = 0; i < segments; i++)
            {
                int from = (int)System.Math.Round((double)i * text.Length / segments);
                int to = (int)System.Math.Round((double)(i + 1) * text.Length / segments);
                from = System.Math.Clamp(from, 0, text.Length);
                to = System.Math.Clamp(to, from, text.Length);

                json.WriteStartObject();
                json.WriteString("text", authored != null ? authored[i] : System.MemoryExtensions.AsSpan(text, from, to - from).ToString());
                json.WriteNumber("start_ms", edges[i]);
                json.WriteNumber("end_ms", edges[i + 1]);
                json.WriteEndObject();
            }
        }
    }
}
