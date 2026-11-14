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
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Logging;
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
    /// replace the application directory wholesale), and, since aligner version 12, local DEMUCS
    /// beside it in a folder of its own (<see cref="DEMUCS_FOLDER_NAME"/>), which the editor's vocals
    /// waveform and an import's isolated vocals run.
    /// </summary>
    public partial class LyricMapImportService : Component, ILyricMapImporter, ILocalAlignerManager
    {
        /// <summary>The managed install folder inside the game's data directory.</summary>
        public const string INSTALL_FOLDER_NAME = "lyriclab";

        /// <summary>The managed Demucs install's folder inside the game's data directory: the same scripts, its own environment.</summary>
        public const string DEMUCS_FOLDER_NAME = "demucs";

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
                    requiredAlignerVersion: ShippedVersion, remoteAlign: useServerAligner ? ServerAligner(api) : null, isolateVocals: isolateVocals,
                    demucsDir: isolateVocals ? demucsDirForRun() : null);

        public Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true, string? language = null,
            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned, bool allowLineFallback = true)
            => LyricMapImporter.ProduceTimingJsonAsync(audioPath, lyricsContent, artist, title, effectiveConfiguredPath(), startDirectories(), progress, token, useAutomaticAlignment,
                language, HighQualityAlignment(config()), vocalMode, requiredAlignerVersion: ShippedVersion, allowLineFallback: allowLineFallback);

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
            return managed != null && AlignerEnvironment.IsLyricLabDir(managed) ? managed : null;
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

        private string? managedInstallDir() => managedDir(INSTALL_FOLDER_NAME);

        private string? managedDemucsDir() => managedDir(DEMUCS_FOLDER_NAME);

        private string? managedDir(string name)
        {
            try
            {
                return storage?.GetFullPath(name);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The directory an alignment run would actually use, or null.</summary>
        private string? resolvedAlignerDir()
            => AlignerEnvironment.ResolveLyricLabDir(effectiveConfiguredPath(), startDirectories());

        public bool IsInstalled
        {
            get
            {
                string? dir = resolvedAlignerDir();
                return dir != null && AlignerEnvironment.EnvironmentReady(dir);
            }
        }

        public bool NeedsRepair
        {
            get
            {
                string? dir = resolvedAlignerDir();
                return dir != null && AlignerEnvironment.EnvironmentNeedsRepair(dir);
            }
        }

        public string? InstalledDevice
        {
            get
            {
                string? dir = resolvedAlignerDir();

                if (dir == null || !AlignerEnvironment.EnvironmentReady(dir))
                    return null;

                try
                {
                    string marker = Path.Combine(dir, AlignerEnvironment.DEVICE_MARKER_FILE);
                    return File.Exists(marker) ? File.ReadAllText(marker).Trim().ToLowerInvariant() : "cpu";
                }
                catch
                {
                    return "cpu";
                }
            }
        }

        // Probed once, and only once even when the first-run screen and an install ask together.
        private readonly Lazy<bool> gpuDetected = new Lazy<bool>(detectNvidiaGpu);

        public bool GpuDetected => gpuDetected.Value;

        public string? InstalledVersion => IsInstalled ? AlignerEnvironment.ReadAlignerVersion(resolvedAlignerDir()) : null;

        /// <summary>
        /// The version both import entry points require of the aligner they would run (backlog 410):
        /// any other is refused by the importer itself, so no caller, the import screen, the editor's
        /// re-align or one yet to be written, can time a map with an aligner this build was not made
        /// for. <see cref="ILocalAlignerManager.VersionStatus"/> reads the same pair through the same
        /// rule for the surfaces that say so in advance.
        /// </summary>
        public string? ShippedVersion => AlignerEnvironment.ReadAlignerVersion(AlignerEnvironment.ResolveLyricLabDir(null, startDirectories()));

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

                // Read without blocking so the 5 s bound also covers an nvidia-smi that hangs: a
                // ReadToEnd here would wait for it before the timeout was ever checked.
                var outputRead = process.StandardOutput.ReadToEndAsync();

                if (!process.WaitForExit(5000) || !outputRead.Wait(5000))
                {
                    try { process.Kill(); }
                    catch { }

                    return false;
                }

                return process.ExitCode == 0 && outputRead.GetResultSafely().Contains("GPU", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Held for the whole of an install. Two entry points start one (Settings and the first-run
        /// screen), and a second run would clear work/ and out/, or the venv, under the first.
        /// </summary>
        private readonly SemaphoreSlim installLock = new SemaphoreSlim(1, 1);

        // ---------------------------------------------------------------------------------------------
        // Demucs (aligner version 12)
        // ---------------------------------------------------------------------------------------------

        public bool DemucsInstalled
        {
            get
            {
                string? dir = managedDemucsDir();
                return dir != null && AlignerEnvironment.EnvironmentReady(dir);
            }
        }

        public bool DemucsNeedsRepair
        {
            get
            {
                string? dir = managedDemucsDir();
                return dir != null && AlignerEnvironment.EnvironmentNeedsRepair(dir);
            }
        }

        /// <summary>Held for the whole of a Demucs install, as <see cref="installLock"/> is for the aligner's.</summary>
        private readonly SemaphoreSlim demucsInstallLock = new SemaphoreSlim(1, 1);

        public async Task<LyricImportResult> InstallDemucsAsync(Action<string> progress, CancellationToken token)
        {
            if (!demucsInstallLock.Wait(0))
                return LyricImportResult.Fail("Demucs is already being installed, wait for that install to finish");

            try
            {
                string? target = managedDemucsDir();

                if (target == null)
                    return LyricImportResult.Fail("no game storage available to install into");

                string? copyFailure = copyComponent(target, progress, "copying Demucs's scripts...");

                if (copyFailure != null)
                    return LyricImportResult.Fail(copyFailure);

                string device = await prepareDevice(target, progress).ConfigureAwait(false);

                progress(device == "cuda" ? "NVIDIA GPU detected, installing the GPU build of Demucs" : "no NVIDIA GPU detected, installing the CPU build of Demucs");

                return await AlignerEnvironment.BootstrapEnvironmentAsync(target, progress, token, device, updateExisting: true, component: LocalComponent.Demucs)
                                               .ConfigureAwait(false);
            }
            finally
            {
                demucsInstallLock.Release();
            }
        }

        public Task<VocalsSeparationResult> SeparateVocalsAsync(string audioPath, Action<string> progress, CancellationToken token)
            => LyricMapImporter.SeparateVocalsForEditorAsync(demucsDirForRun(), audioPath, startDirectories(), progress, token);

        /// <summary>
        /// The managed Demucs install a separation runs in, or null when there is none. Its script is
        /// brought up to this build's first when the game has been updated since Demucs was installed:
        /// the script is all a separation needs from it, and the environment is kept.
        /// </summary>
        private string? demucsDirForRun()
        {
            string? dir = managedDemucsDir();

            if (dir == null || !AlignerEnvironment.IsLyricLabDir(dir))
                return null;

            string? source = AlignerEnvironment.ResolveLyricLabDir(null, startDirectories());

            if (source != null && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)
                               && AlignerEnvironment.ReadAlignerVersion(source) != AlignerEnvironment.ReadAlignerVersion(dir))
            {
                try
                {
                    File.Copy(Path.Combine(source, "align_lyrics.py"), Path.Combine(dir, "align_lyrics.py"), overwrite: true);
                }
                catch (Exception e)
                {
                    Logger.Log($"Could not bring the Demucs install's script up to date: {e.Message}", LoggingTarget.Runtime, LogLevel.Important);
                }
            }

            return dir;
        }

        /// <summary>
        /// Copies the shipped component's scripts into <paramref name="target"/> (an install's folder),
        /// clearing that install's caches when a newer script replaces an older one's. Null, or why it
        /// could not.
        /// </summary>
        private string? copyComponent(string target, Action<string> progress, string message)
        {
            // Source of the component scripts: the copy shipped beside the executable (deployed
            // builds) or a dev checkout found by discovery. The managed install itself also counts
            // (repair-after-update with the shipped copy still preferred for freshness).
            string? source = AlignerEnvironment.ResolveLyricLabDir(null, startDirectories());

            if (source == null && !AlignerEnvironment.IsLyricLabDir(target))
                return "this build did not ship the aligner component, update the game and try again";

            try
            {
                Directory.CreateDirectory(target);

                if (source != null && !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    progress(message);

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
                            progress($"clearing the previous {stale}/ cache...");
                            Directory.Delete(dir, recursive: true);
                        }
                    }
                }

                return null;
            }
            catch (Exception e)
            {
                return $"could not copy the aligner component: {e.Message}";
            }
        }

        /// <summary>
        /// The torch flavour an install in <paramref name="target"/> gets ("cuda" with an NVIDIA GPU,
        /// else "cpu"), dropping an environment built for the other one: the venv pins CPU or CUDA
        /// wheels at install time. Checked on the venv's PRESENCE, not on a completed setup, so a
        /// sentinel-less venv of the wrong flavour is rebuilt rather than adopted by the repair probe.
        /// </summary>
        private Task<string> prepareDevice(string target, Action<string> progress)
        {
            string device = GpuDetected ? "cuda" : "cpu";

            try
            {
                string marker = Path.Combine(target, AlignerEnvironment.DEVICE_MARKER_FILE);

                if (AlignerEnvironment.EnvironmentPresent(target) && File.Exists(marker)
                    && !File.ReadAllText(marker).Trim().Equals(device, StringComparison.OrdinalIgnoreCase))
                {
                    progress("switching the device flavour, rebuilding the environment...");
                    Directory.Delete(Path.Combine(target, ".venv"), recursive: true);
                }
            }
            catch
            {
                // Best effort; bootstrap will report anything fatal.
            }

            return Task.FromResult(device);
        }

        public async Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token)
        {
            // A second install while one runs fails at once rather than queueing to redo the same work.
            if (!installLock.Wait(0))
                return LyricImportResult.Fail("the local auto-aligner is already being installed, wait for that install to finish");

            try
            {
                return await installAsync(progress, token).ConfigureAwait(false);
            }
            finally
            {
                installLock.Release();
            }
        }

        private async Task<LyricImportResult> installAsync(Action<string> progress, CancellationToken token)
        {
            string? target = managedInstallDir();

            if (target == null)
                return LyricImportResult.Fail("no game storage available to install into");

            // Version 12 left Demucs and MMS_FA behind for a model of its own: an install from before it
            // is UNINSTALLED WHOLE, its environment and the models only it used, before the new one is
            // installed fresh (Demucs, should the player want it, is its own install now).
            if (AlignerEnvironment.MustRemovePreviousAligner(target, ShippedVersion))
            {
                progress($"removing the previous auto-aligner (v{AlignerEnvironment.ReadAlignerVersion(target)}) completely...");

                string? removal = AlignerEnvironment.RemovePreviousAligner(target, AlignerEnvironment.TorchHubCheckpointsDir(), keepDemucsModel: DemucsInstalled, progress);

                if (removal != null)
                    return LyricImportResult.Fail(removal);
            }

            string? copyFailure = copyComponent(target, progress, "copying aligner component...");

            if (copyFailure != null)
                return LyricImportResult.Fail(copyFailure);

            string device = await prepareDevice(target, progress).ConfigureAwait(false);

            progress(device == "cuda"
                ? "NVIDIA GPU detected, installing the GPU aligner"
                : "no NVIDIA GPU detected, installing the CPU aligner");

            // An existing environment is UPDATED rather than trusted as it stands (backlog 411): the
            // scripts just copied may need packages the venv does not have yet (version 10's
            // fused-evidence pair), and only the setup's update mode installs them.
            var result = await AlignerEnvironment.BootstrapEnvironmentAsync(target, progress, token, device, updateExisting: true).ConfigureAwait(false);

            if (!result.Success)
                return result;

            // Point the importer at the managed install.
            config()?.SetValue(TypeBeatRulesetSetting.LyricLabPath, target);

            return result;
        }
    }
}
