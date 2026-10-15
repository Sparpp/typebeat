// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using NUnit.Framework;

namespace typebeat.Game.Rulesets.TypeBeat.Tests
{
    /// <summary>
    /// Locates a local maps directory for real-map regression pins. These maps ship separately
    /// (not in this repo), so the directory is supplied via the TYPEBEAT_MAPS_DIR environment
    /// variable; tests that need it gracefully Assert.Ignore when it is unset or the file is
    /// absent, so a fresh checkout runs the rest of the suite without any local setup.
    /// Every real-map pin in this project reports a missing fixture through <see cref="MissingFixture"/>,
    /// so setting TYPEBEAT_REQUIRE_FIXTURES=1 on a machine that has the fixtures turns those skips into
    /// failures instead of a quietly green run. It is opt-in and keyed on its own variable rather than CI,
    /// because the repo's CI has no fixtures and must keep skipping them.
    /// </summary>
    public static class StandaloneMaps
    {
        private const string require_fixtures_variable = "TYPEBEAT_REQUIRE_FIXTURES";

        public static readonly string MapsDir =
            Environment.GetEnvironmentVariable("TYPEBEAT_MAPS_DIR") ?? string.Empty;

        /// <summary>Returns the absolute path of a file under the maps dir, or Assert.Ignore()s the test.</summary>
        public static string Require(params string[] relativeParts)
        {
            if (string.IsNullOrEmpty(MapsDir))
                MissingFixture("TYPEBEAT_MAPS_DIR is not set; skipping real-map pin.");

            string path = MapsDir;

            foreach (string part in relativeParts)
                path = Path.Combine(path, part);

            if (!File.Exists(path))
                MissingFixture($"Standalone type!beat maps checkout not present (expected {path}); skipping real-map pin.");

            return path;
        }

        /// <summary>
        /// Reports a real-map fixture that is not on this machine: Assert.Ignore()s the test by default,
        /// or Assert.Fail()s it when TYPEBEAT_REQUIRE_FIXTURES=1.
        /// </summary>
        [DoesNotReturn]
        public static void MissingFixture(string message)
        {
            if (Environment.GetEnvironmentVariable(require_fixtures_variable) == "1")
                Assert.Fail($"{message} ({require_fixtures_variable}=1 requires every real-map fixture.)");

            Assert.Ignore(message);
        }
    }
}
