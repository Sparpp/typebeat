// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Online;

namespace typebeat.Game.Updater
{
    /// <summary>
    /// The velopack feed URLs the desktop updater checks, in the order it tries them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is type!beat's own velopack feed (the vpk pack output: a RELEASES manifest plus full
    /// packages, hosted by typebeat-web). It MUST never point at upstream osu releases: an installed
    /// build would otherwise "update" itself into osu!lazer. Both URLs below are type!beat hosts, and
    /// both resolve to the same origin, so a package served by either is the same package.
    /// </para>
    /// <para>
    /// The primary rides the direct-origin host rather than the Cloudflare-proxied website root for
    /// the reason documented on <see cref="TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT"/>:
    /// sustained transfers over the CF path stall dead for some users while short requests to the
    /// same host pass. An update package is exactly a sustained transfer, and the symptom was a user
    /// sitting on "Downloading update..." for over half an hour. The origin's reverse proxy serves
    /// the whole application on that host, so <c>/releases</c> is already there with no server change.
    /// </para>
    /// <para>
    /// The fallback exists because the failure is not one-directional: a network that blocks or
    /// mangles the bare origin host while the CF edge works for it would be locked out of updates
    /// entirely if the direct host were the only option. Trying both costs one extra manifest fetch
    /// on the rare path and nothing on the common one.
    /// </para>
    /// </remarks>
    public static class UpdateFeed
    {
        /// <summary>
        /// The feed tried first: the direct-origin host, which bypasses Cloudflare.
        /// </summary>
        public const string PRIMARY_URL = TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT + "/releases";

        /// <summary>
        /// The feed tried if <see cref="PRIMARY_URL"/> fails: the Cloudflare-proxied website root.
        /// </summary>
        public const string FALLBACK_URL = TypebeatEndpointConfiguration.PRODUCTION_ROOT + "/releases";
    }
}
