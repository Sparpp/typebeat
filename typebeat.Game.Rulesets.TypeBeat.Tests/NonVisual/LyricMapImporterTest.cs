// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.ImportLyrics;

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
                // The real Spectator timing.json carries aligner-emitted words[].syllables[], which the
                // loader now threads into SyllableBoundaries, so the packaged map infers Syllable.
                Assert.That(hitObjects[i].Granularity, Is.EqualTo(TimingGranularity.Syllable));
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

        #region The video split (backlog 234)

        [Test]
        public async Task Mp4SourceIsSplitIntoStandaloneAudioPlusBackgroundVideo()
        {
            // A dropped video container becomes TWO files: the extracted audio track (which is what
            // AudioFilename names, what alignment runs on, and what an audio-only download of the set
            // would ship) and the container, kept on as the map's [Events] video. Before the split
            // one file did both jobs, which made an audio-only download silent and let the "delete
            // all videos" maintenance action destroy the song.
            string mp4Path = Path.Combine(tempRoot, "Some Artist - Some Song.mp4");
            File.WriteAllText(mp4Path, "fake video");

            string lyricsPath = Path.Combine(tempRoot, "lyrics.txt");
            File.WriteAllText(lyricsPath, "[00:01.00] one two\n[00:03.00]\n");

            var extractor = FakeAudioTrackExtractor.Producing(".mp3");
            string? alignedAudioPath = null;

            var result = await LyricMapImporter.BuildOszAsync(
                mp4Path, lyricsPath, "Some Artist", "Some Song",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: _ => { },
                token: CancellationToken.None,
                remoteAlign: (audioPath, _, _, _, _, _) =>
                {
                    // The aligner seam only records what it was handed; the LRC line stamps below
                    // produce the timing, so the assertion does not depend on a stub's output.
                    alignedAudioPath = audioPath;
                    return Task.FromResult(RemoteAlignOutcome.Fail("stub"));
                },
                useAutomaticAlignment: true,
                audioExtractor: extractor).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(extractor.VideoPath, Is.EqualTo(mp4Path), "the container is what gets split");
            Assert.That(extractor.OutputDirectory, Is.EqualTo(Path.GetDirectoryName(result.OszPath)),
                "the extracted file belongs in the import temp dir, which the caller cleans up");

            try
            {
                using var archive = ZipFile.OpenRead(result.OszPath!);

                Assert.Multiple(() =>
                {
                    Assert.That(archive.GetEntry("Some Artist - Some Song.mp3"), Is.Not.Null, "the extracted audio must travel in the set");
                    Assert.That(archive.GetEntry("Some Artist - Some Song.mp4"), Is.Not.Null, "the video must travel in the set too");
                });

                string osuText = readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)));

                Assert.Multiple(() =>
                {
                    Assert.That(osuText, Does.Contain("AudioFilename: Some Artist - Some Song.mp3"));

                    // Byte-identical to the line the unsplit import has always written: an extraction
                    // is sample-accurate, so the video needs no offset (backlog 232's seam stays at 0).
                    Assert.That(osuText, Does.Contain("Video,0,\"Some Artist - Some Song.mp4\""));

                    Assert.That(decode(osuText).Metadata.AudioFile, Is.EqualTo("Some Artist - Some Song.mp3"));

                    // The whole point of splitting BEFORE alignment: the aligner (local subprocess or
                    // the 64MB-capped server upload) sees the audio, never the container.
                    Assert.That(alignedAudioPath, Is.EqualTo(extractor.ProducedPath));
                });
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task WithNoExtractorTheVideoStillDoublesAsTheAudio()
        {
            // The degrade, and the reason the split can ship at all: the only real extractor is an
            // ffmpeg binary most machines do not have. Without one, an mp4 import must behave exactly
            // as it did before the split (one media entry, doing both jobs) rather than failing.
            string mp4Path = Path.Combine(tempRoot, "Some Artist - Some Song.mp4");
            File.WriteAllText(mp4Path, "fake video");

            string lyricsPath = Path.Combine(tempRoot, "lyrics.txt");
            File.WriteAllText(lyricsPath, "[00:01.00] one two\n[00:03.00]\n");

            var lines = new List<string>();
            string? alignedAudioPath = null;

            var result = await LyricMapImporter.BuildOszAsync(
                mp4Path, lyricsPath, "Some Artist", "Some Song",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: lines.Add,
                token: CancellationToken.None,
                remoteAlign: (audioPath, _, _, _, _, _) =>
                {
                    alignedAudioPath = audioPath;
                    return Task.FromResult(RemoteAlignOutcome.Fail("stub"));
                },
                useAutomaticAlignment: true,
                audioExtractor: FakeAudioTrackExtractor.Unavailable("no ffmpeg found on this machine")).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);

            try
            {
                using var archive = ZipFile.OpenRead(result.OszPath!);

                string osuText = readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)));

                Assert.Multiple(() =>
                {
                    Assert.That(archive.Entries.Select(e => e.FullName), Has.Exactly(1).Matches<string>(n => n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)));
                    Assert.That(archive.Entries.Any(e => e.FullName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)), Is.False, "nothing was extracted");
                    Assert.That(osuText, Does.Contain("AudioFilename: Some Artist - Some Song.mp4"));
                    Assert.That(osuText, Does.Contain("Video,0,\"Some Artist - Some Song.mp4\""));
                    Assert.That(decode(osuText).Metadata.AudioFile, Is.EqualTo("Some Artist - Some Song.mp4"));
                    Assert.That(alignedAudioPath, Is.EqualTo(mp4Path), "with nothing extracted, the container is still what gets aligned");
                });
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task ABlankMapFromAVideoIsSplitToo()
        {
            // The split sits ABOVE the blank/aligned branch, so a video dropped with no lyrics (the
            // "write the words in the editor" import) is split exactly the same way.
            string mp4Path = Path.Combine(tempRoot, "A - B.mp4");
            File.WriteAllText(mp4Path, "fake video");

            var result = await LyricMapImporter.BuildOszAsync(
                mp4Path, lyricsPath: null, "A", "B",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: _ => { },
                token: CancellationToken.None,
                audioExtractor: FakeAudioTrackExtractor.Producing(".mp3")).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);

            try
            {
                using var archive = ZipFile.OpenRead(result.OszPath!);
                string osuText = readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)));

                Assert.Multiple(() =>
                {
                    Assert.That(archive.GetEntry("A - B.mp3"), Is.Not.Null);
                    Assert.That(archive.GetEntry("A - B.mp4"), Is.Not.Null);
                    Assert.That(osuText, Does.Contain("AudioFilename: A - B.mp3"));
                    Assert.That(osuText, Does.Contain("Video,0,\"A - B.mp4\""));
                });
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task TheDegradeSaysWhyItKeptTheVideoAsTheAudio()
        {
            // The map that comes out of the degrade behaves differently (an audio-only download of it
            // is silent, and "delete all videos" would take its audio), so the reason has to reach the
            // log. It claims no stage of its own, unlike the extraction notice: nothing was extracted,
            // so the progress display holds its step rather than ticking "extracted" over a fallback.
            string mp4Path = Path.Combine(tempRoot, "A - B.mp4");
            File.WriteAllText(mp4Path, "fake video");

            var lines = new List<string>();

            var result = await LyricMapImporter.BuildOszAsync(
                mp4Path, lyricsPath: null, "A", "B",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: lines.Add,
                token: CancellationToken.None,
                audioExtractor: FakeAudioTrackExtractor.Unavailable("no ffmpeg found on this machine")).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);

            try
            {
                string? degrade = lines.FirstOrDefault(l => l.Contains("keeping the video file as the map's audio", StringComparison.Ordinal));

                Assert.Multiple(() =>
                {
                    Assert.That(degrade, Is.Not.Null, "the fallback must explain itself in the progress stream");
                    Assert.That(degrade, Does.Contain("no ffmpeg found on this machine"), "including why");
                    Assert.That(ImportProgressParser.Parse(degrade).Stage, Is.Null);
                });
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task RealFfmpegExtractionProducesAStandaloneAudioFile()
        {
            // The one test that exercises a real container and a real encoder. Self-skipping in the
            // web repo's IsFfmpegAvailable style: the extractor is resolved the way an import would
            // resolve it, and the fixture is SYNTHESISED by that same binary (a 2s tone in an mp4),
            // so no binary media has to be checked into the repo.
            string? ffmpeg = FfmpegAudioTrackExtractor.Resolve(null, new[] { tempRoot });

            if (ffmpeg == null)
                Assert.Ignore("no ffmpeg (aligner venv or PATH); the importer degrades to mp4-as-audio and the split is skipped.");

            string mp4Path = Path.Combine(tempRoot, "Some Artist - Some Song.mp4");

            if (!synthesiseToneMp4(ffmpeg!, mp4Path, out string synthesisError))
                Assert.Ignore($"this ffmpeg cannot synthesise the fixture: {synthesisError}");

            var lines = new List<string>();
            string outputDir = Path.Combine(tempRoot, "extracted");

            var extraction = await new FfmpegAudioTrackExtractor(null, new[] { tempRoot })
                .ExtractAsync(mp4Path, outputDir, lines.Add, CancellationToken.None).ConfigureAwait(false);

            Assert.That(extraction.Success, Is.True, extraction.Reason);

            string audioPath = extraction.AudioPath!;
            byte[] head = File.ReadAllBytes(audioPath);

            Assert.Multiple(() =>
            {
                Assert.That(Path.GetFileNameWithoutExtension(audioPath), Is.EqualTo("Some Artist - Some Song"), "the extracted file keeps the source stem");

                // Never .m4a/.aac: BASS decodes no AAC on Linux, and neither the editor's audio
                // chooser nor the site's player content types know the extension.
                Assert.That(Path.GetExtension(audioPath), Is.AnyOf(".mp3", ".ogg"));

                Assert.That(head.Length, Is.GreaterThan(1024), "two seconds of encoded audio is never this small");
                Assert.That(looksLikeEncodedAudio(head), Is.True, "the output should start with an ID3 tag, an mpeg frame sync or an Ogg page");
                Assert.That(lines, Does.Contain(FfmpegAudioTrackExtractor.EXTRACTING_NOTICE));
            });
        }

        /// <summary>Writes a two-second tone into an mp4 (aac track) with the given ffmpeg.</summary>
        private static bool synthesiseToneMp4(string ffmpeg, string destination, out string error)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string arg in new[]
                     {
                         "-nostdin", "-y", "-v", "error",
                         "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                         "-c:a", "aac", destination,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            try
            {
                using var process = System.Diagnostics.Process.Start(psi)!;
                error = process.StandardError.ReadToEnd().Trim();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && new System.IO.FileInfo(destination).Length > 0)
                    return true;

                if (error.Length == 0)
                    error = $"exit code {process.ExitCode}";

                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static bool looksLikeEncodedAudio(byte[] head)
        {
            if (head.Length < 4)
                return false;

            bool id3 = head[0] == 'I' && head[1] == 'D' && head[2] == '3';
            bool frameSync = head[0] == 0xFF && (head[1] & 0xE0) == 0xE0;
            bool ogg = head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S';

            return id3 || frameSync || ogg;
        }

        /// <summary>
        /// Stands in for ffmpeg: writes a text file where a real extraction would put the audio (the
        /// packaging path decodes nothing), or reports itself unavailable. Also records what it was
        /// asked to split, which is how the "alignment gets the extracted file" pin is made.
        /// </summary>
        private class FakeAudioTrackExtractor : IAudioTrackExtractor
        {
            public string? VideoPath;
            public string? OutputDirectory;
            public string? ProducedPath;

            private readonly string? extension;
            private readonly string reason;

            private FakeAudioTrackExtractor(string? extension, string reason)
            {
                this.extension = extension;
                this.reason = reason;
            }

            public static FakeAudioTrackExtractor Producing(string extension) => new FakeAudioTrackExtractor(extension, string.Empty);

            public static FakeAudioTrackExtractor Unavailable(string reason) => new FakeAudioTrackExtractor(null, reason);

            public Task<AudioExtractionResult> ExtractAsync(string videoPath, string outputDirectory, Action<string> progress, CancellationToken token)
            {
                VideoPath = videoPath;
                OutputDirectory = outputDirectory;

                if (extension == null)
                    return Task.FromResult(AudioExtractionResult.Unavailable(reason));

                Directory.CreateDirectory(outputDirectory);
                ProducedPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(videoPath) + extension);
                File.WriteAllText(ProducedPath, "fake extracted audio");

                return Task.FromResult(AudioExtractionResult.Ok(ProducedPath));
            }
        }

        /// <summary>Removes the temp directory the importer produced outside <c>tempRoot</c>.</summary>
        private static void deleteImportTemp(string? oszPath)
        {
            try
            {
                if (oszPath != null)
                    Directory.Delete(Path.GetDirectoryName(oszPath)!, true);
            }
            catch
            {
                // best effort
            }
        }

        #endregion

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
        public async Task BootstrapReturnsOkWhenEnvironmentAlreadyReady()
        {
            // A built venv exists: bootstrap must short-circuit to Ok (Success) without spawning the
            // setup process. This pins the "Ok result" contract the install notification keys its
            // flip-to-completed on; see TypeBeatSettingsSubsection.startInstall.
            string lab = Path.Combine(tempRoot, "lyriclab");
            Directory.CreateDirectory(lab);
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "# stub");

            string python = LyricMapImporter.PythonExeFor(lab);
            Directory.CreateDirectory(Path.GetDirectoryName(python)!);
            File.WriteAllText(python, "stub exe");
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);

            bool anyProgress = false;
            var result = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, _ => anyProgress = true, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(anyProgress, Is.False, "a ready environment should not emit setup progress");
        }

        [Test]
        public async Task BootstrapFailsWithClearErrorWhenNoSetupScript()
        {
            // No venv and no setup script to build one: bootstrap must fail with a descriptive error
            // (not hang) so the notification surfaces a failure state rather than a stale line.
            string lab = Path.Combine(tempRoot, "lyriclab");
            Directory.CreateDirectory(lab);

            var result = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain(LyricMapImporter.SetupScriptName));
        }

        #region Authoring marks through the import (backlog 202)

        [Test]
        public void MarkFreeLyricsSynthesizeByteIdenticalTiming()
        {
            // The whole of backlog 202's import work is gated on a line carrying a mark. This is
            // the pin on that gate: for lyrics with neither, the document is character for
            // character the three-key line shape the synthesiser has always emitted.
            string lyricsContent = File.ReadAllText(StandaloneMaps.Require("Friday Pilots Club - Spectator", "lyrics.txt"));

            string? actual = LyricMapImporter.SynthesizeTimingJsonFromLrc(lyricsContent);
            Assert.That(actual, Is.Not.Null);

            var lines = LrcParser.Parse(lyricsContent);

            string expected = JsonSerializer.Serialize(new
            {
                version = TimingJsonLoader.SUPPORTED_VERSION,
                song_end_ms = lines[^1].EndTime,
                lines = lines.Select(l => new
                {
                    text = l.RawText,
                    start_ms = l.StartTime,
                    end_ms = l.SingEndTime,
                }).ToArray()
            });

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void PipedLyricsPackageASubdividedMap()
        {
            // One pipe is a request for sub-word timing, so the packaged map leaves Line
            // granularity behind and carries the subdivision the mapper asked for.
            string audioPath = Path.Combine(tempRoot, "A - B.mp3");
            File.WriteAllText(audioPath, "fake audio");

            string? timing = LyricMapImporter.SynthesizeTimingJsonFromLrc("[00:01.00] ple|ase stay\n[00:05.00] plain line\n[00:07.00]\n");
            Assert.That(timing, Is.Not.Null);

            string oszPath = Path.Combine(tempRoot, "piped.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "A", "B", audioPath, timing!, "unused");
            Assert.That(result.Success, Is.True, result.Error);

            using var archive = ZipFile.OpenRead(oszPath);
            var beatmap = decode(readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))));
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(2));
            Assert.That(hitObjects[0].Granularity, Is.EqualTo(TimingGranularity.Syllable));
            Assert.That(hitObjects[0].Line.RawText, Is.EqualTo("please stay"), "the pipe never reaches the stored lyric");

            var unit = hitObjects[0].Line.Units[0];
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(unit.SyllableBoundaries.Count, Is.EqualTo(1));
            Assert.That(unit.SyllableBoundaries[0], Is.EqualTo((unit.StartTime + unit.EndTime) / 2).Within(1e-6));

            // The pipe-free line carries no words[] of its own, so it is still interpolated.
            Assert.That(hitObjects[1].Line.Units.All(u => u.SyllableBoundaries.Count == 0), Is.True);
        }

        [Test]
        public void AMarkerOnlyLyricLineBecomesAFreestyleLine()
        {
            string audioPath = Path.Combine(tempRoot, "A - B.mp3");
            File.WriteAllText(audioPath, "fake audio");

            string? timing = LyricMapImporter.SynthesizeTimingJsonFromLrc("[00:01.00] real one\n[00:03.00] &&&\n[00:09.00] real two\n[00:11.00]\n");
            Assert.That(timing, Is.Not.Null);
            Assert.That(timing, Does.Contain("\"freestyle\":true"), "the decoder needs the opt-in before it reads '&' as a marker");

            string oszPath = Path.Combine(tempRoot, "freestyle.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "A", "B", audioPath, timing!, "unused");
            Assert.That(result.Success, Is.True, result.Error);

            using var archive = ZipFile.OpenRead(oszPath);
            var beatmap = decode(readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))));
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(3));
            Assert.That(hitObjects[1].Line.RawText, Is.EqualTo("&&&"));
            Assert.That(hitObjects[1].StartTime, Is.EqualTo(3000));

            var typingLine = Gameplay.TypingLine.FromLyricLine(hitObjects[1].Line, TimingGranularity.Line);
            Assert.That(typingLine.Cells.Count(c => c.IsFreestyle), Is.EqualTo(3));
        }

        [Test]
        public void FlagFreestyleLinesOnlyTouchesAmpersandLines()
        {
            const string plain = "{\"version\":2,\"lines\":[{\"text\":\"me you\",\"start_ms\":1000,\"end_ms\":2000}]}";
            Assert.That(LyricMapImporter.FlagFreestyleLines(plain), Is.SameAs(plain), "an ampersand-free document is returned verbatim");

            const string marked = "{\"version\":2,\"lines\":["
                                  + "{\"text\":\"me & you\",\"start_ms\":1000,\"end_ms\":2000},"
                                  + "{\"text\":\"plain\",\"start_ms\":3000,\"end_ms\":4000}]}";

            string flagged = LyricMapImporter.FlagFreestyleLines(marked);

            using var doc = JsonDocument.Parse(flagged);
            var lines = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();

            Assert.That(lines[0].TryGetProperty("freestyle", out var flag), Is.True);
            Assert.That(flag.GetBoolean(), Is.True);
            Assert.That(lines[1].TryGetProperty("freestyle", out _), Is.False);

            // Malformed input is a pass-through, not a failure: this is a polish pass.
            Assert.That(LyricMapImporter.FlagFreestyleLines("{ not json &"), Is.EqualTo("{ not json &"));
        }

        #endregion

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
