// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Text.RegularExpressions;

namespace typebeat.Game.Utils
{
    /// <summary>
    /// The newest upstream osu! release type!beat has fully merged, as recorded in tools/upstream-sync/UPSTREAM.
    /// The file is embedded at build time and every sync merge rewrites it, so this never goes stale.
    /// </summary>
    public static class UpstreamVersion
    {
        /// <summary>
        /// The release tag, e.g. "2026.821.0-tachyon". Null if this build has no UPSTREAM file.
        /// </summary>
        public static string? Release { get; } = read();

        private static string? read()
        {
            using var stream = typeof(UpstreamVersion).Assembly.GetManifestResourceStream(@"typebeat.Game.UPSTREAM");

            if (stream == null)
                return null;

            using var reader = new StreamReader(stream);
            var match = Regex.Match(reader.ReadToEnd(), @"^release\s+(\S+)", RegexOptions.Multiline);

            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
