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
        /// (typebeat-web is a single monolith). Subdomain of the owner's apex domain;
        /// map downloads will live on a sibling subdomain once file serving lands (M3).
        /// </summary>
        public const string PRODUCTION_ROOT = @"https://typebeat.mingda.sh";

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
        public const string PRODUCTION_BSS_ROOT = @"https://bss.typebeat.mingda.sh";

        public TypebeatEndpointConfiguration(string apiRoot = PRODUCTION_ROOT)
        {
            WebsiteUrl = APIUrl = apiRoot;

            // OAuth "secret" for the official client. Like osu!'s, this is a public client
            // credential (it ships in source and binaries); the server treats it as a client
            // identifier, not a proof of trust.
            APIClientID = "1";
            APIClientSecret = @"typebeat-official-client";

            // Deliberately dead paths: spectating/multiplayer are out of scope, and the server
            // never offers these hubs. The client's HubClientConnectors retry quietly in the
            // background (network log only), which is the zero-client-risk launch posture.
            SpectatorUrl = $@"{apiRoot}/signalr/spectator";
            MultiplayerUrl = $@"{apiRoot}/signalr/multiplayer";
            MetadataUrl = $@"{apiRoot}/signalr/metadata";

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
