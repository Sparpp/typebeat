// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Linq;
using NUnit.Framework;
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
        [TestCase("no local aligner environment found", ImportStage.Preparing)]
        [TestCase("local aligner environment not set up (run lyriclab/setup.ps1 for word timing)", ImportStage.Preparing)]
        [TestCase("[12:34:57] separation: running demucs (htdemucs) on cuda ...", ImportStage.SeparatingVocals)]
        [TestCase(@"[12:34:57] separation: cached (C:\lyriclab\work\htdemucs\song\vocals.wav)", ImportStage.SeparatingVocals)]
        [TestCase("Separating track C:\\music\\song.wav", ImportStage.SeparatingVocals)]
        [TestCase("[12:35:21] loading MMS_FA aligner model (first run downloads ~1.2 GB)...", ImportStage.LoadingModel)]
        [TestCase("[12:35:40] computing emissions...", ImportStage.AligningLyrics)]
        [TestCase("[12:35:41] emissions: chunk 7/31 frames=1490", ImportStage.AligningLyrics)]
        [TestCase("[12:36:10] emissions: 8407 frames x 32 labels", ImportStage.AligningLyrics)]
        [TestCase("[12:36:11] auto anchors: 1,4,7,11", ImportStage.AligningLyrics)]
        [TestCase("[12:36:30] re-aligned lines 4..8 in 1.2s", ImportStage.AligningLyrics)]
        [TestCase("alignment complete", ImportStage.AligningLyrics)]
        [TestCase("line-timed alignment ready (no word-level timing)", ImportStage.AligningLyrics)]
        [TestCase("uploading to the type!beat server for alignment...", ImportStage.Uploading)]
        [TestCase("waiting for the server aligner (this can take a few minutes)...", ImportStage.WaitingForServer)]
        [TestCase("aligner unavailable (aligner exited with code 1: Traceback), trying next option", ImportStage.FallingBack)]
        [TestCase("server alignment unavailable (sign in to type!beat), trying line-timed fallback", ImportStage.FallingBack)]
        [TestCase("packaging map", ImportStage.Packaging)]
        [TestCase("importing beatmap", ImportStage.Importing)]
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
        public void TestUnknownLinesClaimNoStage(string line)
            => Assert.That(ImportProgressParser.Parse(line).Stage, Is.Null);

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
    }
}
