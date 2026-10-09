// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the import flow's raw-line -> stage summariser. The import pipeline (the C# orchestrator
    /// plus the vendored lyriclab python and demucs underneath it) only speaks free-form text, so
    /// this classification is the whole contract between it and the progress bars: a wrong or
    /// missing rule is either a raw internal on screen or a stage that never lights up.
    /// </summary>
    [TestFixture]
    public class ImportProgressParserTest
    {
        // Verbatim samples from a real run: lyriclab's log() lines carry a wall-clock prefix, demucs
        // and torch hub print bare tqdm bars, and the C# orchestrator interleaves its own notices.
        [TestCase("starting import", ImportStage.Preparing)]
        [TestCase("[12:34:56] lyrics: 24 lines, 180 words; anchor mode: ref", ImportStage.Preparing)]
        [TestCase("[12:35:20] audio: 269.1s at 16k mono (Some Artist - A Song.vocals16k.wav)", ImportStage.Preparing)]
        [TestCase("automatic alignment off, using your line timestamps", ImportStage.Preparing)]
        [TestCase("no line stamps found, using fully automatic alignment (less accurate)", ImportStage.Preparing)]
        [TestCase("some lines are stamped, aligning the unstamped ones inside their sections", ImportStage.Preparing)]
        [TestCase("no local aligner environment found", ImportStage.Preparing)]
        [TestCase("local aligner environment not set up (run lyriclab/setup.ps1 for word timing)", ImportStage.Preparing)]
        [TestCase("[12:34:57] separation: running demucs (htdemucs) on cuda ...", ImportStage.SeparatingVocals)]
        [TestCase(@"[12:34:57] separation: cached (C:\lyriclab\work\htdemucs\song\vocals.wav)", ImportStage.SeparatingVocals)]
        [TestCase("Separating track C:\\music\\song.wav", ImportStage.SeparatingVocals)]
        // Backlog 414: the importer's own line as the --separate-only run starts, and the run's last.
        [TestCase(LyricMapImporter.SEPARATING_VOCALS_PROGRESS, ImportStage.SeparatingVocals)]
        [TestCase("[12:36:40] separation: done, total time: 71s", ImportStage.SeparatingVocals)]
        [TestCase("[12:35:21] loading MMS_FA aligner model (first run downloads ~1.2 GB)...", ImportStage.LoadingModel)]
        [TestCase("[12:35:40] computing emissions...", ImportStage.AligningLyrics)]
        [TestCase("[12:35:41] emissions: chunk 7/31 frames=1490", ImportStage.AligningLyrics)]
        [TestCase("[12:36:10] emissions: 8407 frames x 32 labels", ImportStage.AligningLyrics)]
        [TestCase("[12:36:11] auto anchors: 1,4,7,11", ImportStage.AligningLyrics)]
        [TestCase("[12:36:30] re-aligned lines 4..8 in 1.2s", ImportStage.AligningLyrics)]
        [TestCase("alignment complete", ImportStage.AligningLyrics)]
        [TestCase("line-timed alignment ready (no word-level timing)", ImportStage.AligningLyrics)]
        [TestCase(LyricMapImporter.ENHANCED_LRC_PROGRESS, ImportStage.AligningLyrics)]
        [TestCase("aligner unavailable (aligner exited with code 1: Traceback), trying next option", ImportStage.FallingBack)]
        [TestCase("packaging map", ImportStage.Packaging)]
        [TestCase("importing beatmap", ImportStage.Importing)]
        // The video split's own step. The first case is the PRODUCTION string (the extractor lives in
        // the ruleset assembly, the parser in the shell one, so this test is what ties them together).
        [TestCase(FfmpegAudioTrackExtractor.EXTRACTING_NOTICE, ImportStage.ExtractingAudio)]
        [TestCase("extracted audio to Some Artist - A Song.mp3", ImportStage.ExtractingAudio)]
        public void TestKnownStageLines(string line, ImportStage expected)
            => Assert.That(ImportProgressParser.Parse(line).Stage, Is.EqualTo(expected));

        /// <summary>
        /// Anything the pipeline emits that isn't a recognised stage (python warnings, torch chatter,
        /// bare tqdm bars, the aligner's closing bookkeeping) must NOT claim a stage: the display
        /// keeps whatever step it was on rather than inventing one or printing the line.
        /// </summary>
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("Torch was not compiled with flash attention.")]
        [TestCase("Selected model is a bag of 4 models. You will see that many progress bars per track.")]
        [TestCase(" 45%|####5     | 121.1/269.1 [00:20<00:24,  6.05seconds/s]")]
        [TestCase(@"[12:36:40] outputs written to C:\lyriclab\out\typebeat_import_Some Artist - A Song")]
        [TestCase("[12:36:40] total time: 118s")]
        // The split's degrade: nothing was extracted, so it must not light the extraction step (and
        // "extractor available" must not read as the aligner handover's "unavailable" either).
        [TestCase("no audio extractor available (no ffmpeg found on this machine), keeping the video file as the map's audio")]
        // Backlog 414: an import that could not isolate the vocals packages the map anyway. Its reason
        // names the aligner (and could say "unavailable"), which must not read as an alignment
        // starting or as the aligner handover.
        [TestCase(ImportProgressParser.VOCALS_NOT_ISOLATED_PREFIX + LyricMapImporter.VOCALS_NEED_THE_ALIGNER)]
        [TestCase("vocals not isolated: the separation failed (aligner unavailable, trying next option).")]
        [TestCase(@"[12:36:40] vocals stem written to C:\lyriclab\out\typebeat_vocals_Some Artist - A Song\vocals.wav")]
        public void TestUnknownLinesClaimNoStage(string line)
            => Assert.That(ImportProgressParser.Parse(line).Stage, Is.Null);

        /// <summary>
        /// Backlog 413 INVERTS the retirement pin (backlog 287's <c>TestNoStagePromisesAServer</c>, which
        /// held while no import could reach a server): the server aligner is back as an opt-in, so
        /// exactly three stages name it, the upload, the queue wait and the server's own run, and
        /// every other stage still must not. A local run, or a line-stamped one, never says it is on a
        /// server, and a server run never shows the local aligner's steps as rows of its own.
        /// </summary>
        [Test]
        public void TestOnlyTheServerStagesNameTheServer()
        {
            var server = new[] { ImportStage.Uploading, ImportStage.WaitingForServer, ImportStage.ServerAligning };

            foreach (ImportStage stage in Enum.GetValues<ImportStage>())
            {
                string label = ImportProgressParser.LabelFor(stage);

                if (server.Contains(stage))
                    Assert.That(label, Does.Contain("server"), $"{stage} is a server stage and should say so");
                else
                    Assert.That(label, Does.Not.Contain("server").IgnoreCase, $"{stage}'s label names a server");
            }

            Assert.That(server.Select(ImportProgressParser.LabelFor), Is.EqualTo(new[] { "uploading to the server", "waiting for the server", "aligning on the server" }));
        }

        /// <summary>
        /// The server aligner client's lines (backlog 413), produced by the very builders it uses: the
        /// upload with its percentage, the queue position as a NOTE (never in the label, which may not
        /// carry digits), and the server's own aligner log relayed under one prefix, so the server's
        /// steps become the server row's note and bar rather than local-stage rows.
        /// </summary>
        [Test]
        public void TestServerLines()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.SERVER_UPLOADING), Is.EqualTo(new ImportProgressUpdate(ImportStage.Uploading, null)));
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.SERVER_UPLOADING + " 45%"), Is.EqualTo(new ImportProgressUpdate(ImportStage.Uploading, 0.45f)));

                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerQueueLine(3)), Is.EqualTo(new ImportProgressUpdate(ImportStage.WaitingForServer, null, "position 3 in the queue")));
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerQueueLine(1)), Is.EqualTo(new ImportProgressUpdate(ImportStage.WaitingForServer, null, "next in the queue")));
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerQueueLine(null)), Is.EqualTo(new ImportProgressUpdate(ImportStage.WaitingForServer, null)));

                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerProgressLine(null)), Is.EqualTo(new ImportProgressUpdate(ImportStage.ServerAligning, null)));
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerProgressLine("[12:34:57] separation: running demucs (htdemucs) on cpu ...")),
                    Is.EqualTo(new ImportProgressUpdate(ImportStage.ServerAligning, null, "separating vocals")));
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerProgressLine("[12:35:41] emissions: chunk 7/31 frames=1490")),
                    Is.EqualTo(new ImportProgressUpdate(ImportStage.ServerAligning, 7f / 31f, "aligning lyrics")));
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerProgressLine("[12:35:21] loading MMS_FA aligner model (first run downloads ~1.2 GB)...")),
                    Is.EqualTo(new ImportProgressUpdate(ImportStage.ServerAligning, null, "loading model")));

                // A failure the server relays mid-run is still the server's run, never the local
                // aligner's hand-over to the line stamps.
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerProgressLine("aligner unavailable (x), trying next option")).Stage, Is.EqualTo(ImportStage.ServerAligning));
                Assert.That(ImportProgressParser.Parse(ImportProgressParser.ServerProgressLine("done")), Is.EqualTo(new ImportProgressUpdate(ImportStage.ServerAligning, null)));
            });
        }

        /// <summary>
        /// Whatever a line says, the thing shown to the user is the stage's fixed label. Nothing that
        /// could carry a filename, path, model name, size or counter is ever part of it.
        /// </summary>
        [Test]
        public void TestLabelsCarryNoInternals()
        {
            foreach (ImportStage stage in Enum.GetValues<ImportStage>())
            {
                string label = ImportProgressParser.LabelFor(stage);

                Assert.That(label, Is.Not.Empty);
                Assert.That(label, Is.EqualTo(label.ToLowerInvariant()), $"{stage} label should match the screen's lowercase copy");
                Assert.That(label.Length, Is.LessThanOrEqualTo(25), $"{stage} label should be a short summary");
                Assert.That(label.Any(char.IsDigit), Is.False, $"{stage} label leaks a counter");
                Assert.That(label.Any(c => @"/\.():".Contains(c)), Is.False, $"{stage} label leaks a path or an aside");
            }
        }

        /// <summary>Stage labels are distinct; two steps showing the same text would read as a stall.</summary>
        [Test]
        public void TestLabelsAreDistinct()
        {
            var labels = Enum.GetValues<ImportStage>().Select(ImportProgressParser.LabelFor).ToArray();
            Assert.That(labels.Distinct(), Has.Exactly(labels.Length).Items);
        }

        [Test]
        public void TestTimestampPrefixStripped()
        {
            Assert.That(ImportProgressParser.Clean("[12:34:56] computing emissions..."), Is.EqualTo("computing emissions..."));
            Assert.That(ImportProgressParser.Clean("[9:07] computing emissions..."), Is.EqualTo("computing emissions..."));

            // A bracketed non-timestamp is content, not a prefix, and must survive intact.
            Assert.That(ImportProgressParser.Clean("[warning] something"), Is.EqualTo("[warning] something"));
        }

        [Test]
        public void TestAnsiEscapesStripped()
            => Assert.That(ImportProgressParser.Clean("\u001b[A\u001b[2K 50%|##### | 1/2"), Is.EqualTo("50%|##### | 1/2"));

        /// <summary>tqdm percentages drive the bar; the percentage wins over the same bar's ratio.</summary>
        [Test]
        public void TestPercentageProgress()
        {
            Assert.That(ImportProgressParser.ParseProgress(" 45%|####5     | 121.1/269.1 [00:20<00:24,  6.05seconds/s]"), Is.EqualTo(0.45f).Within(0.001f));
            Assert.That(ImportProgressParser.ParseProgress("  0%|          | 0.0/269.1 [00:00<?, ?seconds/s]"), Is.EqualTo(0f).Within(0.001f));
            Assert.That(ImportProgressParser.ParseProgress("100%|##########| 269.1/269.1 [01:58<00:00,  2.27seconds/s]"), Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void TestRatioProgress()
        {
            Assert.That(ImportProgressParser.ParseProgress("emissions: chunk 7/31 frames=1490"), Is.EqualTo(7f / 31f).Within(0.001f));
            Assert.That(ImportProgressParser.ParseProgress("emissions: chunk 31/31 frames=800"), Is.EqualTo(1f).Within(0.001f));
        }

        /// <summary>
        /// Lines with no notion of completion must leave the bar alone. Paths, versions and dates all
        /// contain a slash or a number and must not be mistaken for a done/total pair.
        /// </summary>
        [TestCase("computing emissions...")]
        [TestCase("emissions: 8407 frames x 32 labels")]
        [TestCase("local aligner environment not set up (run lyriclab/setup.ps1 for word timing)")]
        [TestCase("loading MMS_FA aligner model (first run downloads ~1.2 GB)...")]
        [TestCase("audio: 269.1s at 16k mono (song.vocals16k.wav)")]
        [TestCase("separation: running demucs (htdemucs) on cuda ...")]
        [TestCase("built 2024/01/02")]
        [TestCase("")]
        public void TestNoProgressFraction(string line)
            => Assert.That(ImportProgressParser.ParseProgress(line), Is.Null);

        /// <summary>
        /// Aligner version 10's fused evidence lines (backlog 411, on for every import) arrive after
        /// the version 9 decode, so at most they hold the display on aligning: none may read as a
        /// fall-back (the path falling back to version 9 is not the aligner being unavailable) or
        /// send it back to an earlier stage, and none carries a bogus progress fraction.
        /// </summary>
        [TestCase("[12:34:56] fused evidence: lyrics language english (flag); fuse median3")]
        [TestCase("[12:34:56] fused evidence: lyrics language japanese (flag); only English is validated, running the version 9 path")]
        [TestCase("[12:34:56] fused evidence: not available with vocal mode estimated; running the version 9 path")]
        [TestCase("[12:40:01] fused evidence: downloading the QMUL multilingual weights (57 MB, https://raw.githubusercontent.com/jhuang448/LyricsAlignment-Multilingual/ca1a3923d6c8bf7d20eefa1080f3f104a6173d38/checkpoints/checkpoint_Baseline)")]
        [TestCase("[12:40:09] fused evidence: QMUL multilingual pass, 11873 frames in 6.2s")]
        [TestCase("[12:40:10] fused evidence: decoding the QMUL path ('*' penalty 2)")]
        [TestCase("[12:40:13] fused evidence: decoding the ep path (weight 1, floor -8)")]
        [TestCase("[12:40:16] fused evidence: fuse median3, 41 word start(s) moved from version 9, 17 word(s) flagged for review")]
        [TestCase("[12:40:02] WARNING: fused evidence failed (RuntimeError: a dependency is missing (No module named 'phonemizer'); install phonemizer and espeakng-loader); keeping the version 9 path")]
        public void FusedEvidenceLinesNeverMoveTheDisplayBack(string line)
        {
            var update = ImportProgressParser.Parse(line);

            Assert.That(update.Stage, Is.Null.Or.EqualTo(ImportStage.AligningLyrics));
            Assert.That(update.Progress, Is.Null);
        }
    }
}
