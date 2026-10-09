// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Formats;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.ImportLyrics;
using AlignerVocalMode = typebeat.Game.Beatmaps.AlignerVocalMode;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Ports the standalone MapImporter regression pins (detection / sanitisation / resolution)
    /// to the fork's <see cref="LyricMapImporter"/>, and adds the M6-specific .osz packaging pins:
    /// aligner-timing round-trip, line-only fallback granularity, and metadata escaping.
    /// </summary>
    [TestFixture]
    public class LyricMapImporterTest
    {
        private string tempRoot = null!;

        [SetUp]
        public void SetUp()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "tb_import_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            LyricBeatmapDecoder.Register();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch
            {
                // best effort
            }
        }

        [Test]
        public void HasLineStampsDetection()
        {
            Assert.That(LyricMapImporter.HasLineStamps("[00:01.00] hello\n[00:02.00] world\n[00:03.00]\n"), Is.True);
            Assert.That(LyricMapImporter.HasLineStamps("[ar:Artist]\n[Lyrics]\n[00:01.00] hello\n"), Is.True);
            Assert.That(LyricMapImporter.HasLineStamps("hello\nworld\n"), Is.False);
            Assert.That(LyricMapImporter.HasLineStamps("[00:01.00] hello\nworld\n"), Is.False);
            Assert.That(LyricMapImporter.HasLineStamps(""), Is.False);
            Assert.That(LyricMapImporter.HasLineStamps("[ar:OnlyMetadata]\n"), Is.False);
        }

        [Test]
        public void AnyStampedLineRunsTheAlignerInRefMode()
        {
            // Every line stamped: ref, as before.
            Assert.That(LyricMapImporter.AlignerAnchorMode("[00:01.00] hello\n[00:02.00] world\n[00:03.00]\n"), Is.EqualTo("ref"));

            // Only the section starts stamped (sparse anchors): still ref, where it used to be auto.
            const string sparse = "[00:01.00] hello\nworld\nagain\n[00:09.00] second verse\nmore\n[00:15.00]\n";
            Assert.That(LyricMapImporter.HasLineStamps(sparse), Is.False, "the LRC fallback still needs every line");
            Assert.That(LyricMapImporter.HasAnyLineStamp(sparse), Is.True);
            Assert.That(LyricMapImporter.AlignerAnchorMode(sparse), Is.EqualTo("ref"));

            // A single stamp, even on a later line, is enough.
            Assert.That(LyricMapImporter.AlignerAnchorMode("hello\n[00:05.00] world\n"), Is.EqualTo("ref"));

            // Bare text, metadata tags or a lone end marker carry no line stamp: auto.
            Assert.That(LyricMapImporter.AlignerAnchorMode("hello\nworld\n"), Is.EqualTo("auto"));
            Assert.That(LyricMapImporter.AlignerAnchorMode("[ar:Artist]\n[Lyrics]\nhello\n"), Is.EqualTo("auto"));
            Assert.That(LyricMapImporter.AlignerAnchorMode("hello\nworld\n[00:30.00]\n"), Is.EqualTo("auto"));
            Assert.That(LyricMapImporter.HasAnyLineStamp(""), Is.False);
        }

        /// <summary>
        /// The high-accuracy setting reaches the aligner as <c>--quality full</c>, and ONLY then: off
        /// (the default) adds no quality flag at all, so the run takes the script's own fast default.
        /// Walked from the setting itself through the service's read of it to the built command line,
        /// so neither half can drift without this noticing.
        /// </summary>
        [Test]
        public void HighAccuracySettingAddsQualityFullOnlyWhenOn()
        {
            string lab = makeLab();
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "ALIGNER_VERSION = \"6\"\n");
            const string stamped = "[00:01.00] hello\n[00:02.00] world\n";

            using (var config = new TypeBeatRulesetConfigManager(null, new TypeBeatRuleset().RulesetInfo))
            {
                bool off = LyricMapImportService.HighQualityAlignment(config);
                Assert.That(off, Is.False, "high accuracy is opt-in");

                var fast = LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", stamped, off);

                Assert.That(fast, Does.Not.Contain("--quality"));
                Assert.That(fast, Is.EqualTo(new[] { "align_lyrics.py", "a.mp3", "l.txt", "-o", "out", "--anchors", "ref" }));

                config.SetValue(TypeBeatRulesetSetting.LocalAlignerHighQuality, true);
                bool on = LyricMapImportService.HighQualityAlignment(config);
                Assert.That(on, Is.True);

                var full = LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", stamped, on);

                Assert.That(full, Is.EqualTo(new[] { "align_lyrics.py", "a.mp3", "l.txt", "-o", "out", "--anchors", "ref", "--quality", "full" }));
            }

            // No config to read (the service before the cache resolves) must never mean the slow tier.
            Assert.That(LyricMapImportService.HighQualityAlignment(null), Is.False);

            // The flag rides alongside the others rather than replacing them: a CUDA install on bare
            // text still gets its device and its auto anchors.
            File.WriteAllText(Path.Combine(lab, LyricMapImporter.DEVICE_MARKER_FILE), "cuda\n");

            Assert.That(LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", "hello\nworld\n", true), Is.EqualTo(new[]
            {
                "align_lyrics.py", "a.mp3", "l.txt", "-o", "out", "--device", "cuda", "--anchors", "auto", "--quality", "full",
            }));
        }

        /// <summary>
        /// The setting must never break an import on an aligner that predates the tiers: version 5
        /// (every copy a player has installed until they take the update) exits 2 on an unknown
        /// <c>--quality</c>, which would cost fully stamped lyrics their word timing and fail the
        /// rest outright. So the flag goes only to version 6 and newer, read off the script itself.
        /// </summary>
        [TestCase("ALIGNER_VERSION = \"5\"\n", false)]
        [TestCase("ALIGNER_VERSION = \"6\"\n", true)]
        [TestCase("ALIGNER_VERSION = \"12\"\n", true)]
        [TestCase("ALIGNER_VERSION = \"6b\"\n", false)]
        [TestCase("# a version-1 script, no constant\n", false)]
        public void HighAccuracyFlagIsGatedOnTheScriptVersion(string script, bool expectFlag)
        {
            string lab = makeLab();
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), script);

            Assert.That(LyricMapImporter.AlignerHasQualityTiers(lab), Is.EqualTo(expectFlag));

            var args = withoutFusedEvidenceFlags(LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", "[00:01.00] hello\n", true));

            if (expectFlag)
                Assert.That(args.TakeLast(2), Is.EqualTo(new[] { "--quality", "full" }));
            else
                Assert.That(args, Is.EqualTo(new[] { "align_lyrics.py", "a.mp3", "l.txt", "-o", "out", "--anchors", "ref" }));

            // The notice for the old-script case must not claim a stage of the import display.
            Assert.That(ImportProgressParser.Parse(LyricMapImporter.HIGH_QUALITY_NEEDS_UPDATE).Stage, Is.Null);
        }

        /// <summary>
        /// Backlog 354: a set's estimated vocals reach the aligner as <c>--vocal-mode estimated</c>, and
        /// ONLY when it can honour them: the mode is chosen, the lyrics carry a line stamp to pace from,
        /// and the script is version 7 or newer (version 6, what every player has installed until the
        /// update, exits 2 on the unknown option and would cost the import its word timing). Aligned,
        /// every set's default, adds nothing, so the ordinary command line is unchanged.
        /// </summary>
        [TestCase("ALIGNER_VERSION = \"7\"\n", "[00:01.00] hello\n[00:02.00] world\n", AlignerVocalMode.Estimated, true)]
        [TestCase("ALIGNER_VERSION = \"12\"\n", "[00:01.00] hello\nworld\n", AlignerVocalMode.Estimated, true)]
        [TestCase("ALIGNER_VERSION = \"7\"\n", "[00:01.00] hello\n[00:02.00] world\n", AlignerVocalMode.Aligned, false)]
        [TestCase("ALIGNER_VERSION = \"6\"\n", "[00:01.00] hello\n[00:02.00] world\n", AlignerVocalMode.Estimated, false)]
        [TestCase("ALIGNER_VERSION = \"7\"\n", "hello\nworld\n", AlignerVocalMode.Estimated, false)]
        [TestCase("# a version-1 script, no constant\n", "[00:01.00] hello\n", AlignerVocalMode.Estimated, false)]
        public void EstimatedVocalsReachTheAlignerOnlyWhenItCanHonourThem(string script, string lyrics, AlignerVocalMode mode, bool expectFlag)
        {
            string lab = makeLab();
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), script);

            Assert.That(LyricMapImporter.EstimatedVocalsApply(lab, lyrics, mode), Is.EqualTo(expectFlag));

            var args = withoutFusedEvidenceFlags(LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", lyrics, false, mode));
            string anchors = LyricMapImporter.AlignerAnchorMode(lyrics);

            if (expectFlag)
                Assert.That(args, Is.EqualTo(new[] { "align_lyrics.py", "a.mp3", "l.txt", "-o", "out", "--anchors", anchors, "--vocal-mode", "estimated" }));
            else
                Assert.That(args, Is.EqualTo(new[] { "align_lyrics.py", "a.mp3", "l.txt", "-o", "out", "--anchors", anchors }));
        }

        /// <summary>
        /// The vocal mode rides alongside high accuracy rather than replacing it, and the default
        /// parameter is Aligned, so every caller that predates the mode builds the line it always did.
        /// </summary>
        [Test]
        public void EstimatedVocalsRideAlongsideHighAccuracy()
        {
            string lab = makeLab();
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "ALIGNER_VERSION = \"7\"\n");
            const string stamped = "[00:01.00] hello\n[00:02.00] world\n";

            Assert.That(LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", stamped, true, AlignerVocalMode.Estimated), Is.EqualTo(new[]
            {
                "align_lyrics.py", "a.mp3", "l.txt", "-o", "out", "--anchors", "ref", "--quality", "full", "--vocal-mode", "estimated",
            }));

            Assert.That(LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", stamped, true),
                Is.EqualTo(LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", stamped, true, AlignerVocalMode.Aligned)));
        }

        /// <summary>The command line with backlog 411's fused-evidence flags taken out, for tests about the other flags.</summary>
        private static List<string> withoutFusedEvidenceFlags(IReadOnlyList<string> args)
        {
            var kept = new List<string>();

            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "--evidence" || args[i] == "--lyrics-language")
                    i++;
                else
                    kept.Add(args[i]);
            }

            return kept;
        }

        /// <summary>
        /// Backlog 411: every run of a version 10 or newer script asks for the fused evidence path
        /// and names the import's language, which the aligner routes on (only English takes the path).
        /// An older script would exit 2 on either unknown option and cost the import its word timing,
        /// so it gets neither, read off its version like <c>--quality</c>. The flags come last and ride
        /// alongside every other option; no language means no <c>--lyrics-language</c>, so the
        /// aligner's own detector decides.
        /// </summary>
        [TestCase("ALIGNER_VERSION = \"9\"\n", "english", null)]
        [TestCase("ALIGNER_VERSION = \"10\"\n", "english", "english")]
        [TestCase("ALIGNER_VERSION = \"12\"\n", "japanese", "japanese")]
        [TestCase("ALIGNER_VERSION = \"10\"\n", "  English ", "english")]
        [TestCase("ALIGNER_VERSION = \"10\"\n", null, "")]
        [TestCase("ALIGNER_VERSION = \"10\"\n", "   ", "")]
        [TestCase("ALIGNER_VERSION = \"10b\"\n", "english", null)]
        [TestCase("# a version-1 script, no constant\n", "english", null)]
        public void FusedEvidenceFlagsAreGatedOnTheScriptVersion(string script, string? language, string? expectedLanguage)
        {
            string lab = makeLab();
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), script);
            const string stamped = "[00:01.00] hello\n[00:02.00] world\n";
            var core = new List<string> { "align_lyrics.py", "a.mp3", "l.txt", "-o", "out", "--anchors", "ref" };

            // The unreadable versions predate the tiers too.
            if (LyricMapImporter.AlignerHasQualityTiers(lab))
                core.AddRange(new[] { "--quality", "full" });

            var args = LyricMapImporter.AlignerArguments(lab, "a.mp3", "l.txt", "out", stamped, true, language: language);

            Assert.That(LyricMapImporter.AlignerHasFusedEvidence(lab), Is.EqualTo(expectedLanguage != null));

            if (expectedLanguage == null)
                Assert.That(args, Is.EqualTo(core));
            else if (expectedLanguage.Length == 0)
                Assert.That(args, Is.EqualTo(core.Concat(new[] { "--evidence", "fused" })));
            else
                Assert.That(args, Is.EqualTo(core.Concat(new[] { "--evidence", "fused", "--lyrics-language", expectedLanguage })));
        }

        /// <summary>
        /// The game ships the aligner whose flags it passes: the vendored script is version 10 or
        /// newer and declares both options, so a fresh install or an update is never handed a flag
        /// it would reject. Found the way <see cref="ShippedAlignerAcceptsVocalModes"/> finds it.
        /// </summary>
        [Test]
        public void ShippedAlignerAcceptsFusedEvidence()
        {
            string lab = Path.GetDirectoryName(vendoredAlignerScript())!;
            string script = File.ReadAllText(vendoredAlignerScript());

            Assert.Multiple(() =>
            {
                Assert.That(LyricMapImporter.AlignerHasFusedEvidence(lab), Is.True, $"vendored aligner version {LyricMapImporter.ReadAlignerVersion(lab)}");
                Assert.That(script, Does.Contain("ap.add_argument(\"--evidence\", choices=[\"mms\", \"fused\"]"));
                Assert.That(script, Does.Contain("ap.add_argument(\"--lyrics-language\""));

                // The weights fetch the setup scripts call (see ShippedSetupScriptsInstallTheFusedEvidencePackages).
                Assert.That(script, Does.Contain("def qmul_weights_path():"));
            });
        }

        /// <summary>The vendored aligner script, the closest <c>lyriclab/align_lyrics.py</c> above the test directory.</summary>
        private static string vendoredAlignerScript()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "lyriclab", "align_lyrics.py");

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail("the vendored lyriclab/align_lyrics.py was not found above the test directory");
            return null!;
        }

        /// <summary>
        /// What a run asked for estimated vocals says up front: on, or why not (no stamp is named
        /// before an old install, since updating would not help it). None of the lines may claim a
        /// stage of the import display, and Aligned says nothing at all.
        /// </summary>
        [Test]
        public void EstimatedVocalsNoticeSaysWhyAndClaimsNoStage()
        {
            string lab = makeLab();
            const string stamped = "[00:01.00] hello\n";
            const string bare = "hello\n";

            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "ALIGNER_VERSION = \"7\"\n");
            string? aligned = LyricMapImporter.EstimatedVocalsNotice(lab, stamped, AlignerVocalMode.Aligned);
            string? on = LyricMapImporter.EstimatedVocalsNotice(lab, stamped, AlignerVocalMode.Estimated);

            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "ALIGNER_VERSION = \"6\"\n");
            string? old = LyricMapImporter.EstimatedVocalsNotice(lab, stamped, AlignerVocalMode.Estimated);
            string? oldBare = LyricMapImporter.EstimatedVocalsNotice(lab, bare, AlignerVocalMode.Estimated);

            Assert.Multiple(() =>
            {
                Assert.That(aligned, Is.Null);
                Assert.That(on, Is.EqualTo(LyricMapImporter.ESTIMATED_VOCALS_ON));
                Assert.That(old, Is.EqualTo(LyricMapImporter.ESTIMATED_VOCALS_NEEDS_UPDATE));
                Assert.That(oldBare, Is.EqualTo(LyricMapImporter.ESTIMATED_VOCALS_NEED_STAMPS));

                foreach (string notice in new[]
                         {
                             LyricMapImporter.ESTIMATED_VOCALS_ON, LyricMapImporter.ESTIMATED_VOCALS_NEEDS_UPDATE, LyricMapImporter.ESTIMATED_VOCALS_NEED_STAMPS,
                         })
                    Assert.That(ImportProgressParser.Parse(notice).Stage, Is.Null, notice);

                // The aligner's own line for the mode (align_lyrics.py, version 7) holds the display too.
                Assert.That(ImportProgressParser.Parse("[17:57:27] estimated vocals: every line paced evenly from its stamp; stamp lead 80 ms, 70 ms a letter").Stage,
                    Is.Null);
            });
        }

        /// <summary>
        /// The game ships the aligner it gates on: the vendored script must be new enough to accept
        /// <c>--vocal-mode</c>, or a fresh install would never honour a mapper's choice. Read off the
        /// repo's own <c>lyriclab/</c> (the closest one above the test directory), never through
        /// ResolveLyricLabDir, which prefers whatever set-up checkout sits nearby.
        /// </summary>
        [Test]
        public void ShippedAlignerAcceptsVocalModes()
        {
            string? vendored = null;

            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null && vendored == null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "lyriclab", "align_lyrics.py")))
                    vendored = Path.Combine(dir.FullName, "lyriclab");
            }

            Assert.That(vendored, Is.Not.Null, "the vendored lyriclab/ should sit above the test directory");
            Assert.That(LyricMapImporter.AlignerHasVocalModes(vendored!), Is.True, $"vendored aligner version {LyricMapImporter.ReadAlignerVersion(vendored)}");
        }

        /// <summary>
        /// A setup script that ends with its own "setup failed: WHAT; ADVICE." sentence gets that
        /// sentence shown (without the prefix and the period), since it says what to do; the raw
        /// detail lines after it stay in the log. Output with no such line keeps the generic text.
        /// </summary>
        [Test]
        public void SetupFailureSentenceIsTheScriptsOwn()
        {
            Assert.That(LyricMapImporter.SetupFailureSentence(
                    "installing torch ($Device) - this is the big download... | error: no solution found | "
                    + "setup failed: installing torch failed; check your internet connection and retry, or install the aligner later from Settings. | "
                    + "  detail: exit code 1; the tool's own output is above"),
                Is.EqualTo("installing torch failed; check your internet connection and retry, or install the aligner later from Settings"));

            Assert.That(LyricMapImporter.SetupFailureSentence("py.exe : No suitable Python runtime found"), Is.Null);
            Assert.That(LyricMapImporter.SetupFailureSentence(""), Is.Null);
            Assert.That(LyricMapImporter.SetupFailureSentence("setup failed: ."), Is.Null);
        }

        [Test]
        public async Task BootstrapFailureShowsTheScriptsOwnSentence()
        {
            string lab = makeLab();
            File.WriteAllText(Path.Combine(lab, "setup.ps1"),
                "Write-Output 'installing torch...'\n"
                + "[Console]::Error.WriteLine('setup failed: installing torch failed; check your internet connection and retry, or install the aligner later from Settings.')\n"
                + "[Console]::Error.WriteLine('  detail: exit code 1')\n"
                + "exit 1\n");
            File.WriteAllText(Path.Combine(lab, "setup.sh"),
                "echo 'installing torch...'\n"
                + "echo 'setup failed: installing torch failed; check your internet connection and retry, or install the aligner later from Settings.' >&2\n"
                + "echo '  detail: exit code 1' >&2\n"
                + "exit 1\n");

            var result = await LyricMapImporter.BootstrapEnvironmentAsync(lab, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo("installing torch failed; check your internet connection and retry, or install the aligner later from Settings"));
        }

        [Test]
        public void SanitizeFolderNameRemovesInvalidChars()
        {
            Assert.That(LyricMapImporter.SanitizeFolderName("AC/DC - T.N.T."), Is.EqualTo("AC DC - T.N.T"));
            Assert.That(LyricMapImporter.SanitizeFolderName("a<>:\"|?*b"), Is.EqualTo("a b"));
            Assert.That(LyricMapImporter.SanitizeFolderName("  spaced   out  "), Is.EqualTo("spaced out"));
            Assert.That(LyricMapImporter.SanitizeFolderName("???"), Is.EqualTo("Imported Map"));
        }

        [Test]
        public void GuessArtistTitleFromFilename()
        {
            Assert.That(LyricMapImporter.GuessArtistTitle(@"X:\music\Friday Pilots Club - Spectator Official Audio.mp3"),
                Is.EqualTo(("Friday Pilots Club", "Spectator Official Audio")));
            Assert.That(LyricMapImporter.GuessArtistTitle(@"X:\music\untitled.mp3"),
                Is.EqualTo(("Unknown", "untitled")));
        }

        [Test]
        public void ReadAlignerVersionReadsTheConstantOffTheScript()
        {
            string lab = Path.Combine(tempRoot, "lab");
            Directory.CreateDirectory(lab);

            // A version-1 script predates the constant: null, which the manager reads as "update available".
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "#!/usr/bin/env python\nSAMPLE_RATE = 16000\n");
            Assert.That(LyricMapImporter.ReadAlignerVersion(lab), Is.Null);

            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "#!/usr/bin/env python\n# Bumped when the output changes.\nALIGNER_VERSION = \"2\"\n\nSAMPLE_RATE = 16000\n");
            Assert.That(LyricMapImporter.ReadAlignerVersion(lab), Is.EqualTo("2"));

            // Only the module-level assignment counts, not a mention inside a string or comment.
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "# ALIGNER_VERSION = \"9\" is documented above\n  ALIGNER_VERSION = \"3\"\n");
            Assert.That(LyricMapImporter.ReadAlignerVersion(lab), Is.Null);

            Assert.That(LyricMapImporter.ReadAlignerVersion(Path.Combine(tempRoot, "missing")), Is.Null);
            Assert.That(LyricMapImporter.ReadAlignerVersion(null), Is.Null);

            // The shipped component itself declares a version, so a build never offers "update to null".
            string? vendored = LyricMapImporter.ResolveLyricLabDir(null, AppContext.BaseDirectory);
            Assume.That(vendored, Is.Not.Null, "the vendored lyriclab component was not found beside the test binaries");
            Assert.That(LyricMapImporter.ReadAlignerVersion(vendored), Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void ResolveLyricLabDirWalksUpFromStart()
        {
            // root/typebeat-lyriclab/align_lyrics.py  +  root/repo/(start)
            string lab = Path.Combine(tempRoot, "typebeat-lyriclab");
            Directory.CreateDirectory(lab);
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "# stub");

            string start = Path.Combine(tempRoot, "repo", "bin");
            Directory.CreateDirectory(start);

            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(lab));

            // An explicitly configured valid path wins.
            string configured = Path.Combine(tempRoot, "elsewhere");
            Directory.CreateDirectory(configured);
            File.WriteAllText(Path.Combine(configured, "align_lyrics.py"), "# stub");
            Assert.That(LyricMapImporter.ResolveLyricLabDir(configured, start), Is.EqualTo(Path.GetFullPath(configured)));

            // An invalid configured path falls back to discovery.
            Assert.That(LyricMapImporter.ResolveLyricLabDir(Path.Combine(tempRoot, "nope"), start), Is.EqualTo(lab));

            // Nothing to find within the ascent budget.
            string isolated = Path.Combine(tempRoot, "isolated", "deep", "start");
            Directory.CreateDirectory(isolated);
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, isolated, maxAscendLevels: 1), Is.Null);
        }

        [Test]
        public void ResolveLyricLabDirPrefersVendoredComponentAndReadyVenvs()
        {
            // Vendored component at the repo root (next to the start dir), sibling checkout above it.
            string repoLab = Path.Combine(tempRoot, "repo", "lyriclab");
            string siblingLab = Path.Combine(tempRoot, "typebeat-lyriclab");
            Directory.CreateDirectory(repoLab);
            Directory.CreateDirectory(siblingLab);
            File.WriteAllText(Path.Combine(repoLab, "align_lyrics.py"), "# stub");
            File.WriteAllText(Path.Combine(siblingLab, "align_lyrics.py"), "# stub");

            string start = Path.Combine(tempRoot, "repo", "bin");
            Directory.CreateDirectory(start);

            // Neither has a venv: the vendored in-repo component wins (closest candidate).
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(repoLab));
            Assert.That(LyricMapImporter.EnvironmentReady(repoLab), Is.False);

            // A candidate with a ready venv is preferred over a closer one without.
            string siblingPython = LyricMapImporter.PythonExeFor(siblingLab);
            Directory.CreateDirectory(Path.GetDirectoryName(siblingPython)!);
            File.WriteAllText(siblingPython, "stub exe");
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(siblingLab));

            // Once the vendored component's venv exists too, it wins again.
            string repoPython = LyricMapImporter.PythonExeFor(repoLab);
            Directory.CreateDirectory(Path.GetDirectoryName(repoPython)!);
            File.WriteAllText(repoPython, "stub exe");
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(repoLab));
            Assert.That(LyricMapImporter.EnvironmentPresent(repoLab), Is.True);

            // A python alone is not an install (backlog 353): READY needs the setup sentinel too,
            // and a completed sibling now beats the closer, unconfirmed vendored venv.
            Assert.That(LyricMapImporter.EnvironmentReady(repoLab), Is.False);
            Assert.That(LyricMapImporter.EnvironmentNeedsRepair(repoLab), Is.True);
            File.WriteAllText(LyricMapImporter.SetupSentinelFor(siblingLab), "ok");
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(siblingLab));
            File.WriteAllText(LyricMapImporter.SetupSentinelFor(repoLab), "ok");
            Assert.That(LyricMapImporter.EnvironmentReady(repoLab), Is.True);
            Assert.That(LyricMapImporter.ResolveLyricLabDir(null, start), Is.EqualTo(repoLab));
        }

        [Test]
        public void OszPackagingRoundTripMatchesLoader()
        {
            // Real Spectator provenance: build an .osz from the aligner timing.json, then unzip +
            // decode the packaged .osu and confirm the hit objects match TimingJsonLoader.TryLoad.
            string timingPath = StandaloneMaps.Require("Friday Pilots Club - Spectator", "timing.json");
            string audioPath = StandaloneMaps.Require("Friday Pilots Club - Spectator", "Friday Pilots Club - Spectator Official Audio.mp3");
            string lyricsPath = StandaloneMaps.Require("Friday Pilots Club - Spectator", "lyrics.txt");

            string timingJson = File.ReadAllText(timingPath);
            string lyricsContent = File.ReadAllText(lyricsPath);

            Assert.That(TimingJsonLoader.TryLoad(timingPath, out var expected), Is.True);

            string oszPath = Path.Combine(tempRoot, "spectator.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "Friday Pilots Club", "Spectator", audioPath, timingJson, lyricsContent);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.OszPath, Is.EqualTo(oszPath));
            Assert.That(File.Exists(oszPath), Is.True);

            using var archive = ZipFile.OpenRead(oszPath);

            // Self-contained set: audio + provenance both travel inside.
            Assert.That(archive.GetEntry("Friday Pilots Club - Spectator Official Audio.mp3"), Is.Not.Null, "audio missing");
            Assert.That(archive.GetEntry("timing.json"), Is.Not.Null, "provenance timing.json missing");
            Assert.That(archive.GetEntry("lyrics.txt"), Is.Not.Null, "provenance lyrics.txt missing");

            var osuEntry = archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
            var beatmap = decode(readEntry(osuEntry));
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(expected.Count));
            Assert.That(beatmap.BeatmapInfo.Ruleset.ShortName, Is.EqualTo("typebeat"));

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.That(hitObjects[i].StartTime, Is.EqualTo(expected[i].StartTime), $"line {i} start");
                Assert.That(hitObjects[i].Line.RawText, Is.EqualTo(expected[i].RawText), $"line {i} text");
                Assert.That(hitObjects[i].Line.EndTime, Is.EqualTo(expected[i].EndTime), $"line {i} end");
                Assert.That(hitObjects[i].Line.Units.Count, Is.EqualTo(expected[i].Units.Count), $"line {i} units");
                // The real Spectator timing.json carries aligner-emitted words[].syllables[], which the
                // loader now threads into SyllableBoundaries, so the packaged map infers Syllable.
                Assert.That(hitObjects[i].Granularity, Is.EqualTo(TimingGranularity.Syllable));
            }
        }

        [Test]
        public void TtmlImportDeclaresUnsubmittedOnlineIds()
        {
            const string ttml = "<tt xmlns=\"http://www.w3.org/ns/ttml\"><body dur=\"3.000\"><div><p begin=\"1.000\" end=\"2.000\">hello world</p></div></body></tt>";
            string timing = LyricMapImporter.SynthesizeTimingJsonFromTtml(ttml)!;
            Assert.That(timing, Is.Not.Null);

            string audioPath = Path.Combine(tempRoot, "audio.mp3");
            File.WriteAllBytes(audioPath, new byte[] { 0x49, 0x44, 0x33 });
            string packagePath = Path.Combine(tempRoot, "ttml.osz");
            var result = LyricMapImporter.PackageOsz(packagePath, "Artist", "Title", audioPath, timing, ttml);
            Assert.That(result.Success, Is.True, result.Error);

            using var archive = ZipFile.OpenRead(packagePath);
            string encoded = readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)));
            var imported = decode(encoded);
            Assert.That(encoded, Does.Contain("BeatmapID:0"));
            Assert.That(encoded, Does.Contain("BeatmapSetID:-1"));
            Assert.That(imported.BeatmapInfo.OnlineID, Is.Zero);
            Assert.That(imported.BeatmapInfo.BeatmapSet!.OnlineID, Is.EqualTo(-1));
            Assert.That(imported.HitObjects.OfType<TypeBeatHitObject>().Single().Line.RawText, Is.EqualTo("hello world"));
        }

        [Test]
        public async Task LrcOnlyFallbackPackagesLineGranularityMap()
        {
            // No aligner reachable from the start dir + line-stamped lyrics -> line-granularity map.
            string audioPath = Path.Combine(tempRoot, "Some Artist - Some Song.mp3");
            File.WriteAllText(audioPath, "fake audio");

            string lyricsPath = Path.Combine(tempRoot, "lyrics.txt");
            File.WriteAllText(lyricsPath, "[00:01.00] hello world\n[00:03.00] second line here\n[00:05.00]\n");

            var (artist, title) = LyricMapImporter.GuessArtistTitle(audioPath);

            var result = await LyricMapImporter.BuildOszAsync(
                audioPath, lyricsPath, artist, title,
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: _ => { },
                token: CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.OszPath, Is.Not.Null);

            using var archive = ZipFile.OpenRead(result.OszPath!);
            var osuEntry = archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
            var beatmap = decode(readEntry(osuEntry));
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(2));
            // Since backlog 363 (CHOICE B) an LRC import is syllabified once, here: "hello" and
            // "second" carry their natural subdivisions, so the map is Syllable rather than Line.
            Assert.That(hitObjects.All(h => h.Granularity == TimingGranularity.Syllable), Is.True, "expected syllable granularity");
            Assert.That(hitObjects[0].Line.Units[0].SyllableSplits, Is.EqualTo(Syllabifier.SplitPoints("hello")));
            Assert.That(hitObjects[0].Line.RawText, Is.EqualTo("hello world"));
            Assert.That(hitObjects[0].StartTime, Is.EqualTo(1000));

            // Clean up the temp .osz the importer produced outside tempRoot.
            try
            {
                Directory.Delete(Path.GetDirectoryName(result.OszPath!)!, true);
            }
            catch
            {
                // best effort
            }
        }

        #region Enhanced LRC (backlog 356)

        private const string enhanced_lyrics =
            "[00:01.00]a plain line here\n"
            + "[00:05.00]<00:05.00>Never <00:05.40>gonna <00:05.80>give <00:06.10>you <00:06.50>up<00:07.00>\n"
            + "[00:09.00]<00:09.00>out <00:08.00>of <00:09.50>order\n"
            + "[00:12.00]\n";

        /// <summary>
        /// An enhanced LRC short-circuits the ladder like a TTML: with a working aligner INSTALLED
        /// it is still never run, and the map lands at Word granularity rather than Line.
        /// </summary>
        [Test]
        public async Task EnhancedLrcSkipsAnInstalledAlignerAndImportsWordTimed()
        {
            string lab = makeLab();
            writeFakePython(lab, importsLoad: true);
            File.WriteAllText(LyricMapImporter.SetupSentinelFor(lab), "python=3.11\n");
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);

            string audioPath = Path.Combine(tempRoot, "a.mp3");
            File.WriteAllText(audioPath, "fake");

            var lines = new List<string>();
            var (result, timing) = await LyricMapImporter.ProduceTimingJsonAsync(
                audioPath, enhanced_lyrics, "A", "B", lab, Array.Empty<string>(),
                line => { lock (lines) lines.Add(line); }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(lines, Has.Member(LyricMapImporter.ENHANCED_LRC_PROGRESS));
            Assert.That(lines, Has.None.Contains("aligner"), "the aligner is never consulted");
            Assert.That(lines, Has.None.Contains("no word-level timing"));

            // The one stamp behind its neighbour is clamped, counted and reported.
            Assert.That(result.Notice, Is.EqualTo("1 word stamp was out of order and was clamped"));
            Assert.That(lines, Has.Member(result.Notice));

            Assert.That(TimingJsonLoader.TryParse(timing!, out IReadOnlyList<LyricLine> decoded), Is.True);
            // Syllable, not Word: the once-at-import syllabification (backlog 363) cuts the
            // polysyllabic stamped words exactly as it cuts a plain LRC's or a TTML's.
            Assert.That(TypeBeatEditorOperations.InferGranularity(decoded), Is.EqualTo(TimingGranularity.Syllable));
            Assert.That(decoded[1].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 5000.0, 5400, 5800, 6100, 6500 }));
            Assert.That(decoded[2].Units.Select(u => u.StartTime), Is.EqualTo(new[] { 9000.0, 9000, 9500 }));

            using var document = JsonDocument.Parse(timing!);
            var jsonLines = document.RootElement.GetProperty("lines");
            Assert.That(jsonLines[0].TryGetProperty("words", out _), Is.False, "the unstamped line keeps the plain shape");
            Assert.That(jsonLines[1].GetProperty("words").GetArrayLength(), Is.EqualTo(5));
        }

        [TestCase(".lrc")]
        [TestCase(".elrc")]
        [TestCase(".ELRC")]
        public async Task EnhancedLrcPackagesWithItsNoticeEvenWithAutomaticAlignmentOff(string extension)
        {
            string audioPath = Path.Combine(tempRoot, "Some Artist - Some Song.mp3");
            File.WriteAllText(audioPath, "fake audio");
            string lyricsPath = Path.Combine(tempRoot, "lyrics" + extension);
            File.WriteAllText(lyricsPath, enhanced_lyrics);

            var result = await LyricMapImporter.BuildOszAsync(
                audioPath, lyricsPath, "Some Artist", "Some Song",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: _ => { },
                token: CancellationToken.None,
                useAutomaticAlignment: false).ConfigureAwait(false);

            try
            {
                Assert.That(result.Success, Is.True, result.Error);
                Assert.That(result.Notice, Is.EqualTo("1 word stamp was out of order and was clamped"));

                using var archive = ZipFile.OpenRead(result.OszPath!);
                var osuEntry = archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
                var hitObjects = decode(readEntry(osuEntry)).HitObjects.OfType<TypeBeatHitObject>().ToList();

                Assert.That(hitObjects.Count, Is.EqualTo(3));
                Assert.That(hitObjects.All(h => h.Granularity == TimingGranularity.Syllable), Is.True, "syllabified once at import (backlog 363)");
                Assert.That(hitObjects[1].Line.Units.Select(u => u.StartTime), Is.EqualTo(new[] { 5000d, 5400, 5800, 6100, 6500 }));
                Assert.That(hitObjects[1].Line.Units.Select(u => u.EndTime), Is.EqualTo(new[] { 5400d, 5800, 6100, 6500, 7000 }));
            }
            finally
            {
                try
                {
                    Directory.Delete(Path.GetDirectoryName(result.OszPath!)!, true);
                }
                catch
                {
                    // best effort
                }
            }
        }

        [Test]
        public void ClampedWordStampsNoticeWording()
        {
            Assert.That(LyricMapImporter.ClampedWordStampsNotice(0), Is.Null);
            Assert.That(LyricMapImporter.ClampedWordStampsNotice(1), Is.EqualTo("1 word stamp was out of order and was clamped"));
            Assert.That(LyricMapImporter.ClampedWordStampsNotice(3), Is.EqualTo("3 word stamps were out of order and were clamped"));
        }

        #endregion

        #region The video split (backlog 234)

        [Test]
        public async Task Mp4SourceIsSplitIntoStandaloneAudioPlusBackgroundVideo()
        {
            // A dropped video container becomes TWO files: the extracted audio track (which is what
            // AudioFilename names, what alignment runs on, and what an audio-only download of the set
            // would ship) and the container, kept on as the map's [Events] video. Before the split
            // one file did both jobs, which made an audio-only download silent and let the "delete
            // all videos" maintenance action destroy the song.
            string mp4Path = Path.Combine(tempRoot, "Some Artist - Some Song.mp4");
            File.WriteAllText(mp4Path, "fake video");

            string lyricsPath = Path.Combine(tempRoot, "lyrics.txt");
            File.WriteAllText(lyricsPath, "[00:01.00] one two\n[00:03.00]\n");

            var extractor = FakeAudioTrackExtractor.Producing(".mp3");

            var result = await LyricMapImporter.BuildOszAsync(
                mp4Path, lyricsPath, "Some Artist", "Some Song",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: _ => { },
                token: CancellationToken.None,
                useAutomaticAlignment: true,
                audioExtractor: extractor).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(extractor.VideoPath, Is.EqualTo(mp4Path), "the container is what gets split");
            Assert.That(extractor.OutputDirectory, Is.EqualTo(Path.GetDirectoryName(result.OszPath)),
                "the extracted file belongs in the import temp dir, which the caller cleans up");

            try
            {
                using var archive = ZipFile.OpenRead(result.OszPath!);

                Assert.Multiple(() =>
                {
                    Assert.That(archive.GetEntry("Some Artist - Some Song.mp3"), Is.Not.Null, "the extracted audio must travel in the set");
                    Assert.That(archive.GetEntry("Some Artist - Some Song.mp4"), Is.Not.Null, "the video must travel in the set too");
                });

                string osuText = readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)));

                Assert.Multiple(() =>
                {
                    Assert.That(osuText, Does.Contain("AudioFilename: Some Artist - Some Song.mp3"));

                    // Byte-identical to the line the unsplit import has always written: an extraction
                    // is sample-accurate, so the video needs no offset (backlog 232's seam stays at 0).
                    Assert.That(osuText, Does.Contain("Video,0,\"Some Artist - Some Song.mp4\""));

                    // The whole point of splitting BEFORE alignment: the local aligner sees the audio,
                    // never the container. One variable (BuildOszAsync's effectiveAudioPath) is both
                    // what alignment is handed and what AudioFilename is written from, so the decoded
                    // AudioFile IS that pin. It used to be read off the remote aligner stub's first
                    // argument; that seam is gone with the server-side aligner, and a local run cannot
                    // be stubbed (it needs a real lyriclab environment).
                    Assert.That(decode(osuText).Metadata.AudioFile, Is.EqualTo(Path.GetFileName(extractor.ProducedPath)));
                });
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task WithNoExtractorTheVideoStillDoublesAsTheAudio()
        {
            // The degrade, and the reason the split can ship at all: the only real extractor is an
            // ffmpeg binary most machines do not have. Without one, an mp4 import must behave exactly
            // as it did before the split (one media entry, doing both jobs) rather than failing.
            string mp4Path = Path.Combine(tempRoot, "Some Artist - Some Song.mp4");
            File.WriteAllText(mp4Path, "fake video");

            string lyricsPath = Path.Combine(tempRoot, "lyrics.txt");
            File.WriteAllText(lyricsPath, "[00:01.00] one two\n[00:03.00]\n");

            var lines = new List<string>();

            var result = await LyricMapImporter.BuildOszAsync(
                mp4Path, lyricsPath, "Some Artist", "Some Song",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: lines.Add,
                token: CancellationToken.None,
                useAutomaticAlignment: true,
                audioExtractor: FakeAudioTrackExtractor.Unavailable("no ffmpeg found on this machine")).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);

            try
            {
                using var archive = ZipFile.OpenRead(result.OszPath!);

                string osuText = readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)));

                Assert.Multiple(() =>
                {
                    Assert.That(archive.Entries.Select(e => e.FullName), Has.Exactly(1).Matches<string>(n => n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)));
                    Assert.That(archive.Entries.Any(e => e.FullName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)), Is.False, "nothing was extracted");
                    Assert.That(osuText, Does.Contain("AudioFilename: Some Artist - Some Song.mp4"));
                    Assert.That(osuText, Does.Contain("Video,0,\"Some Artist - Some Song.mp4\""));

                    // With nothing extracted, the container is still what gets aligned, which the one
                    // effectiveAudioPath variable makes the same statement as AudioFilename naming it.
                    Assert.That(decode(osuText).Metadata.AudioFile, Is.EqualTo(Path.GetFileName(mp4Path)));
                });
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task ABlankMapFromAVideoIsSplitToo()
        {
            // The split sits ABOVE the blank/aligned branch, so a video dropped with no lyrics (the
            // "write the words in the editor" import) is split exactly the same way.
            string mp4Path = Path.Combine(tempRoot, "A - B.mp4");
            File.WriteAllText(mp4Path, "fake video");

            var result = await LyricMapImporter.BuildOszAsync(
                mp4Path, lyricsPath: null, "A", "B",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: _ => { },
                token: CancellationToken.None,
                audioExtractor: FakeAudioTrackExtractor.Producing(".mp3")).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);

            try
            {
                using var archive = ZipFile.OpenRead(result.OszPath!);
                string osuText = readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)));

                Assert.Multiple(() =>
                {
                    Assert.That(archive.GetEntry("A - B.mp3"), Is.Not.Null);
                    Assert.That(archive.GetEntry("A - B.mp4"), Is.Not.Null);
                    Assert.That(osuText, Does.Contain("AudioFilename: A - B.mp3"));
                    Assert.That(osuText, Does.Contain("Video,0,\"A - B.mp4\""));
                });
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task TheDegradeSaysWhyItKeptTheVideoAsTheAudio()
        {
            // The map that comes out of the degrade behaves differently (an audio-only download of it
            // is silent, and "delete all videos" would take its audio), so the reason has to reach the
            // log. It claims no stage of its own, unlike the extraction notice: nothing was extracted,
            // so the progress display holds its step rather than ticking "extracted" over a fallback.
            string mp4Path = Path.Combine(tempRoot, "A - B.mp4");
            File.WriteAllText(mp4Path, "fake video");

            var lines = new List<string>();

            var result = await LyricMapImporter.BuildOszAsync(
                mp4Path, lyricsPath: null, "A", "B",
                configuredLyricLabPath: null,
                startDirectories: new[] { tempRoot },
                progress: lines.Add,
                token: CancellationToken.None,
                audioExtractor: FakeAudioTrackExtractor.Unavailable("no ffmpeg found on this machine")).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);

            try
            {
                string? degrade = lines.FirstOrDefault(l => l.Contains("keeping the video file as the map's audio", StringComparison.Ordinal));

                Assert.Multiple(() =>
                {
                    Assert.That(degrade, Is.Not.Null, "the fallback must explain itself in the progress stream");
                    Assert.That(degrade, Does.Contain("no ffmpeg found on this machine"), "including why");
                    Assert.That(ImportProgressParser.Parse(degrade).Stage, Is.Null);
                });
            }
            finally
            {
                deleteImportTemp(result.OszPath);
            }
        }

        [Test]
        public async Task RealFfmpegExtractionProducesAStandaloneAudioFile()
        {
            // The one test that exercises a real container and a real encoder. Self-skipping in the
            // web repo's IsFfmpegAvailable style: the extractor is resolved the way an import would
            // resolve it, and the fixture is SYNTHESISED by that same binary (a 2s tone in an mp4),
            // so no binary media has to be checked into the repo.
            string? ffmpeg = FfmpegAudioTrackExtractor.Resolve(null, new[] { tempRoot });

            if (ffmpeg == null)
                Assert.Ignore("no ffmpeg (aligner venv or PATH); the importer degrades to mp4-as-audio and the split is skipped.");

            string mp4Path = Path.Combine(tempRoot, "Some Artist - Some Song.mp4");

            if (!synthesiseToneMp4(ffmpeg!, mp4Path, out string synthesisError))
                Assert.Ignore($"this ffmpeg cannot synthesise the fixture: {synthesisError}");

            var lines = new List<string>();
            string outputDir = Path.Combine(tempRoot, "extracted");

            var extraction = await new FfmpegAudioTrackExtractor(null, new[] { tempRoot })
                .ExtractAsync(mp4Path, outputDir, lines.Add, CancellationToken.None).ConfigureAwait(false);

            Assert.That(extraction.Success, Is.True, extraction.Reason);

            string audioPath = extraction.AudioPath!;
            byte[] head = File.ReadAllBytes(audioPath);

            Assert.Multiple(() =>
            {
                Assert.That(Path.GetFileNameWithoutExtension(audioPath), Is.EqualTo("Some Artist - Some Song"), "the extracted file keeps the source stem");

                // Never .m4a/.aac: BASS decodes no AAC on Linux, and neither the editor's audio
                // chooser nor the site's player content types know the extension.
                Assert.That(Path.GetExtension(audioPath), Is.AnyOf(".mp3", ".ogg"));

                Assert.That(head.Length, Is.GreaterThan(1024), "two seconds of encoded audio is never this small");
                Assert.That(looksLikeEncodedAudio(head), Is.True, "the output should start with an ID3 tag, an mpeg frame sync or an Ogg page");
                Assert.That(lines, Does.Contain(FfmpegAudioTrackExtractor.EXTRACTING_NOTICE));
            });
        }

        /// <summary>Writes a two-second tone into an mp4 (aac track) with the given ffmpeg.</summary>
        private static bool synthesiseToneMp4(string ffmpeg, string destination, out string error)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string arg in new[]
                     {
                         "-nostdin", "-y", "-v", "error",
                         "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                         "-c:a", "aac", destination,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            try
            {
                using var process = System.Diagnostics.Process.Start(psi)!;
                error = process.StandardError.ReadToEnd().Trim();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && new System.IO.FileInfo(destination).Length > 0)
                    return true;

                if (error.Length == 0)
                    error = $"exit code {process.ExitCode}";

                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static bool looksLikeEncodedAudio(byte[] head)
        {
            if (head.Length < 4)
                return false;

            bool id3 = head[0] == 'I' && head[1] == 'D' && head[2] == '3';
            bool frameSync = head[0] == 0xFF && (head[1] & 0xE0) == 0xE0;
            bool ogg = head[0] == 'O' && head[1] == 'g' && head[2] == 'g' && head[3] == 'S';

            return id3 || frameSync || ogg;
        }

        /// <summary>
        /// Stands in for ffmpeg: writes a text file where a real extraction would put the audio (the
        /// packaging path decodes nothing), or reports itself unavailable. Also records what it was
        /// asked to split, which is how the "alignment gets the extracted file" pin is made.
        /// </summary>
        private class FakeAudioTrackExtractor : IAudioTrackExtractor
        {
            public string? VideoPath;
            public string? OutputDirectory;
            public string? ProducedPath;

            private readonly string? extension;
            private readonly string reason;

            private FakeAudioTrackExtractor(string? extension, string reason)
            {
                this.extension = extension;
                this.reason = reason;
            }

            public static FakeAudioTrackExtractor Producing(string extension) => new FakeAudioTrackExtractor(extension, string.Empty);

            public static FakeAudioTrackExtractor Unavailable(string reason) => new FakeAudioTrackExtractor(null, reason);

            public Task<AudioExtractionResult> ExtractAsync(string videoPath, string outputDirectory, Action<string> progress, CancellationToken token)
            {
                VideoPath = videoPath;
                OutputDirectory = outputDirectory;

                if (extension == null)
                    return Task.FromResult(AudioExtractionResult.Unavailable(reason));

                Directory.CreateDirectory(outputDirectory);
                ProducedPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(videoPath) + extension);
                File.WriteAllText(ProducedPath, "fake extracted audio");

                return Task.FromResult(AudioExtractionResult.Ok(ProducedPath));
            }
        }

        /// <summary>Removes the temp directory the importer produced outside <c>tempRoot</c>.</summary>
        private static void deleteImportTemp(string? oszPath)
        {
            try
            {
                if (oszPath != null)
                    Directory.Delete(Path.GetDirectoryName(oszPath)!, true);
            }
            catch
            {
                // best effort
            }
        }

        #endregion

        [Test]
        public async Task UnstampedLyricsWithoutAlignerFails()
        {
            string audioPath = Path.Combine(tempRoot, "a.mp3");
            File.WriteAllText(audioPath, "fake");
            string lyricsPath = Path.Combine(tempRoot, "plain.txt");
            File.WriteAllText(lyricsPath, "just some words\nwith no timestamps\n");

            var result = await LyricMapImporter.BuildOszAsync(
                audioPath, lyricsPath, "A", "B", null, new[] { tempRoot }, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("timestamp"));
        }

        [Test]
        public async Task BootstrapReturnsOkWhenEnvironmentAlreadyReady()
        {
            // A built venv exists: bootstrap must short-circuit to Ok (Success) without spawning the
            // setup process. This pins the "Ok result" contract the install notification keys its
            // flip-to-completed on; see TypeBeatSettingsSubsection.startInstall.
            string lab = Path.Combine(tempRoot, "lyriclab");
            Directory.CreateDirectory(lab);
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "# stub");

            string python = LyricMapImporter.PythonExeFor(lab);
            Directory.CreateDirectory(Path.GetDirectoryName(python)!);
            File.WriteAllText(python, "stub exe");
            File.WriteAllText(LyricMapImporter.SetupSentinelFor(lab), "python=3.11\n");
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);

            bool anyProgress = false;
            var result = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, _ => anyProgress = true, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(anyProgress, Is.False, "a ready environment should not emit setup progress");
        }

        [Test]
        public async Task BootstrapFailsWithClearErrorWhenNoSetupScript()
        {
            // No venv and no setup script to build one: bootstrap must fail with a descriptive error
            // (not hang) so the notification surfaces a failure state rather than a stale line.
            string lab = Path.Combine(tempRoot, "lyriclab");
            Directory.CreateDirectory(lab);

            var result = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain(LyricMapImporter.SetupScriptName));
        }

        [Test]
        public async Task BootstrapFailureShowsAPlainSentenceNotTheRawScriptError()
        {
            // Backlog 349: a failing setup script used to hand its raw output (a PowerShell
            // NativeCommandError record) straight to the player. The player now gets one plain
            // sentence; the raw text goes to the log only.
            string lab = Path.Combine(tempRoot, "lyriclab");
            Directory.CreateDirectory(lab);
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "# stub");
            File.WriteAllText(Path.Combine(lab, "setup.ps1"), "[Console]::Error.WriteLine('py.exe : No suitable Python runtime found'); exit 103\n");
            File.WriteAllText(Path.Combine(lab, "setup.sh"), "echo 'py.exe : No suitable Python runtime found' >&2\nexit 103\n");

            var lines = new List<string>();
            var result = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, line => { lock (lines) lines.Add(line); }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            lock (lines)
                Assert.That(lines, Has.Some.Contains("No suitable Python"), "the fake script's raw error should have streamed as progress");
            Assert.That(result.Error, Does.Contain("could not finish"));
            Assert.That(result.Error, Does.Not.Contain("No suitable Python"));
            Assert.That(result.Error, Does.Not.Contain("103"));
        }

        #region The vocals stem (backlog 392)

        /// <summary>
        /// A lyriclab stub whose script declares version 9 (the first that writes the vocals stem).
        /// </summary>
        private string makeVocalsLab()
        {
            string lab = Path.Combine(tempRoot, "lyriclab");
            Directory.CreateDirectory(lab);
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), $"ALIGNER_VERSION = \"{LyricMapImporter.PERSISTS_VOCALS_STEM_ALIGNER_VERSION}\"\n");
            return lab;
        }

        [Test]
        public void ProducedVocalsStemIsFoundOnlyForAnAlignerThatWritesIt()
        {
            string lab = makeVocalsLab();
            string outDir = Path.Combine(tempRoot, "out");
            Directory.CreateDirectory(outDir);
            string stem = Path.Combine(outDir, VocalsStem.WAV_FILENAME);
            File.WriteAllBytes(stem, new byte[] { 1, 2, 3 });

            // The stem the aligner wrote is trusted for a version-9 script...
            Assert.That(LyricMapImporter.FindProducedVocalsStem(lab, outDir), Is.EqualTo(stem));

            // ...and ignored for an older one that could not have written it, so a stale file in a
            // reused out dir never becomes a map's vocals waveform.
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "ALIGNER_VERSION = \"8\"\n");
            Assert.That(LyricMapImporter.FindProducedVocalsStem(lab, outDir), Is.Null);

            // A version-9 script with no stem file (a --no-separate run) is likewise no stem.
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), $"ALIGNER_VERSION = \"{LyricMapImporter.PERSISTS_VOCALS_STEM_ALIGNER_VERSION}\"\n");
            File.Delete(stem);
            Assert.That(LyricMapImporter.FindProducedVocalsStem(lab, outDir), Is.Null);
        }

        [Test]
        public void PackageOszCarriesTheVocalsStemUnderItsFixedName()
        {
            string audioPath = Path.Combine(tempRoot, "audio.mp3");
            File.WriteAllText(audioPath, "fake audio");
            string timing = "{\"version\":2,\"song_end_ms\":8000,\"lines\":[{\"text\":\"one two\",\"start_ms\":1000,\"end_ms\":3000}]}";

            string stemWav = Path.Combine(tempRoot, VocalsStem.WAV_FILENAME);
            File.WriteAllBytes(stemWav, new byte[] { 0x52, 0x49, 0x46, 0x46 });

            string oszPath = Path.Combine(tempRoot, "withstem.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "A", "B", audioPath, timing, "one two", vocalsStemSourcePath: stemWav);

            Assert.That(result.Success, Is.True, result.Error);

            using (var archive = ZipFile.OpenRead(oszPath))
            {
                Assert.That(archive.GetEntry(VocalsStem.WAV_FILENAME), Is.Not.Null, "the stem must travel as vocals.wav so the editor finds it");
                Assert.That(archive.GetEntry(VocalsStem.OGG_FILENAME), Is.Null);
            }

            // An Ogg-sourced stem lands under the Ogg name (the encoder's choice upstream).
            string stemOgg = Path.Combine(tempRoot, "some_encoded.ogg");
            File.WriteAllBytes(stemOgg, new byte[] { 0x4f, 0x67, 0x67, 0x53 });
            string oszOgg = Path.Combine(tempRoot, "oggstem.osz");
            var oggResult = LyricMapImporter.PackageOsz(oszOgg, "A", "B", audioPath, timing, "one two", vocalsStemSourcePath: stemOgg);

            Assert.That(oggResult.Success, Is.True, oggResult.Error);

            using (var archive = ZipFile.OpenRead(oszOgg))
                Assert.That(archive.GetEntry(VocalsStem.OGG_FILENAME), Is.Not.Null, "an encoded stem rides as vocals.ogg");
        }

        [Test]
        public void PackageOszWithoutAStemStillSucceedsAndCarriesNone()
        {
            string audioPath = Path.Combine(tempRoot, "audio.mp3");
            File.WriteAllText(audioPath, "fake audio");
            string timing = "{\"version\":2,\"song_end_ms\":8000,\"lines\":[{\"text\":\"one two\",\"start_ms\":1000,\"end_ms\":3000}]}";

            string oszPath = Path.Combine(tempRoot, "nostem.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "A", "B", audioPath, timing, "one two");

            Assert.That(result.Success, Is.True, result.Error);

            using var archive = ZipFile.OpenRead(oszPath);
            Assert.That(archive.GetEntry(VocalsStem.WAV_FILENAME), Is.Null);
            Assert.That(archive.GetEntry(VocalsStem.OGG_FILENAME), Is.Null);
        }

        [Test]
        public void VocalsStemFilenameInAcceptsEitherExtension()
        {
            Assert.Multiple(() =>
            {
                Assert.That(VocalsStem.IsStemFileName(VocalsStem.OGG_FILENAME), Is.True);
                Assert.That(VocalsStem.IsStemFileName(VocalsStem.WAV_FILENAME), Is.True);
                Assert.That(VocalsStem.IsStemFileName("VOCALS.OGG"), Is.True, "case-insensitive, matching the realm lookup");
                Assert.That(VocalsStem.IsStemFileName("audio.mp3"), Is.False);
                Assert.That(VocalsStem.IsStemFileName(null), Is.False);
            });
        }

        [Test]
        public void ShippedAlignerPersistsTheVocalsStem()
        {
            // The vendored script must actually copy the stem into its output dir (backlog 392), and
            // only when it separated. Found the way ShippedAlignerAcceptsVocalModes finds it.
            string? vendored = null;

            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null && vendored == null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "lyriclab", "align_lyrics.py")))
                    vendored = Path.Combine(dir.FullName, "lyriclab", "align_lyrics.py");
            }

            Assert.That(vendored, Is.Not.Null);

            string script = File.ReadAllText(vendored!);

            Assert.Multiple(() =>
            {
                Assert.That(script, Does.Contain("def persist_vocals_stem"));
                Assert.That(script, Does.Contain("persist_vocals_stem(out_dir, stem, wav16)"));
                Assert.That(script, Does.Contain("if not args.no_separate:"), "a --no-separate run aligned on the mix and has no stem to keep");
                Assert.That(LyricMapImporter.AlignerPersistsVocalsStem(Path.GetDirectoryName(vendored!)!), Is.True);
            });
        }

        #endregion

        #region Setup sentinel and repair (backlog 353)

        private string makeLab()
        {
            string lab = Path.Combine(tempRoot, "lyriclab");
            Directory.CreateDirectory(lab);
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), "# stub");
            return lab;
        }

        /// <summary>
        /// A venv python that really runs: exit 0 whatever it is asked (its "imports load") or exit
        /// non-zero (they do not). On Windows a copy of doskey.exe / where.exe, which do exactly that
        /// for "-c ..." without reading stdin; elsewhere a shell script.
        /// </summary>
        private static void writeFakePython(string lab, bool importsLoad)
        {
            string python = LyricMapImporter.PythonExeFor(lab);
            Directory.CreateDirectory(Path.GetDirectoryName(python)!);

            if (OperatingSystem.IsWindows())
            {
                File.Copy(Path.Combine(Environment.SystemDirectory, importsLoad ? "doskey.exe" : "where.exe"), python, overwrite: true);
                return;
            }

            File.WriteAllText(python, importsLoad
                ? "#!/bin/sh\nexit 0\n"
                : "#!/bin/sh\necho \"ModuleNotFoundError: No module named 'torch'\" >&2\nexit 1\n");
            File.SetUnixFileMode(python, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        /// <summary>Fake setup scripts that rebuild a venv WITH its sentinel, noting whether the old venv was still there.</summary>
        private static void writeRebuildingSetupScripts(string lab)
        {
            File.WriteAllText(Path.Combine(lab, "setup.ps1"),
                "if (Test-Path '.venv\\canary.txt') { Set-Content -Path 'saw-canary.txt' -Value 'yes' }\n"
                + "New-Item -ItemType Directory -Force -Path '.venv\\Scripts' | Out-Null\n"
                + "Set-Content -Path '.venv\\Scripts\\python.exe' -Value 'rebuilt'\n"
                + $"Set-Content -Path '.venv\\{LyricMapImporter.SETUP_SENTINEL_FILE}' -Value 'python=3.11'\n"
                + "exit 0\n");
            File.WriteAllText(Path.Combine(lab, "setup.sh"),
                "if [ -e .venv/canary.txt ]; then echo yes > saw-canary.txt; fi\n"
                + "mkdir -p .venv/bin\necho rebuilt > .venv/bin/python\n"
                + $"echo python=3.11 > .venv/{LyricMapImporter.SETUP_SENTINEL_FILE}\n"
                + "exit 0\n");
        }

        [Test]
        public async Task BootstrapDoesNotShortCircuitOnASentinelLessVenv()
        {
            // THE bug: a python.exe left by a setup whose torch install failed read as installed, so
            // the setup never ran again. That python cannot even start here (it is a text file).
            string lab = makeLab();
            string python = LyricMapImporter.PythonExeFor(lab);
            Directory.CreateDirectory(Path.GetDirectoryName(python)!);
            File.WriteAllText(python, "stub exe");

            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.False);
            Assert.That(LyricMapImporter.EnvironmentNeedsRepair(lab), Is.True);

            var lines = new List<string>();
            var result = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, line => { lock (lines) lines.Add(line); }, CancellationToken.None).ConfigureAwait(false);

            // No setup script to rebuild with, so it fails, and it does so without having deleted
            // the venv it had no way to replace.
            Assert.That(result.Success, Is.False, "a sentinel-less venv must not short-circuit to Ok");
            Assert.That(result.Error, Does.Contain(LyricMapImporter.SetupScriptName));
            Assert.That(File.Exists(python), Is.True);
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.False);
            lock (lines)
                Assert.That(lines, Has.Some.Contains("checking the existing aligner environment"));
        }

        [Test]
        public async Task BootstrapAdoptsAWorkingSentinelLessVenv()
        {
            // A healthy install from before the sentinel: its imports load, so it is kept as it is
            // and gets the sentinel the setup would have written. No setup script is present, so
            // any attempt to rebuild would fail this test.
            string lab = makeLab();
            writeFakePython(lab, importsLoad: true);
            File.WriteAllText(Path.Combine(lab, ".venv", "canary.txt"), "the existing environment");

            var result = await LyricMapImporter.BootstrapEnvironmentAsync(lab, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(File.Exists(LyricMapImporter.SetupSentinelFor(lab)), Is.True);
            Assert.That(File.Exists(Path.Combine(lab, ".venv", "canary.txt")), Is.True, "adoption must not touch the venv");
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
        }

        [Test]
        public async Task BootstrapRepairDeletesABrokenVenvAndRerunsSetup()
        {
            // The one-click heal: a sentinel-less venv whose imports fail is deleted BEFORE the setup
            // re-runs (the fake setup notes whether the old venv was still there), and the rebuilt
            // environment is ready.
            string lab = makeLab();
            writeFakePython(lab, importsLoad: false);
            File.WriteAllText(Path.Combine(lab, ".venv", "canary.txt"), "from the broken install");
            writeRebuildingSetupScripts(lab);

            var lines = new List<string>();
            var result = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, line => { lock (lines) lines.Add(line); }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(File.Exists(Path.Combine(lab, ".venv", "canary.txt")), Is.False, "the broken venv should be gone");
            Assert.That(File.Exists(Path.Combine(lab, "saw-canary.txt")), Is.False, "the venv must be deleted before the setup re-runs");
            Assert.That(File.ReadAllText(LyricMapImporter.PythonExeFor(lab)).Trim(), Is.EqualTo("rebuilt"));
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
            lock (lines)
                Assert.That(lines, Has.Some.Contains("incomplete, rebuilding"));
        }

        [Test]
        public async Task BootstrapFailsWhenSetupExitsCleanlyWithoutWritingTheSentinel()
        {
            // A setup that exits 0 but never reached its last act did not complete: plain sentence.
            string lab = makeLab();
            File.WriteAllText(Path.Combine(lab, "setup.ps1"),
                "New-Item -ItemType Directory -Force -Path '.venv\\Scripts' | Out-Null\nSet-Content -Path '.venv\\Scripts\\python.exe' -Value 'x'\nexit 0\n");
            File.WriteAllText(Path.Combine(lab, "setup.sh"), "mkdir -p .venv/bin\necho x > .venv/bin/python\nexit 0\n");

            var result = await LyricMapImporter.BootstrapEnvironmentAsync(lab, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("could not finish"));
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.False);
        }

        /// <summary>The game's vendored lyriclab/ (the source of the shipped scripts), found above the test binaries.</summary>
        private static string vendoredLyricLab()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "lyriclab");

                if (File.Exists(Path.Combine(candidate, "setup.ps1")) && File.Exists(Path.Combine(candidate, "setup.sh")))
                    return candidate;
            }

            Assert.Fail("the vendored lyriclab/ was not found above the test directory");
            return null!;
        }

        [Test]
        public void ShippedSetupScriptsWriteTheSentinelTheGameReads()
        {
            // The sentinel's name is shared by three files; a drift silently makes every install
            // read as needing repair forever (or, the other way, never).
            string lab = vendoredLyricLab();

            Assert.That(File.ReadAllText(Path.Combine(lab, "setup.ps1")), Does.Contain($"$sentinel = '.venv\\{LyricMapImporter.SETUP_SENTINEL_FILE}'"));
            Assert.That(File.ReadAllText(Path.Combine(lab, "setup.sh")), Does.Contain($"SENTINEL='.venv/{LyricMapImporter.SETUP_SENTINEL_FILE}'"));

            // And the setup's own import check is the adoption probe's, so a venv the game adopts
            // passed exactly what a fresh setup would have required of it.
            Assert.That(File.ReadAllText(Path.Combine(lab, "setup.ps1")), Does.Contain($"import {LyricMapImporter.ALIGNER_IMPORTS}\""));
            Assert.That(File.ReadAllText(Path.Combine(lab, "setup.sh")), Does.Contain($"import {LyricMapImporter.ALIGNER_IMPORTS}\""));
        }

        [Test]
        public async Task ShippedSetupScriptRunAgainstAFakeUvEndsReady()
        {
            // The real vendored setup script, driven end to end by the game's bootstrap against a
            // fake uv (nothing is downloaded): uv "venv" drops a python that exits 0 for every
            // "-c" step, "pip" succeeds. PATH is narrowed to the fake plus the system, so a real
            // uv on this machine can never be reached.
            string shipped = vendoredLyricLab();
            string lab = makeLab();
            File.Copy(Path.Combine(shipped, LyricMapImporter.SetupScriptName), Path.Combine(lab, LyricMapImporter.SetupScriptName));

            string fakeUv = Path.Combine(tempRoot, "fakeuv");
            Directory.CreateDirectory(fakeUv);
            string path;

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(fakeUv, "uv.cmd"), string.Join("\r\n",
                    "@echo off",
                    "if /i \"%~1\"==\"venv\" goto venv",
                    "exit /b 0",
                    ":venv",
                    "if not exist .venv\\Scripts mkdir .venv\\Scripts",
                    "copy /y \"%SystemRoot%\\System32\\doskey.exe\" .venv\\Scripts\\python.exe >nul",
                    "exit /b %errorlevel%") + "\r\n");
                path = string.Join(Path.PathSeparator, fakeUv, Environment.SystemDirectory, Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0"));
            }
            else
            {
                string uv = Path.Combine(fakeUv, "uv");
                File.WriteAllText(uv, "#!/bin/sh\nif [ \"$1\" = venv ]; then mkdir -p .venv/bin; printf '#!/bin/sh\\nexit 0\\n' > .venv/bin/python; chmod +x .venv/bin/python; fi\nexit 0\n");
                File.SetUnixFileMode(uv, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                path = string.Join(Path.PathSeparator, fakeUv, "/usr/bin", "/bin");
            }

            string? originalPath = Environment.GetEnvironmentVariable("PATH");
            var lines = new List<string>();
            LyricImportResult result;

            try
            {
                Environment.SetEnvironmentVariable("PATH", path);
                result = await LyricMapImporter.BootstrapEnvironmentAsync(
                    lab, line => { lock (lines) lines.Add(line); }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }

            lock (lines)
                Assert.That(result.Success, Is.True, $"{result.Error}\n{string.Join("\n", lines)}");

            string sentinel = File.ReadAllText(LyricMapImporter.SetupSentinelFor(lab));
            Assert.That(sentinel, Does.Contain("python=3.11"));
            Assert.That(sentinel, Does.Contain("device=cpu"));
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
            lock (lines)
                Assert.That(lines, Has.Some.Contains("verifying the installed packages import"));
        }

        /// <summary>
        /// Backlog 411: both shipped setup scripts install the same required set and the same
        /// version 10 fused-evidence pair (phonemizer, espeakng-loader) by its own call, and fetch
        /// its weights through the aligner's own function; and the pair stays OUT of the import
        /// check that gates the sentinel (and of the adoption probe, which shares it), so a platform
        /// without an espeakng-loader wheel still gets an installed aligner that runs the version 9
        /// path.
        /// </summary>
        [Test]
        public void ShippedSetupScriptsInstallTheFusedEvidencePackages()
        {
            string lab = vendoredLyricLab();
            string ps1 = File.ReadAllText(Path.Combine(lab, "setup.ps1"));
            string sh = File.ReadAllText(Path.Combine(lab, "setup.sh"));

            Assert.Multiple(() =>
            {
                Assert.That(ps1, Does.Contain($"$alignerPackages = '{LyricMapImporter.ALIGNER_PACKAGES}' -split ' '"));
                Assert.That(sh, Does.Contain($"ALIGNER_PACKAGES='{LyricMapImporter.ALIGNER_PACKAGES}'"));
                Assert.That(ps1, Does.Contain($"$fusedPackages = '{LyricMapImporter.FUSED_EVIDENCE_PACKAGES}' -split ' '"));
                Assert.That(sh, Does.Contain($"FUSED_PACKAGES='{LyricMapImporter.FUSED_EVIDENCE_PACKAGES}'"));
                Assert.That(ps1, Does.Contain("& $uvExe pip install --python $py $fusedPackages"));
                Assert.That(sh, Does.Contain("\"$UV\" pip install --python \"$PY\" $FUSED_PACKAGES"));
                Assert.That(ps1, Does.Contain("align_lyrics.qmul_weights_path()"));
                Assert.That(sh, Does.Contain("align_lyrics.qmul_weights_path()"));
                Assert.That(ps1, Does.Contain("[switch]$Update"));
                Assert.That(sh, Does.Contain("--update) UPDATE=1 ;;"));

                Assert.That(LyricMapImporter.ALIGNER_IMPORTS, Does.Not.Contain("phonemizer").And.Not.Contain("espeakng"));
                Assert.That(LyricMapImporter.ALIGNER_PACKAGES, Does.Not.Contain("phonemizer").And.Not.Contain("espeakng"));
            });
        }

        /// <summary>Fake setup scripts that record the arguments they were run with (into setup-args.txt) and exit with <paramref name="exitCode"/>.</summary>
        private static void writeRecordingSetupScripts(string lab, int exitCode)
        {
            File.WriteAllText(Path.Combine(lab, "setup.ps1"), $"Set-Content -Path 'setup-args.txt' -Value ($args -join ' ')\nexit {exitCode}\n");
            File.WriteAllText(Path.Combine(lab, "setup.sh"), $"echo \"$*\" > setup-args.txt\nexit {exitCode}\n");
        }

        /// <summary>A completed install: a venv python whose imports load, its setup sentinel, and a canary in the venv.</summary>
        private string makeCompletedInstall()
        {
            string lab = makeLab();
            writeFakePython(lab, importsLoad: true);
            File.WriteAllText(LyricMapImporter.SetupSentinelFor(lab), "python=3.11");
            File.WriteAllText(Path.Combine(lab, ".venv", "canary.txt"), "the existing environment");
            return lab;
        }

        [Test]
        public async Task BootstrapRunsTheSetupUpdateOnACompletedInstallWhenAsked()
        {
            // The Update button (InstallAsync) copies new scripts over a completed install. Before
            // backlog 411 the bootstrap then returned at once, so a venv never got the packages the
            // new aligner added. With updateExisting it runs the setup's update mode, in place.
            string lab = makeCompletedInstall();
            writeRecordingSetupScripts(lab, exitCode: 0);
            string argsFile = Path.Combine(lab, "setup-args.txt");

            var plain = await LyricMapImporter.BootstrapEnvironmentAsync(lab, _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(plain.Success, Is.True, plain.Error);
            Assert.That(File.Exists(argsFile), Is.False, "without updateExisting a completed install is left as it stands");

            var lines = new List<string>();
            var updated = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, line => { lock (lines) lines.Add(line); }, CancellationToken.None, "cpu", updateExisting: true).ConfigureAwait(false);

            Assert.That(updated.Success, Is.True, updated.Error);
            Assert.That(File.Exists(argsFile), Is.True, "the setup script did not run");
            Assert.That(File.ReadAllText(argsFile).Trim(), Does.EndWith(OperatingSystem.IsWindows() ? "-Update" : "--update"));
            Assert.That(File.Exists(Path.Combine(lab, ".venv", "canary.txt")), Is.True, "an update must not touch the venv");
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
        }

        [Test]
        public async Task BootstrapUpdatesAnAdoptedSentinelLessVenvToo()
        {
            // A pre-sentinel install is adopted in place and then updated, since it is just as
            // likely to lack the new packages.
            string lab = makeLab();
            writeFakePython(lab, importsLoad: true);
            writeRecordingSetupScripts(lab, exitCode: 0);

            var result = await LyricMapImporter.BootstrapEnvironmentAsync(lab, _ => { }, CancellationToken.None, "cpu", updateExisting: true).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(lab, "setup-args.txt")).Trim(), Does.EndWith(OperatingSystem.IsWindows() ? "-Update" : "--update"));
        }

        [Test]
        public async Task AFailedSetupUpdateKeepsTheInstallAndSaysSo()
        {
            // The install still works without the new packages (the aligner runs its version 9
            // path), so a failed update is said and logged, never returned as a failed install.
            string lab = makeCompletedInstall();
            writeRecordingSetupScripts(lab, exitCode: 1);

            var lines = new List<string>();
            var result = await LyricMapImporter.BootstrapEnvironmentAsync(
                lab, line => { lock (lines) lines.Add(line); }, CancellationToken.None, "cpu", updateExisting: true).ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
            Assert.That(File.Exists(Path.Combine(lab, ".venv", "canary.txt")), Is.True);
            lock (lines)
                Assert.That(lines, Has.Some.EqualTo(LyricMapImporter.ENVIRONMENT_UPDATE_INCOMPLETE));
        }

        [Test]
        public async Task ShippedSetupScriptUpdateAgainstAFakeUvInstallsTheNewPackagesInPlace()
        {
            // The real vendored setup script in its update mode, driven by the game's bootstrap
            // against a fake uv that logs every call (nothing is downloaded): the required set and then
            // the version 10 pair, installed into the existing venv; no venv created, torch untouched.
            string shipped = vendoredLyricLab();
            string lab = makeCompletedInstall();
            File.Copy(Path.Combine(shipped, LyricMapImporter.SetupScriptName), Path.Combine(lab, LyricMapImporter.SetupScriptName));
            string sentinelBefore = File.ReadAllText(LyricMapImporter.SetupSentinelFor(lab));

            string fakeUv = Path.Combine(tempRoot, "fakeuv");
            Directory.CreateDirectory(fakeUv);
            string path;

            if (OperatingSystem.IsWindows())
            {
                // The redirect comes first so a trailing digit in the arguments cannot read as a handle.
                File.WriteAllText(Path.Combine(fakeUv, "uv.cmd"), string.Join("\r\n", "@echo off", ">>uv-calls.txt echo %*", "exit /b 0") + "\r\n");
                path = string.Join(Path.PathSeparator, fakeUv, Environment.SystemDirectory, Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0"));
            }
            else
            {
                string uv = Path.Combine(fakeUv, "uv");
                File.WriteAllText(uv, "#!/bin/sh\necho \"$*\" >> uv-calls.txt\nexit 0\n");
                File.SetUnixFileMode(uv, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                path = string.Join(Path.PathSeparator, fakeUv, "/usr/bin", "/bin");
            }

            string? originalPath = Environment.GetEnvironmentVariable("PATH");
            var lines = new List<string>();
            LyricImportResult result;

            try
            {
                Environment.SetEnvironmentVariable("PATH", path);
                result = await LyricMapImporter.BootstrapEnvironmentAsync(
                    lab, line => { lock (lines) lines.Add(line); }, CancellationToken.None, "cpu", updateExisting: true).ConfigureAwait(false);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }

            string callsFile = Path.Combine(lab, "uv-calls.txt");
            // The script's own "is this uv usable" probe (--version) is not an install.
            string[] calls = File.Exists(callsFile)
                ? File.ReadAllLines(callsFile).Select(l => l.Trim()).Where(l => l.Length > 0 && l != "--version").ToArray()
                : Array.Empty<string>();

            lock (lines)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(result.Success, Is.True, $"{result.Error}\n{string.Join("\n", lines)}");
                    Assert.That(lines, Has.Some.Contains("lyriclab environment updated"));
                    Assert.That(lines, Has.None.EqualTo(LyricMapImporter.ENVIRONMENT_UPDATE_INCOMPLETE));
                    Assert.That(lines, Has.Some.Contains("fetching the fused-evidence weights"));
                    Assert.That(calls, Has.Length.EqualTo(2), string.Join("\n", calls));
                    Assert.That(calls.ElementAtOrDefault(0), Does.StartWith("pip install").And.EndWith(LyricMapImporter.ALIGNER_PACKAGES));
                    Assert.That(calls.ElementAtOrDefault(1), Does.StartWith("pip install").And.EndWith(" " + LyricMapImporter.FUSED_EVIDENCE_PACKAGES));
                    Assert.That(calls, Has.None.Contains("torch"));
                    Assert.That(File.Exists(Path.Combine(lab, ".venv", "canary.txt")), Is.True);
                    Assert.That(File.ReadAllText(LyricMapImporter.SetupSentinelFor(lab)), Is.EqualTo(sentinelBefore));
                });
            }
        }

        [Test]
        public async Task ImportWithABrokenSentinelLessVenvPointsAtRepairAndDeletesNothing()
        {
            string lab = makeLab();
            writeFakePython(lab, importsLoad: false);
            string audioPath = Path.Combine(tempRoot, "a.mp3");
            File.WriteAllText(audioPath, "fake");

            var lines = new List<string>();
            var (result, timing) = await LyricMapImporter.ProduceTimingJsonAsync(
                audioPath, "just some words\nwith no timestamps\n", "A", "B", lab, Array.Empty<string>(),
                line => { lock (lines) lines.Add(line); }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(result.Success, Is.False);
            Assert.That(timing, Is.Null);
            Assert.That(result.Error, Does.Contain("Repair local auto-aligner"));
            Assert.That(LyricMapImporter.EnvironmentPresent(lab), Is.True, "an import must never delete the venv");
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.False);
        }

        [Test]
        public async Task ImportAdoptsAWorkingSentinelLessVenv()
        {
            string lab = makeLab();
            writeFakePython(lab, importsLoad: true);
            string audioPath = Path.Combine(tempRoot, "a.mp3");
            File.WriteAllText(audioPath, "fake");

            // The fake python then "runs" the aligner and produces nothing, so the import still
            // fails; what is pinned is that the probe adopted the venv on the way.
            await LyricMapImporter.ProduceTimingJsonAsync(
                audioPath, "just some words\n", "A", "B", lab, Array.Empty<string>(), _ => { }, CancellationToken.None).ConfigureAwait(false);

            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
        }

        [Test]
        public void AlignerThatCannotImportItsPackagesSuggestsRepair()
        {
            // The runtime half: an installed (sentinel) venv whose python cannot import torch. The
            // sentinel is withdrawn so Settings offers Repair, and the error says so in words.
            string lab = makeLab();
            writeFakePython(lab, importsLoad: true);
            File.WriteAllText(LyricMapImporter.SetupSentinelFor(lab), "python=3.11");

            var result = LyricMapImporter.AlignerFailure(lab, 1, "Traceback (most recent call last):\nModuleNotFoundError: No module named 'torch'");

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("Repair local auto-aligner"));
            Assert.That(result.Error, Does.Not.Contain("Traceback"));
            Assert.That(LyricMapImporter.EnvironmentNeedsRepair(lab), Is.True);

            // Any other failure is reported as it always was and leaves the install alone.
            File.WriteAllText(LyricMapImporter.SetupSentinelFor(lab), "python=3.11");
            var other = LyricMapImporter.AlignerFailure(lab, 2, "RuntimeError: demucs did not produce vocals.wav");

            Assert.That(other.Error, Does.StartWith("aligner exited with code 2"));
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
        }

        /// <summary>
        /// Version 10 logs a WARNING when its fused path falls back for want of an OPTIONAL package,
        /// and the line names the missing module. An unrelated failure later in the same run, with
        /// that line still in the tail, must not read as a broken install (which would withdraw the
        /// sentinel of a venv whose required imports load); a real import failure still does.
        /// </summary>
        [Test]
        public void AFusedEvidenceFallbackWarningIsNotAMissingPackage()
        {
            const string warning = "[12:40:02] WARNING: fused evidence failed (RuntimeError: a dependency is missing (No module named 'phonemizer'); "
                                   + "install phonemizer and espeakng-loader); keeping the version 9 path";

            Assert.Multiple(() =>
            {
                Assert.That(LyricMapImporter.IsMissingPackageFailure($"{warning} | Traceback (most recent call last): | OSError: [Errno 28] No space left on device"), Is.False);
                Assert.That(LyricMapImporter.IsMissingPackageFailure("WARNING: something optional raised ImportError, carrying on"), Is.False);
                Assert.That(LyricMapImporter.IsMissingPackageFailure($"{warning} | Traceback (most recent call last): | ModuleNotFoundError: No module named 'torch'"), Is.True);
                Assert.That(LyricMapImporter.IsMissingPackageFailure("Traceback (most recent call last):\nModuleNotFoundError: No module named 'torch'"), Is.True);
                Assert.That(LyricMapImporter.IsMissingPackageFailure("ImportError: DLL load failed while importing _C"), Is.True);
            });
        }

        #endregion

        #region Aligner version gate (backlog 410)

        private const string fully_stamped_lyrics = "[00:01.00] hello\n[00:02.00] world\n";

        /// <summary>
        /// A completed install (venv python and setup sentinel) whose script declares
        /// <paramref name="version"/>, null for a version-1 script with no constant. Its python is
        /// the do-nothing fake, so a run that DID happen produces no timing and the ladder falls back
        /// to the line stamps, which is what makes a refusal distinguishable from a run.
        /// </summary>
        private string makeInstalledLab(string? version)
        {
            string lab = makeLab();
            File.WriteAllText(Path.Combine(lab, "align_lyrics.py"), version == null ? "# predates the constant\n" : $"ALIGNER_VERSION = \"{version}\"\n");
            writeFakePython(lab, importsLoad: true);
            File.WriteAllText(LyricMapImporter.SetupSentinelFor(lab), "python=3.11\n");
            Assert.That(LyricMapImporter.EnvironmentReady(lab), Is.True);
            return lab;
        }

        private string writeFakeAudio()
        {
            string audioPath = Path.Combine(tempRoot, "Some Artist - Some Song.mp3");
            File.WriteAllText(audioPath, "fake audio");
            return audioPath;
        }

        [TestCase("8", "9", AlignerVersionStatus.Older)]
        [TestCase(null, "9", AlignerVersionStatus.Older)]
        [TestCase("10", "9", AlignerVersionStatus.Newer)]
        [TestCase("9", "10", AlignerVersionStatus.Older)]
        [TestCase("9", "9", AlignerVersionStatus.Matches)]
        [TestCase(null, "1", AlignerVersionStatus.Matches)]
        [TestCase("9b", "9", AlignerVersionStatus.Different)]
        [TestCase("9b", "9b", AlignerVersionStatus.Matches)]
        [TestCase("8", null, AlignerVersionStatus.Matches)]
        public void VersionRuleRefusesAnyMismatch(string? installed, string? shipped, AlignerVersionStatus expected)
        {
            Assert.That(LocalAlignerVersion.Compare(installed, shipped), Is.EqualTo(expected));
            Assert.That(LocalAlignerVersion.StatusOf(false, installed, shipped), Is.EqualTo(AlignerVersionStatus.Matches), "nothing installed is never a mismatch");
        }

        /// <summary>
        /// The guard every import entry point goes through: an installed aligner of any version but the
        /// required one is refused with the advice, and never run (no "aligner unavailable ... trying
        /// next option" line, which a run of the fake python would produce on its way to the line
        /// stamps), and the import does NOT quietly fall back to those stamps.
        /// </summary>
        [TestCase("8", "your local auto-aligner is out of date (v8, this version of type!beat needs v9). "
                       + "Update it in Settings > Experimental > Lyric timing to use automatic alignment.")]
        [TestCase(null, "your local auto-aligner is out of date (v1, this version of type!beat needs v9). "
                        + "Update it in Settings > Experimental > Lyric timing to use automatic alignment.")]
        [TestCase("10", "your local auto-aligner is from a newer version of type!beat (v10, this version of type!beat needs v9). "
                        + "Reinstall it in Settings > Experimental > Lyric timing to use automatic alignment.")]
        public async Task MismatchedAlignerIsRefusedAndNeverRun(string? installedVersion, string expectedError)
        {
            string lab = makeInstalledLab(installedVersion);
            string audioPath = writeFakeAudio();

            var lines = new List<string>();
            var (result, timing) = await LyricMapImporter.ProduceTimingJsonAsync(
                audioPath, fully_stamped_lyrics, "A", "B", lab, Array.Empty<string>(),
                line => { lock (lines) lines.Add(line); }, CancellationToken.None,
                useAutomaticAlignment: true, requiredAlignerVersion: "9").ConfigureAwait(false);

            Assert.That(result.Success, Is.False, "refused, not fallen back to the line stamps");
            Assert.That(timing, Is.Null);
            Assert.That(result.Error, Is.EqualTo(expectedError));

            lock (lines)
                Assert.That(lines, Has.None.Contains("trying next option"), "the aligner never ran");
        }

        /// <summary>
        /// The packaging entry point refuses the same way with automatic alignment on, and with it off
        /// imports exactly as before: the line-stamp path never consults the aligner, mismatched or not.
        /// </summary>
        [Test]
        public async Task BuildOszRefusesAMismatchedAlignerOnlyWhenAlignmentIsOn()
        {
            string lab = makeInstalledLab("8");
            string audioPath = writeFakeAudio();
            string lyricsPath = Path.Combine(tempRoot, "lyrics.lrc");
            File.WriteAllText(lyricsPath, fully_stamped_lyrics);

            var lines = new List<string>();
            var refused = await LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, "Some Artist", "Some Song", lab, Array.Empty<string>(),
                line => { lock (lines) lines.Add(line); }, CancellationToken.None, useAutomaticAlignment: true, requiredAlignerVersion: "9").ConfigureAwait(false);

            Assert.That(refused.Success, Is.False);
            Assert.That(refused.Error, Does.StartWith("your local auto-aligner is out of date (v8, this version of type!beat needs v9)"));
            lock (lines)
                Assert.That(lines, Has.None.Contains("trying next option"), "the aligner never ran");

            var lineStamped = await LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, "Some Artist", "Some Song", lab, Array.Empty<string>(),
                _ => { }, CancellationToken.None, useAutomaticAlignment: false, requiredAlignerVersion: "9").ConfigureAwait(false);

            Assert.That(lineStamped.Success, Is.True, lineStamped.Error);
        }

        /// <summary>
        /// Lyrics that need no aligner (an enhanced LRC carries its own word stamps) import with
        /// automatic alignment on even when the installed aligner mismatches: the refusal sits where
        /// the aligner would run, not in front of the whole ladder.
        /// </summary>
        [Test]
        public async Task WordStampedLyricsImportPastAMismatchedAligner()
        {
            string lab = makeInstalledLab("8");
            string audioPath = writeFakeAudio();

            var (result, timing) = await LyricMapImporter.ProduceTimingJsonAsync(
                audioPath, enhanced_lyrics, "A", "B", lab, Array.Empty<string>(), _ => { }, CancellationToken.None,
                useAutomaticAlignment: true, requiredAlignerVersion: "9").ConfigureAwait(false);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(timing, Is.Not.Null);
        }

        [Test]
        public void MatchingOrUnrequiredVersionIsNotRefused()
        {
            string lab = makeInstalledLab("9");

            Assert.That(LyricMapImporter.AlignerVersionRefusal(lab, "9"), Is.Null);
            Assert.That(LyricMapImporter.AlignerVersionRefusal(lab, null), Is.Null, "no required version checks nothing");
            Assert.That(LyricMapImporter.AlignerVersionRefusal(lab, "10"), Does.StartWith("your local auto-aligner is out of date (v9, this version of type!beat needs v10)"));
        }

        #endregion

        #region Authoring marks through the import (backlog 202)

        [Test]
        public void MarkFreeLyricsSynthesizeByteIdenticalTiming()
        {
            // The whole of backlog 202's import work is gated on a line carrying a mark. This is
            // the pin on that gate: for lyrics with neither, the document is character for
            // character the three-key line shape the synthesiser has always emitted. Since backlog
            // 363 the import also syllabifies, so the gate is pinned on lyrics with no word of more
            // than one syllable; the real fixture's syllabified document is pinned by
            // ImportSyllablesTest.
            const string lyricsContent = "[00:01.00]so we go\n[00:03.50]let it be\n[00:06.00]and that is all\n";

            Assert.That(LrcParser.Parse(lyricsContent).SelectMany(l => l.Units).All(u => Syllabifier.SplitPoints(u.Text).Count == 0), Is.True,
                "the fixture must hold one-syllable words only");

            string? actual = LyricMapImporter.SynthesizeTimingJsonFromLrc(lyricsContent);
            Assert.That(actual, Is.Not.Null);

            var lines = LrcParser.Parse(lyricsContent);

            string expected = JsonSerializer.Serialize(new
            {
                version = TimingJsonLoader.SUPPORTED_VERSION,
                song_end_ms = lines[^1].EndTime,
                lines = lines.Select(l => new
                {
                    text = l.RawText,
                    start_ms = l.StartTime,
                    end_ms = l.SingEndTime,
                }).ToArray()
            });

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void PipedLyricsPackageASubdividedMap()
        {
            // One pipe is a request for sub-word timing, so the packaged map leaves Line
            // granularity behind and carries the subdivision the mapper asked for.
            string audioPath = Path.Combine(tempRoot, "A - B.mp3");
            File.WriteAllText(audioPath, "fake audio");

            string? timing = LyricMapImporter.SynthesizeTimingJsonFromLrc("[00:01.00] ple|ase stay\n[00:05.00] plain line\n[00:07.00]\n");
            Assert.That(timing, Is.Not.Null);

            string oszPath = Path.Combine(tempRoot, "piped.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "A", "B", audioPath, timing!, "unused");
            Assert.That(result.Success, Is.True, result.Error);

            using var archive = ZipFile.OpenRead(oszPath);
            var beatmap = decode(readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))));
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(2));
            Assert.That(hitObjects[0].Granularity, Is.EqualTo(TimingGranularity.Syllable));
            Assert.That(hitObjects[0].Line.RawText, Is.EqualTo("please stay"), "the pipe never reaches the stored lyric");

            var unit = hitObjects[0].Line.Units[0];
            Assert.That(unit.SyllableSplits, Is.EqualTo(new[] { 3 }));
            Assert.That(unit.SyllableBoundaries.Count, Is.EqualTo(1));
            Assert.That(unit.SyllableBoundaries[0], Is.EqualTo((unit.StartTime + unit.EndTime) / 2).Within(1e-6));

            // The pipe-free line carries no words[] of its own, so it is still interpolated.
            Assert.That(hitObjects[1].Line.Units.All(u => u.SyllableBoundaries.Count == 0), Is.True);
        }

        [Test]
        public void AMarkerOnlyLyricLineBecomesAFreestyleLine()
        {
            string audioPath = Path.Combine(tempRoot, "A - B.mp3");
            File.WriteAllText(audioPath, "fake audio");

            string? timing = LyricMapImporter.SynthesizeTimingJsonFromLrc("[00:01.00] real one\n[00:03.00] &&&\n[00:09.00] real two\n[00:11.00]\n");
            Assert.That(timing, Is.Not.Null);
            Assert.That(timing, Does.Contain("\"freestyle\":true"), "the decoder needs the opt-in before it reads '&' as a marker");

            string oszPath = Path.Combine(tempRoot, "freestyle.osz");
            var result = LyricMapImporter.PackageOsz(oszPath, "A", "B", audioPath, timing!, "unused");
            Assert.That(result.Success, Is.True, result.Error);

            using var archive = ZipFile.OpenRead(oszPath);
            var beatmap = decode(readEntry(archive.Entries.Single(e => e.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))));
            var hitObjects = beatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            Assert.That(hitObjects.Count, Is.EqualTo(3));
            Assert.That(hitObjects[1].Line.RawText, Is.EqualTo("&&&"));
            Assert.That(hitObjects[1].StartTime, Is.EqualTo(3000));

            var typingLine = Gameplay.TypingLine.FromLyricLine(hitObjects[1].Line);
            Assert.That(typingLine.Cells.Count(c => c.IsFreestyle), Is.EqualTo(3));
        }

        [Test]
        public void FlagFreestyleLinesOnlyTouchesAmpersandLines()
        {
            const string plain = "{\"version\":2,\"lines\":[{\"text\":\"me you\",\"start_ms\":1000,\"end_ms\":2000}]}";
            Assert.That(LyricMapImporter.FlagFreestyleLines(plain), Is.SameAs(plain), "an ampersand-free document is returned verbatim");

            const string marked = "{\"version\":2,\"lines\":["
                                  + "{\"text\":\"me & you\",\"start_ms\":1000,\"end_ms\":2000},"
                                  + "{\"text\":\"plain\",\"start_ms\":3000,\"end_ms\":4000}]}";

            string flagged = LyricMapImporter.FlagFreestyleLines(marked);

            using var doc = JsonDocument.Parse(flagged);
            var lines = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();

            Assert.That(lines[0].TryGetProperty("freestyle", out var flag), Is.True);
            Assert.That(flag.GetBoolean(), Is.True);
            Assert.That(lines[1].TryGetProperty("freestyle", out _), Is.False);

            // Malformed input is a pass-through, not a failure: this is a polish pass.
            Assert.That(LyricMapImporter.FlagFreestyleLines("{ not json &"), Is.EqualTo("{ not json &"));
        }

        #endregion

        [Test]
        public void MetadataEscapingRoundTrips()
        {
            // '!' (the game's namesake) and unicode in artist/title must survive the writer + decoder.
            const string artist = "Sÿntax! 日本語";
            const string title = "type!beat ★ intro";

            const string timing = "{\"version\":2,\"song_end_ms\":8000,\"lines\":["
                                  + "{\"text\":\"one two\",\"start_ms\":1000,\"end_ms\":3000}]}";

            string osuText = LyricOsuFormat.GenerateOsu(artist, title, "audio.mp3", "tester", timing);
            var beatmap = decode(osuText);

            Assert.That(beatmap.Metadata.Artist, Is.EqualTo(artist));
            Assert.That(beatmap.Metadata.ArtistUnicode, Is.EqualTo(artist));
            Assert.That(beatmap.Metadata.Title, Is.EqualTo(title));
            Assert.That(beatmap.Metadata.TitleUnicode, Is.EqualTo(title));
        }

        private static string readEntry(ZipArchiveEntry entry)
        {
            using var reader = new StreamReader(entry.Open());
            return reader.ReadToEnd();
        }

        /// <summary>
        /// A user log (2026-10-02): demucs died with UnicodeEncodeError printing a Japanese track path
        /// through a cp1252 stdout, and the import called the aligner unavailable. Every python we
        /// start now runs in UTF-8, children included, and the log reader decodes it as such.
        /// </summary>
        [Test]
        public void PythonProcessesRunInUtf8()
        {
            var psi = new System.Diagnostics.ProcessStartInfo { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };

            LyricMapImporter.RunPythonInUtf8(psi);

            Assert.Multiple(() =>
            {
                Assert.That(psi.Environment["PYTHONUTF8"], Is.EqualTo("1"), "covers the demucs child python spawns");
                Assert.That(psi.Environment["PYTHONIOENCODING"], Is.EqualTo("utf-8"));
                Assert.That(psi.StandardOutputEncoding, Is.EqualTo(Encoding.UTF8));
                Assert.That(psi.StandardErrorEncoding, Is.EqualTo(Encoding.UTF8));
            });
        }

        [Test]
        public void ShippedAlignerRunsDemucsInUtf8Too()
        {
            // Belt and braces: the vendored script sets the same variables on the demucs subprocess,
            // so a direct invocation outside the game behaves the same way. Found the way
            // ShippedAlignerAcceptsVocalModes finds it: the repo's own lyriclab/ above the test dir.
            string? vendored = null;

            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null && vendored == null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "lyriclab", "align_lyrics.py")))
                    vendored = Path.Combine(dir.FullName, "lyriclab", "align_lyrics.py");
            }

            Assert.That(vendored, Is.Not.Null);

            string script = File.ReadAllText(vendored!);
            int at = script.IndexOf("def separate_vocals", StringComparison.Ordinal);
            string body = script.Substring(at, script.IndexOf("\ndef ", at + 10, StringComparison.Ordinal) - at);

            Assert.Multiple(() =>
            {
                Assert.That(body, Does.Contain("env[\"PYTHONUTF8\"] = \"1\""));
                Assert.That(body, Does.Contain("env[\"PYTHONIOENCODING\"] = \"utf-8\""));
                Assert.That(LyricMapImporter.AlignerPersistsVocalsStem(Path.GetDirectoryName(vendored!)!), Is.True,
                    "installed older copies must be offered the update, and the stem needs version 9 or newer");
            });
        }

        [Test]
        public void NoFallbackFailureNamesTheSituationThatApplies()
        {
            const string tail = "  File \"align_lyrics.py\", line 4049, in main |     align_src = separate_vocals(song_wav, work, | subprocess.CalledProcessError: Command '[...]' returned non-zero exit status 1.";

            string notInstalled = LyricMapImporter.NoFallbackFailureMessage(false, false, null);
            string broken = LyricMapImporter.NoFallbackFailureMessage(false, true, null);
            string failed = LyricMapImporter.NoFallbackFailureMessage(false, false, tail);
            string failedPartial = LyricMapImporter.NoFallbackFailureMessage(true, false, tail);

            Assert.Multiple(() =>
            {
                Assert.That(notInstalled, Does.StartWith("no auto-aligner is available").And.Contain("Install the local auto-aligner"));
                Assert.That(broken, Does.Contain("install is incomplete").And.Contain("Repair local auto-aligner"));
                Assert.That(failed, Does.StartWith("the local auto-aligner is installed but its run failed"));
                Assert.That(failed, Does.Contain("subprocess.CalledProcessError"), "the last line of the tail is the one that stopped it");
                Assert.That(failed, Does.Not.Contain("line 4049"), "not the whole tail");
                Assert.That(failed, Does.Not.Contain("Install the local auto-aligner"), "it is installed; do not tell them to install it");
                Assert.That(failedPartial, Does.Contain("only some lines have [mm:ss.xx] timestamps"));
                Assert.That(LyricMapImporter.ShortAlignerFailure(new string('x', 400)), Has.Length.EqualTo(160));
            });
        }

        private static typebeat.Game.Beatmaps.Beatmap decode(string osuText)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(osuText));
            using var reader = new LineBufferedReader(stream);
            return typebeat.Game.Beatmaps.Formats.Decoder.GetDecoder<typebeat.Game.Beatmaps.Beatmap>(reader).Decode(reader);
        }
    }
}
