// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Updater;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the velopack feed URLs the desktop updater checks. These are literals on purpose:
    /// the point of the test is that a change to either one is a deliberate, visible edit, since
    /// pointing the updater at the wrong host either breaks updates for everyone or (if it were
    /// ever aimed at upstream osu releases) turns an installed type!beat into osu!lazer.
    /// </summary>
    [TestFixture]
    public class UpdateFeedTest
    {
        /// <summary>
        /// The primary feed is the Cloudflare-proxied website root, the same order as the game API
        /// (see <see cref="Game.Online.ApiHostSelector"/>), so the origin stays out of every client's
        /// half-hourly check.
        /// </summary>
        [Test]
        public void PrimaryFeedIsTheProxiedWebsiteRoot()
            => Assert.That(UpdateFeed.PRIMARY_URL, Is.EqualTo("https://typebeat.sh/releases"));

        /// <summary>
        /// The fallback is the direct-origin host, for a network whose Cloudflare path fails
        /// (see <see cref="Game.Online.TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT"/>).
        /// </summary>
        [Test]
        public void FallbackFeedIsTheDirectOriginHost()
            => Assert.That(UpdateFeed.FALLBACK_URL, Is.EqualTo("https://bss.typebeat.sh/releases"));

        /// <summary>
        /// The feed and the API agree on which host goes first.
        /// </summary>
        [Test]
        public void FeedOrderMatchesTheApiHostOrder()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UpdateFeed.PRIMARY_URL, Is.EqualTo(Game.Online.ApiHostSelector.PRIMARY_ROOT + "/releases"));
                Assert.That(UpdateFeed.FALLBACK_URL, Is.EqualTo(Game.Online.ApiHostSelector.FALLBACK_ROOT + "/releases"));
            });
        }

        /// <summary>
        /// A stalled download pins the origin first for the rest of the session: the cohort whose sustained
        /// transfers stall on Cloudflare still gets the manifest through it, so a failed check alone would
        /// never move them off it.
        /// </summary>
        [Test]
        public void AStalledDownloadPutsTheOriginFirst()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UpdateFeed.InCheckOrder(false), Is.EqualTo(new[] { UpdateFeed.PRIMARY_URL, UpdateFeed.FALLBACK_URL }));
                Assert.That(UpdateFeed.InCheckOrder(true), Is.EqualTo(new[] { UpdateFeed.FALLBACK_URL, UpdateFeed.PRIMARY_URL }));
            });
        }

        [Test]
        public void FeedsAreDistinctTypebeatHosts()
        {
            Assert.Multiple(() =>
            {
                // a fallback equal to the primary would silently make the retry pointless.
                Assert.That(UpdateFeed.FALLBACK_URL, Is.Not.EqualTo(UpdateFeed.PRIMARY_URL));

                Assert.That(UpdateFeed.PRIMARY_URL, Does.StartWith("https://"));
                Assert.That(UpdateFeed.FALLBACK_URL, Does.StartWith("https://"));

                // never upstream osu: an installed build would "update" itself into osu!lazer.
                Assert.That(UpdateFeed.PRIMARY_URL, Does.Not.Contain("ppy.sh"));
                Assert.That(UpdateFeed.FALLBACK_URL, Does.Not.Contain("ppy.sh"));
            });
        }
    }
}
