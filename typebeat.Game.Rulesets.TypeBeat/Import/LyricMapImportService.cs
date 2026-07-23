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
using osu.Framework.Graphics;
using osu.Framework.Platform;
using typebeat.Game.Online.API;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// DI adapter bridging the shell's <see cref="ILyricMapImporter"/> seam to the static
    /// <see cref="LyricMapImporter"/> core. Owns the concerns the core cannot reach on its own:
    /// the ruleset-scoped <see cref="TypeBeatRulesetSetting.LyricLabPath"/> override, the game's
    /// runtime location (start directories for aligner discovery), and the API session behind
    /// server-side alignment (<see cref="RemoteAlignClient"/>), which is offered only in deployed
    /// builds, never in a dev build (those use the local lyriclab checkout). A <see cref="Component"/>
    /// so it can resolve the ruleset config cache; typebeat.Desktop caches it and adds it to the hierarchy.
    ///
    /// Also implements <see cref="ILocalAlignerManager"/>: installing the local auto-aligner into
    /// the game's DATA directory (so its multi-GB environment survives Velopack updates, which
    /// replace the application directory wholesale) and gating the local path behind
    /// <see cref="TypeBeatRulesetSetting.LocalAlignerEnabled"/>.
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
        private IAPIProvider? api { get; set; }

        [Resolved(CanBeNull = true)]
        private OsuGameBase? game { get; set; }

        [Resolved(CanBeNull = true)]
        private Storage? storage { get; set; }

        public (string Artist, string Title) GuessArtistTitle(string audioPath) => LyricMapImporter.GuessArtistTitle(audioPath);

        public Task<LyricImportResult> BuildOszAsync(
            string audioPath, string lyricsPath, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = false)
            => LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, artist, title, effectiveConfiguredPath(), effectiveStartDirectories(), progress, token, remoteAligner(), useAutomaticAlignment);

        public Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true)
            => LyricMapImporter.ProduceTimingJsonAsync(audioPath, lyricsContent, artist, title, effectiveConfiguredPath(), effectiveStartDirectories(), progress, token, remoteAligner(), useAutomaticAlignment);

        private RemoteAligner? remoteAligner()
        {
            var capturedApi = api;

            if (capturedApi == null)
                return null;

            // Server-side alignment exists for SHIPPED builds, which carry no local Python/torch.
            // A development build (non-deployed: AssemblyVersion.Major == 0) has the vendored
            // lyriclab beside the repo and must use it, never offload to the production aligner,
            // so the remote fallback is withheld here and import resolves local aligner -> LRC only.
            if (game?.IsDeployedBuild != true)
                return null;

            return (audioPath, lyricsContent, artist, title, progress, token) =>
                RemoteAlignClient.AlignAsync(capturedApi, audioPath, lyricsContent, artist, title, progress, token);
        }

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

        private bool localAlignerEnabled() => config()?.Get<bool>(TypeBeatRulesetSetting.LocalAlignerEnabled) ?? true;

        /// <summary>
        /// The configured lyriclab path for import runs; null when the local aligner is switched
        /// off, which (together with empty start directories) makes discovery find nothing and the
        /// pipeline go straight to the server aligner / LRC fallback.
        /// </summary>
        private string? effectiveConfiguredPath()
        {
            if (!localAlignerEnabled())
                return null;

            string? configured = config()?.Get<string>(TypeBeatRulesetSetting.LyricLabPath);

            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            // No explicit path: prefer the managed install when it exists.
            string? managed = managedInstallDir();
            return managed != null && LyricMapImporter.IsLyricLabDir(managed) ? managed : null;
        }

        private IEnumerable<string> effectiveStartDirectories()
            => localAlignerEnabled() ? startDirectories() : Array.Empty<string>();

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
            => LyricMapImporter.ResolveLyricLabDir(effectiveConfiguredPathIgnoringEnable(), startDirectories());

        // Install state must be reportable even while the "use local aligner" toggle is off,
        // so the settings UI can say "installed but disabled" rather than "not installed".
        private string? effectiveConfiguredPathIgnoringEnable()
        {
            string? configured = config()?.Get<string>(TypeBeatRulesetSetting.LyricLabPath);

            if (!string.IsNullOrWhiteSpace(configured))
                return configured;

            string? managed = managedInstallDir();
            return managed != null && LyricMapImporter.IsLyricLabDir(managed) ? managed : null;
        }

        public bool IsInstalled
        {
            get
            {
                string? dir = resolvedAlignerDir();
                return dir != null && LyricMapImporter.EnvironmentReady(dir);
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
                }
            }
            catch (Exception e)
            {
                return LyricImportResult.Fail($"could not copy the aligner component: {e.Message}");
            }

            string device = GpuDetected ? "cuda" : "cpu";

            // A previously built environment of the other torch flavour must be rebuilt; the venv
            // pins CPU or CUDA wheels at install time.
            try
            {
                string marker = Path.Combine(target, LyricMapImporter.DEVICE_MARKER_FILE);

                if (LyricMapImporter.EnvironmentReady(target) && File.Exists(marker)
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

            var result = await LyricMapImporter.BootstrapEnvironmentAsync(target, progress, token, device).ConfigureAwait(false);

            if (!result.Success)
                return result;

            // Point the importer at the managed install and make sure the local path is active.
            config()?.SetValue(TypeBeatRulesetSetting.LyricLabPath, target);
            config()?.SetValue(TypeBeatRulesetSetting.LocalAlignerEnabled, true);

            return result;
        }
    }
}
