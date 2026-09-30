// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Orchestrates the vendored lyriclab aligner (mp3 + lyrics -> word/syllable timing.json) and
// packages the result as an importable .osz. Adapted from the standalone type!beat MapImporter:
// resolution / environment / sanitisation / process running are preserved (unit-testable against
// temp dirs), but the INSTALL step now builds an .osz (via the ruleset's single .osu writer
// LyricOsuFormat.GenerateOsu) instead of writing a maps/ folder. A line-granularity LRC-only
// fallback packages a map straight from LrcParser when the aligner is unavailable.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    public static class LyricMapImporter
    {
        /// <summary>
        /// Candidate folder names, in preference order: the vendored in-repo component first,
        /// then the standalone sibling checkout it was adopted from.
        /// </summary>
        public static readonly string[] LyricLabFolderNames = { "lyriclab", "typebeat-lyriclab" };

        /// <summary>
        /// Environment bootstrap script shipped beside the aligner: PowerShell on Windows,
        /// the POSIX shell counterpart on Linux/macOS.
        /// </summary>
        public static string SetupScriptName => OperatingSystem.IsWindows() ? "setup.ps1" : "setup.sh";

        private const string aligner_script = "align_lyrics.py";

        /// <summary>
        /// Marker file written beside the venv recording which torch flavour the environment was
        /// built with ("cuda" or "cpu"). When it says cuda, alignment runs with --device cuda.
        /// </summary>
        public const string DEVICE_MARKER_FILE = "device.txt";

        private const int cancelled_exit_code = int.MinValue;

        /// <summary>Creator tag stamped into generated maps' [Metadata].</summary>
        public const string CREATOR = "typebeat-lyriclab";

        /// <summary>
        /// The timing.json (v2) of a map with NO lyrics: a valid document with an empty lines[].
        /// Everything downstream already treats "no lines" as a legitimate state (the .osu writer
        /// emits a header and nothing else, <see cref="Beatmaps.LyricBeatmapDecoder"/> produces zero
        /// hit objects, the editor loads and can author the first line), so an audio-only import
        /// needs no format of its own, just this document in place of the aligner's output.
        /// </summary>
        public const string BLANK_TIMING_JSON = "{\"version\":2,\"lines\":[]}";

        /// <summary>
        /// Locates the aligner component. An explicitly configured valid path always wins. Otherwise
        /// walk up from each start directory collecting "lyriclab" (vendored, sits at the fork's repo
        /// root) and "typebeat-lyriclab" (sibling checkout) candidates, preferring one whose venv is
        /// already set up. Start directories are probed in order (game runtime base dir, then any
        /// extras such as the working directory), so the closest ready environment wins.
        /// </summary>
        public static string? ResolveLyricLabDir(string? configuredPath, IEnumerable<string> startDirectories, int maxAscendLevels = 6)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && IsLyricLabDir(configuredPath))
                return Path.GetFullPath(configuredPath);

            var candidates = new List<string>();

            foreach (string start in startDirectories)
            {
                if (string.IsNullOrEmpty(start))
                    continue;

                DirectoryInfo? dir;

                try
                {
                    dir = new DirectoryInfo(start);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i <= maxAscendLevels && dir != null; i++)
                {
                    foreach (string name in LyricLabFolderNames)
                    {
                        string candidate = Path.Combine(dir.FullName, name);
                        if (IsLyricLabDir(candidate) && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                            candidates.Add(candidate);
                    }

                    dir = dir.Parent;
                }
            }

            if (candidates.Count == 0)
                return null;

            return candidates.FirstOrDefault(EnvironmentReady) ?? candidates[0];
        }

        /// <summary>Single-start-directory convenience overload (test parity with the standalone).</summary>
        public static string? ResolveLyricLabDir(string? configuredPath, string startDirectory, int maxAscendLevels = 6)
            => ResolveLyricLabDir(configuredPath, new[] { startDirectory }, maxAscendLevels);

        /// <summary>The aligner venv exists and is runnable.</summary>
        public static bool EnvironmentReady(string lyricLabDir) => File.Exists(PythonExeFor(lyricLabDir));

        public static bool IsLyricLabDir(string dir)
            => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, aligner_script));

        private static readonly Regex aligner_version_line = new Regex(@"^ALIGNER_VERSION\s*=\s*""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>
        /// The <c>ALIGNER_VERSION = "..."</c> the aligner script in <paramref name="lyricLabDir"/>
        /// declares, read off the source text so no Python has to run. Null for a directory with no
        /// script and for a version-1 script, which predates the constant.
        /// </summary>
        public static string? ReadAlignerVersion(string? lyricLabDir)
        {
            if (string.IsNullOrEmpty(lyricLabDir))
                return null;

            try
            {
                string path = Path.Combine(lyricLabDir, aligner_script);

                if (!File.Exists(path))
                    return null;

                var match = aligner_version_line.Match(File.ReadAllText(path));
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        public static string PythonExeFor(string lyricLabDir)
            => OperatingSystem.IsWindows()
                ? Path.Combine(lyricLabDir, ".venv", "Scripts", "python.exe")
                : Path.Combine(lyricLabDir, ".venv", "bin", "python");

        /// <summary>
        /// One-time environment bootstrap: runs the component's setup script (venv + pinned packages,
        /// a multi-GB first-time download). No-op when the venv already exists. Not auto-invoked by
        /// <see cref="BuildOszAsync"/> (which prefers the instant LRC fallback); exposed for an
        /// explicit "set up aligner" action.
        /// </summary>
        public static async Task<LyricImportResult> BootstrapEnvironmentAsync(string lyricLabDir, Action<string> progress, CancellationToken token, string device = "cpu")
        {
            if (EnvironmentReady(lyricLabDir))
                return LyricImportResult.Ok(string.Empty);

            string script = Path.Combine(lyricLabDir, SetupScriptName);

            if (!File.Exists(script))
                return LyricImportResult.Fail($"aligner environment missing and no {SetupScriptName} to build it in {lyricLabDir}");

            progress(device == "cuda"
                ? "setting up the aligner environment (GPU), one-time download of packages (~2.5 GB), please wait..."
                : "setting up the aligner environment, one-time download of packages (~2 GB), please wait...");

            var psi = new ProcessStartInfo
            {
                WorkingDirectory = lyricLabDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-File");
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add("-Device");
                psi.ArgumentList.Add(device);
            }
            else
            {
                // POSIX shells run the .sh bootstrap directly. Invoking through "bash" (rather than
                // executing the script path) means we don't depend on the executable bit surviving a
                // checkout or zip extraction.
                psi.FileName = "bash";
                psi.ArgumentList.Add(script);
                psi.ArgumentList.Add(device);
            }

            (int exitCode, string tail) = await RunProcessAsync(psi, progress, token).ConfigureAwait(false);

            if (exitCode == cancelled_exit_code)
                return LyricImportResult.Fail("environment setup cancelled");

            if (exitCode != 0)
                return LyricImportResult.Fail($"environment setup exited with code {exitCode}: {tail}");

            if (!EnvironmentReady(lyricLabDir))
                return LyricImportResult.Fail($"environment setup finished but no venv python at {PythonExeFor(lyricLabDir)}");

            // Record which torch flavour this environment carries so alignment runs pick the device.
            try
            {
                await File.WriteAllTextAsync(Path.Combine(lyricLabDir, DEVICE_MARKER_FILE), device, token).ConfigureAwait(false);
            }
            catch
            {
                // Non-fatal: alignment falls back to CPU without the marker.
            }

            progress("aligner environment ready");
            return LyricImportResult.Ok(string.Empty);
        }

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
        /// </summary>
        public static async Task<LyricImportResult> BuildOszAsync(
            string audioPath, string? lyricsPath, string artist, string title,
            string? configuredLyricLabPath, IEnumerable<string> startDirectories,
            Action<string> progress, CancellationToken token,
            bool useAutomaticAlignment = true, IAudioTrackExtractor? audioExtractor = null, string? language = null)
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
                progress("packaging map");
                return PackageOsz(oszPath, artist, title, effectiveAudioPath, BLANK_TIMING_JSON, string.Empty, videoSourcePath, language);
            }

            // The import screen's language is authoritative. Older callers without a selection
            // continue to detect a language from the lyric script.
            language ??= LyricOriginals.DetectLanguage(new[] { lyricsContent });

            (LyricImportResult result, string? timing) = await ProduceTimingJsonAsync(
                effectiveAudioPath, lyricsContent, artist, title, configuredLyricLabPath, startDirectories, progress, token, useAutomaticAlignment,
                language).ConfigureAwait(false);

            if (!result.Success || timing == null)
                return result;

            progress("packaging map");
            return PackageOsz(oszPath, artist, title, effectiveAudioPath, timing, lyricsContent, videoSourcePath, language, progress);
        }

        /// <summary>
        /// Produces timing.json (v2) text from an audio file and raw lyrics WITHOUT packaging an
        /// .osz: the headless path the in-editor "align to this audio" import uses. With
        /// <paramref name="useAutomaticAlignment"/> the preference order is: local aligner
        /// subprocess (word/syllable granularity) → line-granularity LRC fallback. Without it, the
        /// aligner is skipped entirely and only the LRC line-stamp path is used (instant, line
        /// granularity). Never triggers the ~2 GB local bootstrap. The lyrics text is written to a
        /// temp file for the aligner and cleaned up.
        ///
        /// <para>There is no third rung: the server-side aligner this ladder used to offload to is
        /// retired, so the LOCAL install (Settings &gt; Experimental) is the only automatic path and
        /// [mm:ss.xx] line stamps are the only other source of timing.</para>
        ///
        /// <para>A TTML (<see cref="TtmlParser.LooksLikeTtml"/>) short-circuits the whole ladder:
        /// the file is already word-timed, so it is converted directly and neither the aligner nor
        /// the audio is consulted. See <see cref="SynthesizeTimingJsonFromTtml"/>.</para>
        /// </summary>
        /// <remarks>The optional language is the one non-Latin lyrics are romanised under (backlog
        /// 330): the map's own when the caller has one, else null to detect it from the lyrics'
        /// script.</remarks>
        public static async Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            string? configuredLyricLabPath, IEnumerable<string> startDirectories,
            Action<string> progress, CancellationToken token, bool useAutomaticAlignment = true,
            string? language = null)
        {
            language ??= LyricOriginals.DetectLanguage(new[] { lyricsContent });

            if (!File.Exists(audioPath))
                return (LyricImportResult.Fail($"audio file not found: {audioPath}"), null);

            // Empty lyrics is its own outcome; before this it fell all the way through to the
            // confusing "no aligner available" message.
            if (string.IsNullOrWhiteSpace(lyricsContent))
                return (LyricImportResult.Fail(
                    "the lyrics are empty, add the song's words (ideally with [mm:ss.xx] line "
                    + "timestamps) before importing."), null);

            // A TTML is already word- (often syllable-) timed by its own producer, so it needs no
            // aligner, no audio and no line stamps: it is converted and used as it stands. Checked
            // FIRST because a TTML is an XML document that would otherwise be handed to the aligner
            // as if it were a lyrics file, which is exactly the mistake this replaces.
            if (TtmlParser.LooksLikeTtml(lyricsContent))
            {
                string? ttmlTiming = SynthesizeTimingJsonFromTtml(lyricsContent, language: language);

                if (ttmlTiming == null)
                    return (LyricImportResult.Fail("the .ttml produced no usable lyric lines."), null);

                progress("using the TTML's own word timing");
                return (LyricImportResult.Ok(string.Empty), ttmlTiming);
            }

            // Automatic alignment (the local aligner subprocess) is opt-in: off by default so an
            // import uses the user's own line stamps without a slow multi-minute run. When off, jump
            // straight to the LRC line-stamp path below.
            if (!useAutomaticAlignment)
            {
                progress("automatic alignment off, using your line timestamps");

                if (!HasLineStamps(lyricsContent))
                    return (LyricImportResult.Fail(HasAnyLineStamp(lyricsContent)
                        ? "only some lines have [mm:ss.xx] timestamps, and without automatic alignment every "
                          + "line needs one. Stamp the rest, or turn on \"automatic alignment\" to have the "
                          + "unstamped lines placed between your stamps."
                        : "these lyrics have no [mm:ss.xx] line timestamps. Add line stamps, or turn on "
                          + "\"automatic alignment\" to have the words timed for you."), null);

                return synthesizeFromLrc(lyricsContent, progress, language);
            }

            string? lyricLabDir = ResolveLyricLabDir(configuredLyricLabPath, startDirectories);
            bool alignerUsable = lyricLabDir != null && EnvironmentReady(lyricLabDir);

            if (alignerUsable)
            {
                string lyricsTemp = Path.Combine(Path.GetTempPath(), "typebeat_align", Guid.NewGuid().ToString("N") + ".txt");
                Directory.CreateDirectory(Path.GetDirectoryName(lyricsTemp)!);
                await File.WriteAllTextAsync(lyricsTemp, lyricsContent, token).ConfigureAwait(false);

                try
                {
                    (LyricImportResult alignerResult, string? timingJson) = await runAlignerAsync(
                        lyricLabDir!, audioPath, lyricsTemp, artist, title, lyricsContent, progress, token).ConfigureAwait(false);

                    if (alignerResult.Success && timingJson != null)
                    {
                        progress("alignment complete");
                        return (LyricImportResult.Ok(string.Empty), RomaniseLines(FlagFreestyleLines(timingJson), language));
                    }

                    if (token.IsCancellationRequested)
                        return (alignerResult, null);

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
                    : $"local aligner environment not set up (run lyriclab/{SetupScriptName} for word timing)");
            }

            // LRC-only fallback: line-granularity timing straight from the line stamps, and the last
            // rung now that server-side alignment is retired. With nothing to fall back on, the hint
            // names the two things the user can actually do: install the local aligner, or stamp the
            // lines. It no longer offers signing in, which bought alignment and now buys nothing.
            if (!HasLineStamps(lyricsContent))
            {
                return (LyricImportResult.Fail(
                    (HasAnyLineStamp(lyricsContent)
                        ? "no auto-aligner is available and only some lines have [mm:ss.xx] timestamps, "
                          + "so the unstamped ones have no time to fall back on. "
                        : "no auto-aligner is available and the lyrics have no [mm:ss.xx] line timestamps "
                          + "to fall back on. ")
                    + "Install the local auto-aligner (Settings > Experimental > "
                    + "Install the local auto-aligner), or add [mm:ss.xx] line stamps to the lyrics."), null);
            }

            return synthesizeFromLrc(lyricsContent, progress, language);
        }

        /// <summary>Line-granularity timing straight from [mm:ss.xx] line stamps (no word timing).</summary>
        private static (LyricImportResult Result, string? TimingJson) synthesizeFromLrc(string lyricsContent, Action<string> progress, string? language)
        {
            string? fallbackTiming = SynthesizeTimingJsonFromLrc(lyricsContent, language);

            if (fallbackTiming == null)
                return (LyricImportResult.Fail("the line-stamped lyrics produced no usable lines."), null);

            progress("line-timed alignment ready (no word-level timing)");
            return (LyricImportResult.Ok(string.Empty), fallbackTiming);
        }

        /// <summary>Runs the aligner subprocess and returns the produced timing.json text on success.</summary>
        private static async Task<(LyricImportResult Result, string? TimingJson)> runAlignerAsync(
            string lyricLabDir, string audioPath, string lyricsPath, string artist, string title,
            string lyricsContent, Action<string> progress, CancellationToken token)
        {
            string python = PythonExeFor(lyricLabDir);
            string outDir = Path.Combine(lyricLabDir, "out", "typebeat_import_" + SanitizeFolderName($"{artist} - {title}"));

            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = lyricLabDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // The venv's scripts dir carries the provisioned ffmpeg (setup copies the static
            // imageio-ffmpeg build there); prepend it so align_lyrics.py's `ffmpeg` shell-out
            // resolves without a system-wide install.
            string venvBin = Path.GetDirectoryName(python)!;
            string existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            psi.Environment["PATH"] = venvBin + Path.PathSeparator + existingPath;

            psi.ArgumentList.Add(aligner_script);
            psi.ArgumentList.Add(audioPath);
            psi.ArgumentList.Add(lyricsPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outDir);

            // Environments built with CUDA torch (device marker "cuda") align on the GPU,
            // dramatically faster separation/emission on machines with a good NVIDIA card.
            string deviceMarker = Path.Combine(lyricLabDir, DEVICE_MARKER_FILE);

            if (File.Exists(deviceMarker) && File.ReadAllText(deviceMarker).Trim().Equals("cuda", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("--device");
                psi.ArgumentList.Add("cuda");
            }

            // Explicit either way: "ref" as soon as ONE line is stamped (sparse anchors place the
            // unstamped lines inside their section's window), "auto" only for bare text.
            string anchorMode = AlignerAnchorMode(lyricsContent);
            psi.ArgumentList.Add("--anchors");
            psi.ArgumentList.Add(anchorMode);

            if (anchorMode == "auto")
                progress("no line stamps found, using fully automatic alignment (less accurate)");
            else if (!HasLineStamps(lyricsContent))
                progress("some lines are stamped, aligning the unstamped ones inside their sections");

            (int exitCode, string tail) = await RunProcessAsync(psi, progress, token).ConfigureAwait(false);

            if (exitCode == cancelled_exit_code)
                return (LyricImportResult.Fail("import cancelled"), null);

            if (exitCode != 0)
                return (LyricImportResult.Fail($"aligner exited with code {exitCode}: {tail}"), null);

            string? timingPath = Directory.Exists(outDir)
                ? Directory.EnumerateFiles(outDir, "*.timing.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;

            if (timingPath == null)
                return (LyricImportResult.Fail($"the aligner produced no timing.json in {outDir}"), null);

            string timingJson = await File.ReadAllTextAsync(timingPath, token).ConfigureAwait(false);
            return (LyricImportResult.Ok(string.Empty), timingJson);
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

            return SynthesizedTimingJson.Write(lines, wordTiming: false, songEndMs: lines[^1].EndTime);
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

            return SynthesizedTimingJson.Write(lines, wordTiming: true, songEndMs: metadata.SongEndMs ?? lines[^1].SingEndTime);
        }

        /// <summary>
        /// Marks every line of an ALIGNER-produced timing.json whose text carries a '&amp;' with
        /// <c>"freestyle": true</c> (backlog 202). The aligner passes the mapper's markers through
        /// in the line text but knows nothing about the flag, and without it the decoder reads the
        /// ampersand as ordinary lyric punctuation and strips it. A document with no ampersand
        /// anywhere is returned VERBATIM, so nothing an aligner has ever produced before moves.
        /// Any parse problem also returns the input unchanged: this is a polish pass, not a gate.
        /// </summary>
        public static string FlagFreestyleLines(string timingJson)
        {
            if (string.IsNullOrEmpty(timingJson) || timingJson.IndexOf(Typeability.FREESTYLE_MARKER) < 0)
                return timingJson;

            try
            {
                if (JsonNode.Parse(timingJson) is not JsonObject root
                    || root["lines"] is not JsonArray lines)
                {
                    return timingJson;
                }

                bool changed = false;

                foreach (JsonNode? node in lines)
                {
                    if (node is not JsonObject line || line["freestyle"] != null)
                        continue;

                    if (line["text"]?.GetValue<string>() is not string text || text.IndexOf(Typeability.FREESTYLE_MARKER) < 0)
                        continue;

                    line["freestyle"] = true;
                    changed = true;
                }

                return changed ? root.ToJsonString() : timingJson;
            }
            catch (Exception)
            {
                return timingJson;
            }
        }

        /// <summary>
        /// THE ORIGINAL TEXT for an ALIGNER-produced timing.json (backlog 330): every line (and word)
        /// whose text needs romanising gets the romanisation as its <c>text</c> and the source as its
        /// <c>original</c>, instead of reaching the decode raw and being deleted there. The LRC and
        /// TTML paths romanise as they parse (<see cref="LrcParser.Parse"/>,
        /// <see cref="TtmlParser.TryParseRaw"/>); this is the same rule for the one document that
        /// arrives already written.
        ///
        /// <para>With words[], a line whose source tokens match its words one for one pairs them
        /// (the line text is authoritative for spelling, as the decode's pairing is), and otherwise
        /// each word is romanised from its own text; the line's text is then the romanised words
        /// joined, so the pairing still holds. A word the romaniser cannot spell is written with an
        /// empty text beside its original and its syllables dropped. A word whose syllables spell
        /// its source exactly carries its cuts into the romanisation through the romaniser's units
        /// (see <see cref="LyricOriginals.RomanisedWord.MapSplits"/>), merging the syllables whose
        /// cut does not carry. Without words[] the line is romanised as a whole and an unspellable
        /// word survives only in the line's original. A document with nothing to romanise is
        /// returned VERBATIM, and any parse problem returns the input unchanged.</para>
        /// </summary>
        public static string RomaniseLines(string timingJson, string? language)
        {
            if (string.IsNullOrEmpty(timingJson))
                return timingJson;

            try
            {
                if (JsonNode.Parse(timingJson) is not JsonObject root || root["lines"] is not JsonArray lines)
                    return timingJson;

                bool changed = false;

                foreach (JsonNode? node in lines)
                {
                    if (node is not JsonObject line || line["text"] is not JsonValue textValue || !textValue.TryGetValue(out string? text))
                        continue;

                    var words = line["words"] as JsonArray;

                    bool needs(string? s) => LyricOriginals.CarriesOriginal(s) || Romaniser.NeedsRomanising(s, language);

                    if (!needs(text) && (words == null || !words.OfType<JsonObject>().Any(w => w["text"] is JsonValue v && v.TryGetValue(out string? t) && needs(t))))
                        continue;

                    changed = true;
                    string[] sourceTokens = LyricOriginals.CollapseWhitespace(text).Split(' ');

                    if (words == null || words.Count == 0)
                    {
                        var romanisedLine = LyricOriginals.RomaniseLine(text, language);
                        line["text"] = romanisedLine.Text;

                        if (romanisedLine.Original != null)
                            setAfterText(line, romanisedLine.Original);

                        continue;
                    }

                    bool byLineTokens = words.Count == sourceTokens.Length;
                    var expanded = new JsonArray();

                    for (int i = 0; i < words.Count; i++)
                    {
                        if (words[i] is not JsonObject sourceWord)
                            continue;

                        string source = byLineTokens
                            ? sourceTokens[i]
                            : sourceWord["text"] is JsonValue value && value.TryGetValue(out string? raw) ? raw ?? string.Empty : string.Empty;

                        foreach (JsonObject part in splitJapaneseTimedWord(sourceWord, source, language))
                            expanded.Add(part);
                    }

                    line["words"] = expanded;
                    var typed = new List<string>();
                    bool anyFlagged = false;

                    foreach (JsonNode? entry in expanded)
                    {
                        if (entry is not JsonObject word)
                            continue;

                        string source = word["text"] is JsonValue wv && wv.TryGetValue(out string? wt) ? wt ?? string.Empty : string.Empty;

                        var romanised = LyricOriginals.RomaniseWord(source, language);

                        if (romanised.Flagged)
                        {
                            anyFlagged = true;
                            word["text"] = string.Empty;
                            setAfterText(word, romanised.Original);
                            word.Remove("syllables");
                            word.Remove("split_chars");
                            continue;
                        }

                        word["text"] = romanised.Text;

                        if (romanised.Original != null)
                            setAfterText(word, romanised.Original);

                        carrySyllables(word, romanised);

                        if (romanised.Text.Length > 0)
                            typed.Add(romanised.Text);
                    }

                    line["text"] = string.Join(' ', typed);

                    string collapsed = LyricOriginals.CollapseWhitespace(text);

                    if ((anyFlagged || LyricOriginals.CarriesOriginal(collapsed)) && collapsed != string.Join(' ', typed))
                        setAfterText(line, collapsed);
                }

                return changed ? root.ToJsonString() : timingJson;
            }
            catch (Exception)
            {
                return timingJson;
            }
        }

        /// <summary>
        /// A timed Japanese phrase may arrive as one aligner word with its pieces listed as
        /// syllables. Move dictionary word boundaries into words[] before romanisation. Existing
        /// syllable times are retained inside each new word; a boundary inside a timed piece is
        /// interpolated across that piece.
        /// </summary>
        private static IReadOnlyList<JsonObject> splitJapaneseTimedWord(JsonObject word, string source, string? language)
        {
            IReadOnlyList<string>? parts = JapaneseReading.Segment(source, language);

            if (parts == null)
            {
                var unchanged = (JsonObject)word.DeepClone();
                unchanged["text"] = source;
                return new[] { unchanged };
            }

            if (!readMs(word["start_ms"], out double wordStart) || !readMs(word["end_ms"], out double wordEnd) || wordEnd <= wordStart)
            {
                var unchanged = (JsonObject)word.DeepClone();
                unchanged["text"] = source;
                return new[] { unchanged };
            }

            var syllables = new List<(string Text, double Start, double End)>();

            if (word["syllables"] is JsonArray authored)
            {
                foreach (JsonNode? node in authored)
                {
                    if (node is not JsonObject syllable || syllable["text"] is not JsonValue value
                        || !value.TryGetValue(out string? text) || text == null
                        || !readMs(syllable["start_ms"], out double start)
                        || !readMs(syllable["end_ms"], out double end))
                    {
                        syllables.Clear();
                        break;
                    }

                    syllables.Add((text, start, end));
                }
            }

            if (string.Concat(syllables.Select(s => s.Text)) != source)
                syllables.Clear();

            var result = new List<JsonObject>(parts.Count);
            int partStart = 0;

            foreach (string part in parts)
            {
                int partEnd = partStart + part.Length;
                var split = (JsonObject)word.DeepClone();
                split["text"] = part;
                split.Remove("original");
                split.Remove("split_chars");
                split.Remove("syllables");

                if (syllables.Count == 0)
                {
                    split["start_ms"] = wordStart + (wordEnd - wordStart) * partStart / source.Length;
                    split["end_ms"] = wordStart + (wordEnd - wordStart) * partEnd / source.Length;
                }
                else
                {
                    var pieces = new JsonArray();
                    int syllableStart = 0;

                    foreach (var syllable in syllables)
                    {
                        int syllableEnd = syllableStart + syllable.Text.Length;
                        int from = Math.Max(partStart, syllableStart);
                        int to = Math.Min(partEnd, syllableEnd);

                        if (from < to)
                        {
                            double duration = syllable.End - syllable.Start;
                            double start = syllable.Start + duration * (from - syllableStart) / syllable.Text.Length;
                            double end = syllable.Start + duration * (to - syllableStart) / syllable.Text.Length;
                            pieces.Add(new JsonObject
                            {
                                ["text"] = source.Substring(from, to - from),
                                ["start_ms"] = start,
                                ["end_ms"] = end,
                            });
                        }

                        syllableStart = syllableEnd;
                    }

                    split["start_ms"] = ((JsonObject)pieces[0]!)["start_ms"]!.DeepClone();
                    split["end_ms"] = ((JsonObject)pieces[^1]!)["end_ms"]!.DeepClone();
                    split["syllables"] = pieces;
                }

                result.Add(split);
                partStart = partEnd;
            }

            return result;
        }

        private static bool readMs(JsonNode? value, out double milliseconds)
            => double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds);

        /// <summary>
        /// Sets <c>"original"</c> on <paramref name="node"/> straight after its <c>"text"</c>, the
        /// position every writer of the format puts it in (see <see cref="LyricOriginals"/>).
        /// </summary>
        private static void setAfterText(JsonObject node, string? original)
        {
            var entries = node.Where(kv => kv.Key != "original").ToList();
            node.Clear();

            foreach (var (key, value) in entries)
            {
                node[key] = value;

                if (key == "text")
                    node["original"] = original;
            }
        }

        /// <summary>
        /// A romanised aligner word's syllables (see <see cref="RomaniseLines"/>): when the syllable
        /// texts spell the word's source exactly, the source cuts between them are carried into the
        /// romanisation as <c>split_chars</c>, a syllable whose cut does not carry merging into the
        /// one before it (its end becomes that syllable's end). Otherwise the syllables' times are
        /// left alone and the split is derived at decode, as for any word without one.
        /// </summary>
        private static void carrySyllables(JsonObject word, LyricOriginals.RomanisedWord romanised)
        {
            if (word["syllables"] is not JsonArray syllables || syllables.Count < 2)
                return;

            var texts = new List<string>();

            foreach (JsonNode? syllable in syllables)
            {
                if (syllable is not JsonObject s || s["text"] is not JsonValue v || !v.TryGetValue(out string? t) || t == null)
                    return;

                texts.Add(t);
            }

            if (string.Concat(texts) != romanised.Source)
                return;

            var cuts = new List<int>();
            int at = 0;

            for (int i = 0; i < texts.Count - 1; i++)
            {
                at += texts[i].Length;
                cuts.Add(at);
            }

            var kept = romanised.MapSplits(cuts);
            var keptCuts = new HashSet<int>(kept.Select(k => k.SourceCut));
            var merged = new JsonArray();
            JsonObject? current = null;

            for (int i = 0; i < syllables.Count; i++)
            {
                var syllable = (JsonObject)syllables[i]!.DeepClone();

                // Syllable i starts at cut i - 1; a cut that did not carry folds it into the one before.
                if (current != null && !keptCuts.Contains(i - 1))
                {
                    current["end_ms"] = syllable["end_ms"]?.DeepClone();
                    continue;
                }

                merged.Add(syllable);
                current = syllable;
            }

            // The syllable texts now spell the romanisation, cut where the kept splits say.
            var splits = kept.Select(k => k.TextSplit).ToList();
            var segmentStarts = new List<int> { 0 };
            segmentStarts.AddRange(splits);
            segmentStarts.Add(romanised.Text.Length);

            for (int i = 0; i < merged.Count && i + 1 < segmentStarts.Count; i++)
                ((JsonObject)merged[i]!)["text"] = romanised.Text.Substring(segmentStarts[i], segmentStarts[i + 1] - segmentStarts[i]);

            word["syllables"] = merged;

            if (splits.Count > 0)
            {
                var array = new JsonArray();

                foreach (int split in splits)
                    array.Add(split);

                word["split_chars"] = array;
            }
            else
            {
                word.Remove("split_chars");
            }
        }

        /// <summary>
        /// THE IMPORT-SIDE BACKING-VOCAL STRIP (backlog 255). Removes every bracketed span from the
        /// line texts of an ALIGNER-produced timing.json and drops the lines that are left with
        /// nothing to type, so the .osu an import writes never stores a bracket in the first place.
        ///
        /// <para>It exists because the strip used to happen on DECODE, and the map format now
        /// preserves a literal '(' instead. The two halves of the owner's decision meet exactly
        /// here: the file being ingested still loses its backing vocals, and everything downstream
        /// of the stored map treats a bracket as ordinary punctuation. Because
        /// <see cref="LyricOsuFormat.GenerateOsu"/> re-emits these line objects verbatim into
        /// [Lyrics], stripping here is what keeps an import's decoded lines byte-identical to what
        /// <see cref="TimingJsonLoader.TryLoad"/> reports for the same timing.json.</para>
        ///
        /// <para>Only the line <c>text</c> is touched: words[] is deliberately left alone, so a
        /// PARTIAL strip still changes the token count and still sends that line to the
        /// interpolation fallback in <see cref="TimingJsonLoader.BuildLines"/>, exactly as the
        /// decode-side strip did. A whole bracketed line is REMOVED from lines[], which is the same
        /// outcome as the decode dropping it (the previous line's window runs to the next surviving
        /// line's start). A document whose line texts carry no bracket at all is returned VERBATIM,
        /// so no map that could not have been affected has a single byte moved, and any parse
        /// problem also returns the input unchanged: this is a polish pass, not a gate.</para>
        /// </summary>
        public static string StripBackingVocalLines(string timingJson)
        {
            if (string.IsNullOrEmpty(timingJson))
                return timingJson;

            try
            {
                if (JsonNode.Parse(timingJson) is not JsonObject root
                    || root["lines"] is not JsonArray lines)
                {
                    return timingJson;
                }

                var kept = new JsonArray();
                bool changed = false;

                foreach (JsonNode? node in lines)
                {
                    // A non-object element, or one whose "text" is missing or not a string, is
                    // carried through untouched: the decode rejects it on its own terms, and
                    // second-guessing that here would be a second copy of the same rule.
                    if (node is not JsonObject line
                        || line["text"] is not JsonValue textValue
                        || !textValue.TryGetValue(out string? text))
                    {
                        kept.Add(node?.DeepClone());
                        continue;
                    }

                    string stripped = Typeability.StripBackingVocals(text);

                    if (stripped == text)
                    {
                        kept.Add(line.DeepClone());
                        continue;
                    }

                    changed = true;

                    // The same emptiness rule the decode applies, so an import cannot store a line
                    // the decode would then drop (or drop one it would have kept).
                    bool freestyle = line["freestyle"] is JsonValue flag && flag.TryGetValue(out bool on) && on;
                    string normalized = Typeability.Normalize(stripped, keepFreestyleMarkers: freestyle, keepSplitMarkers: true);

                    if (TimingJsonLoader.YieldsNoCells(normalized))
                        continue;

                    // The STRIPPED RAW text, not the normalized form: the rest of the normalization
                    // still happens on decode, exactly where it always did.
                    var copy = (JsonObject)line.DeepClone();
                    copy["text"] = stripped;
                    kept.Add(copy);
                }

                if (!changed)
                    return timingJson;

                root["lines"] = kept;
                return root.ToJsonString();
            }
            catch (Exception)
            {
                return timingJson;
            }
        }

        /// <summary>
        /// Zips a self-contained .osz: generated .osu (with computed preview/lead-in), the original
        /// audio, and provenance (timing.json + lyrics.txt). Overwrites <paramref name="oszPath"/>.
        ///
        /// <para><paramref name="videoSourcePath"/> is the map's background video when it is its OWN
        /// file (an import that split a container: audio and video are two entries in the archive).
        /// Left null, a container still sitting in the audio slot doubles as the video exactly as it
        /// did before the split existed, and the archive holds the one file. Either way the [Events]
        /// line is written at offset 0: an extraction is sample-accurate, so there is nothing to
        /// correct, and 0 is the byte-identical legacy <c>Video,0,"file"</c> form.</para>
        /// </summary>
        /// <remarks>
        /// The language is written into the map metadata whenever supplied, including for a blank
        /// map. Callers without a language leave it unspecified. The optional progress callback receives the
        /// import summary line (the words that could not be romanised), which also comes back as
        /// <see cref="LyricImportResult.Notice"/>.
        /// </remarks>
        public static LyricImportResult PackageOsz(string oszPath, string artist, string title, string audioSourcePath, string timingJson, string lyricsContent,
                                                   string? videoSourcePath = null, string? language = null, Action<string>? progress = null)
        {
            try
            {
                // THE IMPORT SUMMARY (backlog 330): what the stored map will decode to, read through
                // the loader itself, so the words listed are exactly the ones the editor will show
                // as unromanised.
                string? notice = null;
                if (TimingJsonLoader.TryParse(timingJson, out IReadOnlyList<LyricLine> decodedLines))
                {
                    notice = LyricOriginals.ImportSummary(LyricOriginals.UnromanisedWords(decodedLines).ToList());

                    if (notice != null)
                        progress?.Invoke(notice);
                }

                string? offeredLanguage = string.IsNullOrWhiteSpace(language) ? null : LyricOriginals.OfferedLanguage(language).ToCanonicalName();

                string audioFilename = Path.GetFileName(audioSourcePath);

                // Deliberately the UNSTRIPPED document: preview point and lead-in are read off the
                // aligner's own line times, so they are the same numbers this map has always got.
                (double previewTime, double audioLeadIn) = computePolish(timingJson);

                string? videoSource = videoSourcePath ?? (LyricImportExtensions.IsVideo(audioSourcePath) ? audioSourcePath : null);
                string? videoFilename = videoSource == null ? null : Path.GetFileName(videoSource);

                // The import boundary (backlog 255): backing vocals leave the lyric HERE, on the way
                // into the stored map, because the decode now reads a bracket as a literal mark.
                // The provenance timing.json written below keeps the aligner's text untouched.
                string osuText = LyricOsuFormat.GenerateOsu(artist, title, audioFilename, CREATOR, StripBackingVocalLines(timingJson),
                    previewTime, audioLeadIn, videoFilename: videoFilename, language: string.IsNullOrEmpty(offeredLanguage) ? null : offeredLanguage);

                if (File.Exists(oszPath))
                    File.Delete(oszPath);

                using (var archive = ZipFile.Open(oszPath, ZipArchiveMode.Create))
                {
                    string osuName = $"{SanitizeFolderName(artist)} - {SanitizeFolderName(title)} ({CREATOR}) [typebeat].osu";

                    var osuEntry = archive.CreateEntry(osuName);
                    using (var writer = new StreamWriter(osuEntry.Open()))
                        writer.Write(osuText);

                    archive.CreateEntryFromFile(audioSourcePath, audioFilename);

                    // The video only earns an entry of its own when it IS its own file; an
                    // unsplit container is already in the archive under the audio's name, and a
                    // second entry with that name would be a duplicate the importer would reject.
                    if (videoSource != null && !string.Equals(videoFilename, audioFilename, StringComparison.OrdinalIgnoreCase))
                        archive.CreateEntryFromFile(videoSource, videoFilename!);

                    // Provenance: original inputs travel inside the set (ignored by the game, kept for re-alignment).
                    using (var writer = new StreamWriter(archive.CreateEntry("timing.json").Open()))
                        writer.Write(timingJson);

                    using (var writer = new StreamWriter(archive.CreateEntry("lyrics.txt").Open()))
                        writer.Write(lyricsContent);
                }

                return LyricImportResult.Ok(oszPath) with { Notice = notice };
            }
            catch (ArgumentException e)
            {
                return LyricImportResult.Fail($"the map data was rejected: {e.Message}");
            }
            catch (Exception e)
            {
                return LyricImportResult.Fail($"packaging the map failed: {e.Message}");
            }
        }

        /// <summary>
        /// Preview point (~40% through the song, else the first line) and a lead-in when the first
        /// line starts within 2s. Parses the timing.json defensively; any problem -> sane defaults.
        /// A map with no lines at all (a blank import) gets no preview point and no lead-in: there
        /// is no first line to lead into, and -1 is the format's "unset" preview.
        /// </summary>
        private static (double PreviewTime, double AudioLeadIn) computePolish(string timingJson)
        {
            const double lead_in_threshold_ms = 2000;

            double firstLineStart = 0;
            bool anyLine = false;
            double? songEndMs = null;

            try
            {
                using var doc = JsonDocument.Parse(timingJson);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("song_end_ms", out JsonElement end) && end.ValueKind == JsonValueKind.Number)
                    songEndMs = end.GetDouble();

                if (root.TryGetProperty("lines", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement line in lines.EnumerateArray())
                    {
                        if (line.ValueKind == JsonValueKind.Object
                            && line.TryGetProperty("start_ms", out JsonElement start)
                            && start.ValueKind == JsonValueKind.Number)
                        {
                            firstLineStart = start.GetDouble();
                            anyLine = true;
                            break;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Defaults below.
            }

            if (!anyLine)
                return (-1, 0);

            double previewTime = songEndMs is > 0 ? songEndMs.Value * 0.4 : firstLineStart;
            double audioLeadIn = firstLineStart < lead_in_threshold_ms ? lead_in_threshold_ms : 0;
            return (previewTime, audioLeadIn);
        }

        // Demucs (vocal separation) streams a tqdm bar measured in audio-seconds, emitting raw
        // float counters like "169.64999999999998/269.09999999999997" that render absurdly long.
        // Round any 3+-decimal number in a progress line down to one decimal place for display.
        private static readonly Regex noisy_decimal = new Regex(@"\d+\.\d{3,}", RegexOptions.Compiled);

        private static string trimNoisyDecimals(string line) =>
            noisy_decimal.Replace(line, m =>
                double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    ? v.ToString("0.#", CultureInfo.InvariantCulture)
                    : m.Value);

        /// <summary>
        /// Runs a redirected process streaming non-empty output lines to <paramref name="progress"/>
        /// (background thread!). Returns the exit code (<see cref="cancelled_exit_code"/> when the
        /// token fired and the process tree was killed) and the last few output lines.
        /// </summary>
        private static async Task<(int ExitCode, string Tail)> RunProcessAsync(ProcessStartInfo psi, Action<string> progress, CancellationToken token)
        {
            var tail = new Queue<string>();
            using var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (_, e) => report(e.Data);
            process.ErrorDataReceived += (_, e) => report(e.Data);

            void report(string? line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    return;

                lock (tail)
                {
                    tail.Enqueue(line);
                    while (tail.Count > 8)
                        tail.Dequeue();
                }

                progress(trimNoisyDecimals(line.Trim()));
            }

            string tailString()
            {
                lock (tail)
                    return string.Join(" | ", tail);
            }

            try
            {
                if (!process.Start())
                    return (-1, "failed to start process");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // already exited
                }

                return (cancelled_exit_code, tailString());
            }

            return (process.ExitCode, tailString());
        }
    }
}
