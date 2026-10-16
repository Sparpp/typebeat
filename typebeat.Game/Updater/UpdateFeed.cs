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
    /// The primary is the Cloudflare-proxied website root, the same order the game API uses (see
    /// <see cref="ApiHostSelector"/>) and for the same reason: DDoS absorption. Every client checks
    /// this feed at startup and then every 30 minutes, so with the origin first every one of them hit
    /// the origin directly at least twice an hour.
    /// </para>
    /// <para>
    /// The fallback is the direct-origin host, for the failure documented on
    /// <see cref="TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT"/>: a network whose CF path is
    /// blocked or broken for it can still reach the origin. The origin's reverse proxy serves the whole
    /// application on that host, so <c>/releases</c> is already there with no server change.
    /// </para>
    /// <para>
    /// A failed manifest fetch is not the only way that path fails. The cohort it fails for passes
    /// short requests and stalls on sustained ones, so the manifest arrives over Cloudflare and the
    /// package download then stalls. A stalled download therefore pins the origin first for the rest of
    /// the session (<see cref="InCheckOrder"/>), until a download completes.
    /// </para>
    /// </remarks>
    public static class UpdateFeed
    {
        /// <summary>
        /// The feed tried first: the Cloudflare-proxied website root.
        /// </summary>
        public const string PRIMARY_URL = TypebeatEndpointConfiguration.PRODUCTION_ROOT + "/releases";

        /// <summary>
        /// The feed tried if <see cref="PRIMARY_URL"/> fails: the direct-origin host, which bypasses Cloudflare.
        /// </summary>
        public const string FALLBACK_URL = TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT + "/releases";

        /// <summary>
        /// The feeds an update check tries, first to last: <see cref="PRIMARY_URL"/> then
        /// <see cref="FALLBACK_URL"/>, or the other way round once a download has stalled this session.
        /// </summary>
        public static string[] InCheckOrder(bool downloadStalledThisSession)
            => downloadStalledThisSession
                ? new[] { FALLBACK_URL, PRIMARY_URL }
                : new[] { PRIMARY_URL, FALLBACK_URL };
    }
}
