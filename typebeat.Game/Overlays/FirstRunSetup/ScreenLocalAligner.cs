// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Localisation;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Overlays.FirstRunSetup
{
    /// <summary>
    /// First-run pitch for the LOCAL lyriclab auto-aligner: explains what the aligner does (AI
    /// word-by-word lyric timing for created maps), why installing it locally is worth it on a
    /// machine with a good GPU (no server queue, nothing uploaded), and offers the one-time
    /// multi-GB install right here. Entirely skippable — server-side alignment remains the
    /// default for everyone else, and the install stays available from Settings.
    /// </summary>
    [LocalisableDescription(typeof(FirstRunSetupOverlayStrings), nameof(FirstRunSetupOverlayStrings.LocalAligner))]
    public partial class ScreenLocalAligner : WizardScreen
    {
        [Resolved(CanBeNull = true)]
        private ILocalAlignerManager alignerManager { get; set; }

        private ProgressRoundedButton installButton = null!;
        private OsuTextFlowContainer statusText = null!;

        // TextFlowContainer.Text is write-only; tracks whether anything has claimed the status
        // line yet so the async GPU probe doesn't overwrite install progress.
        private bool statusClaimed;

        private CancellationTokenSource installCancellation;

        [BackgroundDependencyLoader]
        private void load()
        {
            Content.Children = new Drawable[]
            {
                new OsuTextFlowContainer(cp => cp.Font = OsuFont.Default.With(size: CONTENT_FONT_SIZE))
                {
                    Text = FirstRunSetupOverlayStrings.LocalAlignerDescription,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                },
                installButton = new ProgressRoundedButton
                {
                    RelativeSizeAxes = Axes.X,
                    Text = FirstRunSetupOverlayStrings.InstallLocalAligner,
                    Action = startInstall,
                },
                statusText = new OsuTextFlowContainer(cp => cp.Font = OsuFont.Default.With(size: CONTENT_FONT_SIZE - 4))
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                },
            };

            if (alignerManager == null)
            {
                installButton.Enabled.Value = false;
                statusText.Text = "The aligner installer is unavailable in this build.";
                return;
            }

            if (alignerManager.IsInstalled)
            {
                installButton.Enabled.Value = false;
                installButton.Complete();
                statusText.Text = "The local auto-aligner is already installed.";
                return;
            }

            // GPU probe spawns a process — do it off the load path and annotate the pitch once known.
            Task.Run(() =>
            {
                bool gpu = alignerManager.GpuDetected;

                Schedule(() =>
                {
                    if (!statusClaimed)
                    {
                        statusText.Text = gpu
                            ? "An NVIDIA GPU was detected — the fast GPU build will be installed (~2.5 GB)."
                            : "No NVIDIA GPU detected — the CPU build will be installed (~2 GB). Alignment will be slower but still fully local.";
                    }
                });
            });
        }

        private void startInstall()
        {
            if (alignerManager == null)
                return;

            statusClaimed = true;
            installCancellation = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    var result = await alignerManager.InstallAsync(
                        line => Schedule(() => statusText.Text = line),
                        installCancellation.Token).ConfigureAwait(false);

                    Schedule(() =>
                    {
                        if (result.Success)
                        {
                            installButton.Complete();
                            statusText.Text = "The local auto-aligner is installed and will be used for your imports.";
                        }
                        else
                        {
                            installButton.Abort();
                            statusText.Text = $"Install failed: {result.Error} — you can retry, or install later from Settings. Server alignment keeps working either way.";
                        }
                    });
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Local aligner install failed");
                    Schedule(() =>
                    {
                        installButton.Abort();
                        statusText.Text = "Install failed unexpectedly — you can install later from Settings.";
                    });
                }
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            installCancellation?.Cancel();
        }
    }
}
