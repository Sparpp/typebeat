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
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// DI adapter bridging the shell's <see cref="ILyricMapImporter"/> seam to the static
    /// <see cref="LyricMapImporter"/> core. Owns the concerns the core cannot reach on its own: the
    /// ruleset-scoped <see cref="TypeBeatRulesetSetting.LyricLabPath"/> override and the game's
    /// runtime location (start directories for aligner discovery). A <see cref="Component"/> so it
    /// can resolve the ruleset config cache; typebeat.Desktop caches it and adds it to the hierarchy.
    ///
    /// There is no remote seam any more: the server-side aligner is retired, so the ladder is the
    /// LOCAL aligner then the LRC line stamps, identically in a dev and a deployed build (the older
    /// shape withheld the remote rung from dev builds off <c>IsDeployedBuild</c>, which is now moot).
    ///
    /// Also implements <see cref="ILocalAlignerManager"/>: installing the local auto-aligner into
    /// the game's DATA directory (so its multi-GB environment survives Velopack updates, which
    /// replace the application directory wholesale) and gating the local path behind
    /// <see cref="TypeBeatRulesetSetting.LocalAlignerEnabled"/>, a gate that stays open while that
    /// switch is hidden (see <see cref="LocalAlignerEnabled"/>).
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

        public (string Artist, string Title) GuessArtistTitle(string audioPath) => LyricMapImporter.GuessArtistTitle(audioPath);

        public Task<LyricImportResult> BuildOszAsync(
            string audioPath, string? lyricsPath, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment, BeatmapLanguage language,
            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned)
            => language == BeatmapLanguage.Unspecified
                ? Task.FromResult(LyricImportResult.Fail("select a language before importing"))
                : LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, artist, title, effectiveConfiguredPath(), effectiveStartDirectories(), progress, token,
                    useAutomaticAlignment, language: language.ToCanonicalName(), highQualityAlignment: HighQualityAlignment(config()), vocalMode: vocalMode,
                    requiredAlignerVersion: ShippedVersion);

        public Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true, string? language = null,
            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned)
            => LyricMapImporter.ProduceTimingJsonAsync(audioPath, lyricsContent, artist, title, effectiveConfiguredPath(), effectiveStartDirectories(), progress, token, useAutomaticAlignment,
                language, HighQualityAlignment(config()), vocalMode, requiredAlignerVersion: ShippedVersion);

        /// <summary>
        /// Whether an import runs the aligner at its full tier. Read at the start of each import,
        /// so a change in Settings applies to the next one. Off when there is no config to read,
        /// the opposite of <see cref="localAlignerEnabled"/>'s fallback on purpose: a missing config
        /// must never make every import four times slower. Static over an explicit config so a test
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

        private bool localAlignerEnabled() => LocalAlignerEnabled(config());

        /// <summary>
        /// Whether the import path may use an installed local aligner. While the switch is hidden
        /// (<see cref="TypeBeatRulesetConfigManager.LOCAL_ALIGNER_TOGGLE_SURFACED"/>, backlog 381) this is
        /// always true: being installed is the opt-in, and a stored <c>False</c> left by an older
        /// install is ignored rather than migrated, so the row survives for a returning hosted aligner
        /// but cannot strand a player with an aligner that never runs and a failure telling them to
        /// install it. With the switch surfaced, the stored value is honoured and a missing config
        /// reads as on. Static over an explicit config so a test can pin both arms.
        /// </summary>
        internal static bool LocalAlignerEnabled(TypeBeatRulesetConfigManager? config,
                                                 bool toggleSurfaced = TypeBeatRulesetConfigManager.LOCAL_ALIGNER_TOGGLE_SURFACED)
            => !toggleSurfaced || (config?.Get<bool>(TypeBeatRulesetSetting.LocalAlignerEnabled) ?? true);

        /// <summary>
        /// The configured lyriclab path for import runs; null when the local aligner is switched
        /// off, which (together with empty start directories) makes discovery find nothing and the
        /// pipeline go straight to the LRC line-stamp fallback.
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

        // Install state must be reportable whatever the (currently hidden) "use local aligner"
        // switch holds, so the settings button reads the install itself, never the switch.
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

            // Point the importer at the managed install and make sure the local path is active.
            config()?.SetValue(TypeBeatRulesetSetting.LyricLabPath, target);
            config()?.SetValue(TypeBeatRulesetSetting.LocalAlignerEnabled, true);

            return result;
        }
    }
}
