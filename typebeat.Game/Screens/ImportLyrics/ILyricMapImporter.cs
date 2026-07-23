// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// Shell-side seam for the type!beat lyric-map import pipeline. The concrete implementation
    /// lives in the ruleset (it depends on the ruleset's <c>LyricOsuFormat</c>/<c>LrcParser</c>/
    /// <c>TimingJsonLoader</c>), which cannot be referenced from typebeat.Game; typebeat.Desktop
    /// (which references both) caches an instance so <see cref="ImportLyricsScreen"/> can resolve it.
    /// </summary>
    public interface ILyricMapImporter
    {
        /// <summary>Best-effort "Artist - Title" split of an audio filename, for prefilling the UI.</summary>
        (string Artist, string Title) GuessArtistTitle(string audioPath);

        /// <summary>
        /// Runs the full import and packages the result as a self-contained .osz in a temp
        /// directory. With <paramref name="useAutomaticAlignment"/> the automatic aligner (local
        /// subprocess or server) provides word-level timing; without it only the line-stamp LRC
        /// path is used. Progress lines stream through <paramref name="progress"/> on a background
        /// thread; marshal to the update thread yourself. Cancelling kills any spawned process tree.
        /// </summary>
        Task<LyricImportResult> BuildOszAsync(
            string audioPath, string lyricsPath, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = false);

        /// <summary>
        /// Aligns raw lyrics text to an audio file and returns timing.json (v2) text WITHOUT
        /// packaging an .osz; used by the in-editor "auto-time to this song" flow, which then
        /// parses the timing into the open beatmap. Same aligner/LRC-fallback behaviour as
        /// <see cref="BuildOszAsync"/>. The timing.json text is returned as the tuple's second
        /// element; <see cref="LyricImportResult.OszPath"/> is unused on this path.
        /// </summary>
        Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true);
    }

    /// <summary>Outcome of <see cref="ILyricMapImporter.BuildOszAsync"/>.</summary>
    public readonly record struct LyricImportResult(bool Success, string? OszPath, string? Error)
    {
        public static LyricImportResult Ok(string oszPath) => new LyricImportResult(true, oszPath, null);
        public static LyricImportResult Fail(string error) => new LyricImportResult(false, null, error);
    }
}
