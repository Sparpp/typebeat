// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
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
    /// server-side alignment (<see cref="RemoteAlignClient"/>) — which is offered only in deployed
    /// builds, never in a dev build (those use the local lyriclab checkout). A <see cref="Component"/>
    /// so it can resolve the ruleset config cache; typebeat.Desktop caches it and adds it to the hierarchy.
    /// </summary>
    public partial class LyricMapImportService : Component, ILyricMapImporter
    {
        [Resolved(CanBeNull = true)]
        private IRulesetConfigCache? configCache { get; set; }

        [Resolved(CanBeNull = true)]
        private IAPIProvider? api { get; set; }

        [Resolved(CanBeNull = true)]
        private OsuGameBase? game { get; set; }

        public (string Artist, string Title) GuessArtistTitle(string audioPath) => LyricMapImporter.GuessArtistTitle(audioPath);

        public Task<LyricImportResult> BuildOszAsync(
            string audioPath, string lyricsPath, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = false)
            => LyricMapImporter.BuildOszAsync(audioPath, lyricsPath, artist, title, configuredPath(), startDirectories(), progress, token, remoteAligner(), useAutomaticAlignment);

        public Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true)
            => LyricMapImporter.ProduceTimingJsonAsync(audioPath, lyricsContent, artist, title, configuredPath(), startDirectories(), progress, token, remoteAligner(), useAutomaticAlignment);

        private RemoteAligner? remoteAligner()
        {
            var capturedApi = api;

            if (capturedApi == null)
                return null;

            // Server-side alignment exists for SHIPPED builds, which carry no local Python/torch.
            // A development build (non-deployed: AssemblyVersion.Major == 0) has the vendored
            // lyriclab beside the repo and must use it, never offload to the production aligner —
            // so the remote fallback is withheld here and import resolves local aligner -> LRC only.
            if (game?.IsDeployedBuild != true)
                return null;

            return (audioPath, lyricsContent, artist, title, progress, token) =>
                RemoteAlignClient.AlignAsync(capturedApi, audioPath, lyricsContent, artist, title, progress, token);
        }

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
