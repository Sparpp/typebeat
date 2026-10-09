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
    /// word-by-word lyric timing for created maps), why it is worth the one-time multi-GB download
    /// on a machine with a good GPU (nothing uploaded, no account needed), and offers the install
    /// right here. Entirely skippable: with no aligner installed a signed-in player can opt in to the
    /// server aligner on the import screen (backlog 413), otherwise an import times itself from the
    /// lyrics' own [mm:ss.xx] line stamps, and the install stays available from Settings.
    /// </summary>
    [LocalisableDescription(typeof(FirstRunSetupOverlayStrings), nameof(FirstRunSetupOverlayStrings.LocalAligner))]
    public partial class ScreenLocalAligner : WizardScreen
    {
        [Resolved(CanBeNull = true)]
        private ILocalAlignerManager? alignerManager { get; set; }

        private ProgressRoundedButton installButton = null!;
        private OsuTextFlowContainer statusText = null!;

        // TextFlowContainer.Text is write-only; tracks whether anything has claimed the status
        // line yet so the async GPU probe doesn't overwrite install progress.
        private bool statusClaimed;

        private CancellationTokenSource installCancellation = null!;

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
                // A mismatched install blocks automatic alignment (backlog 410), so the line says so
                // rather than presenting the update as optional.
                statusText.Text = LocalAlignerVersion.FirstRunStatus(alignerManager.VersionStatus, alignerManager.InstalledVersion, alignerManager.ShippedVersion)
                                  ?? "The local auto-aligner is already installed.";
                return;
            }

            // A venv whose setup never completed: the same button repairs it (see InstallAsync).
            if (alignerManager.NeedsRepair)
            {
                statusClaimed = true;
                statusText.Text = "An earlier install of the local auto-aligner did not finish. Press the button to repair it: a working environment is kept (seconds), a broken one is downloaded again.";
                return;
            }

            // GPU probe spawns a process; do it off the load path and annotate the pitch once known.
            var manager = alignerManager;
            Task.Run(() =>
            {
                bool gpu = manager.GpuDetected;

                Schedule(() =>
                {
                    if (!statusClaimed)
                    {
                        statusText.Text = gpu
                            ? "An NVIDIA GPU was detected. The fast GPU build will be installed (~2.5 GB)."
                            : "No NVIDIA GPU detected. The CPU build will be installed (~2 GB). Alignment will be slower but still fully local.";
                    }
                });
            });
        }

        private void startInstall()
        {
            var manager = alignerManager;

            if (manager == null)
                return;

            statusClaimed = true;
            installCancellation = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    var result = await manager.InstallAsync(
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
                            statusText.Text = $"Install failed: {result.Error}. You can retry, or install later from Settings. Until then, imports can use the server aligner (sign in and tick it on the import screen) or your lyrics' own [mm:ss.xx] line stamps.";
                        }
                    });
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Local aligner install failed");
                    Schedule(() =>
                    {
                        installButton.Abort();
                        statusText.Text = "Install failed unexpectedly. You can install later from Settings.";
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
