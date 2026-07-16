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
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !isTrusted(uri))
            {
                Logger.Log($@"Blocking resource lookup from external website: {url}", LoggingTarget.Network, LogLevel.Important);
                return string.Empty;
            }

            return url;
        }

        private bool isTrusted(Uri uri)
        {
            if (uri.IsLoopback)
                return true;

            return hostMatches(uri, apiUri) || hostMatches(uri, websiteUri);
        }

        // Trust the endpoint host itself and any subdomain of it (the maps/media host will live
        // on a sibling subdomain of the same apex once file serving lands).
        private static bool hostMatches(Uri candidate, Uri? trusted)
        {
            if (trusted == null)
                return false;

            if (candidate.Host.Equals(trusted.Host, StringComparison.OrdinalIgnoreCase))
                return true;

            string apex = apexOf(trusted.Host);
            return candidate.Host.EndsWith($@".{apex}", StringComparison.OrdinalIgnoreCase);
        }

        private static string apexOf(string host)
        {
            string[] parts = host.Split('.');
            return parts.Length <= 2 ? host : string.Join(@".", parts[^2..]);
        }
    }
}
