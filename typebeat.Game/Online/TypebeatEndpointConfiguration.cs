// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Online
{
    /// <summary>
    /// Endpoints for the type!beat server ("typebeat-web": one ASP.NET Core monolith serving
    /// both the website and the API; see docs/online-architecture.md in the project root).
    /// </summary>
    public class TypebeatEndpointConfiguration : EndpointConfiguration
    {
        /// <summary>
        /// The production server: one host serves both the website and the API
        /// (typebeat-web is a single monolith). The game's own apex domain; map downloads
        /// will live on a subdomain of it once file serving lands (M3).
        /// </summary>
        public const string PRODUCTION_ROOT = @"https://typebeat.sh";

        /// <summary>
        /// The host production lived on before the move to <see cref="PRODUCTION_ROOT"/>. The
        /// server keeps answering on it for the builds that have it compiled in, and links to
        /// it are everywhere players already pasted them (chat history, map descriptions), so
        /// it stays a trusted domain. Nothing new is ever requested from it.
        /// </summary>
        public const string LEGACY_PRODUCTION_HOST = @"typebeat.mingda.sh";

        /// <summary>
        /// Direct-origin host for production beatmap submission, bypassing Cloudflare.
        /// A user's package uploads died 7/7 with mid-body connection resets between their
        /// machine and the CF edge: short requests to the CF-proxied production host passed,
        /// but sustained request bodies (the multipart package upload) died deterministically
        /// on that path (CF Security Events showed nothing, so this is an on-path middlebox,
        /// not server- or WAF-side). Deterministic per path, so backlog 188's transport retry
        /// cannot route around it; the only fix is to skip CF for BSS uploads entirely. This
        /// is a DNS-only (grey-clouded) subdomain pointed at the same origin as
        /// <see cref="PRODUCTION_ROOT"/>. Bearer auth is host-agnostic, so the token from the
        /// normal API root works here unchanged, and the /bss path prefix is unchanged and
        /// pinned by typebeat-web/docs/m3-spec.md.
        /// </summary>
        public const string PRODUCTION_BSS_ROOT = @"https://bss.typebeat.sh";

        public TypebeatEndpointConfiguration(string apiRoot = PRODUCTION_ROOT)
        {
            WebsiteUrl = APIUrl = apiRoot;

            // OAuth "secret" for the official client. Like osu!'s, this is a public client
            // credential (it ships in source and binaries); the server treats it as a client
            // identifier, not a proof of trust.
            APIClientID = "1";
            APIClientSecret = @"typebeat-official-client";

            // No SignalR hubs: spectating, multiplayer and the metadata hub are out of scope and
            // the server offers none of them, so SpectatorUrl, MultiplayerUrl and MetadataUrl stay
            // EMPTY, and an empty hub URL builds no connector at all (APIAccess.GetHubConnector).
            // The clients then run exactly as they do against DummyAPIAccess: never connected, and
            // every call bails out on IsConnected. Pointing these at paths the server does not
            // serve used to leave three connectors failing and retrying for the whole session,
            // each failure an exception rethrown through the connect chain, which under an
            // attached debugger stalled the game every few seconds. Set them again (e.g.
            // $@"{apiRoot}/signalr/spectator") once the server actually hosts a hub.

            // Beatmap submission (the BSS-compatible endpoint subset) is served by the same
            // monolith under the /bss path prefix. Production uploads are pointed at the
            // direct-origin host (see PRODUCTION_BSS_ROOT) instead of apiRoot to bypass
            // Cloudflare, which was killing sustained upload bodies mid-request. The
            // conditional is load-bearing: a non-production apiRoot (dev's localhost target,
            // or the TYPEBEAT_API_URL override in OsuGameBase.CreateEndpoints) must keep
            // deriving BSS from itself, or local submission testing would silently target
            // production's direct host instead of the server actually under test.
            BeatmapSubmissionServiceUrl = apiRoot == PRODUCTION_ROOT
                ? $@"{PRODUCTION_BSS_ROOT}/bss"
                : $@"{apiRoot}/bss";

            // Unknown liveness is treated as "up"; a real probe can be added later.
            LivenessProbeUrl = null;
        }
    }
}
