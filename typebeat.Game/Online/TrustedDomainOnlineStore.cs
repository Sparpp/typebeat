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
    public sealed class TrustedDomainOnlineStore : OnlineStore
    {
        private readonly Uri? apiUri;
        private readonly Uri? websiteUri;

        public TrustedDomainOnlineStore(EndpointConfiguration endpoints)
        {
            Uri.TryCreate(endpoints.APIUrl, UriKind.Absolute, out apiUri);
            Uri.TryCreate(endpoints.WebsiteUrl, UriKind.Absolute, out websiteUri);
        }

        protected override string GetLookupUrl(string url)
        {
            if (!TrustedDomains.IsTrustedUrl(url, apiUri, websiteUri))
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
