// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// DI adapter bridging the shell's <see cref="ILyricMapImporter"/> seam to the static
    /// <see cref="LyricMapImporter"/> core. Owns the concerns the core cannot reach on its own: the
    /// ruleset-scoped <see cref="TypeBeatRulesetSetting.LyricLabPath"/> override, the game's
    /// runtime location (start directories for aligner discovery), and the API session behind the
    /// server aligner (<see cref="RemoteAlignClient"/>). A <see cref="Component"/> so it can resolve
    /// the ruleset config cache; typebeat.Desktop caches it and adds it to the hierarchy.
    ///
    /// <para>The server aligner (backlog 413) is offered to an import only when the import screen's
    /// opt-in asks for it, in a dev build and a deployed one alike: it is the player's choice now,
    /// not a fallback the build decides on. Otherwise the ladder is the LOCAL aligner, used whenever
    /// it is installed, then the LRC line stamps. The editor's re-align stays local-only.</para>
    ///
    /// Also implements <see cref="ILocalAlignerManager"/>: installing the local auto-aligner into
    /// the game's DATA directory (so its multi-GB environment survives Velopack updates, which
    /// replace the application directory wholesale).
    /// </summary>
    public partial class LyricMapImportService : Component, ILyricMapImporter, ILocalAlignerManager
    {
        /// <summary>The managed install folder inside the game's data directory.</summary>
        public const string INSTALL_FOLDER_NAME = "lyriclab";

        /// <summary>Component files copied from the shipped/dev checkout into the managed install.</summary>
        private static readonly string[] component_files = { "align_lyrics.py", "setup.ps1", "setup.sh", "debug_decode.py", "README.md" };

        [Resolved(CanBeNull = true)]
        private IRulesetConfigCache? configCache { get; set; }

        [Resolved(CanBeNull = true)]
        private Storage? storage { get; set; }

        [Resolved(CanBeNull = true)]
        private IAPIProvider? api { get; set; }

        public (string Artist, string Title) GuessArtistTitle(string audioPath) => LyricMapImporter.GuessArtistTitle(audioPath);

        public Task<LyricImportResult> BuildOszAsync(
            string audioPath, string? lyricsPath, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment, BeatmapLanguage language,
            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned, bool useServerAligner = false, bool isolateVocals = false)
            => language == BeatmapLanguage.Unspecified
                ? Task.FromResult(LyricImportResult.Fail("select a language before importing"))
                : LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, artist, title, effectiveConfiguredPath(), startDirectories(), progress, token,
                    useAutomaticAlignment, language: language.ToCanonicalName(), highQualityAlignment: HighQualityAlignment(config()), vocalMode: vocalMode,
                    requiredAlignerVersion: ShippedVersion, remoteAlign: useServerAligner ? ServerAligner(api) : null, isolateVocals: isolateVocals);

        public Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true, string? language = null,
            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned)
            => LyricMapImporter.ProduceTimingJsonAsync(audioPath, lyricsContent, artist, title, effectiveConfiguredPath(), startDirectories(), progress, token, useAutomaticAlignment,
                language, HighQualityAlignment(config()), vocalMode, requiredAlignerVersion: ShippedVersion);

        public Bindable<bool>? ServerAlignerPreference => config()?.GetBindable<bool>(TypeBeatRulesetSetting.UseServerAligner);

        public Bindable<bool>? IsolateVocalsPreference => config()?.GetBindable<bool>(TypeBeatRulesetSetting.IsolateVocalsOnImport);

        /// <summary>
        /// The server rung for an import that opted in (backlog 413). Never null once asked for: with
        /// no API session to send it on the job fails with the sign-in message, which is the truth,
        /// rather than the import quietly running locally instead. Static over an explicit provider
        /// so a test can drive it.
        /// </summary>
        internal static RemoteAligner ServerAligner(IAPIProvider? api)
            => (request, progress, token) => api == null
                ? Task.FromResult(RemoteAlignOutcome.Fail(RemoteAlignClient.SIGN_IN_REQUIRED))
                : RemoteAlignClient.AlignAsync(api, request, progress, token);

        /// <summary>
        /// Whether an import runs the aligner at its full tier. Read at the start of each import,
        /// so a change in Settings applies to the next one. Off when there is no config to read: a
        /// missing config must never make every import four times slower. Static over an explicit config so a test
        /// can pin the setting's effect without resolving this component.
        /// </summary>
        internal static bool HighQualityAlignment(TypeBeatRulesetConfigManager? config)
            => config?.Get<bool>(TypeBeatRulesetSetting.LocalAlignerHighQuality) ?? false;

        private TypeBeatRulesetConfigManager? config()
        {
            try
            {
                return configCache?.GetConfigFor(new TypeBeatRuleset()) as TypeBeatRulesetConfigManager;
            }
            catch
            {
                // Config unavailable (cache not loaded / ruleset unregistered); discovery covers it.
                return null;
            }
        }

        /// <summary>
        /// The configured lyriclab path for import runs: the explicit setting, else the managed
        /// install when it exists, else null (discovery then walks up from the start directories).
        /// There is no switch in front of it any more (backlog 413 deleted the hidden "use local
        /// auto-aligner" one): an installed aligner is used unless the import opted in to the server.
        /// </summary>
        private string? effectiveConfiguredPath()
        {
            string? configured = config()?.Get<string>(TypeBeatRulesetSetting.LyricLabPath);

            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            // No explicit path: prefer the managed install when it exists.
            string? managed = managedInstallDir();
            return managed != null && LyricMapImporter.IsLyricLabDir(managed) ? managed : null;
        }

        /// <summary>
        /// Where directory discovery starts walking up from: next to the running assembly (deployed
        /// builds ship the lyriclab component beside the executable) and the process working
        /// directory (a dev `dotnet run` from repo root has lyriclab/ a few levels up).
        /// </summary>
        private static IEnumerable<string> startDirectories()
        {
            yield return AppContext.BaseDirectory;
            yield return Environment.CurrentDirectory;
        }

        // ---------------------------------------------------------------------------------------------
        // ILocalAlignerManager
        // ---------------------------------------------------------------------------------------------

        private string? managedInstallDir()
        {
            try
            {
                return storage?.GetFullPath(INSTALL_FOLDER_NAME);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The directory an alignment run would actually use, or null.</summary>
        private string? resolvedAlignerDir()
            => LyricMapImporter.ResolveLyricLabDir(effectiveConfiguredPath(), startDirectories());

        public bool IsInstalled
        {
            get
            {
                string? dir = resolvedAlignerDir();
                return dir != null && LyricMapImporter.EnvironmentReady(dir);
            }
        }

        public bool NeedsRepair
        {
            get
            {
                string? dir = resolvedAlignerDir();
                return dir != null && LyricMapImporter.EnvironmentNeedsRepair(dir);
            }
        }

        public string? InstalledDevice
        {
            get
            {
                string? dir = resolvedAlignerDir();

                if (dir == null || !LyricMapImporter.EnvironmentReady(dir))
                    return null;

                try
                {
                    string marker = Path.Combine(dir, LyricMapImporter.DEVICE_MARKER_FILE);
                    return File.Exists(marker) ? File.ReadAllText(marker).Trim().ToLowerInvariant() : "cpu";
                }
                catch
                {
                    return "cpu";
                }
            }
        }

        private bool? gpuDetected;

        public bool GpuDetected => gpuDetected ??= detectNvidiaGpu();

        public string? InstalledVersion => IsInstalled ? LyricMapImporter.ReadAlignerVersion(resolvedAlignerDir()) : null;

        /// <summary>
        /// The version both import entry points require of the aligner they would run (backlog 410):
        /// any other is refused by the importer itself, so no caller, the import screen, the editor's
        /// re-align or one yet to be written, can time a map with an aligner this build was not made
        /// for. <see cref="ILocalAlignerManager.VersionStatus"/> reads the same pair through the same
        /// rule for the surfaces that say so in advance.
        /// </summary>
        public string? ShippedVersion => LyricMapImporter.ReadAlignerVersion(LyricMapImporter.ResolveLyricLabDir(null, startDirectories()));

        /// <summary>
        /// Best-effort NVIDIA detection: nvidia-smi ships with the driver and is on PATH on any
        /// machine with a working NVIDIA card. Absence (or failure) simply means the CPU flavour.
        /// </summary>
        private static bool detectNvidiaGpu()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "-L",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (process == null)
                    return false;

                string output = process.StandardOutput.ReadToEnd();

                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(); }
                    catch { }

                    return false;
                }

                return process.ExitCode == 0 && output.Contains("GPU", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public async Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token)
        {
            string? target = managedInstallDir();

            if (target == null)
                return LyricImportResult.Fail("no game storage available to install into");

            // Source of the component scripts: the copy shipped beside the executable (deployed
            // builds) or a dev checkout found by discovery. The managed install itself also counts
            // (repair-after-update with the shipped copy still preferred for freshness).
            string? source = LyricMapImporter.ResolveLyricLabDir(null, startDirectories());

            if (source == null && !LyricMapImporter.IsLyricLabDir(target))
                return LyricImportResult.Fail("this build did not ship the aligner component, update the game and try again");

            try
            {
                Directory.CreateDirectory(target);

                if (source != null && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    progress("copying aligner component...");

                    foreach (string name in component_files)
                    {
                        string from = Path.Combine(source, name);

                        if (File.Exists(from))
                            File.Copy(from, Path.Combine(target, name), overwrite: true);
                    }

                    // A reinstall is how an existing install picks up a newer aligner, so the OLD
                    // one has to go with it: its cached model output (work/) was produced by the
                    // previous script and would be trusted by the new one, and its per-import
                    // outputs (out/) are stale. The environment (.venv) is the multi-GB part and
                    // is deliberately kept; the device marker stays with it.
                    foreach (string stale in new[] { "work", "out" })
                    {
                        string dir = Path.Combine(target, stale);

                        if (Directory.Exists(dir))
                        {
                            progress($"clearing the previous aligner's {stale}/ cache...");
                            Directory.Delete(dir, recursive: true);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                return LyricImportResult.Fail($"could not copy the aligner component: {e.Message}");
            }

            string device = GpuDetected ? "cuda" : "cpu";

            // A previously built environment of the other torch flavour must be rebuilt; the venv
            // pins CPU or CUDA wheels at install time. Checked on the venv's PRESENCE, not on a
            // completed setup, so a sentinel-less venv of the wrong flavour is rebuilt rather than
            // adopted by the repair probe.
            try
            {
                string marker = Path.Combine(target, LyricMapImporter.DEVICE_MARKER_FILE);

                if (LyricMapImporter.EnvironmentPresent(target) && File.Exists(marker)
                    && !File.ReadAllText(marker).Trim().Equals(device, StringComparison.OrdinalIgnoreCase))
                {
                    progress("switching aligner device flavour, rebuilding the environment...");
                    Directory.Delete(Path.Combine(target, ".venv"), recursive: true);
                }
            }
            catch
            {
                // Best effort; bootstrap will report anything fatal.
            }

            progress(device == "cuda"
                ? "NVIDIA GPU detected, installing the GPU aligner"
                : "no NVIDIA GPU detected, installing the CPU aligner");

            // An existing environment is UPDATED rather than trusted as it stands (backlog 411): the
            // scripts just copied may need packages the venv does not have yet (version 10's
            // fused-evidence pair), and only the setup's update mode installs them.
            var result = await LyricMapImporter.BootstrapEnvironmentAsync(target, progress, token, device, updateExisting: true).ConfigureAwait(false);

            if (!result.Success)
                return result;

            // Point the importer at the managed install.
            config()?.SetValue(TypeBeatRulesetSetting.LyricLabPath, target);

            return result;
        }
    }
}
