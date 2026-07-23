// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Extensions;
using typebeat.Game.IO.Archives;
using typebeat.Game.Models;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Submission;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the submission export path: server-allocated online IDs must be stamped into each
    /// <c>.osu</c> through the NATIVE encoder, so the [Lyrics] section survives, while every
    /// other file in the set is copied byte-for-byte.
    /// </summary>
    [TestFixture]
    public class SubmissionBeatmapExporterTest
    {
        private static readonly byte[] audio_bytes = { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x10 };

        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        [Test]
        public void StampsAllocatedIdsAndPreservesLyrics()
        {
            using var storage = new TemporaryNativeStorage($"submission-export-{Guid.NewGuid()}");

            var sourceBeatmap = buildBeatmap();
            var setInfo = buildSetInfo(storage, sourceBeatmap, beatmapOnlineId: -1, out byte[] sourceOsuBytes);

            var response = new PutBeatmapSetResponse
            {
                BeatmapSetId = 777,
                BeatmapIds = new[] { 1234u },
            };

            using var package = export(storage, setInfo, response);
            using var archive = new ZipArchiveReader(package);

            // the audio file is copied byte-for-byte.
            Assert.That(readAllBytes(archive.GetStream("audio.mp3")), Is.EqualTo(audio_bytes));

            byte[] exportedOsuBytes = readAllBytes(archive.GetStream("map.osu"));
            string exportedOsu = Encoding.UTF8.GetString(exportedOsuBytes);

            Assert.That(exportedOsu, Does.Contain("BeatmapID:1234"));
            Assert.That(exportedOsu, Does.Contain("BeatmapSetID:777"));

            var reloaded = decode(exportedOsu);

            Assert.That(reloaded.BeatmapInfo.OnlineID, Is.EqualTo(1234));
            Assert.That(reloaded.BeatmapInfo.BeatmapSet, Is.Not.Null);
            Assert.That(reloaded.BeatmapInfo.BeatmapSet!.OnlineID, Is.EqualTo(777));

            // the [Lyrics] section survives the stamping re-encode in full.
            var expectedLines = sourceBeatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();
            var actualLines = reloaded.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(actualLines, Has.Count.EqualTo(expectedLines.Count));

            for (int i = 0; i < expectedLines.Count; i++)
            {
                Assert.That(actualLines[i].Line.RawText, Is.EqualTo(expectedLines[i].Line.RawText), $"line {i} text");
                Assert.That(actualLines[i].Line.StartTime, Is.EqualTo(expectedLines[i].Line.StartTime), $"line {i} start");
                Assert.That(actualLines[i].Line.SingEndTime, Is.EqualTo(expectedLines[i].Line.SingEndTime), $"line {i} sing end");
                Assert.That(actualLines[i].Line.Units.Count, Is.EqualTo(expectedLines[i].Line.Units.Count), $"line {i} unit count");
            }

            // apart from the two stamped ID lines, the .osu is unchanged.
            byte[] restamped = Encoding.UTF8.GetBytes(exportedOsu
                                                      .Replace($"BeatmapID:1234{Environment.NewLine}", string.Empty)
                                                      .Replace($"BeatmapSetID:777{Environment.NewLine}", string.Empty));
            Assert.That(restamped, Is.EqualTo(sourceOsuBytes));
        }

        [Test]
        public void KeepsPreviouslyAssignedId()
        {
            using var storage = new TemporaryNativeStorage($"submission-export-{Guid.NewGuid()}");

            var setInfo = buildSetInfo(storage, buildBeatmap(), beatmapOnlineId: 555, out _);

            var response = new PutBeatmapSetResponse
            {
                BeatmapSetId = 777,
                BeatmapIds = new[] { 555u },
            };

            using var package = export(storage, setInfo, response);
            using var archive = new ZipArchiveReader(package);

            var reloaded = decode(Encoding.UTF8.GetString(readAllBytes(archive.GetStream("map.osu"))));

            Assert.That(reloaded.BeatmapInfo.OnlineID, Is.EqualTo(555));
            Assert.That(reloaded.BeatmapInfo.BeatmapSet!.OnlineID, Is.EqualTo(777));
        }

        [Test]
        public void ThrowsOnUnrecognisedId()
        {
            using var storage = new TemporaryNativeStorage($"submission-export-{Guid.NewGuid()}");

            // realm claims an online ID the server did not allocate.
            var setInfo = buildSetInfo(storage, buildBeatmap(), beatmapOnlineId: 999, out _);

            var response = new PutBeatmapSetResponse
            {
                BeatmapSetId = 777,
                BeatmapIds = new[] { 555u },
            };

            Assert.Throws<InvalidOperationException>(() => export(storage, setInfo, response).Dispose());
        }

        private static Beatmap buildBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.Metadata.Artist = "An Artist";
            beatmap.Metadata.Title = "A Title";
            beatmap.Metadata.AudioFile = "audio.mp3";

            var lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = "hello world",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 2800,
                    Units = new[]
                    {
                        new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "world", StartTime = 1900, EndTime = 2800, Source = TimingSource.Explicit },
                    },
                },
                new LyricLine
                {
                    RawText = "typing is a rhythm game",
                    StartTime = 3000,
                    EndTime = 6000,
                    SingEndTime = 5500,
                    Units = new[]
                    {
                        new TimedUnit { Text = "typing", StartTime = 3000, EndTime = 3400, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "is", StartTime = 3400, EndTime = 3800, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "a", StartTime = 3800, EndTime = 4200, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "rhythm", StartTime = 4200, EndTime = 4800, Source = TimingSource.Explicit },
                        new TimedUnit { Text = "game", StartTime = 4800, EndTime = 5500, Source = TimingSource.Explicit },
                    },
                },
            };

            for (int i = 0; i < lines.Count; i++)
            {
                beatmap.HitObjects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = TimingGranularity.Word,
                });
            }

            return beatmap;
        }

        private static BeatmapSetInfo buildSetInfo(TemporaryNativeStorage storage, Beatmap beatmap, int beatmapOnlineId, out byte[] osuBytes)
        {
            // Written the same way `BeatmapManager.save` writes .osu files (UTF-8 with BOM), so
            // the byte-equality assertion below reflects what a real saved file round-trips to.
            using (var ms = new MemoryStream())
            {
                using (var sw = new StreamWriter(ms, Encoding.UTF8, 1024, true))
                    TypeBeatBeatmapEncoder.Encode(beatmap, sw);

                osuBytes = ms.ToArray();
            }

            var beatmapInfo = new BeatmapInfo(new TypeBeatRuleset().RulesetInfo)
            {
                DifficultyName = "type!beat",
                Hash = writeToFileStore(storage, osuBytes),
                OnlineID = beatmapOnlineId,
            };

            var setInfo = new BeatmapSetInfo(new[] { beatmapInfo });
            setInfo.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = beatmapInfo.Hash }, "map.osu"));
            setInfo.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = writeToFileStore(storage, audio_bytes) }, "audio.mp3"));

            return setInfo;
        }

        private static string writeToFileStore(TemporaryNativeStorage storage, byte[] contents)
        {
            string hash;

            using (var ms = new MemoryStream(contents))
                hash = ms.ComputeSHA2Hash();

            var fileStore = storage.GetStorageForDirectory("files");

            using (var stream = fileStore.GetStream(new RealmFile { Hash = hash }.GetStoragePath(), FileAccess.Write, FileMode.Create))
                stream.Write(contents);

            return hash;
        }

        private static MemoryStream export(TemporaryNativeStorage storage, BeatmapSetInfo setInfo, PutBeatmapSetResponse response)
        {
            var exporter = new SubmissionBeatmapExporter(storage, response);

            var output = new MemoryStream();
            exporter.ExportToStream(setInfo, output, null);
            output.Seek(0, SeekOrigin.Begin);
            return output;
        }

        private static Beatmap decode(string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            using var reader = new typebeat.Game.IO.LineBufferedReader(stream);
            return (Beatmap)typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }

        private static byte[] readAllBytes(Stream stream)
        {
            using (stream)
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
