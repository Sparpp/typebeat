// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 163: the client died at startup on Linux because the window icon embedded in
// typebeat.Desktop was a .ico. osu.Framework pins ImageSharp 3.1.11, which has NO ICO decoder (that
// landed in ImageSharp 4.0), so SetIconFromStream's first decode always fails; the framework then
// falls back to its own .ico parser, which off Windows just feeds the container's largest entry back
// to ImageSharp. In our .ico files that entry is a headerless DIB, so it fails a second time, and
// that throw is raised from inside the framework's catch block and escapes OsuGameDesktop.SetHost.
// Windows hid the bug because it builds the icon through a native Win32 call instead.
//
// These pins therefore assert the thing that actually broke: whatever file typebeat.Desktop embeds
// as its window icon must decode under the ImageSharp version we ship.
//
// The tests read the source tree rather than the compiled resource. Covering the embedded stream
// directly would need a ProjectReference from this suite to typebeat.Desktop, which is legal but
// pulls Velopack, DiscordRichPresence and a second WinExe entry point into every test run for one
// assertion. The tradeoff taken here instead: these tests are coupled to the SHAPE of the csproj and
// of the SetHost call site, so restructuring either (a wildcard include, a shared .props file, a
// different resource lookup) makes them go stale rather than fail usefully, and they do not prove
// that the build actually emitted the resource.

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class WindowIconTest
    {
        private static string repoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "typebeat.sln")))
                dir = dir.Parent;

            Assert.That(dir, Is.Not.Null, "could not locate the repository root (no typebeat.sln above the test output directory)");
            return dir!.FullName;
        }

        private static string desktopProjectPath() => Path.Combine(repoRoot(), "typebeat.Desktop", "typebeat.Desktop.csproj");

        /// <summary>
        /// The single embedded resource declared by typebeat.Desktop, as (include path, logical name).
        /// </summary>
        private static (string include, string? logicalName) windowIconResource()
        {
            string csproj = File.ReadAllText(desktopProjectPath());

            var matches = Regex.Matches(csproj, "<EmbeddedResource\\b[^>]*>");
            Assert.That(matches, Has.Count.EqualTo(1), "typebeat.Desktop is expected to embed exactly one resource, the window icon");

            string element = matches[0].Value;

            var include = Regex.Match(element, "Include=\"(?<v>[^\"]+)\"");
            Assert.That(include.Success, Is.True, $"the embedded window icon has no Include attribute: {element}");

            var logical = Regex.Match(element, "LogicalName=\"(?<v>[^\"]+)\"");

            return (include.Groups["v"].Value, logical.Success ? logical.Groups["v"].Value : null);
        }

        private static string windowIconPath()
        {
            string include = windowIconResource().include.Replace('\\', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(desktopProjectPath())!, include));
        }

        [Test]
        public void WindowIconFileExists()
        {
            string path = windowIconPath();
            Assert.That(File.Exists(path), Is.True, $"typebeat.Desktop embeds a window icon that is not in the tree: {path}");
        }

        /// <summary>
        /// The regression pin. A .ico here throws UnknownImageFormatException under the pinned
        /// ImageSharp, on every platform, which is exactly what shipped to the user.
        /// </summary>
        [Test]
        public void WindowIconDecodesUnderPinnedImageSharp()
        {
            string path = windowIconPath();

            using var stream = File.OpenRead(path);

            Image<Rgba32> image;

            try
            {
                image = Image.Load<Rgba32>(stream);
            }
            catch (Exception e)
            {
                Assert.Fail($"the embedded window icon does not decode under the ImageSharp version we ship, so SetIconFromStream cannot use it: {path}{Environment.NewLine}{e}");
                return;
            }

            using (image)
            {
                Assert.That(image.Width, Is.GreaterThan(0));
                Assert.That(image.Height, Is.GreaterThan(0));
            }
        }

        /// <summary>
        /// The runtime lookup is by manifest name, so the name the csproj assigns and the name
        /// OsuGameDesktop asks for have to agree, or the icon is silently never set.
        /// </summary>
        [Test]
        public void WindowIconLogicalNameMatchesTheRuntimeLookup()
        {
            (string include, string? logicalName) = windowIconResource();

            string source = File.ReadAllText(Path.Combine(repoRoot(), "typebeat.Desktop", "OsuGameDesktop.cs"));

            var lookup = Regex.Match(source, "GetManifestResourceStream\\(GetType\\(\\), \"(?<v>[^\"]+)\"\\)");
            Assert.That(lookup.Success, Is.True, "OsuGameDesktop no longer looks the window icon up by manifest resource name");

            // GetManifestResourceStream(type, name) resolves to "<type namespace>.<name>".
            string requested = $"typebeat.Desktop.{lookup.Groups["v"].Value}";

            // Without an explicit LogicalName the manifest name is derived from the file name.
            string declared = logicalName ?? $"typebeat.Desktop.{Path.GetFileName(include)}";

            Assert.That(declared, Is.EqualTo(requested));
        }
    }
}
