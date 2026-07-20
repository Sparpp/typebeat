// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Online
{
    /// <summary>
    /// Endpoints for a locally running typebeat-web instance (Kestrel's default launch port for
    /// the server project). Used by debug builds; any build can override the target via the
    /// <c>TYPEBEAT_API_URL</c> environment variable (see <c>OsuGameBase.CreateEndpoints</c>).
    /// </summary>
    public class TypebeatDevEndpointConfiguration : TypebeatEndpointConfiguration
    {
        public TypebeatDevEndpointConfiguration()
            : base(@"http://localhost:5089")
        {
        }
    }
}
