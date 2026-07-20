// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.IO.Network;

namespace typebeat.Game.Online.API
{
    /// <summary>
    /// osu-framework's <see cref="WebRequest"/> silently upgrades every <c>http://</c> request to
    /// <c>https://</c>. That is correct for the production server (<c>https://typebeat.mingda.sh</c>),
    /// but a locally running typebeat-web instance (<c>http://localhost:5089</c>) speaks plain HTTP,
    /// so the upgrade makes every dev API call fail the TLS handshake.
    ///
    /// This opts an individual request out of the upgrade ONLY when it targets a loopback host, so
    /// local development needs no dev certificate while any non-loopback (i.e. production) request
    /// stays strictly HTTPS. Non-loopback and unparseable URIs are left untouched.
    /// </summary>
    internal static class LocalDevInsecure
    {
        public static void Allow(WebRequest request, string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsLoopback)
                request.AllowInsecureRequests = true;
        }
    }
}
