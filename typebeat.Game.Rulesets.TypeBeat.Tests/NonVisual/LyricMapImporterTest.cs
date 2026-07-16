// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Ports the standalone MapImporter regression pins (detection / sanitisation / resolution)
    /// to the fork's <see cref="LyricMapImporter"/>, and adds the M6-specific .osz packaging pins:
    /// aligner-timing round-trip, line-only fallback granularity, and metadata escaping.
    /// </summary>
    [TestFixture]
    public class LyricMapImporterTest
    {
        private string tempRoot = null!;

        [SetUp]
        public void SetUp()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "tb_import_" + Guid.NewGuid().ToString("N"));
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
        public void HasLineStampsDetection()
        {
            Assert.That(LyricMapImporter.HasLineStamps("[00:01.00] hello\n[00:02.00] world\n[00:03.00]\n"), Is.True);
            Assert.That(LyricMapImporter.HasLineStamps("[ar:Artist]\n[Lyrics]\n[00:01.00] hello\n"), Is.True);
            Assert.That(LyricMapImporter.HasLineStamps("hello\nworld\n"), Is.False);
            Assert.That(LyricMapImporter.HasLineStamps("[00:01.00] hello\nworld\n"), Is.False);
            Assert.That(LyricMapImporter.HasLineStamps(""), Is.False);
            Assert.That(LyricMapImporter.HasLineStamps("[ar:OnlyMetadata]\n"), Is.False);
        }

        [Test]
        public void SanitizeFolderNameRemovesInvalidChars()
        {
            Assert.That(LyricMapImporter.SanitizeFolderName("AC/DC - T.N.T."), Is.EqualTo("AC DC - T.N.T"));
            Assert.That(LyricMapImporter.SanitizeFolderName("a<>:\"|?*b"), Is.EqualTo("a b"));
            Assert.That(LyricMapImporter.SanitizeFolderName("  spaced   out  "), Is.EqualTo("spaced out"));
            Assert.That(LyricMapImporter.SanitizeFolderName("???"), Is.EqualTo("Imported Map"));
        }

        [Test]
        public void GuessArtistTitleFromFilename()
        {
            Assert.That(LyricMapImporter.GuessArtistTitle(@"X:\music\Friday Pilots Club - Spectator Official Audio.mp3"),
                Is.EqualTo(("Friday Pilots Club", "Spectator Official Audio")));
            Assert.That(LyricMapImporter.GuessArtistTitle(@"X:\music\untitled.mp3"),
                Is.EqualTo(("Unknown", "untitled")));
        }

        [Test]
        public void ResolveLyricLabDirWalksUpFromStart()
        {
            // root/typebeat-lyriclab/align_lyrics.py  +  root/repo/(start)
            string lab = Path.Combine(tempRoot, "typebeat-lyriclab");
            Directory.CreateDirectory(lab);
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "# stub");

            string start = Path.Combine(tempRoot, "repo", "bin");
            Directory.CreateDirectory(start);

            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(lab));

            // An explicitly configured valid path wins.
            string configured = Path.Combine(tempRoot, "elsewhere");
            Directory.CreateDirectory(configured);
            File.WriteAllText(Path.Combine(configured, "align_lyrics.py"), "# stub");
            Assert.That(LyricMapImporter.ResolveLyricLabDir(configured, start), Is.EqualTo(Path.GetFullPath(configured)));

            // An invalid configured path falls back to discovery.
            Assert.That(LyricMapImporter.ResolveLyricLabDir(Path.Combine(tempRoot, "nope"), start), Is.EqualTo(lab));

            // Nothing to find within the ascent budget.
            string isolated = Path.Combine(tempRoot, "isolated", "deep", "start");
            Directory.CreateDirectory(isolated);
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, isolated, maxAscendLevels: 1), Is.Null);
        }

        [Test]
        public void ResolveLyricLabDirPrefersVendoredComponentAndReadyVenvs()
        {
            // Vendored component at the repo root (next to the start dir), sibling checkout above it.
            string repoLab = Path.Combine(tempRoot, "repo", "lyriclab");
            string siblingLab = Path.Combine(tempRoot, "typebeat-lyriclab");
            Directory.CreateDirectory(repoLab);
            Directory.CreateDirectory(siblingLab);
            File.WriteAllText(Path.Combine(repoLab, "align_lyrics.py"), "# stub");
            File.WriteAllText(Path.Combine(siblingLab, "align_lyrics.py"), "# stub");

            string start = Path.Combine(tempRoot, "repo", "bin");
            Directory.CreateDirectory(start);

            // Neither has a venv: the vendored in-repo component wins (closest candidate).
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(repoLab));
            Assert.That(LyricMapImporter.EnvironmentReady(repoLab), Is.False);

            // A candidate with a ready venv is preferred over a closer one without.
            string siblingPython = LyricMapImporter.PythonExeFor(siblingLab);
            Directory.CreateDirectory(Path.GetDirectoryName(siblingPython)!);
            File.WriteAllText(siblingPython, "stub exe");
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(siblingLab));

            // Once the vendored component's venv exists too, it wins again.
            string repoPython = LyricMapImporter.PythonExeFor(repoLab);
            Directory.CreateDirectory(Path.GetDirectoryName(repoPython)!);
            File.WriteAllText(repoPython, "stub exe");
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(repoLab));
            Assert.That(LyricMapImporter.EnvironmentReady(repoLab), Is.True);
        }

        [Test]
        public void OszPackagingRoundTripMatchesLoader()
        {
            // Real Spectator provenance: build an .osz from the aligner timing.json, then unzip +
            // decode the packaged .osu and confirm the hit objects match TimingJsonLoader.TryLoad.
            string timingPath = StandaloneMaps.Require("Friday Pilots Club - Spectator", "timing.json");
            string audioPath = StandaloneMaps.Require("Friday Pilots Club - Spectator", "Friday Pilots Club - Spectator Official Audio.mp3");
            string lyricsPath = StandaloneMaps.Require("Friday Pilots Club - Spectator", "lyrics.txt");

            string timingJson = File.ReadAllText(timingPath);
            string lyricsContent = File.ReadAllText(lyricsPath);

            Assert.That(TimingJsonLoader.TryLoad(timingPath, out var expected), Is.True);

            string oszPath = Path.Combine(tempRoot, "spectator.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "Friday Pilots Club", "Spectator", audioPath, timingJson, lyricsContent);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.OszPath, Is.EqualTo(oszPath));
            Assert.That(File.Exists(oszPath), Is.True);

            using var archive = ZipFile.OpenRead(oszPath);

            // Self-contained set: audio + provenance both travel inside.
            Assert.That(archive.GetEntry("Friday Pilots Club - Spectator Official Audio.mp3"), Is.Not.Null, "audio missing");
            Assert.That(archive.GetEntry("timing.json"), Is.Not.Null, "provenance timing.json missing");
            Assert.That(archive.GetEntry("lyrics.txt"), Is.Not.Null, "provenance lyrics.txt missing");

            var osuEntry = archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
            var beatmap = decode(readEntry(osuEntry));
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(expected.Count));
            Assert.That(beatmap.BeatmapInfo.Ruleset.ShortName, Is.EqualTo("typebeat"));

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.That(hitObjects[i].StartTime, Is.EqualTo(expected[i].StartTime), $"line {i} start");
                Assert.That(hitObjects[i].Line.RawText, Is.EqualTo(expected[i].RawText), $"line {i} text");
                Assert.That(hitObjects[i].Line.EndTime, Is.EqualTo(expected[i].EndTime), $"line {i} end");
                Assert.That(hitObjects[i].Line.Units.Count, Is.EqualTo(expected[i].Units.Count), $"line {i} units");
                Assert.That(hitObjects[i].Granularity, Is.EqualTo(TimingGranularity.Word));
            }
        }

        [Test]
        public async Task LrcOnlyFallbackPackagesLineGranularityMap()
        {
            // No aligner reachable from the start dir + line-stamped lyrics -> line-granularity map.
            string audioPath = Path.Combine(tempRoot, "Some Artist - Some Song.mp3");
            File.WriteAllText(audioPath, "fake audio");

            string lyricsPath = Path.Combine(tempRoot, "lyrics.txt");
            File.WriteAllText(lyricsPath, "[00:01.00] hello world\n[00:03.00] second line here\n[00:05.00]\n");

            var (artist, title) = LyricMapImporter.GuessArtistTitle(audioPath);

            var result = await LyricMapImporter.BuildOszAsync(
                audioPath, lyricsPath, artist, title,
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: _ => { },
                token: CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.OszPath, Is.Not.Null);

            using var archive = ZipFile.OpenRead(result.OszPath!);
            var osuEntry = archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
            var beatmap = decode(readEntry(osuEntry));
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(2));
            Assert.That(hitObjects.All(h => h.Granularity == TimingGranularity.Line), Is.True, "expected line granularity");
            Assert.That(hitObjects[0].Line.RawText, Is.EqualTo("hello world"));
            Assert.That(hitObjects[0].StartTime, Is.EqualTo(1000));

            // Clean up the temp .osz the importer produced outside tempRoot.
            try
            {
                Directory.Delete(Path.GetDirectoryName(result.OszPath!)!, true);
            }
            catch
            {
                // best effort
            }
        }

        [Test]
        public void Mp4SourceBecomesAudioAndBackgroundVideo()
        {
            // A video container packaged as the map's audio also becomes its background video:
            // the .osu references the same file from AudioFilename and an [Events] Video entry.
            string mp4Path = Path.Combine(tempRoot, "Some Artist - Some Song.mp4");
            File.WriteAllText(mp4Path, "fake video");

            const string timing = "{\"version\":2,\"song_end_ms\":8000,\"lines\":["
                                  + "{\"text\":\"one two\",\"start_ms\":1000,\"end_ms\":3000}]}";

            string oszPath = Path.Combine(tempRoot, "video.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "Some Artist", "Some Song", mp4Path, timing, "[00:01.00] one two\n");

            Assert.That(result.Success, Is.True, result.Error);

            using var archive = ZipFile.OpenRead(oszPath);
            var osuEntry = archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
            string osuText = readEntry(osuEntry);

            Assert.That(osuText, Does.Contain("AudioFilename: Some Artist - Some Song.mp4"));
            Assert.That(osuText, Does.Contain("Video,0,\"Some Artist - Some Song.mp4\""));

            var beatmap = decode(osuText);
            Assert.That(beatmap.Metadata.AudioFile, Is.EqualTo("Some Artist - Some Song.mp4"));
        }

        [Test]
        public async Task UnstampedLyricsWithoutAlignerFails()
        {
            string audioPath = Path.Combine(tempRoot, "a.mp3");
            File.WriteAllText(audioPath, "fake");
            string lyricsPath = Path.Combine(tempRoot, "plain.txt");
            File.WriteAllText(lyricsPath, "just some words\nwith no timestamps\n");

            var result = await LyricMapImporter.BuildOszAsync(
                audioPath, lyricsPath, "A", "B", null, new[] { tempRoot }, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("timestamp"));
        }

        [Test]
        public void MetadataEscapingRoundTrips()
        {
            // '!' (the game's namesake) and unicode in artist/title must survive the writer + decoder.
            const string artist = "Sÿntax! 日本語";
            const string title = "type!beat ★ intro";

            const string timing = "{\"version\":2,\"song_end_ms\":8000,\"lines\":["
                                  + "{\"text\":\"one two\",\"start_ms\":1000,\"end_ms\":3000}]}";

            string osuText = LyricOsuFormat.GenerateOsu(artist, title, "audio.mp3", "tester", timing);
            var beatmap = decode(osuText);

            Assert.That(beatmap.Metadata.Artist, Is.EqualTo(artist));
            Assert.That(beatmap.Metadata.ArtistUnicode, Is.EqualTo(artist));
            Assert.That(beatmap.Metadata.Title, Is.EqualTo(title));
            Assert.That(beatmap.Metadata.TitleUnicode, Is.EqualTo(title));
        }

        private static string readEntry(ZipArchiveEntry entry)
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }

        private static typebeat.Game.Beatmaps.Beatmap decode(string osuText)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osuText));
            using var reader = new LineBufferedReader(stream);
            return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);
        }
    }
}
