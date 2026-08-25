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
        /// The primary feed rides the direct-origin host, which bypasses Cloudflare, because
        /// CF-proxied sustained transfers stall dead for some users
        /// (see <see cref="Game.Online.TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT"/>).
        /// </summary>
        [Test]
        public void PrimaryFeedIsTheDirectOriginHost()
            => Assert.That(UpdateFeed.PRIMARY_URL, Is.EqualTo("https://bss.typebeat.mingda.sh/releases"));

        /// <summary>
        /// The fallback exists for the opposite failure: a network that blocks the bare origin
        /// while the Cloudflare edge works for it.
        /// </summary>
        [Test]
        public void FallbackFeedIsTheProxiedWebsiteRoot()
            => Assert.That(UpdateFeed.FALLBACK_URL, Is.EqualTo("https://typebeat.mingda.sh/releases"));

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
