// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// Shell-side seam for managing the LOCAL lyriclab auto-aligner (the AI that times lyrics
    /// word-by-word on the player's own machine, and, since the server-side aligner was retired, the
    /// only automatic timing path there is). The concrete implementation lives in the ruleset beside
    /// <see cref="ILyricMapImporter"/>; typebeat.Desktop caches it so the first-run setup screen and
    /// the settings section can drive installs.
    /// </summary>
    public interface ILocalAlignerManager
    {
        /// <summary>
        /// Whether a ready-to-run local aligner environment exists: the venv built AND its setup
        /// completed (the setup sentinel is present).
        /// </summary>
        bool IsInstalled { get; }

        /// <summary>
        /// A venv exists but no setup ever completed on it: an install that died part way, or one
        /// made before completed setups were recorded. <see cref="InstallAsync"/> REPAIRS it: the
        /// venv is kept when its packages import, and deleted and rebuilt when they do not.
        /// Never true together with <see cref="IsInstalled"/>.
        /// </summary>
        bool NeedsRepair { get; }

        /// <summary>"cuda" or "cpu" for an installed environment; null when not installed.</summary>
        string? InstalledDevice { get; }

        /// <summary>Best-effort NVIDIA GPU detection (nvidia-smi); picks the CUDA install flavour.</summary>
        bool GpuDetected { get; }

        /// <summary>
        /// The <c>ALIGNER_VERSION</c> of the aligner script the importer would actually run; null
        /// when nothing is installed or the script predates versioning.
        /// </summary>
        string? InstalledVersion { get; }

        /// <summary>The <c>ALIGNER_VERSION</c> of the component this build ships; null when the build carries none.</summary>
        string? ShippedVersion { get; }

        /// <summary>
        /// How the installed aligner's version stands against the shipped one (backlog 410). Anything
        /// but <see cref="AlignerVersionStatus.Matches"/> blocks automatic alignment until the player
        /// presses <see cref="InstallAsync"/>'s button, which refreshes the scripts and keeps the
        /// multi-GB environment. Derived, never stored, so every implementation shares the one rule
        /// in <see cref="LocalAlignerVersion.StatusOf"/>.
        /// </summary>
        AlignerVersionStatus VersionStatus => LocalAlignerVersion.StatusOf(IsInstalled, InstalledVersion, ShippedVersion);

        /// <summary>
        /// Installs (or repairs) the local aligner: copies the shipped component into the game's
        /// data directory, builds its Python environment (a one-time multi-GB download; CUDA build
        /// when <see cref="GpuDetected"/>), and points the importer at it. Progress lines stream on
        /// a background thread; marshal to the update thread yourself. Safe to call when already
        /// installed: the scripts are replaced by the shipped copy and the aligner's caches are
        /// cleared, while the environment is kept. When <see cref="NeedsRepair"/>, this is the repair.
        /// </summary>
        Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token);
    }
}
