// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;

namespace typebeat.Game.Rulesets.TypeBeat.Tests
{
    /// <summary>
    /// Locates a local maps directory for real-map regression pins. These maps ship separately
    /// (not in this repo), so the directory is supplied via the TYPEBEAT_MAPS_DIR environment
    /// variable; tests that need it gracefully Assert.Ignore when it is unset or the file is
    /// absent — so a fresh checkout runs the rest of the suite without any local setup.
    /// </summary>
    public static class StandaloneMaps
    {
        public static readonly string MapsDir =
            Environment.GetEnvironmentVariable("TYPEBEAT_MAPS_DIR") ?? string.Empty;

        /// <summary>Returns the absolute path of a file under the maps dir, or Assert.Ignore()s the test.</summary>
        public static string Require(params string[] relativeParts)
        {
            if (string.IsNullOrEmpty(MapsDir))
                Assert.Ignore("TYPEBEAT_MAPS_DIR is not set; skipping real-map pin.");

            string path = MapsDir;

            foreach (string part in relativeParts)
                path = Path.Combine(path, part);

            if (!File.Exists(path))
                Assert.Ignore($"Standalone type!beat maps checkout not present (expected {path}); skipping real-map pin.");

            return path;
        }
    }
}
