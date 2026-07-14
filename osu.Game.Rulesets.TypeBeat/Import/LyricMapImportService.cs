// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Game.Rulesets.TypeBeat.Configuration;
using osu.Game.Screens.ImportLyrics;

namespace osu.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// DI adapter bridging the shell's <see cref="ILyricMapImporter"/> seam to the static
    /// <see cref="LyricMapImporter"/> core. Owns the concern the core cannot reach on its own:
    /// the ruleset-scoped <see cref="TypeBeatRulesetSetting.LyricLabPath"/> override and the game's
    /// runtime location (start directories for aligner discovery). A <see cref="Component"/> so it
    /// can resolve the ruleset config cache; osu.Desktop caches it and adds it to the hierarchy.
    /// </summary>
    public partial class LyricMapImportService : Component, ILyricMapImporter
    {
        [Resolved(CanBeNull = true)]
        private IRulesetConfigCache? configCache { get; set; }

        public (string Artist, string Title) GuessArtistTitle(string audioPath) => LyricMapImporter.GuessArtistTitle(audioPath);

        public Task<LyricImportResult> BuildOszAsync(
            string audioPath, string lyricsPath, string artist, string title,
            Action<string> progress, CancellationToken token)
            => LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, artist, title, configuredPath(), startDirectories(), progress, token);

        public Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token)
            => LyricMapImporter.ProduceTimingJsonAsync(audioPath, lyricsContent, artist, title, configuredPath(), startDirectories(), progress, token);

        private string? configuredPath()
        {
            try
            {
                if (configCache?.GetConfigFor(new TypeBeatRuleset()) is TypeBeatRulesetConfigManager config)
                    return config.Get<string>(TypeBeatRulesetSetting.LyricLabPath);
            }
            catch
            {
                // Config unavailable (cache not loaded / ruleset unregistered) — discovery covers it.
            }

            return null;
        }

        /// <summary>
        /// Where directory discovery starts walking up from: next to the running assembly (deployed
        /// builds have lyriclab/ beside the executable) and the process working directory (a dev
        /// `dotnet run` from repo root has lyriclab/ a few levels up).
        /// </summary>
        private static IEnumerable<string> startDirectories()
        {
            yield return AppContext.BaseDirectory;
            yield return Environment.CurrentDirectory;
        }
    }
}
