// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Overlays;
using typebeat.Game.Screens.Edit.Submission;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Drives <see cref="ImportProgressDisplay"/> with verbatim transcripts of real import runs and
    /// asserts on the rows it grows. The point of the panel is that the user sees a handful of short
    /// stage labels instead of the pipeline's chatter, so the assertions are on exactly that: which
    /// rows exist, in what order, and that nothing raw ever becomes a row.
    /// </summary>
    [TestFixture]
    public partial class TestSceneImportProgressDisplay : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        private ImportProgressDisplay display = null!;

        private string[] labels() => display.ChildrenOfType<SubmissionStageProgress>().Select(s => s.StageDescription.ToString()).ToArray();

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create display", () => Child = display = new ImportProgressDisplay
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Width = 0.7f,
            });
        }

        [Test]
        public void TestLocalAlignerRun()
        {
            AddStep("report a local aligner run", () => report(
                "starting import",
                "[12:34:56] lyrics: 24 lines, 180 words; anchor mode: ref",
                "[12:34:57] separation: running demucs (htdemucs) on cuda ...",
                "Selected model is a bag of 4 models. You will see that many progress bars per track.",
                "  0%|          | 0.0/269.1 [00:00<?, ?seconds/s]",
                " 45%|####5     | 121.1/269.1 [00:20<00:24,  6.05seconds/s]",
                "100%|##########| 269.1/269.1 [01:58<00:00,  2.27seconds/s]",
                "[12:35:20] audio: 269.1s at 16k mono (Some Artist - A Song.vocals16k.wav)",
                "[12:35:21] loading MMS_FA aligner model (first run downloads ~1.2 GB)...",
                "[12:35:40] computing emissions...",
                "[12:35:41] emissions: chunk 7/31 frames=1490",
                "[12:36:10] emissions: 8407 frames x 32 labels",
                "[12:36:40] outputs written to C:\\lyriclab\\out\\typebeat_import_Some Artist - A Song",
                "[12:36:40] total time: 118s",
                "alignment complete",
                "packaging map",
                "importing beatmap"));

            // The transcript revisits preparation ("audio: ...") after separation has started; the
            // panel stays where it is rather than growing a second "preparing" row or walking back.
            AddAssert("rows are the short stage summaries", () => labels(), () => Is.EqualTo(new[]
            {
                "preparing",
                "separating vocals",
                "loading model",
                "aligning lyrics",
                "packaging map",
                "importing beatmap",
            }));

            AddStep("complete", () => display.Complete());
            AddAssert("no rows added by completion", () => labels().Length, () => Is.EqualTo(6));
        }

        [Test]
        public void TestServerRun()
        {
            AddStep("report a server run", () => report(
                "starting import",
                "no local aligner environment found",
                "uploading to the type!beat server for alignment...",
                "waiting for the server aligner (this can take a few minutes)...",
                "[12:34:57] separation: running demucs (htdemucs) on cpu ...",
                "[12:35:40] computing emissions...",
                "server alignment complete",
                "packaging map",
                "importing beatmap"));

            AddAssert("server steps lead the run", () => labels(), () => Is.EqualTo(new[]
            {
                "preparing",
                "uploading to the server",
                "waiting for the server",
                "separating vocals",
                "aligning lyrics",
                "packaging map",
                "importing beatmap",
            }));
        }

        [Test]
        public void TestFallbackStartsANewRound()
        {
            AddStep("report a local run that gives up", () => report(
                "starting import",
                "[12:34:57] separation: running demucs (htdemucs) on cuda ...",
                "[12:35:40] computing emissions...",
                "aligner unavailable (aligner exited with code 1: Traceback), trying next option",
                "uploading to the type!beat server for alignment...",
                "[12:36:57] separation: running demucs (htdemucs) on cpu ..."));

            AddAssert("handover is its own row and the retry gets fresh rows", () => labels(), () => Is.EqualTo(new[]
            {
                "preparing",
                "separating vocals",
                "aligning lyrics",
                "trying another aligner",
                "uploading to the server",
                "separating vocals",
            }));
        }

        [Test]
        public void TestUnknownLinesNeverBecomeRows()
        {
            AddStep("report chatter only", () => report(
                "Torch was not compiled with flash attention.",
                "  0%|          | 0.0/269.1 [00:00<?, ?seconds/s]",
                "UserWarning: torchaudio._backend.set_audio_backend has been deprecated",
                "",
                "   "));

            AddAssert("a single generic row", () => labels(), () => Is.EqualTo(new[] { "preparing" }));
        }

        [Test]
        public void TestFailureKeepsTheRunningRow()
        {
            AddStep("report then fail", () =>
            {
                report("starting import", "[12:35:40] computing emissions...");
                display.Fail("aligner exited with code 1: ModuleNotFoundError: no module named 'demucs'");
            });

            AddAssert("failure lands on the running row", () => labels(), () => Is.EqualTo(new[] { "preparing", "aligning lyrics" }));

            AddStep("reset for a retry", () => display.Reset());
            AddAssert("rows cleared", () => labels(), () => Is.Empty);
        }

        private void report(params string[] lines)
        {
            foreach (string line in lines)
                display.Report(line);
        }
    }
}
