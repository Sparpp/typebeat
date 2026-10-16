// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;
using typebeat.Game.Beatmaps;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// The local lyriclab aligner as the importer finds it: where it is, whether its venv is
    /// installed (and how a broken one is adopted or rebuilt), the setup script that builds it,
    /// which version the installed script is and what its command line can therefore carry.
    /// </summary>
    public static class AlignerEnvironment
    {
        /// <summary>
        /// Candidate folder names, in preference order: the vendored in-repo component first,
        /// then the standalone sibling checkout it was adopted from.
        /// </summary>
        public static readonly string[] LyricLabFolderNames = { "lyriclab", "typebeat-lyriclab" };

        /// <summary>
        /// Environment bootstrap script shipped beside the aligner: PowerShell on Windows,
        /// the POSIX shell counterpart on Linux/macOS.
        /// </summary>
        public static string SetupScriptName => OperatingSystem.IsWindows() ? "setup.ps1" : "setup.sh";

        private const string aligner_script = "align_lyrics.py";

        /// <summary>
        /// Marker file written beside the venv recording which torch flavour the environment was
        /// built with ("cuda" or "cpu"). When it says cuda, alignment runs with --device cuda.
        /// </summary>
        public const string DEVICE_MARKER_FILE = "device.txt";

        /// <summary>
        /// Locates the aligner component. An explicitly configured valid path always wins. Otherwise
        /// walk up from each start directory collecting "lyriclab" (vendored, sits at the fork's repo
        /// root) and "typebeat-lyriclab" (sibling checkout) candidates, preferring one whose venv is
        /// already set up. Start directories are probed in order (game runtime base dir, then any
        /// extras such as the working directory), so the closest ready environment wins.
        /// </summary>
        public static string? ResolveLyricLabDir(string? configuredPath, IEnumerable<string> startDirectories, int maxAscendLevels = 6)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && IsLyricLabDir(configuredPath))
                return Path.GetFullPath(configuredPath);

            var candidates = new List<string>();

            foreach (string start in startDirectories)
            {
                if (string.IsNullOrEmpty(start))
                    continue;

                DirectoryInfo? dir;

                try
                {
                    dir = new DirectoryInfo(start);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i <= maxAscendLevels && dir != null; i++)
                {
                    foreach (string name in LyricLabFolderNames)
                    {
                        string candidate = Path.Combine(dir.FullName, name);
                        if (IsLyricLabDir(candidate) && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                            candidates.Add(candidate);
                    }

                    dir = dir.Parent;
                }
            }

            if (candidates.Count == 0)
                return null;

            // A completed install first, then a venv that exists but has not been confirmed (it may
            // still verify, see TryAdoptEnvironmentAsync), then the closest component.
            return candidates.FirstOrDefault(EnvironmentReady) ?? candidates.FirstOrDefault(EnvironmentPresent) ?? candidates[0];
        }

        /// <summary>Single-start-directory convenience overload (test parity with the standalone).</summary>
        public static string? ResolveLyricLabDir(string? configuredPath, string startDirectory, int maxAscendLevels = 6)
            => ResolveLyricLabDir(configuredPath, new[] { startDirectory }, maxAscendLevels);

        /// <summary>
        /// The setup sentinel (backlog 353), inside <c>.venv</c>: the setup scripts write it as the
        /// LAST act of a fully successful run, and the game writes it itself when a venv from before
        /// the sentinel existed proves its imports load (<see cref="TryAdoptEnvironmentAsync"/>). The
        /// literal is shared with <c>lyriclab/setup.ps1</c> and <c>lyriclab/setup.sh</c>, which a
        /// test pins.
        /// </summary>
        public const string SETUP_SENTINEL_FILE = ".typebeat-setup-ok";

        public static string SetupSentinelFor(string lyricLabDir) => Path.Combine(lyricLabDir, ".venv", SETUP_SENTINEL_FILE);

        /// <summary>
        /// The aligner venv is INSTALLED: its python exists AND a setup completed (the sentinel).
        /// A python alone is not enough: before backlog 349 a failed torch install could leave one
        /// behind, and this check trusting it was what kept such installs broken forever.
        /// </summary>
        public static bool EnvironmentReady(string lyricLabDir)
            => EnvironmentPresent(lyricLabDir) && File.Exists(SetupSentinelFor(lyricLabDir));

        /// <summary>A venv python exists, whether or not a setup ever completed.</summary>
        public static bool EnvironmentPresent(string lyricLabDir) => File.Exists(PythonExeFor(lyricLabDir));

        /// <summary>
        /// A venv with no setup sentinel: either an install that never finished, or a healthy one
        /// made before the sentinel existed. <see cref="TryAdoptEnvironmentAsync"/> tells them apart.
        /// </summary>
        public static bool EnvironmentNeedsRepair(string lyricLabDir) => EnvironmentPresent(lyricLabDir) && !EnvironmentReady(lyricLabDir);

        /// <summary>
        /// The modules the aligner imports, checked by the setup scripts and by the adoption probe
        /// alike. Deliberately NOT the fused-evidence modules (phonemizer, espeakng_loader, aligner
        /// version 10): the aligner falls back to its version 9 path without them, so a platform
        /// with no espeakng-loader wheel must still read as installed.
        /// </summary>
        public const string ALIGNER_IMPORTS = "torch, torchaudio, demucs, soundfile, pyphen, num2words";

        /// <summary>
        /// The REQUIRED packages the setup scripts install besides torch, in the order and spelling
        /// both scripts carry (a test pins them). A failure to install them fails the setup.
        /// </summary>
        public const string ALIGNER_PACKAGES = "demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg";

        /// <summary>
        /// The OPTIONAL fused-evidence pair (aligner version 10), installed by its own uv call after
        /// <see cref="ALIGNER_PACKAGES"/> so a platform with no espeakng-loader wheel still completes
        /// its setup (the aligner then runs its version 9 path). A full setup installs it best
        /// effort; the update path (<see cref="BootstrapEnvironmentAsync"/> with
        /// <c>updateExisting</c>) is how an existing venv gets it, and reports when it could not.
        /// </summary>
        public const string FUSED_EVIDENCE_PACKAGES = "phonemizer espeakng-loader";

        /// <summary>
        /// How long the adoption probe may take. A cold torch import is a few seconds on an SSD and
        /// can be tens on a slow disk; past this the environment is treated as not verified.
        /// </summary>
        private static readonly TimeSpan probe_timeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// ADOPTS a sentinel-less venv when it works: runs the venv's python on the aligner's imports
        /// and, when they all load, writes the sentinel the setup would have written, so an install
        /// made before the sentinel existed (or built by hand from the README) keeps working without
        /// a re-download. True when the environment is (now) ready; false when there is no venv or
        /// the imports fail, in which case nothing is changed. Never deletes anything.
        /// </summary>
        public static async Task<bool> TryAdoptEnvironmentAsync(string lyricLabDir, CancellationToken token)
        {
            if (EnvironmentReady(lyricLabDir))
                return true;

            if (!EnvironmentPresent(lyricLabDir))
                return false;

            if (!await probeImportsAsync(lyricLabDir, token).ConfigureAwait(false))
                return false;

            try
            {
                await File.WriteAllTextAsync(SetupSentinelFor(lyricLabDir),
                    $"adopted=true\ncreated={DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}\n", token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.Log($"Aligner environment verified but its setup sentinel could not be written: {e.Message}", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }

            Logger.Log($"Adopted the existing aligner environment at {lyricLabDir}: its imports load, setup sentinel written", LoggingTarget.Runtime);
            return true;
        }

        private static async Task<bool> probeImportsAsync(string lyricLabDir, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = PythonExeFor(lyricLabDir),
                WorkingDirectory = lyricLabDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            RunPythonInUtf8(psi);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"import {ALIGNER_IMPORTS}");

            try
            {
                using var process = Process.Start(psi);

                if (process == null)
                    return false;

                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync(token);
                var stderr = process.StandardError.ReadToEndAsync(token);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(probe_timeout);

                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch { }

                    Logger.Log($"Aligner environment probe did not finish within {probe_timeout.TotalSeconds:0}s", LoggingTarget.Runtime, LogLevel.Important);
                    return false;
                }

                if (process.ExitCode == 0)
                    return true;

                string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
                Logger.Log($"Aligner environment probe failed (exit code {process.ExitCode}): {output.Trim()}", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }
            catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
            {
                Logger.Log($"Aligner environment probe could not run: {e.Message}", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }
        }

        /// <summary>
        /// Whether an aligner failure's output says the venv cannot import what the aligner needs,
        /// the runtime signature of an install that never finished.
        /// </summary>
        /// <remarks>
        /// The aligner's own <c>WARNING:</c> lines are skipped: each names something it survived,
        /// and since version 10 one of them is the fused evidence path falling back for want of an
        /// OPTIONAL package ("a dependency is missing (No module named 'phonemizer')"). Read as a
        /// broken install, a later unrelated failure with that line still in the tail would withdraw
        /// the sentinel of a venv whose required imports load fine. Its <c>fallback:</c> lines are
        /// skipped for the same reason: each reports a broad handler carrying on without something.
        /// </remarks>
        public static bool IsMissingPackageFailure(string output)
            => output.Split(new[] { " | ", "\n" }, StringSplitOptions.None)
                     .Where(line => !aligner_warning_line.IsMatch(line))
                     .Any(line => line.Contains("ModuleNotFoundError", StringComparison.Ordinal)
                                  || line.Contains("No module named", StringComparison.Ordinal)
                                  || line.Contains("ImportError", StringComparison.Ordinal));

        /// <summary>An aligner log line reporting what it survived: an optional "[hh:mm:ss] " then "WARNING:", or a "fallback:" line.</summary>
        private static readonly Regex aligner_warning_line = new Regex(@"^\s*((\[\d{1,2}:\d{2}:\d{2}\]\s*)?WARNING:|fallback:)", RegexOptions.Compiled);

        /// <summary>
        /// What a player is told to do about a venv that cannot import its packages. A bare clause,
        /// wrapped by the import surfaces like every other error.
        /// </summary>
        public const string REPAIR_SUGGESTION = "the local auto-aligner's install is incomplete (its Python packages do not load). "
                                                + "Repair it in Settings > Experimental > Repair local auto-aligner";

        public static bool IsLyricLabDir(string dir)
            => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, aligner_script));

        private static readonly Regex aligner_version_line = new Regex(@"^ALIGNER_VERSION\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// The <c>ALIGNER_VERSION = "..."</c> the aligner script in <paramref name="lyricLabDir"/>
        /// declares, read off the source text so no Python has to run. Null for a directory with no
        /// script and for a version-1 script, which predates the constant.
        /// </summary>
        public static string? ReadAlignerVersion(string? lyricLabDir)
        {
            if (string.IsNullOrEmpty(lyricLabDir))
                return null;

            try
            {
                string path = Path.Combine(lyricLabDir, aligner_script);

                if (!File.Exists(path))
                    return null;

                var match = aligner_version_line.Match(File.ReadAllText(path));
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Every python we launch (the aligner, the import probe, the setup script's children) runs
        /// in UTF-8. Python on Windows otherwise writes stdout in the console code page, cp1252 on a
        /// Western install, and the first non-Latin character it prints raises UnicodeEncodeError:
        /// demucs prints "Separating track {path}", so a Japanese or Chinese song title killed the
        /// separation before it started and the import reported the aligner as unavailable (a user
        /// log, 2026-10-02). PYTHONUTF8 covers the child processes python spawns too, which is where
        /// demucs runs; the reader encodings match so the log tail is readable rather than mojibake.
        /// Set only on a redirected start info (the encodings are invalid otherwise).
        /// </summary>
        public static void RunPythonInUtf8(ProcessStartInfo psi)
        {
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
        }

        public static string PythonExeFor(string lyricLabDir)
            => OperatingSystem.IsWindows()
                ? Path.Combine(lyricLabDir, ".venv", "Scripts", "python.exe")
                : Path.Combine(lyricLabDir, ".venv", "bin", "python");

        /// <summary>
        /// What the player sees when the setup script fails. Both install surfaces wrap it
        /// ("Install failed: ...", "Aligner install failed: ..."), so it is a bare clause.
        /// </summary>
        private const string setup_failed_message = "the aligner setup could not finish (check your internet connection and retry; the details are in the game log)";

        private const string setup_failed_prefix = "setup failed: ";

        /// <summary>
        /// The plain sentence setup.ps1 / setup.sh end a failed run with ("setup failed: WHAT;
        /// ADVICE."), as a bare clause for the install surfaces to wrap: the prefix and the trailing
        /// period dropped. It says what actually failed and what to do about it (close whatever holds
        /// the folder, install uv yourself), which the generic <see cref="setup_failed_message"/>
        /// cannot. Null when the output tail has no such line (an older script, or one that died
        /// before it could say anything), so the caller falls back to the generic text. The raw
        /// "detail:" lines after the sentence stay in the log only.
        /// </summary>
        /// <param name="tail">The last output lines joined by " | ", as RunProcessAsync returns them.</param>
        public static string? SetupFailureSentence(string tail)
        {
            string? line = tail.Split(" | ").Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(setup_failed_prefix, StringComparison.Ordinal));

            if (line == null)
                return null;

            string sentence = line.Substring(setup_failed_prefix.Length).TrimEnd().TrimEnd('.').Trim();
            return sentence.Length > 0 ? sentence : null;
        }

        /// <summary>
        /// One-time environment bootstrap: runs the component's setup script (venv + pinned packages,
        /// a multi-GB first-time download). No-op when a COMPLETED install exists (venv python plus
        /// the setup sentinel). Not auto-invoked by <see cref="LyricMapImporter.BuildOszAsync"/> (which prefers the
        /// instant LRC fallback); exposed for an explicit "set up aligner" action.
        ///
        /// <para>REPAIR (backlog 353). A venv WITHOUT the sentinel is either a setup that never
        /// finished or a healthy install from before the sentinel existed. It is probed first
        /// (<see cref="TryAdoptEnvironmentAsync"/>): when its imports load it is adopted as it stands,
        /// in seconds, and when they do not it is DELETED and the setup re-run from scratch, which is
        /// how a half-broken install heals on one click.</para>
        ///
        /// <para>UPDATE (backlog 411). With <paramref name="updateExisting"/>, a completed (or just
        /// adopted) install is not left as it stands: the setup script runs in its update mode
        /// (<c>-Update</c> / <c>--update</c>), which installs <see cref="ALIGNER_PACKAGES"/> and then
        /// <see cref="FUSED_EVIDENCE_PACKAGES"/> into the existing venv (torch is not re-run, what
        /// is already satisfied is left alone) and fetches the fused-evidence weights. Without it the Update button would copy a version 10 script
        /// over a venv that never got phonemizer and espeakng-loader, and every import would
        /// silently run the version 9 path. The update is best effort: a failure is logged and
        /// said, never returned as one, since the install still works (on that version 9 path).</para>
        /// </summary>
        public static async Task<LyricImportResult> BootstrapEnvironmentAsync(string lyricLabDir, Action<string> progress, CancellationToken token, string device = "cpu",
                                                                              bool updateExisting = false)
        {
            if (EnvironmentReady(lyricLabDir))
                return updateExisting ? await updateEnvironmentAsync(lyricLabDir, progress, token, device).ConfigureAwait(false) : LyricImportResult.Ok(string.Empty);

            if (EnvironmentPresent(lyricLabDir))
            {
                progress("checking the existing aligner environment...");

                if (await TryAdoptEnvironmentAsync(lyricLabDir, token).ConfigureAwait(false))
                {
                    if (updateExisting)
                        return await updateEnvironmentAsync(lyricLabDir, progress, token, device).ConfigureAwait(false);

                    progress("aligner environment ready");
                    return LyricImportResult.Ok(string.Empty);
                }

                if (token.IsCancellationRequested)
                    return LyricImportResult.Fail("environment setup cancelled");
            }

            string script = Path.Combine(lyricLabDir, SetupScriptName);

            if (!File.Exists(script))
                return LyricImportResult.Fail($"aligner environment missing and no {SetupScriptName} to build it in {lyricLabDir}");

            // Only now, with a script to rebuild it, does a broken venv go.
            string venv = Path.Combine(lyricLabDir, ".venv");

            if (Directory.Exists(venv))
            {
                progress("the existing aligner environment is incomplete, rebuilding it...");

                try
                {
                    Directory.Delete(venv, recursive: true);
                }
                catch (Exception e)
                {
                    Logger.Log($"Could not remove the incomplete aligner environment at {venv}: {e}", LoggingTarget.Runtime, LogLevel.Important);
                    return LyricImportResult.Fail("the old aligner environment could not be removed (close anything that might be using it and retry; the details are in the game log)");
                }
            }

            progress(device == "cuda"
                ? "setting up the aligner environment (GPU), one-time download of packages (~2.5 GB), please wait..."
                : "setting up the aligner environment, one-time download of packages (~2 GB), please wait...");

            (int exitCode, string tail) = await ImportProcessRunner.RunProcessAsync(setupStartInfo(lyricLabDir, script, device, update: false), progress, token).ConfigureAwait(false);

            if (exitCode == ImportProcessRunner.CANCELLED_EXIT_CODE)
                return LyricImportResult.Fail("environment setup cancelled");

            // The script's raw output (a PowerShell error record, a uv or pip traceback) is for the
            // log, not the player: they get one plain sentence and the log keeps the detail. The
            // sentence is the script's own when it wrote one (see SetupFailureSentence), else ours.
            if (exitCode != 0)
            {
                Logger.Log($"Aligner environment setup exited with code {exitCode}: {tail}", LoggingTarget.Runtime, LogLevel.Important);
                return LyricImportResult.Fail(SetupFailureSentence(tail) ?? setup_failed_message);
            }

            if (!EnvironmentReady(lyricLabDir))
            {
                Logger.Log($"Aligner environment setup exited 0 but left no venv python and setup sentinel at {SetupSentinelFor(lyricLabDir)}: {tail}", LoggingTarget.Runtime,
                    LogLevel.Important);
                return LyricImportResult.Fail(setup_failed_message);
            }

            // Record which torch flavour this environment carries so alignment runs pick the device.
            try
            {
                await File.WriteAllTextAsync(Path.Combine(lyricLabDir, DEVICE_MARKER_FILE), device, token).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal: alignment falls back to CPU without the marker.
            }

            progress("aligner environment ready");
            return LyricImportResult.Ok(string.Empty);
        }

        /// <summary>
        /// Said when the update mode of the setup could not bring a completed install up to date
        /// (no connection, no wheel for this platform). The install still works: the aligner runs
        /// its version 9 path without the fused-evidence packages, and pressing the button again
        /// retries the update.
        /// </summary>
        public const string ENVIRONMENT_UPDATE_INCOMPLETE = "the aligner's newer packages could not be installed, so imports keep the previous accuracy until they are "
                                                            + "(press Reinstall local auto-aligner in Settings to retry; the details are in the game log)";

        /// <summary>
        /// The update half of <see cref="BootstrapEnvironmentAsync"/>: runs the setup script in its
        /// update mode on a completed install. Never fails the install over it (see
        /// <see cref="ENVIRONMENT_UPDATE_INCOMPLETE"/>); only a cancellation is returned as one.
        /// </summary>
        private static async Task<LyricImportResult> updateEnvironmentAsync(string lyricLabDir, Action<string> progress, CancellationToken token, string device)
        {
            string script = Path.Combine(lyricLabDir, SetupScriptName);

            if (!File.Exists(script))
            {
                Logger.Log($"Aligner environment kept as it is: no {SetupScriptName} in {lyricLabDir} to update it with", LoggingTarget.Runtime, LogLevel.Important);
                progress("aligner environment ready");
                return LyricImportResult.Ok(string.Empty);
            }

            progress("updating the aligner's packages in the existing environment (a short download)...");

            (int exitCode, string tail) = await ImportProcessRunner.RunProcessAsync(setupStartInfo(lyricLabDir, script, device, update: true), progress, token).ConfigureAwait(false);

            if (exitCode == ImportProcessRunner.CANCELLED_EXIT_CODE)
                return LyricImportResult.Fail("environment setup cancelled");

            if (exitCode != 0)
            {
                Logger.Log($"Aligner environment update exited with code {exitCode}, the environment is kept as it was: {tail}", LoggingTarget.Runtime, LogLevel.Important);
                progress(ENVIRONMENT_UPDATE_INCOMPLETE);
                return LyricImportResult.Ok(string.Empty);
            }

            progress("aligner environment ready");
            return LyricImportResult.Ok(string.Empty);
        }

        /// <summary>
        /// The setup script's process: powershell on Windows, bash elsewhere, with the torch
        /// flavour and, for <paramref name="update"/>, the script's update switch.
        /// </summary>
        private static ProcessStartInfo setupStartInfo(string lyricLabDir, string script, string device, bool update)
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = lyricLabDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            RunPythonInUtf8(psi);

            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add("-Device");
                psi.ArgumentList.Add(device);

                if (update)
                    psi.ArgumentList.Add("-Update");
            }
            else
            {
                // POSIX shells run the .sh bootstrap directly. Invoking through "bash" (rather than
                // executing the script path) means we don't depend on the executable bit surviving a
                // checkout or zip extraction.
                psi.FileName = "bash";
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(device);

                if (update)
                    psi.ArgumentList.Add("--update");
            }

            return psi;
        }

        /// <summary>
        /// Why the aligner in <paramref name="lyricLabDir"/> must not run under this build, as the
        /// import failure that says so (both versions, and where to update); null when its
        /// <c>ALIGNER_VERSION</c> is <paramref name="requiredAlignerVersion"/>, or when no version is
        /// required. ANY mismatch refuses, older or newer (<see cref="LocalAlignerVersion.Compare"/>).
        /// </summary>
        public static string? AlignerVersionRefusal(string lyricLabDir, string? requiredAlignerVersion)
        {
            string? installed = ReadAlignerVersion(lyricLabDir);
            return LocalAlignerVersion.ImportRefusal(LocalAlignerVersion.Compare(installed, requiredAlignerVersion), installed, requiredAlignerVersion);
        }

        /// <summary>
        /// The result of an aligner run that exited non-zero. "The venv exists but python cannot
        /// import torch" means the install is demonstrably broken whatever its sentinel says: the
        /// sentinel is withdrawn so Settings offers Repair (and the next import probes instead of
        /// trusting it), and the player is told that Repair is the fix. Any other failure keeps
        /// its exit code and output tail, as before.
        /// </summary>
        public static LyricImportResult AlignerFailure(string lyricLabDir, int exitCode, string tail)
        {
            if (!IsMissingPackageFailure(tail))
                return LyricImportResult.Fail($"aligner exited with code {exitCode}: {tail}");

            Logger.Log($"Aligner could not import its packages (exit code {exitCode}), marking the environment for repair: {tail}", LoggingTarget.Runtime, LogLevel.Important);

            try
            {
                File.Delete(SetupSentinelFor(lyricLabDir));
            }
            catch
            {
                // Best effort: the message still names the fix.
            }

            return LyricImportResult.Fail(REPAIR_SUGGESTION);
        }

        /// <summary>
        /// The command line handed to the venv's python for one aligner run, script first. Built
        /// apart from <see cref="LyricMapImporter.runAlignerAsync"/> so a test can read exactly what the aligner would
        /// be asked to do without a python to run it.
        ///
        /// <para><paramref name="highQuality"/> adds <c>--quality full</c>, the tier that listens to
        /// the song eight times instead of twice, but only when the script in
        /// <paramref name="lyricLabDir"/> is version 6 or newer (<see cref="AlignerHasQualityTiers"/>).
        /// An older script rejects the unknown option with exit code 2, which would cost the import
        /// its word timing, and a player's installed copy stays at its old version until they take
        /// the offered update. Off, NOTHING is added rather than <c>--quality fast</c>: the script's
        /// own default is fast, and leaving the flag out keeps the ordinary run's command line
        /// exactly what an older script already accepts.</para>
        ///
        /// <para><paramref name="vocalMode"/> <see cref="AlignerVocalMode.Estimated"/> adds
        /// <c>--vocal-mode estimated</c> on the same terms: only when the script is version 7 or newer
        /// (<see cref="AlignerHasVocalModes"/>), and only when the lyrics carry a line stamp, since the
        /// mode paces every line from its stamp (the script would ignore it on bare text anyway, and
        /// leaving it out keeps that command line the one every version accepts).
        /// <see cref="AlignerVocalMode.Aligned"/> adds nothing.</para>
        ///
        /// <para>Every run of a version 10 or newer script (<see cref="AlignerHasFusedEvidence"/>)
        /// adds <c>--evidence fused</c> (backlog 411, the owner's call: on for every English import,
        /// not behind a setting) and, when the import has one, <c>--lyrics-language</c> with its
        /// canonical language (<paramref name="language"/>, "english", "japanese", ...). The aligner
        /// decides per song: only English takes the fused path, and anything else (another
        /// language, estimated vocals, the packages or weights missing) runs its version 9 path and
        /// says why. Without a language the aligner's own conservative detector decides. An older
        /// script gets neither flag, which it would reject.</para>
        /// </summary>
        public static IReadOnlyList<string> AlignerArguments(
            string lyricLabDir, string audioPath, string lyricsPath, string outDir, string lyricsContent, bool highQuality,
            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned, string? language = null)
        {
            var args = new List<string> { aligner_script, audioPath, lyricsPath, "-o", outDir };

            addDevice(args, lyricLabDir);

            // Explicit either way: "ref" as soon as ONE line is stamped (sparse anchors place the
            // unstamped lines inside their section's window), "auto" only for bare text.
            args.Add("--anchors");
            args.Add(LyricMapImporter.AlignerAnchorMode(lyricsContent));

            if (highQuality && AlignerHasQualityTiers(lyricLabDir))
            {
                args.Add("--quality");
                args.Add("full");
            }

            if (EstimatedVocalsApply(lyricLabDir, lyricsContent, vocalMode))
            {
                args.Add("--vocal-mode");
                args.Add("estimated");
            }

            if (AlignerHasFusedEvidence(lyricLabDir))
            {
                args.Add("--evidence");
                args.Add("fused");

                string? lyricsLanguage = AlignerLyricsLanguage(language);

                if (lyricsLanguage != null)
                {
                    args.Add("--lyrics-language");
                    args.Add(lyricsLanguage);
                }
            }

            return args;
        }

        /// <summary>
        /// Environments built with CUDA torch (device marker "cuda") run on the GPU, dramatically
        /// faster separation/emission on machines with a good NVIDIA card.
        /// </summary>
        private static void addDevice(List<string> args, string lyricLabDir)
        {
            string deviceMarker = Path.Combine(lyricLabDir, DEVICE_MARKER_FILE);

            if (File.Exists(deviceMarker) && File.ReadAllText(deviceMarker).Trim().Equals("cuda", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--device");
                args.Add("cuda");
            }
        }

        /// <summary>
        /// The command line of one <c>--separate-only</c> run (backlog 414), script first, as
        /// <see cref="AlignerArguments"/> is for an alignment: the audio, the flag, the output dir the
        /// stem is written to, and the device an alignment run would use. No lyrics, quality, vocal
        /// mode or evidence: the separation is the same for all of them, and it is cached in the
        /// aligner's work dir under the names an alignment of the same song uses.
        /// </summary>
        public static IReadOnlyList<string> SeparateOnlyArguments(string lyricLabDir, string audioPath, string outDir)
        {
            var args = new List<string> { aligner_script, audioPath, "--separate-only", "-o", outDir };
            addDevice(args, lyricLabDir);
            return args;
        }

        /// <summary>
        /// The first aligner version with <c>--separate-only</c> (backlog 414). Version 11 never
        /// shipped before the flag did, so every version 11 install carries it.
        /// </summary>
        public const int SEPARATE_ONLY_ALIGNER_VERSION = 11;

        /// <summary>
        /// Whether the aligner script in <paramref name="lyricLabDir"/> accepts <c>--separate-only</c>,
        /// read off its version exactly as <see cref="AlignerHasQualityTiers"/> reads it.
        /// </summary>
        public static bool AlignerSeparatesOnly(string lyricLabDir) => alignerVersionAtLeast(lyricLabDir, SEPARATE_ONLY_ALIGNER_VERSION);

        /// <summary>
        /// The <c>--lyrics-language</c> value for an import's language: its canonical lowercase name,
        /// or null (no flag, the aligner's detector decides) when there is none.
        /// </summary>
        public static string? AlignerLyricsLanguage(string? language)
            => string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();

        /// <summary>
        /// The first aligner version with the fused evidence path (<c>--evidence</c>,
        /// <c>--lyrics-language</c>; backlog 411).
        /// </summary>
        public const int FUSED_EVIDENCE_ALIGNER_VERSION = 10;

        /// <summary>
        /// Whether the aligner script in <paramref name="lyricLabDir"/> accepts <c>--evidence</c> and
        /// <c>--lyrics-language</c>, read off its version exactly as <see cref="AlignerHasQualityTiers"/>
        /// reads it.
        /// </summary>
        public static bool AlignerHasFusedEvidence(string lyricLabDir) => alignerVersionAtLeast(lyricLabDir, FUSED_EVIDENCE_ALIGNER_VERSION);

        /// <summary>
        /// Whether a run asked for <paramref name="vocalMode"/> actually passes
        /// <c>--vocal-mode estimated</c>: the mode is <see cref="AlignerVocalMode.Estimated"/>, the
        /// lyrics carry at least one line stamp, and the script accepts the flag.
        /// </summary>
        public static bool EstimatedVocalsApply(string lyricLabDir, string lyricsContent, AlignerVocalMode vocalMode)
            => vocalMode == AlignerVocalMode.Estimated && LyricMapImporter.HasAnyLineStamp(lyricsContent) && AlignerHasVocalModes(lyricLabDir);

        /// <summary>The first aligner version with evidence tiers (<c>--quality</c>).</summary>
        public const int QUALITY_TIERS_ALIGNER_VERSION = 6;

        /// <summary>The first aligner version with <c>--vocal-mode</c> (backlog 354).</summary>
        public const int VOCAL_MODE_ALIGNER_VERSION = 7;

        /// <summary>
        /// The first aligner version that WRITES the isolated vocals stem into its output dir as
        /// <c>vocals.wav</c> (backlog 392). Read off the script's version like
        /// <see cref="AlignerHasQualityTiers"/>: an older installed copy persists nothing, so the
        /// importer finds no stem and packages the map without one, exactly as a map had before.
        /// </summary>
        public const int PERSISTS_VOCALS_STEM_ALIGNER_VERSION = 9;

        /// <summary>
        /// Whether the aligner script in <paramref name="lyricLabDir"/> writes the vocals stem into
        /// its output dir on a run (see <see cref="PERSISTS_VOCALS_STEM_ALIGNER_VERSION"/>).
        /// </summary>
        public static bool AlignerPersistsVocalsStem(string lyricLabDir) => alignerVersionAtLeast(lyricLabDir, PERSISTS_VOCALS_STEM_ALIGNER_VERSION);

        /// <summary>
        /// Whether the aligner script in <paramref name="lyricLabDir"/> accepts <c>--vocal-mode</c>,
        /// read off its version exactly as <see cref="AlignerHasQualityTiers"/> reads it.
        /// </summary>
        public static bool AlignerHasVocalModes(string lyricLabDir) => alignerVersionAtLeast(lyricLabDir, VOCAL_MODE_ALIGNER_VERSION);

        private static bool alignerVersionAtLeast(string lyricLabDir, int minimum)
            => int.TryParse(ReadAlignerVersion(lyricLabDir), NumberStyles.None, CultureInfo.InvariantCulture, out int version)
               && version >= minimum;

        /// <summary>
        /// Said when estimated vocals are chosen but the installed aligner predates them. Worded, like
        /// <see cref="HIGH_QUALITY_NEEDS_UPDATE"/>, to claim no stage in ImportProgressParser.
        /// </summary>
        public const string ESTIMATED_VOCALS_NEEDS_UPDATE = "estimated vocals need a newer install, update it in Settings > Experimental; "
                                                            + "this import times the words from the audio";

        /// <summary>Said when estimated vocals are chosen for lyrics with no line stamp to pace from.</summary>
        public const string ESTIMATED_VOCALS_NEED_STAMPS = "estimated vocals need [mm:ss.xx] line stamps to pace from; "
                                                           + "this import times the words from the audio";

        /// <summary>Said when estimated vocals are on for a run.</summary>
        public const string ESTIMATED_VOCALS_ON = "estimated vocals on, every line is paced evenly from its stamp";

        /// <summary>
        /// The progress line an aligner run asked for <paramref name="vocalMode"/> says up front: null
        /// for <see cref="AlignerVocalMode.Aligned"/>, else whether estimated vocals are on or why this
        /// run cannot honour them (no line stamp, checked first since an update would not help; an
        /// install older than <see cref="VOCAL_MODE_ALIGNER_VERSION"/>).
        /// </summary>
        public static string? EstimatedVocalsNotice(string lyricLabDir, string lyricsContent, AlignerVocalMode vocalMode)
        {
            if (vocalMode != AlignerVocalMode.Estimated)
                return null;

            if (!LyricMapImporter.HasAnyLineStamp(lyricsContent))
                return ESTIMATED_VOCALS_NEED_STAMPS;

            if (!AlignerHasVocalModes(lyricLabDir))
            {
                Logger.Log($"Estimated vocals are chosen but the aligner in {lyricLabDir} is version {ReadAlignerVersion(lyricLabDir) ?? "1"}, "
                           + "which has no --vocal-mode; running its default", LoggingTarget.Runtime, LogLevel.Important);
                return ESTIMATED_VOCALS_NEEDS_UPDATE;
            }

            return ESTIMATED_VOCALS_ON;
        }

        /// <summary>
        /// Whether the aligner script in <paramref name="lyricLabDir"/> accepts <c>--quality</c>:
        /// its <see cref="ReadAlignerVersion"/> is a whole number of at least
        /// <see cref="QUALITY_TIERS_ALIGNER_VERSION"/>. False for anything unreadable, so an
        /// unknown script gets the command line every version accepts.
        /// </summary>
        public static bool AlignerHasQualityTiers(string lyricLabDir) => alignerVersionAtLeast(lyricLabDir, QUALITY_TIERS_ALIGNER_VERSION);

        /// <summary>
        /// Said when the high-accuracy setting is on but the installed aligner predates it. Worded
        /// to claim no stage in ImportProgressParser (no "align", so not "aligner" either; no
        /// "anchor", "model" or "unavailable"), so the display holds where it is.
        /// </summary>
        public const string HIGH_QUALITY_NEEDS_UPDATE = "high-accuracy mode needs a newer install, update it in Settings > Experimental; "
                                                        + "this import runs at the normal speed";
    }
}
