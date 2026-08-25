// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Logging;
using osu.Framework.Threading;
using typebeat.Game;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Screens.Play;
using typebeat.Game.Updater;
using Velopack;
using Velopack.Sources;
using UpdateManager = typebeat.Game.Updater.UpdateManager;

namespace typebeat.Desktop.Updater
{
    public partial class VelopackUpdateManager : UpdateManager
    {
        [Resolved]
        private INotificationOverlay notificationOverlay { get; set; } = null!;

        [Resolved]
        private OsuGameBase game { get; set; } = null!;

        [Resolved]
        private ILocalUserPlayInfo? localUserInfo { get; set; }

        private bool isInGameplay => localUserInfo?.PlayingState.Value != LocalUserPlayingState.NotPlaying;

        private ScheduledDelegate? scheduledBackgroundCheck;

        /// <summary>
        /// How often the stall watchdog is consulted while a download is in flight. Far finer than
        /// <see cref="DownloadStallWatchdog.STALL_TIMEOUT_MS"/>, so the poll interval contributes
        /// only a few seconds of slack to when a stall is noticed.
        /// </summary>
        private const double stall_poll_interval_ms = 5000;

        /// <summary>
        /// Set when the watchdog, not the user, cancelled the in-flight download. Written from the
        /// update thread's poll and read from the download task, hence volatile.
        /// </summary>
        private volatile bool downloadStalled;

        /// <summary>
        /// Monotonic millisecond source for the stall watchdog. Deliberately not a wall clock (which
        /// jumps) and not the drawable clock (which only advances on the update thread, while
        /// velopack's progress callbacks arrive on its own download thread).
        /// </summary>
        private static double currentTimeMs() => Environment.TickCount64;

        private void scheduleNextUpdateCheck()
        {
            scheduledBackgroundCheck?.Cancel();
            scheduledBackgroundCheck = Scheduler.AddDelayed(() =>
            {
                log("Running scheduled background update check...");
                CheckForUpdate();
            }, 60000 * 30);
        }

        protected override async Task<bool> PerformUpdateCheck(CancellationToken cancellationToken)
        {
            scheduledBackgroundCheck?.Cancel();

            if (isInGameplay)
            {
                log("Update check cancelled - user is in gameplay");
                scheduleNextUpdateCheck();
                return false;
            }

            try
            {
                // the feed URLs and why there are two of them live in UpdateFeed.
                Velopack.UpdateManager updateManager = createUpdateManager(UpdateFeed.PRIMARY_URL);
                UpdateInfo? update;

                try
                {
                    update = await updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    // the primary feed is the direct-origin host, which a network may block outright.
                    // One retry against the Cloudflare-proxied root before giving up for this cycle.
                    log($"Update check against the primary feed failed ({e.Message}), retrying against the fallback feed");

                    updateManager = createUpdateManager(UpdateFeed.FALLBACK_URL);
                    update = await updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    log("Update check cancelled");
                    scheduleNextUpdateCheck();
                    return true;
                }

                if (update == null)
                {
                    // No update is available.
                    log("No update found");
                    scheduleNextUpdateCheck();
                    return false;
                }

                // Download update in the background while notifying awaiters of the update being available.
                log($"New update available: {update.TargetFullRelease.Version}");
                downloadUpdate(updateManager, update, cancellationToken);
                return true;
            }
            catch (Exception e)
            {
                log($"Update check failed with error ({e.Message})");

                // we shouldn't crash on a web failure. or any failure for the matter.
                scheduleNextUpdateCheck();
                return true;
            }
        }

        private static Velopack.UpdateManager createUpdateManager(string feedUrl)
        {
            IUpdateSource updateSource = new SimpleWebSource(feedUrl);

            return new Velopack.UpdateManager(updateSource, new UpdateOptions
            {
                AllowVersionDowngrade = true
            });
        }

        private void downloadUpdate(Velopack.UpdateManager updateManager, UpdateInfo update, CancellationToken cancellationToken) => Task.Run(async () =>
        {
            log($"Beginning download of update {update.TargetFullRelease.Version}...");

            UpdateDownloadProgressNotification progressNotification = new UpdateDownloadProgressNotification(cancellationToken)
            {
                CompletionClickAction = () =>
                {
                    restartToApplyUpdate(updateManager, update);
                    return true;
                }
            };

            downloadStalled = false;

            var watchdog = new DownloadStallWatchdog(currentTimeMs());

            // not wrapped in `using`: the poll delegate runs on the update thread and cancels this,
            // so it is disposed there too (see the finally below) rather than racing a tick.
            var stallCancellation = new CancellationTokenSource();
            ScheduledDelegate? stallPoll = null;

            try
            {
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(progressNotification.CancellationToken, cancellationToken, stallCancellation.Token))
                {
                    progressNotification.StartDownload();
                    runOutsideOfGameplay(() => notificationOverlay.Post(progressNotification), cts.Token);

                    stallPoll = Scheduler.AddDelayed(() =>
                    {
                        if (downloadStalled || !watchdog.IsStalled(currentTimeMs()))
                            return;

                        downloadStalled = true;
                        log($"Update download made no progress for {DownloadStallWatchdog.STALL_TIMEOUT_MS / 1000} seconds, cancelling");
                        stallCancellation.Cancel();
                    }, stall_poll_interval_ms, true);

                    await updateManager.DownloadUpdatesAsync(update, p =>
                    {
                        watchdog.ReportProgress(p, currentTimeMs());
                        progressNotification.Progress = p / 100f;
                    }, cts.Token).ConfigureAwait(false);

                    runOutsideOfGameplay(() => progressNotification.State = ProgressNotificationState.Completed, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                progressNotification.FailDownload();

                if (downloadStalled)
                {
                    // a stall is not the user's decision, so it gets a way back: an explicit retry
                    // affordance (FailDownload closes the progress notification outright) plus the
                    // usual background schedule, which velopack resumes the partial package with.
                    log(@"Update download stalled");
                    postStalledDownloadNotification(cancellationToken);
                    scheduleNextUpdateCheck();
                }
                else
                    log(@"Update cancelled");
            }
            catch (Exception e)
            {
                // In the case of an error, a separate notification will be displayed.
                progressNotification.FailDownload();
                Logger.Error(e, @"Update failed!");

                // without this the background check loop ends here, and a single failed download
                // means the client never looks for an update again until it is restarted.
                scheduleNextUpdateCheck();
            }
            finally
            {
                // both hop to the update thread so they cannot interleave with a poll tick that is
                // already running and about to touch `stallCancellation`.
                Scheduler.Add(() =>
                {
                    stallPoll?.Cancel();
                    stallCancellation.Dispose();
                });
            }

            return true;
        }, cancellationToken);

        private void postStalledDownloadNotification(CancellationToken cancellationToken) => runOutsideOfGameplay(() => notificationOverlay.Post(new SimpleNotification
        {
            Text = @"Update download stalled. Click to retry.",
            Icon = FontAwesome.Solid.Download,
            Activated = () =>
            {
                CheckForUpdate();
                return true;
            }
        }), cancellationToken);

        private void runOutsideOfGameplay(Action action, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (isInGameplay)
            {
                Scheduler.AddDelayed(() => runOutsideOfGameplay(action, cancellationToken), 1000);
                return;
            }

            action();
        }

        private void restartToApplyUpdate(Velopack.UpdateManager updateManager, UpdateInfo update)
        {
            game.RestartOnExitAction = () => updateManager.WaitExitThenApplyUpdates(update.TargetFullRelease);
            game.AttemptExit();
        }

        private static void log(string text) => Logger.Log($"VelopackUpdateManager: {text}");
    }
}
