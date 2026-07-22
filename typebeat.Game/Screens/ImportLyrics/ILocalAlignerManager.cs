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
    /// word-by-word on the player's own machine, instead of the shared server queue). The concrete
    /// implementation lives in the ruleset beside <see cref="ILyricMapImporter"/>; typebeat.Desktop
    /// caches it so the first-run setup screen and the settings section can drive installs.
    /// </summary>
    public interface ILocalAlignerManager
    {
        /// <summary>Whether a ready-to-run local aligner environment exists (venv built).</summary>
        bool IsInstalled { get; }

        /// <summary>"cuda" or "cpu" for an installed environment; null when not installed.</summary>
        string? InstalledDevice { get; }

        /// <summary>Best-effort NVIDIA GPU detection (nvidia-smi) — picks the CUDA install flavour.</summary>
        bool GpuDetected { get; }

        /// <summary>
        /// Installs (or repairs) the local aligner: copies the shipped component into the game's
        /// data directory, builds its Python environment (a one-time multi-GB download; CUDA build
        /// when <see cref="GpuDetected"/>), and points the importer at it. Progress lines stream on
        /// a background thread — marshal to the update thread yourself. Safe to call when already
        /// installed (refreshes scripts, keeps the environment).
        /// </summary>
        Task<LyricImportResult> InstallAsync(Action<string> progress, CancellationToken token);
    }
}
