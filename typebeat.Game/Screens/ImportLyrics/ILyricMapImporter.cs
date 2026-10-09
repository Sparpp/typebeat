// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Bindables;
using typebeat.Game.Beatmaps;

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
        /// directory. With <paramref name="useAutomaticAlignment"/> word-level timing comes from the
        /// server aligner when <paramref name="useServerAligner"/> asks for it (backlog 413, the import
        /// screen's opt-in), else from the local aligner subprocess; without it (or with no aligner
        /// installed) only the line-stamp LRC path is used. A server run that fails fails the import
        /// with the server's reason: it never falls back to the local aligner or to the line stamps.
        /// Progress lines stream through <paramref name="progress"/> on a background thread; marshal
        /// to the update thread yourself. Cancelling kills any spawned process tree, or withdraws the
        /// server job.
        ///
        /// <para><paramref name="lyricsPath"/> is optional: null (or a file holding only whitespace)
        /// skips alignment entirely and packages a BLANK map, audio + metadata with zero lyric
        /// lines, to be written and timed in the editor.</para>
        /// <para><paramref name="language"/> is selected in the import screen. It is saved as map
        /// metadata and used when romanising lyrics.</para>
        /// <para><paramref name="vocalMode"/> is how the local aligner times the words (see
        /// <see cref="AlignerVocalMode"/>); it reaches the aligner's command line and nothing else,
        /// so the packaged map carries no trace of it. The caller persists it on the imported set.</para>
        /// </summary>
        Task<LyricImportResult> BuildOszAsync(
            string audioPath, string? lyricsPath, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment, BeatmapLanguage language,
            AlignerVocalMode vocalMode = AlignerVocalMode.Aligned, bool useServerAligner = false);

        /// <summary>
        /// Aligns raw lyrics text to an audio file and returns timing.json (v2) text WITHOUT
        /// packaging an .osz; used by the in-editor "auto-time to this song" flow, which then
        /// parses the timing into the open beatmap. Same local aligner/LRC-fallback behaviour as
        /// <see cref="BuildOszAsync"/>, LOCAL only: the server aligner is an import-screen opt-in and
        /// the editor's re-align does not offer it. The timing.json text is returned as the tuple's second
        /// element; <see cref="LyricImportResult.OszPath"/> is unused on this path.
        /// </summary>
        /// <remarks>The optional language is the map's, which non-Latin lyrics are romanised under;
        /// null detects it from the lyrics' own script. The vocal mode is the map set's stored
        /// <see cref="BeatmapSetInfo.AlignerVocalMode"/>.</remarks>
        Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true,
            string? language = null, AlignerVocalMode vocalMode = AlignerVocalMode.Aligned);

        /// <summary>
        /// The import screen's remembered "use server aligner" choice (backlog 413), a bound copy of
        /// the stored ruleset setting; null when this build has nowhere to keep it (headless, tests).
        /// </summary>
        Bindable<bool>? ServerAlignerPreference => null;
    }

    /// <summary>
    /// Outcome of <see cref="ILyricMapImporter.BuildOszAsync"/>. <see cref="Notice"/> is a
    /// successful import's summary for the user, when it has one: the words the romaniser could not
    /// spell, which the map carries with no typed text until the mapper romanises them.
    /// </summary>
    public readonly record struct LyricImportResult(bool Success, string? OszPath, string? Error, string? Notice = null)
    {
        public static LyricImportResult Ok(string oszPath) => new LyricImportResult(true, oszPath, null);
        public static LyricImportResult Fail(string error) => new LyricImportResult(false, null, error);
    }
}
