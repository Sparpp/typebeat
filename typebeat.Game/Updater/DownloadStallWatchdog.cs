// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Updater
{
    /// <summary>
    /// Decides whether an in-flight update download has stopped making progress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the download had no time limit at all: a throttled connection that
    /// delivers nothing simply leaves the notification reading "Downloading update..." forever
    /// (observed: over 35 minutes). A plain overall timeout is the wrong instrument, since a slow
    /// but honest download of a full package can legitimately take a long time. What is being
    /// watched here is MOVEMENT, not elapsed time.
    /// </para>
    /// <para>
    /// This type holds no clock of its own: every method takes the current time in milliseconds from
    /// its caller, which keeps the decision pure and directly testable. Any monotonic millisecond
    /// source will do as long as the caller uses one consistently.
    /// </para>
    /// <para>
    /// Velopack reports download progress as an integer percentage (0 to 100), so the finest movement
    /// this can see is one percent. The deliberate tradeoff: a connection moving slower than one
    /// percent per <see cref="STALL_TIMEOUT_MS"/> reads as stalled even though it is technically
    /// alive. That is acceptable because velopack keeps the partial package on disk, so the attempt
    /// that follows a stall resumes from where this one stopped rather than starting over. Repeatedly
    /// tripping the watchdog on a very slow link therefore still converges, whereas hanging forever
    /// does not.
    /// </para>
    /// </remarks>
    public class DownloadStallWatchdog
    {
        /// <summary>
        /// How long the reported percentage may sit unchanged before the download counts as stalled.
        /// </summary>
        public const double STALL_TIMEOUT_MS = 60_000;

        private readonly object stateLock = new object();

        private double lastMovementMs;

        /// <summary>
        /// The last percentage seen. -1 until the first report, so a genuine first report of 0
        /// still counts as movement.
        /// </summary>
        private int lastPercent = -1;

        /// <summary>
        /// Starts the watchdog. A download that never reports anything is measured against
        /// <paramref name="startMs"/>, so it stalls out rather than waiting indefinitely.
        /// </summary>
        public DownloadStallWatchdog(double startMs)
        {
            lastMovementMs = startMs;
        }

        /// <summary>
        /// Feeds a progress report in. Only a CHANGED percentage counts as movement: velopack calls
        /// the progress callback repeatedly with the same value while bytes trickle in below one
        /// percent, and treating those as movement would defeat the whole check.
        /// </summary>
        /// <remarks>
        /// Reports arrive on velopack's download thread while <see cref="IsStalled"/> is polled from
        /// the update thread, hence the lock.
        /// </remarks>
        public void ReportProgress(int percent, double nowMs)
        {
            lock (stateLock)
            {
                if (percent == lastPercent)
                    return;

                lastPercent = percent;
                lastMovementMs = nowMs;
            }
        }

        /// <summary>
        /// Whether more than <see cref="STALL_TIMEOUT_MS"/> has passed since the last movement
        /// (or since construction, if nothing has moved yet). Exactly at the timeout is not yet
        /// stalled: the boundary belongs to the download.
        /// </summary>
        public bool IsStalled(double nowMs)
        {
            lock (stateLock)
                return nowMs - lastMovementMs > STALL_TIMEOUT_MS;
        }
    }
}
