// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace typebeat.Game.Online
{
    /// <summary>
    /// Restricts online image/resource lookups (avatars, covers, backgrounds) to the hosts of
    /// the configured type!beat endpoints, so arbitrary embedded URLs can't trigger requests to
    /// third-party servers. Loopback is always allowed for local development.
    /// </summary>
    /// <remarks>
    /// The hosts are read from the endpoint configuration on every lookup rather than copied out once: it is
    /// the same instance <see cref="API.APIAccess"/> holds, and the API host failover rewrites its
    /// <see cref="EndpointConfiguration.APIUrl"/> mid-session, so a snapshot would keep trusting the
    /// abandoned host and stop trusting the one now in use.
    /// </remarks>
    public sealed class TrustedDomainOnlineStore : OnlineStore
    {
        private readonly EndpointConfiguration endpoints;

        public TrustedDomainOnlineStore(EndpointConfiguration endpoints)
        {
            this.endpoints = endpoints;
        }

        /// <summary>
        /// Whether a lookup of <paramref name="url"/> would be let through, against the endpoint hosts as they are right now.
        /// </summary>
        public bool IsTrusted(string url) => TrustedDomains.IsTrustedUrl(url, endpoints);

        protected override string GetLookupUrl(string url)
        {
            if (!IsTrusted(url))
            {
                Logger.Log($@"Blocking resource lookup from external website: {url}", LoggingTarget.Network, LogLevel.Important);
                return string.Empty;
            }

            return url;
        }

        /// <summary>
        /// The URL to fetch <paramref name="name"/> from when it is a trusted loopback <c>http://</c> address (a local dev server), else
        /// null. The framework's own lookup upgrades every http:// URL to https://, which fails the TLS handshake against such a server,
        /// so <see cref="LoopbackAwareStore"/> fetches these itself.
        /// </summary>
        public string? GetLoopbackUrl(string name)
        {
            string url = GetLookupUrl(name);

            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp ? url : null;
        }
    }
}
