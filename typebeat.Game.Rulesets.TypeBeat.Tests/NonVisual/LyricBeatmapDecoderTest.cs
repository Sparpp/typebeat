// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Round-trip pins for the M6 format skeleton: timing.json -> [Lyrics] .osu text
    /// (via <see cref="LyricOsuFormat"/>, the same writer the .osz tool uses) -> decode
    /// (via the registered <see cref="LyricBeatmapDecoder"/>) -> hit objects identical to
    /// what <see cref="TimingJsonLoader.TryLoad"/> produces from the source file directly.
    /// </summary>
    [TestFixture]
    public class LyricBeatmapDecoderTest
    {
        private const string synthetic_timing_json =
            "{\"version\":2,\"song_end_ms\":20000,\"lines\":[" +
            "{\"text\":\"one two three\",\"start_ms\":1000,\"end_ms\":4000,\"words\":[{\"text\":\"one\",\"start_ms\":1000,\"end_ms\":2000}]}," +
            "{\"text\":\"(backing only)\",\"start_ms\":4200,\"end_ms\":4600}," +
            "{\"text\":\"aa bb\",\"start_ms\":5000,\"end_ms\":7000,\"estimated\":true,\"words\":[{\"text\":\"aa\",\"start_ms\":5000,\"end_ms\":6000,\"score\":0.1},{\"text\":\"bb\",\"start_ms\":6000,\"end_ms\":7500}]}]}";

        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        private static typebeat.Game.Beatmaps.Beatmap decode(string osuText)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osuText));
            using var reader = new LineBufferedReader(stream);
            return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);
        }

        [Test]
        public void SyntheticRoundTripMatchesLoader()
        {
            string osuText = LyricOsuFormat.GenerateOsu("An Artist", "A Title", "audio.mp3", "tester", synthetic_timing_json);

            // Loader ground truth (via a temp file, its only entry point).
            string tempPath = Path.Combine(Path.GetTempPath(), $"tb_dec_{System.Guid.NewGuid():N}.json");
            File.WriteAllText(tempPath, synthetic_timing_json);

            try
            {
                Assert.That(TimingJsonLoader.TryLoad(tempPath, out var expected), Is.True);

                var beatmap = decode(osuText);
                var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

                // The backing-vocal-only line is dropped by both paths.
                Assert.That(beatmap.HitObjects.Count, Is.EqualTo(hitObjects.Count));
                Assert.That(hitObjects.Count, Is.EqualTo(expected.Count));
                Assert.That(hitObjects.Count, Is.EqualTo(2));

                Assert.That(beatmap.BeatmapInfo.Ruleset.ShortName, Is.EqualTo("typebeat"));
                Assert.That(beatmap.Metadata.Artist, Is.EqualTo("An Artist"));
                Assert.That(beatmap.Metadata.Title, Is.EqualTo("A Title"));
                Assert.That(beatmap.Metadata.AudioFile, Is.EqualTo("audio.mp3"));

                for (int i = 0; i < expected.Count; i++)
                {
                    assertLinesEqual(expected[i], hitObjects[i], i);
                    Assert.That(hitObjects[i].LineIndex, Is.EqualTo(i));
                    Assert.That(hitObjects[i].Granularity, Is.EqualTo(TimingGranularity.Word));
                }

                // Estimated flag survives the trip (drives Line-tier judging).
                Assert.That(hitObjects[1].Line.Estimated, Is.True);

                // Low word score survives (0.1 < LOW_CONFIDENCE_SCORE widens windows).
                Assert.That(hitObjects[1].Line.Units[0].Confidence, Is.EqualTo(0.1).Within(1e-9));
            }
            finally
            {
                File.Delete(tempPath);
            }
        }

        [Test]
        public void RealSpectatorRoundTripMatchesLoader()
        {
            string timingPath = StandaloneMaps.Require("Friday Pilots Club - Spectator", "timing.json");
            string timingJson = File.ReadAllText(timingPath);

            Assert.That(TimingJsonLoader.TryLoad(timingPath, out var expected), Is.True);

            string osuText = LyricOsuFormat.GenerateOsu("Friday Pilots Club", "Spectator", "audio.mp3", "typebeat-lyriclab", timingJson);
            var beatmap = decode(osuText);
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(expected.Count)); // 36
            Assert.That(beatmap.BeatmapInfo.Ruleset.ShortName, Is.EqualTo("typebeat"));

            for (int i = 0; i < expected.Count; i++)
                assertLinesEqual(expected[i], hitObjects[i], i);
        }

        [Test]
        public void NonLyricFilesStillDecodeViaLegacyFallback()
        {
            const string legacy_text = "osu file format v14\n\n[Metadata]\nTitle:legacy\n";

            // A stock legacy header must NOT be captured by the typebeat decoder.
            // (Ruleset identity no longer distinguishes the two decoders: legacy mode 0 maps to
            // type!beat by design now that TypeBeatRuleset claims online ruleset ID 0, so pin
            // the decoder choice itself.)
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(legacy_text)))
            using (var reader = new LineBufferedReader(stream))
                Assert.That(typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader), Is.Not.InstanceOf<LyricBeatmapDecoder>());

            var beatmap = decode(legacy_text);
            Assert.That(beatmap.HitObjects, Is.Empty);
            Assert.That(beatmap.Metadata.Title, Is.EqualTo("legacy"));
        }

        private static void assertLinesEqual(LyricLine expected, TypeBeatHitObject actual, int index)
        {
            var line = actual.Line;

            Assert.That(actual.StartTime, Is.EqualTo(expected.StartTime), $"line {index} start");
            Assert.That(line.RawText, Is.EqualTo(expected.RawText), $"line {index} text");
            Assert.That(line.StartTime, Is.EqualTo(expected.StartTime), $"line {index} StartTime");
            Assert.That(line.EndTime, Is.EqualTo(expected.EndTime), $"line {index} EndTime");
            Assert.That(line.SingEndTime, Is.EqualTo(expected.SingEndTime), $"line {index} SingEndTime");
            Assert.That(line.SealGraceMs, Is.EqualTo(expected.SealGraceMs), $"line {index} SealGraceMs");
            Assert.That(line.Estimated, Is.EqualTo(expected.Estimated), $"line {index} Estimated");

            Assert.That(line.Units.Count, Is.EqualTo(expected.Units.Count), $"line {index} unit count");

            for (int u = 0; u < expected.Units.Count; u++)
            {
                Assert.That(line.Units[u].Text, Is.EqualTo(expected.Units[u].Text), $"line {index} unit {u} text");
                Assert.That(line.Units[u].StartTime, Is.EqualTo(expected.Units[u].StartTime).Within(1e-9), $"line {index} unit {u} start");
                Assert.That(line.Units[u].EndTime, Is.EqualTo(expected.Units[u].EndTime).Within(1e-9), $"line {index} unit {u} end");
                Assert.That(line.Units[u].Source, Is.EqualTo(expected.Units[u].Source), $"line {index} unit {u} source");
                Assert.That(line.Units[u].Confidence, Is.EqualTo(expected.Units[u].Confidence).Within(1e-9), $"line {index} unit {u} confidence");
            }
        }
    }
}
