// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Orchestrates the vendored lyriclab aligner (mp3 + lyrics -> word/syllable timing.json) and
// packages the result as an importable .osz. Adapted from the standalone type!beat MapImporter:
// resolution / environment / sanitisation / process running are preserved (unit-testable against
// temp dirs), but the INSTALL step now builds an .osz (via the ruleset's single .osu writer
// LyricOsuFormat.GenerateOsu) instead of writing a maps/ folder. A line-granularity LRC-only
// fallback packages a map straight from LrcParser when the aligner is unavailable. This file holds
// that ladder; the installed aligner (AlignerEnvironment), the passes over its timing.json
// (TimingJsonPostProcess), the packaging (OszPackager) and the process runs (ImportProcessRunner)
// each have their own.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    public static class LyricMapImporter
    {
        /// <summary>
        /// The timing.json (v2) of a map with NO lyrics: a valid document with an empty lines[].
        /// Everything downstream already treats "no lines" as a legitimate state (the .osu writer
        /// emits a header and nothing else, <see cref="Beatmaps.LyricBeatmapDecoder"/> produces zero
        /// hit objects, the editor loads and can author the first line), so an audio-only import
        /// needs no format of its own, just this document in place of the aligner's output.
        /// </summary>
        public const string BLANK_TIMING_JSON = "{\"version\":2,\"lines\":[]}";

        /// <summary>
        /// True when every content line carries a leading [mm:ss.xx] stamp: the precondition for
        /// the LRC-only fallback, which has no other source for an unstamped line's time. Metadata
        /// tag lines ([ar:...], [Lyrics]) are neutral. The ALIGNER needs less, see
        /// <see cref="HasAnyLineStamp"/>.
        /// </summary>
        public static bool HasLineStamps(string lyricsContent)
        {
            (int stamped, int unstamped) = countStampedLines(lyricsContent);
            return stamped > 0 && unstamped == 0;
        }

        /// <summary>
        /// True when at least one line carries a [mm:ss.xx] stamp. That is enough for the aligner's
        /// "ref" mode since sparse anchors (aligner version 3): a stamped line opens a section and
        /// the unstamped lines after it are placed inside [that stamp, the next stamp), so stamping
        /// only the section starts still beats fully automatic alignment by a wide margin.
        /// </summary>
        public static bool HasAnyLineStamp(string lyricsContent) => countStampedLines(lyricsContent).Stamped > 0;

        /// <summary>
        /// The <c>--anchors</c> mode the aligner is run with: "ref" whenever any line is stamped,
        /// "auto" for bare text.
        /// </summary>
        public static string AlignerAnchorMode(string lyricsContent) => HasAnyLineStamp(lyricsContent) ? "ref" : "auto";

        /// <summary>
        /// Counts the lines that carry a leading [mm:ss.xx] stamp and the content lines that do not.
        /// A tag line with no text after it (a bare stamp is the end marker, [ar:...] is metadata)
        /// is neither.
        /// </summary>
        private static (int Stamped, int Unstamped) countStampedLines(string lyricsContent)
        {
            if (string.IsNullOrWhiteSpace(lyricsContent))
                return (0, 0);

            int stamped = 0, unstamped = 0;

            foreach (string raw in lyricsContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0)
                    continue;

                if (line.StartsWith('['))
                {
                    int close = line.IndexOf(']');

                    if (close > 1)
                    {
                        if (LrcParser.TryParseTimestamp(line.Substring(1, close - 1), out _) && line.Substring(close + 1).Trim().Length > 0)
                            stamped++;

                        // Timestamped or metadata tag line, either way not a bare content line.
                        continue;
                    }
                }

                unstamped++;
            }

            return (stamped, unstamped);
        }

        /// <summary>Removes path-invalid chars, collapses whitespace, trims trailing dots/spaces.</summary>
        public static string SanitizeFolderName(string name)
        {
            var sb = new StringBuilder(name.Length);

            foreach (char c in name)
                sb.Append(isInvalidInFolderName(c) ? ' ' : c);

            string cleaned = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
            return cleaned.Length == 0 ? "Imported Map" : cleaned;
        }

        /// <summary>
        /// Whether a character has to be replaced before a name becomes a folder this game can make.
        /// </summary>
        /// <remarks>
        /// A FIXED set rather than <see cref="Path.GetInvalidFileNameChars"/>. That answers for the
        /// HOST, and a map folder is not host-local: an .osz packed on macOS is extracted on Windows,
        /// where <c>&lt; &gt; : " | ? *</c> are all illegal and the folder would simply fail to
        /// create. Control characters go too, for the same reason.
        /// </remarks>
        private static bool isInvalidInFolderName(char c)
            => c < ' ' || c == '\0' || invalid_folder_chars.IndexOf(c) >= 0;

        private const string invalid_folder_chars = "<>:\"/\\|?*";

        /// <summary>Prefill guess from an "Artist - Title.mp3" style filename.</summary>
        public static (string Artist, string Title) GuessArtistTitle(string audioPath)
        {
            string stem = fileStem(audioPath).Trim();
            int sep = stem.IndexOf(" - ", StringComparison.Ordinal);

            if (sep < 0)
                return ("Unknown", stem.Length == 0 ? "Imported Map" : stem);

            string artist = stem.Substring(0, sep).Trim();
            string title = stem.Substring(sep + 3).Trim();
            return (artist.Length == 0 ? "Unknown" : artist, title.Length == 0 ? stem : title);
        }

        /// <summary>
        /// The file name of <paramref name="audioPath"/> minus its extension, reading either
        /// platform's separators. An importer is handed whatever the user browsed to, and a path
        /// copied from a Windows machine carries backslashes that <see cref="Path"/> does not treat
        /// as separators on macOS or Linux.
        /// </summary>
        private static string fileStem(string audioPath)
        {
            int separator = audioPath.LastIndexOfAny(new[] { '/', '\\' });
            string name = separator >= 0 ? audioPath.Substring(separator + 1) : audioPath;
            int extension = name.LastIndexOf('.');

            return extension > 0 ? name.Substring(0, extension) : name;
        }

        /// <summary>
        /// Full import: aligner subprocess when its environment is ready (word/syllable granularity),
        /// otherwise a line-granularity LRC fallback when the lyrics are line-stamped. Produces a
        /// self-contained .osz (generated .osu + audio + provenance timing.json + lyrics.txt) under a
        /// unique temp directory and returns its path. Never auto-triggers the multi-GB bootstrap.
        ///
        /// <para>BLANK IMPORT. <paramref name="lyricsPath"/> is optional: with no lyrics file (or a
        /// file that holds only whitespace) the aligner is skipped entirely and the packaged map is
        /// BLANK, audio + metadata with zero lyric lines, for authoring in the editor from scratch.</para>
        ///
        /// <para>The import screen passes the selected <paramref name="language"/> through to lyric
        /// romanisation and map metadata, including blank maps. A null value keeps script detection
        /// for callers that do not use the import screen.</para>
        ///
        /// <para>VIDEO SPLIT. A video container in the audio slot is split up front (see
        /// <see cref="IAudioTrackExtractor"/>): the extracted audio becomes the map's
        /// AudioFilename and the thing every later step consumes, the container stays on as the
        /// map's [Events] video, and both travel in the .osz. With no extractor on the machine the
        /// container keeps doing both jobs, as it always did.</para>
        ///
        /// <para>ISOLATED VOCALS (backlog 414). With <paramref name="isolateVocals"/> an import whose
        /// timing step produced no vocals stem (line stamps, a TTML or word-stamped LRC, the server
        /// aligner, a blank map) runs the local aligner's <c>--separate-only</c> mode after it, so the
        /// editor's vocals waveform has a stem; see <see cref="VocalsIsolationRefusal"/> for when it
        /// can. A LOCAL alignment run already wrote one and is not repeated. Failure is never fatal:
        /// the map packages without a stem and the result's notice says why. Cancelling still cancels
        /// the import. <paramref name="vocalsSeparationRunner"/> replaces the process runner for that
        /// one step (tests); null runs the real subprocess.</para>
        /// </summary>
        public static async Task<LyricImportResult> BuildOszAsync(
            string audioPath, string? lyricsPath, string artist, string title,
            string? configuredLyricLabPath, IEnumerable<string> startDirectories,
            Action<string> progress, CancellationToken token,
            bool useAutomaticAlignment = true, IAudioTrackExtractor? audioExtractor = null, string? language = null,
            bool highQualityAlignment = false, AlignerVocalMode vocalMode = AlignerVocalMode.Aligned, string? requiredAlignerVersion = null,
            RemoteAligner? remoteAlign = null, bool isolateVocals = false, ProcessRunner? vocalsSeparationRunner = null)
        {
            if (!File.Exists(audioPath))
                return LyricImportResult.Fail($"audio file not found: {audioPath}");

            bool lyricsRequested = !string.IsNullOrWhiteSpace(lyricsPath);

            // A path that was given but does not exist is still an error: only the ABSENCE of a
            // lyrics file means "blank map", never a typo'd or vanished one.
            if (lyricsRequested && !File.Exists(lyricsPath))
                return LyricImportResult.Fail($"lyrics file not found: {lyricsPath}");

            string oszDir = Path.Combine(Path.GetTempPath(), "typebeat_import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(oszDir);
            string oszPath = Path.Combine(oszDir, SanitizeFolderName($"{artist} - {title}") + ".osz");

            // THE SPLIT, before anything else touches the file. Doing it here (rather than in the
            // packaging step) means the blank branch and the aligned branch both get it, and the
            // ALIGNER receives the extracted audio rather than the whole container: the local
            // subprocess decodes less, and the server upload (capped at 64MB) carries a fraction of
            // the bytes. The extracted file lands beside the .osz, so the caller's cleanup of the
            // import temp directory takes it too.
            string effectiveAudioPath = audioPath;
            string? videoSourcePath = null;

            if (LyricImportExtensions.IsVideo(audioPath))
            {
                var extractor = audioExtractor ?? new FfmpegAudioTrackExtractor(configuredLyricLabPath, startDirectories);
                AudioExtractionResult extraction = await extractor.ExtractAsync(audioPath, oszDir, progress, token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                    return LyricImportResult.Fail("import cancelled");

                if (extraction.Success)
                {
                    effectiveAudioPath = extraction.AudioPath!;
                    videoSourcePath = audioPath;
                }
                else
                {
                    // No extractor, or one that could not encode: DEGRADE to what an mp4 import has
                    // always done (the container is the audio and the video at once) rather than
                    // failing an import that used to work. Said out loud, because the map that comes
                    // out behaves differently: an audio-only download of it is silent, and the
                    // "delete all videos" maintenance action would take its audio with it.
                    progress($"no audio extractor available ({extraction.Reason}), keeping the video file as the map's audio");
                }
            }

            string lyricsContent = lyricsRequested
                ? await File.ReadAllTextAsync(lyricsPath!, token).ConfigureAwait(false)
                : string.Empty;

            // No words to time (no file at all, or an empty one): package the blank map straight
            // away. Deliberately NOT an error here, unlike ProduceTimingJsonAsync, where the caller
            // explicitly asked for lyrics to be aligned and empty input can only be a mistake.
            if (string.IsNullOrWhiteSpace(lyricsContent))
            {
                progress("no lyrics, creating a blank map");

                (string? blankStem, string? blankNotice, bool blankCancelled) = await VocalsStemForPackageAsync(null, isolateVocals, effectiveAudioPath, artist, title, oszDir,
                    configuredLyricLabPath, startDirectories, requiredAlignerVersion, progress, token, vocalsSeparationRunner).ConfigureAwait(false);

                if (blankCancelled)
                    return LyricImportResult.Fail("import cancelled");

                progress("packaging map");
                return withNotice(OszPackager.PackageOsz(oszPath, artist, title, effectiveAudioPath, BLANK_TIMING_JSON, string.Empty, videoSourcePath, language,
                    vocalsStemSourcePath: blankStem), blankNotice);
            }

            // The import screen's language is authoritative. Older callers without a selection
            // continue to detect a language from the lyric script.
            language ??= LyricOriginals.DetectLanguage(new[] { lyricsContent });

            (LyricImportResult result, string? timing, string? vocalsStemSource) = await produceTimingJsonAsync(
                effectiveAudioPath, lyricsContent, artist, title, configuredLyricLabPath, startDirectories, progress, token, useAutomaticAlignment,
                language, highQualityAlignment, vocalMode, requiredAlignerVersion, remoteAlign).ConfigureAwait(false);

            if (!result.Success || timing == null)
                return result;

            (string? vocalsStemSourceFile, string? vocalsNotice, bool cancelled) = await VocalsStemForPackageAsync(vocalsStemSource, isolateVocals, effectiveAudioPath, artist, title,
                oszDir, configuredLyricLabPath, startDirectories, requiredAlignerVersion, progress, token, vocalsSeparationRunner).ConfigureAwait(false);

            if (cancelled)
                return LyricImportResult.Fail("import cancelled");

            progress("packaging map");

            var packaged = OszPackager.PackageOsz(oszPath, artist, title, effectiveAudioPath, timing, lyricsContent, videoSourcePath, language, progress, vocalsStemSourceFile);

            // The timing step's own notice (an enhanced LRC's clamped stamps) rides with the
            // packaging summary rather than being replaced by it, and the vocals note after both.
            return withNotice(withNotice(packaged, result.Notice, first: true), vocalsNotice);
        }

        /// <summary>
        /// A successful result with <paramref name="notice"/> joined to its own notice (after it, or
        /// before it with <paramref name="first"/>); a failure or a null notice is returned unchanged.
        /// </summary>
        private static LyricImportResult withNotice(LyricImportResult result, string? notice, bool first = false)
        {
            if (!result.Success || notice == null)
                return result;

            return result with
            {
                Notice = result.Notice == null ? notice : first ? notice + " " + result.Notice : result.Notice + " " + notice
            };
        }

        /// <summary>
        /// The vocals stem file the .osz carries, from the stem the timing step produced (a local
        /// alignment run's, backlog 392) or, failing that and with <paramref name="isolateVocals"/>,
        /// a <c>--separate-only</c> run of the local aligner (backlog 414). The 16 kHz mono wav is
        /// re-encoded to Ogg Vorbis beside the .osz when an encoder is available, because a map a
        /// mapper downloads should not carry ~1.9 MB a minute for a view-only surface; with no
        /// encoder the wav is packaged as it stands (the editor reads either).
        ///
        /// <para>The second element is the import's note when the vocals were asked for and could not
        /// be isolated (<see cref="ImportProgressParser.VOCALS_NOT_ISOLATED_PREFIX"/> and the reason),
        /// also said as a progress line; null otherwise. The third is true only when the token fired
        /// during the separation, which cancels the import: anything else is a map without a stem.</para>
        /// </summary>
        internal static async Task<(string? StemFile, string? Notice, bool Cancelled)> VocalsStemForPackageAsync(
            string? producedStem, bool isolateVocals, string audioPath, string artist, string title, string oszDir,
            string? configuredLyricLabPath, IEnumerable<string> startDirectories, string? requiredAlignerVersion,
            Action<string> progress, CancellationToken token, ProcessRunner? runner)
        {
            string? stem = producedStem;
            string? notice = null;

            if (stem == null && isolateVocals)
            {
                string? lyricLabDir = AlignerEnvironment.ResolveLyricLabDir(configuredLyricLabPath, startDirectories);
                string? failure = VocalsIsolationRefusal(lyricLabDir, requiredAlignerVersion);

                if (failure == null)
                {
                    (string? separated, string? separationFailure, bool cancelled) = await separateVocalsAsync(lyricLabDir!, audioPath, artist, title, progress, token, runner)
                        .ConfigureAwait(false);

                    if (cancelled)
                        return (null, null, true);

                    stem = separated;
                    failure = separationFailure;
                }

                if (failure != null)
                {
                    notice = ImportProgressParser.VOCALS_NOT_ISOLATED_PREFIX + failure;
                    Logger.Log($"Vocals not isolated for {artist} - {title}: {failure}", LoggingTarget.Runtime);
                    progress(notice);
                }
            }

            if (stem == null)
                return (null, notice, false);

            string stemFile = await OszPackager.EncodedVocalsStemAsync(stem, oszDir, configuredLyricLabPath, startDirectories, token).ConfigureAwait(false) ?? stem;
            return (stemFile, notice, false);
        }

        /// <summary>
        /// Why the local aligner in <paramref name="lyricLabDir"/> cannot isolate the vocals for an
        /// import, as the reason the import's note gives; null when it can: it is installed (the setup
        /// sentinel, <see cref="AlignerEnvironment.EnvironmentReady"/>; a sentinel-less venv is not probed here), it is
        /// the version this build ships (backlog 410's rule, <see cref="AlignerEnvironment.AlignerVersionRefusal"/>) and
        /// it has <c>--separate-only</c> (<see cref="AlignerEnvironment.AlignerSeparatesOnly"/>). Signing in is irrelevant:
        /// the separation is local only (the server never sends a stem).
        /// </summary>
        public static string? VocalsIsolationRefusal(string? lyricLabDir, string? requiredAlignerVersion)
        {
            if (lyricLabDir == null || !AlignerEnvironment.EnvironmentPresent(lyricLabDir))
                return VOCALS_NEED_THE_ALIGNER;

            if (!AlignerEnvironment.EnvironmentReady(lyricLabDir))
                return VOCALS_NEED_A_REPAIR;

            string? versionRefusal = AlignerEnvironment.AlignerVersionRefusal(lyricLabDir, requiredAlignerVersion);

            if (versionRefusal != null)
                return versionRefusal;

            if (!AlignerEnvironment.AlignerSeparatesOnly(lyricLabDir))
                return $"the local auto-aligner is v{AlignerEnvironment.ReadAlignerVersion(lyricLabDir) ?? "1"}, which cannot isolate the vocals on its own "
                       + $"(v{AlignerEnvironment.SEPARATE_ONLY_ALIGNER_VERSION} or newer needed). Update it in Settings > Experimental > Lyric timing.";

            return null;
        }

        /// <summary>The isolation note's reason when no local auto-aligner is installed.</summary>
        public const string VOCALS_NEED_THE_ALIGNER = "the local auto-aligner is not installed. Install it in Settings > Experimental > Lyric timing.";

        /// <summary>The isolation note's reason when the local auto-aligner's install never finished.</summary>
        public const string VOCALS_NEED_A_REPAIR = "the local auto-aligner's install is incomplete. Repair it in Settings > Experimental > Lyric timing.";

        /// <summary>
        /// The progress line said as the separation starts. Worded to claim the separating vocals
        /// stage in <see cref="ImportProgressParser"/>, which the aligner's own "separation: ..." lines
        /// then drive.
        /// </summary>
        public const string SEPARATING_VOCALS_PROGRESS = "separating vocals for the editor's vocals waveform";

        /// <summary>
        /// One <c>--separate-only</c> run of the aligner (backlog 414): the venv's python with the
        /// alignment run's environment (<see cref="pythonStartInfo"/>), the produced stem found as an
        /// alignment run's is (<see cref="FindProducedVocalsStem"/>). Returns the stem, or why there is
        /// none (never throws), or Cancelled when the token fired.
        /// </summary>
        private static async Task<(string? StemPath, string? Failure, bool Cancelled)> separateVocalsAsync(
            string lyricLabDir, string audioPath, string artist, string title, Action<string> progress, CancellationToken token, ProcessRunner? runner)
        {
            if (token.IsCancellationRequested)
                return (null, null, true);

            string outDir = Path.Combine(lyricLabDir, "out", "typebeat_vocals_" + SanitizeFolderName($"{artist} - {title}"));

            try
            {
                // A stem left by an earlier import of the same name must never be packaged as this one's.
                string stale = Path.Combine(outDir, VocalsStem.WAV_FILENAME);

                if (File.Exists(stale))
                    File.Delete(stale);

                var psi = pythonStartInfo(lyricLabDir);

                foreach (string arg in AlignerEnvironment.SeparateOnlyArguments(lyricLabDir, audioPath, outDir))
                    psi.ArgumentList.Add(arg);

                progress(SEPARATING_VOCALS_PROGRESS);

                (int exitCode, string tail) = await (runner ?? ImportProcessRunner.RunProcessAsync)(psi, progress, token).ConfigureAwait(false);

                if (exitCode == ImportProcessRunner.CANCELLED_EXIT_CODE || token.IsCancellationRequested)
                    return (null, null, true);

                if (exitCode != 0)
                {
                    string why = string.IsNullOrWhiteSpace(tail) ? $"exit code {exitCode}" : ShortAlignerFailure(tail);
                    Logger.Log($"Vocals separation exited with code {exitCode}: {tail}", LoggingTarget.Runtime, LogLevel.Important);
                    return (null, $"the separation failed ({why}).", false);
                }

                string? stem = FindProducedVocalsStem(lyricLabDir, outDir);
                return stem == null ? (null, $"the separation wrote no {VocalsStem.WAV_FILENAME}.", false) : (stem, null, false);
            }
            catch (Exception e)
            {
                if (token.IsCancellationRequested)
                    return (null, null, true);

                Logger.Error(e, "Vocals separation could not run");
                return (null, $"the separation could not run ({e.Message}).", false);
            }
        }

        /// <summary>
        /// A process run the importer can hand to a stand-in: the exit code (the cancelled sentinel
        /// when the token fired) and the last output lines joined by " | ", as
        /// <see cref="ImportProcessRunner.RunProcessAsync"/> returns them.
        /// </summary>
        public delegate Task<(int ExitCode, string Tail)> ProcessRunner(ProcessStartInfo psi, Action<string> progress, CancellationToken token);

        /// <summary>
        /// Produces timing.json (v2) text from an audio file and raw lyrics WITHOUT packaging an
        /// .osz: the headless path the in-editor "align to this audio" import uses. With
        /// <paramref name="useAutomaticAlignment"/> the preference order is: local aligner
        /// subprocess (word/syllable granularity) → line-granularity LRC fallback. Without it, the
        /// aligner is skipped entirely and only the LRC line-stamp path is used (instant, line
        /// granularity). Never triggers the ~2 GB local bootstrap. The lyrics text is written to a
        /// temp file for the aligner and cleaned up.
        ///
        /// <para>This entry point is LOCAL only: the editor's re-align uses it, and the server aligner
        /// (backlog 413) is an opt-in of the import screen's <see cref="BuildOszAsync"/> alone.</para>
        ///
        /// <para>A TTML (<see cref="TtmlParser.LooksLikeTtml"/>) short-circuits the whole ladder:
        /// the file is already word-timed, so it is converted directly and neither the aligner nor
        /// the audio is consulted. See <see cref="SynthesizeTimingJsonFromTtml"/>.</para>
        /// </summary>
        /// <remarks>The optional language is the one non-Latin lyrics are romanised under (backlog
        /// 330): the map's own when the caller has one, else null to detect it from the lyrics'
        /// script. <paramref name="highQualityAlignment"/> runs the aligner at its full tier (see
        /// <see cref="AlignerEnvironment.AlignerArguments"/>) and changes nothing on the TTML or line-stamp paths;
        /// <paramref name="vocalMode"/> is the map set's stored aligner vocal mode, which likewise
        /// reaches only the aligner's command line. <paramref name="requiredAlignerVersion"/> is the
        /// <c>ALIGNER_VERSION</c> this build ships: an installed aligner of any OTHER version is refused
        /// rather than run (backlog 410, see <see cref="AlignerEnvironment.AlignerVersionRefusal"/>); null checks nothing.
        /// <paramref name="allowLineFallback"/> false makes an aligner that ran and failed a failure
        /// (<see cref="AlignerRunFailedMessage"/>) instead of a line-timed result, for a caller that
        /// would otherwise overwrite word timing with it.</remarks>
        public static async Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            string? configuredLyricLabPath, IEnumerable<string> startDirectories,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true,
            string? language = null, bool highQualityAlignment = false, AlignerVocalMode vocalMode = AlignerVocalMode.Aligned,
            string? requiredAlignerVersion = null, bool allowLineFallback = true)
        {
            // The editor's in-place re-align uses the stem-less view: it edits the lines of a map
            // already on disk and never re-packages, so a stem this run produces is dropped.
            (LyricImportResult result, string? timing, _) = await produceTimingJsonAsync(audioPath, lyricsContent, artist, title, configuredLyricLabPath,
                startDirectories, progress, token, useAutomaticAlignment, language, highQualityAlignment, vocalMode, requiredAlignerVersion,
                allowLineFallback: allowLineFallback).ConfigureAwait(false);

            return (result, timing);
        }

        /// <summary>
        /// The core of the timing ladder, shared by <see cref="ProduceTimingJsonAsync"/> (which drops
        /// the stem) and <see cref="BuildOszAsync"/> (which keeps it). The third element is the
        /// isolated vocals stem the aligner wrote (backlog 392): the file path when this run produced
        /// one, null when it did not (no separation, an older aligner, or a non-aligner path). The
        /// stem is kept ONLY by the packaging import; the editor's in-place re-align drops it, since
        /// the map's folder is what an import writes and the editor does not own it.
        ///
        /// <para>THE LADDER, once automatic alignment is on and the lyrics need aligning (a TTML or a
        /// word-stamped LRC short-circuits it): <paramref name="remoteAlign"/> set (the import opted
        /// in to the server aligner, backlog 413) means the server, and ONLY the server, whatever is
        /// installed here; else the local aligner when it is installed and the version this build
        /// ships; else the line stamps. A failed server job fails the import with the server's own
        /// reason rather than dropping silently to the local aligner or to the line stamps: the player
        /// asked for word timing from the server.</para>
        ///
        /// <para>A local aligner that ran and failed drops to the line stamps with a notice saying so
        /// (<see cref="LineTimedFallbackNotice"/>), or, with <paramref name="allowLineFallback"/> false,
        /// fails with <see cref="AlignerRunFailedMessage"/>.</para>
        /// </summary>
        private static async Task<(LyricImportResult Result, string? TimingJson, string? VocalsStemPath)> produceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            string? configuredLyricLabPath, IEnumerable<string> startDirectories,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true,
            string? language = null, bool highQualityAlignment = false, AlignerVocalMode vocalMode = AlignerVocalMode.Aligned,
            string? requiredAlignerVersion = null, RemoteAligner? remoteAlign = null, bool allowLineFallback = true)
        {
            language ??= LyricOriginals.DetectLanguage(new[] { lyricsContent });

            if (!File.Exists(audioPath))
                return (LyricImportResult.Fail($"audio file not found: {audioPath}"), null, null);

            // Empty lyrics is its own outcome; before this it fell all the way through to the
            // confusing "no aligner available" message.
            if (string.IsNullOrWhiteSpace(lyricsContent))
                return (LyricImportResult.Fail(
                    "the lyrics are empty, add the song's words (ideally with [mm:ss.xx] line "
                    + "timestamps) before importing."), null, null);

            // A TTML is already word- (often syllable-) timed by its own producer, so it needs no
            // aligner, no audio and no line stamps: it is converted and used as it stands. Checked
            // FIRST because a TTML is an XML document that would otherwise be handed to the aligner
            // as if it were a lyrics file, which is exactly the mistake this replaces.
            if (TtmlParser.LooksLikeTtml(lyricsContent))
            {
                string? ttmlTiming = SynthesizeTimingJsonFromTtml(lyricsContent, language: language);

                if (ttmlTiming == null)
                    return (LyricImportResult.Fail("the .ttml produced no usable lyric lines."), null, null);

                progress("using the TTML's own word timing");
                return (LyricImportResult.Ok(string.Empty), ttmlTiming, null);
            }

            // An ENHANCED LRC (inline <mm:ss.xx> word stamps, backlog 356) is word-timed by its own
            // author exactly as a TTML is, so it short-circuits the ladder the same way: it never
            // runs the aligner and never collapses to the line-only path below.
            if (LrcParser.HasWordStamps(lyricsContent))
            {
                string? wordTiming = SynthesizeTimingJsonFromEnhancedLrc(lyricsContent, language, out int clampedStamps);

                if (wordTiming == null)
                    return (LyricImportResult.Fail("the word-stamped LRC produced no usable lyric lines."), null, null);

                progress(ENHANCED_LRC_PROGRESS);

                string? clampNotice = ClampedWordStampsNotice(clampedStamps);

                if (clampNotice != null)
                    progress(clampNotice);

                return (LyricImportResult.Ok(string.Empty) with { Notice = clampNotice }, wordTiming, null);
            }

            // Automatic alignment (the local aligner subprocess, or the server aligner the import
            // opted in to) is opt-in: off by default so an import uses the user's own line stamps
            // without a slow multi-minute run. When off, jump straight to the LRC line-stamp path.
            if (!useAutomaticAlignment)
            {
                progress("automatic alignment off, using your line timestamps");

                if (!HasLineStamps(lyricsContent))
                    return (LyricImportResult.Fail(HasAnyLineStamp(lyricsContent)
                        ? "only some lines have [mm:ss.xx] timestamps, and without automatic alignment every "
                          + "line needs one. Stamp the rest, or turn on \"automatic alignment\" to have the "
                          + "unstamped lines placed between your stamps."
                        : "these lyrics have no [mm:ss.xx] line timestamps. Add line stamps, or turn on "
                          + "\"automatic alignment\" to have the words timed for you."), null, null);

                return synthesizeFromLrc(lyricsContent, progress, language);
            }

            if (remoteAlign != null)
                return await alignOnServerAsync(remoteAlign, audioPath, lyricsContent, artist, title, language, vocalMode, progress, token).ConfigureAwait(false);

            string? lyricLabDir = AlignerEnvironment.ResolveLyricLabDir(configuredLyricLabPath, startDirectories);

            // An installed aligner whose version is not this build's never runs, not even the adoption
            // probe below (backlog 410). Deliberately a FAILURE rather than a quiet drop to the line
            // stamps: the player asked for word timing, and a map timed by a different aligner than
            // the one this build was made for, or silently timed by lines instead, is not what they
            // asked for. Only reached when automatic alignment is on and the lyrics need aligning
            // (a TTML or a word-stamped LRC returned above), and only for a venv that exists, since
            // nothing can run without one and that case keeps its own "not installed" handling.
            if (lyricLabDir != null && AlignerEnvironment.EnvironmentPresent(lyricLabDir))
            {
                string? refusal = AlignerEnvironment.AlignerVersionRefusal(lyricLabDir, requiredAlignerVersion);

                if (refusal != null)
                {
                    progress($"local aligner refused: {refusal}");
                    return (LyricImportResult.Fail(refusal), null, null);
                }
            }

            bool alignerUsable = lyricLabDir != null && AlignerEnvironment.EnvironmentReady(lyricLabDir);

            // A venv with no setup sentinel gets ONE probe here, where the import is already a
            // minutes-long job: a healthy install from before the sentinel is adopted and used, a
            // broken one is skipped (never deleted from here) and the player is pointed at Repair.
            if (!alignerUsable && lyricLabDir != null && AlignerEnvironment.EnvironmentPresent(lyricLabDir))
            {
                progress("checking the aligner environment...");
                alignerUsable = await AlignerEnvironment.TryAdoptEnvironmentAsync(lyricLabDir, token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                    return (LyricImportResult.Fail("import cancelled"), null, null);
            }

            string? alignerFailure = null;

            if (alignerUsable)
            {
                string lyricsTemp = Path.Combine(Path.GetTempPath(), "typebeat_align", Guid.NewGuid().ToString("N") + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(lyricsTemp)!);
                await File.WriteAllTextAsync(lyricsTemp, lyricsContent, token).ConfigureAwait(false);

                try
                {
                    (LyricImportResult alignerResult, string? timingJson, string? vocalsStem) = await runAlignerAsync(
                        lyricLabDir!, audioPath, lyricsTemp, artist, title, lyricsContent, highQualityAlignment, vocalMode, language, progress, token).ConfigureAwait(false);

                    if (alignerResult.Success && timingJson != null)
                    {
                        progress("alignment complete");
                        return (LyricImportResult.Ok(string.Empty),
                            ImportSyllables.ApplyToTimingJson(TimingJsonPostProcess.RomaniseLines(TimingJsonPostProcess.FlagFreestyleLines(timingJson), language)), vocalsStem);
                    }

                    if (token.IsCancellationRequested)
                        return (alignerResult, null, null);

                    alignerFailure = alignerResult.Error;
                    progress($"aligner unavailable ({alignerResult.Error}), trying next option");
                }
                finally
                {
                    try { File.Delete(lyricsTemp); }
                    catch { /* best-effort cleanup */ }
                }
            }
            else
            {
                progress(lyricLabDir == null
                    ? "no local aligner environment found"
                    : AlignerEnvironment.EnvironmentPresent(lyricLabDir)
                        ? $"local aligner unavailable: {AlignerEnvironment.REPAIR_SUGGESTION}"
                        : $"local aligner environment not set up (run lyriclab/{AlignerEnvironment.SetupScriptName} for word timing)");
            }

            // The aligner ran and failed: a caller that would write the result over word timing
            // gets the failure rather than line timing reported as a success.
            // Without line stamps there is nothing to fall back to anyway, and the message below also
            // tells the user how to add them, so only a refusal in place of a real fallback goes here.
            if (alignerFailure != null && !allowLineFallback && HasLineStamps(lyricsContent))
                return (LyricImportResult.Fail(AlignerRunFailedMessage(alignerFailure)), null, null);

            bool needsRepair = lyricLabDir != null && AlignerEnvironment.EnvironmentNeedsRepair(lyricLabDir);

            // LRC-only fallback: line-granularity timing straight from the line stamps, the last rung.
            // With nothing to fall back on, the hint names what the user can actually do: install the
            // local aligner, opt in to the server aligner, or stamp the lines.
            if (!HasLineStamps(lyricsContent))
            {
                return (LyricImportResult.Fail(NoFallbackFailureMessage(HasAnyLineStamp(lyricsContent), needsRepair, alignerFailure)), null, null);
            }

            var lineTimed = synthesizeFromLrc(lyricsContent, progress, language);

            // The same fallback after a failed run is not what the player asked for, so the result
            // says so rather than reading as an ordinary success.
            if (alignerFailure != null && lineTimed.Result.Success)
                return (lineTimed.Result with { Notice = LineTimedFallbackNotice(alignerFailure) }, lineTimed.TimingJson, lineTimed.VocalsStemPath);

            return lineTimed;
        }

        /// <summary>
        /// The server rung (backlog 413): one job on the server aligner, its timing.json post-processed
        /// exactly as a local run's is (freestyle flags, romanisation, import-time syllables). The
        /// server never sends a vocals stem, so the map packages without one. Failure is the server's
        /// own message and is final; cancellation reads as the cancelled import it is.
        /// </summary>
        private static async Task<(LyricImportResult Result, string? TimingJson, string? VocalsStemPath)> alignOnServerAsync(
            RemoteAligner remoteAlign, string audioPath, string lyricsContent, string artist, string title, string? language,
            AlignerVocalMode vocalMode, Action<string> progress, CancellationToken token)
        {
            RemoteAlignOutcome outcome;

            try
            {
                outcome = await remoteAlign(ServerAlignRequest(audioPath, lyricsContent, artist, title, language, vocalMode), progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return (LyricImportResult.Fail("import cancelled"), null, null);
            }

            if (!outcome.Success || outcome.TimingJson == null)
                return (LyricImportResult.Fail(outcome.Error ?? "the server aligner failed"), null, null);

            progress(ImportProgressParser.ServerProgressLine("done"));
            return (LyricImportResult.Ok(string.Empty),
                ImportSyllables.ApplyToTimingJson(TimingJsonPostProcess.RomaniseLines(TimingJsonPostProcess.FlagFreestyleLines(outcome.TimingJson), language)), null);
        }

        /// <summary>
        /// The server job for one import: the same choices the local command line carries
        /// (<see cref="AlignerEnvironment.AlignerArguments"/>). The language is what <c>--lyrics-language</c> would get
        /// (empty for none), and estimated vocals are asked for on the local rule's terms, only when
        /// the lyrics carry a line stamp to pace from. The server picks the anchors itself
        /// (<see cref="AlignerAnchorMode"/>) and always runs its default (fast) quality tier.
        /// </summary>
        public static RemoteAlignRequest ServerAlignRequest(string audioPath, string lyricsContent, string artist, string title, string? language, AlignerVocalMode vocalMode)
            => new RemoteAlignRequest(audioPath, lyricsContent, artist, title, AlignerEnvironment.AlignerLyricsLanguage(language) ?? string.Empty,
                vocalMode == AlignerVocalMode.Estimated && HasAnyLineStamp(lyricsContent) ? "estimated" : "aligned");

        /// <summary>
        /// The failure shown when no word timing could be produced and the lyrics carry no line
        /// stamps to fall back on. Three situations, three different next steps, so the sentence
        /// names the one that applies: the aligner is not installed (install it), its install is
        /// broken (repair it), or it IS installed and its run failed (retry, then repair and send the
        /// log). The third used to read as the first, telling a player with a working install to
        /// install it, which is the message that sent the 2026-10-02 log in.
        /// </summary>
        /// <param name="partialStamps">Some lines are stamped, so only the unstamped ones lack a time.</param>
        /// <param name="needsRepair">The install is present but demonstrably incomplete.</param>
        /// <param name="alignerFailure">The aligner's own error when it ran and exited non-zero; null when it never ran.</param>
        public static string NoFallbackFailureMessage(bool partialStamps, bool needsRepair, string? alignerFailure)
        {
            string stamps = partialStamps
                ? "only some lines have [mm:ss.xx] timestamps, so the unstamped ones have no time to fall back on. "
                : "the lyrics have no [mm:ss.xx] line timestamps to fall back on. ";

            if (alignerFailure != null)
            {
                return $"the local auto-aligner is installed but its run failed ({ShortAlignerFailure(alignerFailure)}), and {stamps}"
                       + "Try the import again; if it keeps failing, repair the aligner (Settings > Experimental > "
                       + "Repair local auto-aligner) and share the import log, or add [mm:ss.xx] line stamps to the lyrics.";
            }

            return "no auto-aligner is available and " + stamps
                   + (needsRepair
                       ? "The local auto-aligner's install is incomplete: repair it (Settings > Experimental > "
                         + "Repair local auto-aligner), or add [mm:ss.xx] line stamps to the lyrics."
                       : "Install the local auto-aligner (Settings > Experimental > "
                         + "Install the local auto-aligner), or add [mm:ss.xx] line stamps to the lyrics.");
        }

        /// <summary>
        /// The aligner's error for the sentence above: its tail is up to eight lines joined by " | ",
        /// and the last of them is the exception that stopped it, which is the one worth reading.
        /// Capped so a long path does not swallow the advice that follows.
        /// </summary>
        public static string ShortAlignerFailure(string error)
        {
            string last = error.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? error.Trim();
            return last.Length <= 160 ? last : last[..157] + "...";
        }

        /// <summary>
        /// The import notice for a map that fell back to its line stamps because the local aligner
        /// ran and failed: the import succeeded, but without the word timing that was asked for.
        /// </summary>
        public static string LineTimedFallbackNotice(string alignerFailure)
            => $"word timing failed: {ShortAlignerFailure(alignerFailure)}; this map is line-timed";

        /// <summary>
        /// The failure when the local aligner ran and failed and the caller allowed no line-timed
        /// fallback (the editor's re-align, which would otherwise replace word timing with it).
        /// </summary>
        public static string AlignerRunFailedMessage(string alignerFailure)
            => $"the local auto-aligner is installed but its run failed ({ShortAlignerFailure(alignerFailure)}). "
               + "Try again; if it keeps failing, repair the aligner (Settings > Experimental > "
               + "Repair local auto-aligner) and share the import log.";

        /// <summary>Line-granularity timing straight from [mm:ss.xx] line stamps (no word timing).</summary>
        private static (LyricImportResult Result, string? TimingJson, string? VocalsStemPath) synthesizeFromLrc(string lyricsContent, Action<string> progress, string? language)
        {
            string? fallbackTiming = SynthesizeTimingJsonFromLrc(lyricsContent, language);

            if (fallbackTiming == null)
                return (LyricImportResult.Fail("the line-stamped lyrics produced no usable lines."), null, null);

            progress("line-timed alignment ready (no word-level timing)");
            return (LyricImportResult.Ok(string.Empty), fallbackTiming, null);
        }

        /// <summary>
        /// Runs the aligner subprocess and returns the produced timing.json text on success, plus the
        /// isolated vocals stem it wrote into its output dir (null when none was produced, which is
        /// both a version-8-or-older script and a run that did not separate).
        /// </summary>
        private static async Task<(LyricImportResult Result, string? TimingJson, string? VocalsStemPath)> runAlignerAsync(
            string lyricLabDir, string audioPath, string lyricsPath, string artist, string title,
            string lyricsContent, bool highQuality, AlignerVocalMode vocalMode, string? language, Action<string> progress, CancellationToken token)
        {
            string outDir = Path.Combine(lyricLabDir, "out", "typebeat_import_" + SanitizeFolderName($"{artist} - {title}"));

            var psi = pythonStartInfo(lyricLabDir);

            foreach (string arg in AlignerEnvironment.AlignerArguments(lyricLabDir, audioPath, lyricsPath, outDir, lyricsContent, highQuality, vocalMode, language))
                psi.ArgumentList.Add(arg);

            if (AlignerAnchorMode(lyricsContent) == "auto")
                progress("no line stamps found, using fully automatic alignment (less accurate)");
            else if (!HasLineStamps(lyricsContent))
                progress("some lines are stamped, aligning the unstamped ones inside their sections");

            // Said up front because the run is several times longer than the player is used to.
            // Worded to claim no stage in ImportProgressParser (no "align", no "anchor"), so the
            // display holds on the preparing stage instead of jumping ahead to aligning. An install
            // older than the tiers runs without the flag (see AlignerArguments), and says why.
            if (highQuality)
            {
                if (AlignerEnvironment.AlignerHasQualityTiers(lyricLabDir))
                    progress("high-accuracy mode on, this import takes about 4x longer");
                else
                {
                    Logger.Log($"High-accuracy alignment is on but the aligner in {lyricLabDir} is version {AlignerEnvironment.ReadAlignerVersion(lyricLabDir) ?? "1"}, "
                               + $"which has no --quality; running its default", LoggingTarget.Runtime, LogLevel.Important);
                    progress(AlignerEnvironment.HIGH_QUALITY_NEEDS_UPDATE);
                }
            }

            // The map set's estimated vocals choice (backlog 354), said up front because the result
            // looks nothing like an aligned one. A choice the run cannot honour says why instead.
            string? vocalModeNotice = AlignerEnvironment.EstimatedVocalsNotice(lyricLabDir, lyricsContent, vocalMode);

            if (vocalModeNotice != null)
                progress(vocalModeNotice);

            (int exitCode, string tail) = await ImportProcessRunner.RunProcessAsync(psi, progress, token).ConfigureAwait(false);

            if (exitCode == ImportProcessRunner.CANCELLED_EXIT_CODE)
                return (LyricImportResult.Fail("import cancelled"), null, null);

            if (exitCode != 0)
                return (AlignerEnvironment.AlignerFailure(lyricLabDir, exitCode, tail), null, null);

            string? timingPath = Directory.Exists(outDir)
                ? Directory.EnumerateFiles(outDir, "*.timing.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;

            if (timingPath == null)
                return (LyricImportResult.Fail($"the aligner produced no timing.json in {outDir}"), null, null);

            string timingJson = await File.ReadAllTextAsync(timingPath, token).ConfigureAwait(false);
            return (LyricImportResult.Ok(string.Empty), timingJson, FindProducedVocalsStem(lyricLabDir, outDir));
        }

        /// <summary>
        /// The start info of one aligner script run, arguments still to add: the venv's python in the
        /// lyriclab dir, output redirected, UTF-8 (<see cref="AlignerEnvironment.RunPythonInUtf8"/>), and the venv's
        /// scripts dir first on PATH, which carries the provisioned ffmpeg (setup copies the static
        /// imageio-ffmpeg build there), so align_lyrics.py's `ffmpeg` shell-out resolves without a
        /// system-wide install. Shared by an alignment run and a <c>--separate-only</c> one.
        /// </summary>
        private static ProcessStartInfo pythonStartInfo(string lyricLabDir)
        {
            string python = AlignerEnvironment.PythonExeFor(lyricLabDir);

            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = lyricLabDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            string venvBin = Path.GetDirectoryName(python)!;
            string existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            psi.Environment["PATH"] = venvBin + Path.PathSeparator + existingPath;
            AlignerEnvironment.RunPythonInUtf8(psi);
            return psi;
        }

        /// <summary>
        /// The vocals stem wav the aligner wrote into <paramref name="outDir"/>, or null: the file is
        /// trusted only when the script's version is one that writes it (see
        /// <see cref="AlignerEnvironment.AlignerPersistsVocalsStem"/>), so a leftover file from anything else is ignored.
        /// Absent for a version-8-or-older script or a run that did not separate; that is a map with
        /// no vocals waveform, not a failure.
        /// </summary>
        internal static string? FindProducedVocalsStem(string lyricLabDir, string outDir)
        {
            if (!AlignerEnvironment.AlignerPersistsVocalsStem(lyricLabDir))
                return null;

            string stemPath = Path.Combine(outDir, VocalsStem.WAV_FILENAME);
            return File.Exists(stemPath) ? stemPath : null;
        }

        /// <summary>
        /// Builds a version-2 timing.json (line objects, no words[] -> Line granularity) from the
        /// line-stamped lyrics via the regression-anchored <see cref="LrcParser"/>. Returns null when
        /// the lyrics yield no lines. Text is emitted through a real JSON writer so punctuation,
        /// quotes and unicode escape correctly.
        ///
        /// <para>The two AUTHORING MARKS a line can carry are what break out of that shape
        /// (backlog 202), and only for the lines that carry them, so lyrics with neither serialize
        /// byte for byte as they always have:</para>
        /// <list type="bullet">
        /// <item>'&amp;' adds <c>"freestyle": true</c>, the opt-in the decoder needs before it will
        /// read an ampersand as a freestyle cell rather than as lyric punctuation.</item>
        /// <item>'|' adds a <c>words[]</c> for that line carrying the parser's interpolated word
        /// spans plus <c>syllables[]</c> / <c>split_chars</c> for the subdivided words. Emitting
        /// any words[] at all moves the WHOLE map off Line granularity (to Syllable, since the
        /// subdivisions come with it), which is the point: a pipe is a request for sub-word
        /// timing, and Line granularity persists none.</item>
        /// </list>
        /// </summary>
        public static string? SynthesizeTimingJsonFromLrc(string lyricsContent, string? language = null)
        {
            var lines = LrcParser.Parse(lyricsContent, language);

            if (lines.Count == 0)
                return null;

            return SynthesizedTimingJson.Write(ImportSyllables.Apply(lines), wordTiming: false, songEndMs: lines[^1].EndTime);
        }

        /// <summary>The progress line an enhanced LRC import reports (backlog 356).</summary>
        public const string ENHANCED_LRC_PROGRESS = "word-timed alignment from the LRC's word stamps";

        /// <summary>
        /// The import notice for an enhanced LRC whose word stamps had to be clamped (out of order,
        /// or outside their line), or null when none were.
        /// </summary>
        public static string? ClampedWordStampsNotice(int clamped) => clamped switch
        {
            <= 0 => null,
            1 => "1 word stamp was out of order and was clamped",
            _ => $"{clamped} word stamps were out of order and were clamped",
        };

        /// <summary>
        /// Builds a version-2 timing.json from an ENHANCED LRC (inline <c>&lt;mm:ss.xx&gt;</c> word
        /// stamps, backlog 356) through the same serializer the other two paths use. Unlike the
        /// plain LRC path this writes <c>words[]</c> for every word-stamped line, so the map lands at
        /// Word granularity; a line of the file without inline stamps keeps the plain line shape and
        /// the loader interpolates it, as a Word-granularity map does for any untouched line.
        /// Returns null when the lyrics yield no lines.
        /// </summary>
        public static string? SynthesizeTimingJsonFromEnhancedLrc(string lyricsContent, string? language, out int clampedWordStamps)
        {
            var lines = LrcParser.Parse(lyricsContent, language, out clampedWordStamps);

            if (lines.Count == 0)
                return null;

            // The once-at-import syllabification (backlog 363) applies here exactly as it does to the
            // plain LRC and TTML paths: a stamped word the author did not subdivide is cut by the
            // Syllabifier so the mapper can see and edit the cut.
            return SynthesizedTimingJson.Write(ImportSyllables.Apply(lines), wordTiming: true, songEndMs: lines[^1].EndTime);
        }

        /// <summary>
        /// Builds a version-2 timing.json from an Apple Music TTML, through the same serializer the
        /// LRC path uses, so a converted .ttml lands in the map as the very document the aligner
        /// would have produced. Returns null when the TTML yields no usable lines.
        ///
        /// <para>UNLIKE the LRC path this always writes <c>words[]</c>: a TTML is word-timed by
        /// construction, so the whole map comes out at Word granularity (Syllable where a word's
        /// pieces subdivide it) rather than collapsing to line stamps. The lines' own timings are
        /// the song's; the document's <c>leadingSilence</c>/<c>lyricOffset</c> are reported by
        /// <see cref="TtmlParser"/> and are NOT applied here (see its remarks), and
        /// <paramref name="offsetMs"/> is the caller's own correction for a rip that needs one.</para>
        /// </summary>
        public static string? SynthesizeTimingJsonFromTtml(string ttmlContent, double offsetMs = 0, string? language = null)
        {
            // The RAW reading, deliberately: this document is the map's stored provenance, and a
            // word that overruns its line boundary has to survive into it so the decode can derive
            // the line's seal grace (exactly as it does for an aligner document).
            if (!TtmlParser.TryParseRaw(ttmlContent, out IReadOnlyList<LyricLine> lines, out TtmlParser.TtmlMetadata metadata, offsetMs, language)
                || lines.Count == 0)
            {
                return null;
            }

            return SynthesizedTimingJson.Write(ImportSyllables.Apply(lines), wordTiming: true, songEndMs: metadata.SongEndMs ?? lines[^1].SingEndTime);
        }
    }
}
