// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Updater;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the movement check that gives the update download a way to end. Before it the
    /// download had no limit at all, and a throttled connection left the client reading
    /// "Downloading update..." indefinitely (observed: over 35 minutes).
    /// The watchdog holds no clock, so every case here drives time explicitly.
    /// </summary>
    [TestFixture]
    public class DownloadStallWatchdogTest
    {
        private const double timeout = DownloadStallWatchdog.STALL_TIMEOUT_MS;

        [Test]
        public void TimeoutIsOneMinute()
            => Assert.That(DownloadStallWatchdog.STALL_TIMEOUT_MS, Is.EqualTo(60000).Within(1e-9));

        [Test]
        public void FreshWatchdogIsNotStalledBeforeTheTimeout()
        {
            var watchdog = new DownloadStallWatchdog(1000);

            Assert.Multiple(() =>
            {
                Assert.That(watchdog.IsStalled(1000), Is.False);
                Assert.That(watchdog.IsStalled(1000 + timeout / 2), Is.False);
            });
        }

        /// <summary>
        /// A download that never reports a single percent is measured from construction, so it
        /// stalls out rather than waiting for a first report that is never coming.
        /// </summary>
        [Test]
        public void NoMovementAtAllStallsAfterTheTimeout()
        {
            var watchdog = new DownloadStallWatchdog(1000);

            Assert.That(watchdog.IsStalled(1000 + timeout + 1), Is.True);
        }

        [Test]
        public void MovementResetsTheClock()
        {
            var watchdog = new DownloadStallWatchdog(0);

            // 50 seconds in, still fine; a report at 50s then buys a fresh minute from there.
            Assert.That(watchdog.IsStalled(50000), Is.False);
            watchdog.ReportProgress(1, 50000);

            Assert.Multiple(() =>
            {
                // the point past which the un-reset watchdog would have been stalled.
                Assert.That(watchdog.IsStalled(timeout + 1), Is.False);
                Assert.That(watchdog.IsStalled(50000 + timeout), Is.False);
                Assert.That(watchdog.IsStalled(50000 + timeout + 1), Is.True);
            });
        }

        /// <summary>
        /// The load-bearing case: velopack calls the progress callback repeatedly with the same
        /// integer percentage while bytes trickle in below one percent. Counting those as movement
        /// would make the watchdog unable to ever fire on the exact connection it exists for.
        /// </summary>
        [Test]
        public void RepeatedReportsOfTheSamePercentDoNotReset()
        {
            var watchdog = new DownloadStallWatchdog(0);

            watchdog.ReportProgress(7, 1000);

            for (double now = 2000; now <= 1000 + timeout; now += 1000)
                watchdog.ReportProgress(7, now);

            Assert.Multiple(() =>
            {
                Assert.That(watchdog.IsStalled(1000 + timeout), Is.False);
                Assert.That(watchdog.IsStalled(1000 + timeout + 1), Is.True);
            });
        }

        /// <summary>
        /// A first report of 0 is real movement (velopack's first callback), not a no-op against
        /// an uninitialised percentage.
        /// </summary>
        [Test]
        public void FirstReportOfZeroPercentCountsAsMovement()
        {
            var watchdog = new DownloadStallWatchdog(0);

            watchdog.ReportProgress(0, 30000);

            Assert.Multiple(() =>
            {
                Assert.That(watchdog.IsStalled(30000 + timeout), Is.False);
                Assert.That(watchdog.IsStalled(30000 + timeout + 1), Is.True);
            });
        }

        /// <summary>
        /// The boundary belongs to the download: exactly at the timeout is not yet stalled, one
        /// millisecond past it is. Pinned so the comparison cannot silently flip strictness.
        /// </summary>
        [Test]
        public void BoundaryIsExclusive()
        {
            var watchdog = new DownloadStallWatchdog(500);

            Assert.Multiple(() =>
            {
                Assert.That(watchdog.IsStalled(500 + timeout - 1), Is.False);
                Assert.That(watchdog.IsStalled(500 + timeout), Is.False);
                Assert.That(watchdog.IsStalled(500 + timeout + 1), Is.True);
            });
        }

        /// <summary>
        /// Movement after a stall has already been observed clears it: nothing latches inside the
        /// watchdog, the caller owns the decision to give up.
        /// </summary>
        [Test]
        public void MovementAfterAStallClearsIt()
        {
            var watchdog = new DownloadStallWatchdog(0);

            Assert.That(watchdog.IsStalled(timeout + 1), Is.True);

            watchdog.ReportProgress(42, timeout + 1);

            Assert.That(watchdog.IsStalled(timeout + 2), Is.False);
        }

        /// <summary>
        /// A percentage going backwards (a resumed attempt restarting its count) is still a change,
        /// so it counts as movement.
        /// </summary>
        [Test]
        public void PercentGoingBackwardsCountsAsMovement()
        {
            var watchdog = new DownloadStallWatchdog(0);

            watchdog.ReportProgress(60, 1000);
            watchdog.ReportProgress(5, 40000);

            Assert.That(watchdog.IsStalled(40000 + timeout), Is.False);
        }
    }
}
