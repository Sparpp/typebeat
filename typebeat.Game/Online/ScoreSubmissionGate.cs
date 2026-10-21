// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Online
{
    /// <summary>
    /// Decides whether this build may submit scores to the server it is pointed at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every build, debug included, talks to the production server by default (see
    /// <see cref="OsuGameBase.CreateEndpoints"/>), so browsing, downloads and leaderboards work out of the box
    /// while developing. A debug build is also a build whose engine may have been changed a minute ago, and
    /// production leaderboards are shared, so debug builds never request a score token from production: no
    /// token means nothing is submitted, and the play is still saved locally.
    /// </para>
    /// <para>
    /// Pointing <c>TYPEBEAT_API_URL</c> at any other server (a local typebeat-web, a staging box) lifts the
    /// gate, because exercising submission against a server under test is the dev-server workflow. An override
    /// that names a production host is still production and stays gated.
    /// </para>
    /// </remarks>
    public static class ScoreSubmissionGate
    {
        /// <summary>
        /// Whether this assembly was compiled as a debug build.
        /// </summary>
        public static bool IsDevelopmentBuild { get; } =
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>
        /// The reason shown when a play is not submitted because of this gate.
        /// </summary>
        public const string REFUSAL_MESSAGE = @"Development builds do not submit scores to the production server. Set TYPEBEAT_API_URL to a development server to test submission.";

        /// <summary>
        /// Whether a build (<paramref name="isDevelopmentBuild"/>) whose API currently targets
        /// <paramref name="apiRoot"/> may request a score token and submit.
        /// </summary>
        public static bool AllowsSubmission(bool isDevelopmentBuild, string? apiRoot)
            => !isDevelopmentBuild || !IsProductionRoot(apiRoot);

        /// <summary>
        /// Whether <paramref name="apiRoot"/> is a production host: the production root, any subdomain of it (the
        /// direct-origin host the API fails over to is one), or the pre-move production host and its subdomains.
        /// A root that does not parse counts as production, so the gate fails closed.
        /// </summary>
        public static bool IsProductionRoot(string? apiRoot)
        {
            if (!Uri.TryCreate(apiRoot, UriKind.Absolute, out Uri? uri))
                return true;

            return isHostOrSubdomain(uri.Host, new Uri(TypebeatEndpointConfiguration.PRODUCTION_ROOT).Host)
                   || isHostOrSubdomain(uri.Host, TypebeatEndpointConfiguration.LEGACY_PRODUCTION_HOST);
        }

        private static bool isHostOrSubdomain(string host, string root)
            => host.Equals(root, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith($@".{root}", StringComparison.OrdinalIgnoreCase);
    }
}
