// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using NUnit.Framework;

namespace typebeat.Game.Rulesets.TypeBeat.Tests
{
    /// <summary>
    /// Locates the Star Rating Sandbox's own working files for the pins that hold this port against
    /// the sandbox's reading. They cannot be assumed present: <c>tools/</c> is ignored by this
    /// repository, and the Typability Index's predictor table lives in a sibling checkout of its
    /// own. Each directory is therefore supplied through an environment variable and a test whose
    /// file is missing <see cref="Assert.Ignore(string)"/>s rather than failing, so a fresh checkout
    /// runs the rest of the suite with no local setup - the same contract as
    /// <see cref="StandaloneMaps"/> for the real-map pins.
    ///
    /// <para>When the variable is unset the working checkout's own beside-the-repository location is
    /// tried first, so an author who already has the trees needs no configuration.</para>
    /// </summary>
    public static class SandboxFixtures
    {
        /// <summary>The sandbox's own directory (songs.json, sr-config.json, typability.json).</summary>
        public static string RequireSandbox(string fallbackDirectory, params string[] relativeParts)
            => Require("TYPEBEAT_SANDBOX_DIR", fallbackDirectory, relativeParts);

        /// <summary>The Typability Index authors' own checkout.</summary>
        public static string RequireTypabilityIndex(string fallbackDirectory, params string[] relativeParts)
            => Require("TYPEBEAT_TYPABILITY_INDEX_DIR", fallbackDirectory, relativeParts);

        private static string Require(string variable, string fallbackDirectory, params string[] relativeParts)
        {
            string configured = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
            string path = configured.Length > 0 ? configured : fallbackDirectory;

            foreach (string part in relativeParts)
                path = Path.Combine(path, part);

            if (!File.Exists(path))
                Assert.Ignore($"{variable} is not set and {path} is absent; skipping the sandbox pin.");

            return path;
        }
    }
}
