// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The game side's tripwire for code mirrored in typebeat-web. Every parity test lives in the
    /// server repo (WireCompat compiles both), so an edit to a mirrored file here used to pass this
    /// repo with no signal at all. This fixture hashes each mirrored file against the checked-in
    /// manifest (<c>NonVisual/fixtures/mirror-manifest.txt</c>) and fails on any change, so the
    /// author is made to look at the twin before the hash is bumped.
    ///
    /// <para>It proves nothing about the twin itself; the parity tests in typebeat-web do that. It
    /// only makes sure nobody edits one half without being told the other half exists.</para>
    ///
    /// <para>The hash is SHA-256 over the file's bytes with a leading UTF-8 BOM removed and every
    /// CRLF turned into LF, so a Windows checkout (CRLF, per .gitattributes) and a Linux one hash
    /// the same. To regenerate the manifest after a deliberate mirrored edit, run from the repo root:
    /// <c>python3 -c "import hashlib,sys;[print(hashlib.sha256(open(p,'rb').read().removeprefix(b'\xef\xbb\xbf').replace(b'\r\n',b'\n')).hexdigest()+'  '+p) for p in sys.argv[1:]]" &lt;paths&gt;</c>,
    /// or copy the replacement lines this test prints when it fails.</para>
    /// </summary>
    [TestFixture]
    public class MirrorManifestTest
    {
        private const string manifest_path = "typebeat.Game.Rulesets.TypeBeat.Tests/NonVisual/fixtures/mirror-manifest.txt";

        /// <summary>Every file the manifest lists still hashes to the value recorded for it.</summary>
        [Test]
        public void MirroredFilesMatchTheManifest()
        {
            string root = repositoryRoot();
            var entries = readManifest(Path.Combine(root, manifest_path));

            Assert.That(entries, Is.Not.Empty, "the mirror manifest lists no files");

            var problems = new List<string>();

            foreach (var (expected, relativePath) in entries)
            {
                string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(path))
                {
                    problems.Add($"missing: {relativePath} (renamed or deleted? move its typebeat-web twin with it and update the manifest)");
                    continue;
                }

                string actual = NormalisedSha256(File.ReadAllBytes(path));

                if (actual != expected)
                    problems.Add($"{actual}  {relativePath}");
            }

            Assert.That(problems, Is.Empty,
                "mirror edit: update typebeat-web and bump the manifest (" + manifest_path + "). "
                + "Replacement lines for the files that changed:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems));
        }

        /// <summary>A CRLF checkout, with or without a BOM, hashes the same as an LF one; content still counts.</summary>
        [Test]
        public void TheHashIgnoresLineEndingsAndTheByteOrderMark()
        {
            byte[] lf = Encoding.UTF8.GetBytes("a\nb\n");
            byte[] crlf = Encoding.UTF8.GetBytes("a\r\nb\r\n");
            byte[] bomCrlf = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(crlf).ToArray();

            Assert.That(NormalisedSha256(crlf), Is.EqualTo(NormalisedSha256(lf)));
            Assert.That(NormalisedSha256(bomCrlf), Is.EqualTo(NormalisedSha256(lf)));
            Assert.That(NormalisedSha256(Encoding.UTF8.GetBytes("a\nc\n")), Is.Not.EqualTo(NormalisedSha256(lf)));

            // sha256("a\nb\n"), the value the regeneration one-liner gives the same text.
            Assert.That(NormalisedSha256(lf), Is.EqualTo("911169ddaaf146aff539f58c26c489af3b892dff0fe283c1c264c65ae5aa59a2"));
        }

        /// <summary>
        /// SHA-256 (lowercase hex) of <paramref name="bytes"/> after removing a leading UTF-8 BOM and
        /// turning every CRLF into LF. A lone CR is kept. Must stay byte for byte the same rule as
        /// the regeneration one-liner in the class summary.
        /// </summary>
        public static string NormalisedSha256(byte[] bytes)
        {
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var normalised = new List<byte>(bytes.Length);

            for (int i = start; i < bytes.Length; i++)
            {
                if (bytes[i] == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n')
                    continue;

                normalised.Add(bytes[i]);
            }

            return Convert.ToHexString(SHA256.HashData(normalised.ToArray())).ToLowerInvariant();
        }

        private static List<(string Hash, string Path)> readManifest(string path)
        {
            Assert.That(File.Exists(path), Is.True, $"the mirror manifest was not found at {path}");

            var entries = new List<(string, string)>();

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();

                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                int split = line.IndexOf(' ');
                Assert.That(split, Is.EqualTo(64), $"malformed manifest line: {line}");

                entries.Add((line[..split], line[split..].Trim()));
            }

            return entries;
        }

        /// <summary>The repository root: the closest directory above the test directory holding the ruleset project.</summary>
        private static string repositoryRoot()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "typebeat.Game.Rulesets.TypeBeat", "typebeat.Game.Rulesets.TypeBeat.csproj")))
                    return dir.FullName;
            }

            Assert.Fail("the repository root was not found above the test directory");
            return null!;
        }
    }
}
