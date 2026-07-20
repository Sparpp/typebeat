// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Online
{
    /// <summary>
    /// Endpoints for the type!beat server ("typebeat-web" — one ASP.NET Core monolith serving
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

        public TypebeatEndpointConfiguration(string apiRoot = PRODUCTION_ROOT)
        {
            WebsiteUrl = APIUrl = apiRoot;

            // OAuth "secret" for the official client. Like osu!'s, this is a public client
            // credential (it ships in source and binaries) — the server treats it as a client
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
            // monolith under the /bss path prefix.
            BeatmapSubmissionServiceUrl = $@"{apiRoot}/bss";

            // Unknown liveness is treated as "up"; a real probe can be added later.
            LivenessProbeUrl = null;
        }
    }
}
