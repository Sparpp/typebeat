// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The audio-only ("blank") import: a song file with no lyrics packages a structurally valid,
    /// listable, editable map that simply has zero lyric lines. Pins the .osz contents, the decoded
    /// beatmap, the editor save round-trip, and the fact that such a map is recognised as blank (the
    /// predicate every gameplay entry point gates on).
    /// </summary>
    [TestFixture]
    public class BlankMapImportTest
    {
        private string tempRoot = null!;

        [SetUp]
        public void SetUp()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "tb_blank_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            LyricBeatmapDecoder.Register();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch
            {
                // best effort
            }
        }

        [Test]
        public async Task NoLyricsFileProducesStructurallyValidBlankMap()
        {
            string audioPath = Path.Combine(tempRoot, "Some Artist - Some Song.mp3");
            File.WriteAllText(audioPath, "fake audio");

            var (artist, title) = LyricMapImporter.GuessArtistTitle(audioPath);

            bool sawBlankNotice = false;

            var result = await LyricMapImporter.BuildOszAsync(
                audioPath, lyricsPath: null, artist, title,
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: line =>
                {
                    if (line.Contains("blank map", StringComparison.OrdinalIgnoreCase))
                        sawBlankNotice = true;
                },
                token: CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.OszPath, Is.Not.Null);
            Assert.That(sawBlankNotice, Is.True, "the blank path should announce itself in the progress stream");

            try
            {
                using var archive = ZipFile.OpenRead(result.OszPath!);

                // Self-contained set, exactly as a lyric import produces: the .osu, the audio, and
                // the (here empty) provenance pair.
                Assert.That(archive.GetEntry("Some Artist - Some Song.mp3"), Is.Not.Null, "audio missing");
                Assert.That(archive.GetEntry("timing.json"), Is.Not.Null, "provenance timing.json missing");
                Assert.That(archive.GetEntry("lyrics.txt"), Is.Not.Null, "provenance lyrics.txt missing");

                var osuEntry = archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
                string osuText = readEntry(osuEntry);

                Assert.That(osuText, Does.StartWith(LyricBeatmapDecoder.MAGIC));
                Assert.That(osuText, Does.Contain("AudioFilename: Some Artist - Some Song.mp3"));

                // No lines means no first line to lead into and nothing to preview from.
                Assert.That(osuText, Does.Contain("AudioLeadIn: 0"));
                Assert.That(osuText, Does.Contain("PreviewTime: -1"));

                var beatmap = decode(osuText);

                Assert.That(beatmap.BeatmapInfo.Ruleset.ShortName, Is.EqualTo("typebeat"), "a blank map is still a typebeat map");
                Assert.That(beatmap.Metadata.Artist, Is.EqualTo("Some Artist"));
                Assert.That(beatmap.Metadata.Title, Is.EqualTo("Some Song"));
                Assert.That(beatmap.Metadata.AudioFile, Is.EqualTo("Some Artist - Some Song.mp3"));
                Assert.That(beatmap.HitObjects, Is.Empty, "a blank map carries no lyric lines");

                // The predicate every gameplay entry point gates on.
                Assert.That(BlankBeatmap.IsBlank(beatmap), Is.True);

                // Nothing to rate, and nothing that could divide by zero on the way there.
                Assert.That(LyricDifficulty.Compute(Array.Empty<LyricLine>()), Is.EqualTo(0));
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task EmptyLyricsFileAlsoProducesABlankMap()
        {
            // Dropping a lyrics file that turns out to be empty is the same request as dropping
            // none: a blank map, not the "the lyrics are empty" error the in-editor align path gives.
            string audioPath = Path.Combine(tempRoot, "A - B.mp3");
            File.WriteAllText(audioPath, "fake audio");

            string lyricsPath = Path.Combine(tempRoot, "empty.txt");
            File.WriteAllText(lyricsPath, "   \n\n  \n");

            var result = await LyricMapImporter.BuildOszAsync(
                audioPath, lyricsPath, "A", "B", null, new[] { tempRoot }, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);

            try
            {
                using var archive = ZipFile.OpenRead(result.OszPath!);
                var osuEntry = archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));

                Assert.That(decode(readEntry(osuEntry)).HitObjects, Is.Empty);
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task MissingLyricsFileIsStillAnError()
        {
            // Absence means "blank map"; a path that was given but does not exist is a mistake and
            // must not be silently turned into an empty map.
            string audioPath = Path.Combine(tempRoot, "A - B.mp3");
            File.WriteAllText(audioPath, "fake audio");

            var result = await LyricMapImporter.BuildOszAsync(
                audioPath, Path.Combine(tempRoot, "nope.txt"), "A", "B",
                null, new[] { tempRoot }, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("lyrics file not found"));
        }

        [Test]
        public void BlankMapSurvivesTheEditorSaveRoundTrip()
        {
            // Saving a still-empty map out of the editor must produce a file that decodes back to
            // the same blank map (metadata intact, zero lines), not a corrupt or unreadable one.
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.BeatmapInfo.Metadata.Artist = "Some Artist";
            beatmap.BeatmapInfo.Metadata.Title = "Some Song";
            beatmap.BeatmapInfo.Metadata.AudioFile = "audio.mp3";

            var writer = new StringWriter();
            TypeBeatBeatmapEncoder.Encode(beatmap, writer);

            var decoded = decode(writer.ToString());

            Assert.That(decoded.HitObjects, Is.Empty);
            Assert.That(decoded.Metadata.Artist, Is.EqualTo("Some Artist"));
            Assert.That(decoded.Metadata.Title, Is.EqualTo("Some Song"));
            Assert.That(decoded.Metadata.AudioFile, Is.EqualTo("audio.mp3"));
            Assert.That(BlankBeatmap.IsBlank(decoded), Is.True);
        }

        [Test]
        public void ABlankMapStopsBeingBlankOnceALineExists()
        {
            // The counter-case, so the guard cannot pass by always answering "blank".
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            Assert.That(BlankBeatmap.IsBlank(beatmap), Is.True);

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = "hello",
                    StartTime = 1000,
                    EndTime = 2000,
                    SingEndTime = 2000,
                    Units = new[] { new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 2000 } },
                },
                Granularity = TimingGranularity.Line,
            });

            Assert.That(BlankBeatmap.IsBlank(beatmap), Is.False);

            // A beatmap that failed to load is not "blank": that is a different failure with its
            // own reporting, and answering "no lyrics" to it would misdirect the user.
            Assert.That(BlankBeatmap.IsBlank((IBeatmap?)null), Is.False);
        }

        private static void deleteImportTemp(string? oszPath)
        {
            if (oszPath == null)
                return;

            try
            {
                Directory.Delete(Path.GetDirectoryName(oszPath)!, true);
            }
            catch
            {
                // best effort
            }
        }

        private static string readEntry(ZipArchiveEntry entry)
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }

        private static Beatmap decode(string osuText)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osuText));
            using var reader = new LineBufferedReader(stream);
            return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }
    }
}
