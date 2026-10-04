// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.IO.Network;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace typebeat.Game.Online
{
    /// <summary>
    /// Wraps a <see cref="TrustedDomainOnlineStore"/> so images served by a local dev server (covers, avatars, previews at
    /// <c>http://localhost</c>) load. osu-framework's <see cref="OnlineStore"/> upgrades every http:// lookup to https://, which fails the
    /// TLS handshake against a plain-HTTP loopback server; loopback lookups are fetched here with the upgrade switched off instead.
    /// Every other lookup goes to the wrapped store unchanged.
    /// </summary>
    public sealed class LoopbackAwareStore : IResourceStore<byte[]>
    {
        private readonly TrustedDomainOnlineStore inner;

        public LoopbackAwareStore(TrustedDomainOnlineStore inner)
        {
            this.inner = inner;
        }

        public byte[]? Get(string name) => inner.GetLoopbackUrl(name) is { } url ? fetch(url, CancellationToken.None) : inner.Get(name);

        public Task<byte[]?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            if (inner.GetLoopbackUrl(name) is not { } url)
                return inner.GetAsync(name, cancellationToken);

            return Task.Run(() => fetch(url, cancellationToken), cancellationToken);
        }

        public Stream? GetStream(string name)
        {
            if (inner.GetLoopbackUrl(name) is not { } url)
                return inner.GetStream(name);

            byte[]? data = fetch(url, CancellationToken.None);
            return data == null ? null : new MemoryStream(data);
        }

        public IEnumerable<string> GetAvailableResources() => inner.GetAvailableResources();

        public void Dispose() => inner.Dispose();

        private static byte[]? fetch(string url, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new WebRequest(url) { AllowInsecureRequests = true };
                request.PerformAsync(cancellationToken).GetAwaiter().GetResult();
                return request.GetResponseData();
            }
            catch (Exception e)
            {
                Logger.Log($@"Failed to fetch {url} from the local dev server: {e.Message}", LoggingTarget.Network, LogLevel.Verbose);
                return null;
            }
        }
    }
}
