// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using NUnit.Framework;

namespace osu.Game.Rulesets.TypeBeat.Tests
{
    /// <summary>
    /// Locates the standalone type!beat repo's maps directory for real-map regression pins.
    /// The path is intentionally hardcoded to the known sibling checkout; tests that need it
    /// gracefully Assert.Ignore when the checkout is not present on this machine.
    /// </summary>
    public static class StandaloneMaps
    {
        public const string MAPS_DIR = @"C:\Users\Mingda\Documents\type!beat\maps";

        /// <summary>Returns the absolute path of a file under maps/, or Assert.Ignore()s the test.</summary>
        public static string Require(params string[] relativeParts)
        {
            string path = MAPS_DIR;

            foreach (string part in relativeParts)
                path = Path.Combine(path, part);

            if (!File.Exists(path))
                Assert.Ignore($"Standalone type!beat maps checkout not present (expected {path}); skipping real-map pin.");

            return path;
        }
    }
}
