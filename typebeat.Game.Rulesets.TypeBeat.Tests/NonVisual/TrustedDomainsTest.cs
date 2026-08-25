// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Online;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the endpoint-host trust logic shared by <see cref="Game.Online.Chat.ExternalLinkOpener"/>
    /// (external link warning suppression) and <see cref="TrustedDomainOnlineStore"/> (online resource lookups).
    /// Notably pins that trust is decided on the parsed host, not a raw string prefix:
    /// "https://typebeat.mingda.sh.evil.com" must not pass as trusted.
    /// </summary>
    [TestFixture]
    public class TrustedDomainsTest
    {
        [TestCase("https://typebeat.mingda.sh")]
        [TestCase("https://typebeat.mingda.sh/beatmapsets/123")]
        [TestCase("https://TYPEBEAT.MINGDA.SH/beatmapsets/123")]
        [TestCase("https://maps.mingda.sh/previews/123.mp3")] // sibling subdomain of the apex (future media host)
        [TestCase("http://localhost:5089/beatmapsets/123")] // loopback local development
        [TestCase("http://127.0.0.1:5089/previews/123.mp3")]
        public void TrustedUrls(string url)
            => Assert.That(TrustedDomains.IsTrustedUrl(url, new TypebeatEndpointConfiguration()), Is.True);

        [TestCase("https://typebeat.mingda.sh.evil.com/phish")] // the raw prefix bypass this logic replaces
        [TestCase("https://typebeat.mingda.sh@evil.com/phish")] // userinfo trick: actual host is evil.com
        [TestCase("https://evil.com/https://typebeat.mingda.sh")]
        [TestCase("https://evilmingda.sh/x")] // apex must match on a label boundary
        [TestCase("https://mingda.sh")] // the bare apex itself is not a configured endpoint
        [TestCase("https://example.com")]
        [TestCase("mailto:someone@typebeat.mingda.sh")] // non-web schemes are never a trusted domain
        [TestCase("typebeat://s/123")]
        [TestCase("/beatmapsets/123")] // relative: callers resolve against the website root before checking
        [TestCase("not a url")]
        public void UntrustedUrls(string url)
            => Assert.That(TrustedDomains.IsTrustedUrl(url, new TypebeatEndpointConfiguration()), Is.False);

        [Test]
        public void LoopbackDevEndpointsRemainTrusted()
        {
            var devEndpoints = new TypebeatEndpointConfiguration("http://localhost:5089");

            Assert.That(TrustedDomains.IsTrustedUrl("http://localhost:5089/beatmapsets/123", devEndpoints), Is.True);
            Assert.That(TrustedDomains.IsTrustedUrl("https://evil.com/", devEndpoints), Is.False);
        }

        /// <summary>
        /// Production beatmap submission is pinned to the direct-origin host that bypasses
        /// Cloudflare (see <see cref="TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT"/>),
        /// not the CF-proxied website/API root.
        /// </summary>
        [Test]
        public void ProductionBeatmapSubmissionUsesDirectOriginHost()
        {
            var endpoints = new TypebeatEndpointConfiguration();

            Assert.That(endpoints.BeatmapSubmissionServiceUrl, Is.EqualTo("https://bss.typebeat.mingda.sh/bss"));
        }

        /// <summary>
        /// Regression guard for the dev / TYPEBEAT_API_URL-override path: a non-production
        /// apiRoot must keep deriving BeatmapSubmissionServiceUrl from itself rather than
        /// silently targeting production's direct-origin BSS host.
        /// </summary>
        [Test]
        public void NonProductionApiRootDerivesOwnBeatmapSubmissionUrl()
        {
            var endpoints = new TypebeatEndpointConfiguration("http://localhost:5089");

            Assert.That(endpoints.BeatmapSubmissionServiceUrl, Is.EqualTo("http://localhost:5089/bss"));
        }
    }
}
