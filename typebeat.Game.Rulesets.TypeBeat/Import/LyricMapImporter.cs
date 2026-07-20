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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
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

        public const string SetupScriptName = "setup.ps1";

        private const string aligner_script = "align_lyrics.py";

        private const int cancelled_exit_code = int.MinValue;

        /// <summary>Creator tag stamped into generated maps' [Metadata].</summary>
        public const string CREATOR = "typebeat-lyriclab";

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

        public static string PythonExeFor(string lyricLabDir)
            => Path.Combine(lyricLabDir, ".venv", "Scripts", "python.exe");

        /// <summary>
        /// One-time environment bootstrap: runs the component's setup.ps1 (venv + pinned packages,
        /// a multi-GB first-time download). No-op when the venv already exists. Not auto-invoked by
        /// <see cref="BuildOszAsync"/> (which prefers the instant LRC fallback); exposed for an
        /// explicit "set up aligner" action.
        /// </summary>
        public static async Task<LyricImportResult> BootstrapEnvironmentAsync(string lyricLabDir, Action<string> progress, CancellationToken token)
        {
            if (EnvironmentReady(lyricLabDir))
                return LyricImportResult.Ok(string.Empty);

            string script = Path.Combine(lyricLabDir, SetupScriptName);

            if (!File.Exists(script))
                return LyricImportResult.Fail($"aligner environment missing and no {SetupScriptName} to build it in {lyricLabDir}");

            progress("setting up the aligner environment — one-time download of packages (~2 GB), please wait...");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = lyricLabDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);

            (int exitCode, string tail) = await RunProcessAsync(psi, progress, token).ConfigureAwait(false);

            if (exitCode == cancelled_exit_code)
                return LyricImportResult.Fail("environment setup cancelled");

            if (exitCode != 0)
                return LyricImportResult.Fail($"environment setup exited with code {exitCode}: {tail}");

            if (!EnvironmentReady(lyricLabDir))
                return LyricImportResult.Fail($"environment setup finished but no venv python at {PythonExeFor(lyricLabDir)}");

            progress("aligner environment ready");
            return LyricImportResult.Ok(string.Empty);
        }

        /// <summary>
        /// True when every content line carries a leading [mm:ss.xx] stamp — the aligner's
        /// high-accuracy "ref" mode, and the precondition for the LRC-only fallback. Metadata tag
        /// lines ([ar:...], [Lyrics]) are neutral. Unstamped lyrics run with "--anchors auto".
        /// </summary>
        public static bool HasLineStamps(string lyricsContent)
        {
            if (string.IsNullOrWhiteSpace(lyricsContent))
                return false;

            bool anyStamp = false;

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
                        if (LrcParser.TryParseTimestamp(line.Substring(1, close - 1), out _))
                            anyStamp = true;

                        // Timestamped or metadata tag line — either way not a bare content line.
                        continue;
                    }
                }

                return false; // a content line without a stamp -> not fully stamped
            }

            return anyStamp;
        }

        /// <summary>Removes path-invalid chars, collapses whitespace, trims trailing dots/spaces.</summary>
        public static string SanitizeFolderName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);

            foreach (char c in name)
                sb.Append(invalid.Contains(c) ? ' ' : c);

            string cleaned = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
            return cleaned.Length == 0 ? "Imported Map" : cleaned;
        }

        /// <summary>Prefill guess from an "Artist - Title.mp3" style filename.</summary>
        public static (string Artist, string Title) GuessArtistTitle(string audioPath)
        {
            string stem = Path.GetFileNameWithoutExtension(audioPath).Trim();
            int sep = stem.IndexOf(" - ", StringComparison.Ordinal);

            if (sep < 0)
                return ("Unknown", stem.Length == 0 ? "Imported Map" : stem);

            string artist = stem.Substring(0, sep).Trim();
            string title = stem.Substring(sep + 3).Trim();
            return (artist.Length == 0 ? "Unknown" : artist, title.Length == 0 ? stem : title);
        }

        /// <summary>
        /// Full import: aligner subprocess when its environment is ready (word/syllable granularity),
        /// otherwise a line-granularity LRC fallback when the lyrics are line-stamped. Produces a
        /// self-contained .osz (generated .osu + audio + provenance timing.json + lyrics.txt) under a
        /// unique temp directory and returns its path. Never auto-triggers the multi-GB bootstrap.
        /// </summary>
        public static async Task<LyricImportResult> BuildOszAsync(
            string audioPath, string lyricsPath, string artist, string title,
            string? configuredLyricLabPath, IEnumerable<string> startDirectories,
            Action<string> progress, CancellationToken token, RemoteAligner? remoteAlign = null,
            bool useAutomaticAlignment = true)
        {
            if (!File.Exists(audioPath))
                return LyricImportResult.Fail($"audio file not found: {audioPath}");
            if (!File.Exists(lyricsPath))
                return LyricImportResult.Fail($"lyrics file not found: {lyricsPath}");

            string oszDir = Path.Combine(Path.GetTempPath(), "typebeat_import", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(oszDir);
            string oszPath = Path.Combine(oszDir, SanitizeFolderName($"{artist} - {title}") + ".osz");

            string lyricsContent = await File.ReadAllTextAsync(lyricsPath, token).ConfigureAwait(false);

            (LyricImportResult result, string? timing) = await ProduceTimingJsonAsync(
                audioPath, lyricsContent, artist, title, configuredLyricLabPath, startDirectories, progress, token, remoteAlign, useAutomaticAlignment).ConfigureAwait(false);

            if (!result.Success || timing == null)
                return result;

            progress("packaging map");
            return PackageOsz(oszPath, artist, title, audioPath, timing, lyricsContent);
        }

        /// <summary>
        /// Produces timing.json (v2) text from an audio file and raw lyrics WITHOUT packaging an
        /// .osz — the headless path the in-editor "align to this audio" import uses. With
        /// <paramref name="useAutomaticAlignment"/> the preference order is: local aligner
        /// subprocess (word/syllable granularity) → server-side alignment via
        /// <paramref name="remoteAlign"/> (same granularity, needs a signed-in session) →
        /// line-granularity LRC fallback. Without it, the automatic aligners are skipped entirely
        /// and only the LRC line-stamp path is used (instant, line granularity). Never triggers the
        /// ~2 GB local bootstrap. The lyrics text is written to a temp file for the aligner and
        /// cleaned up.
        /// </summary>
        public static async Task<(LyricImportResult Result, string? TimingJson)> ProduceTimingJsonAsync(
            string audioPath, string lyricsContent, string artist, string title,
            string? configuredLyricLabPath, IEnumerable<string> startDirectories,
            Action<string> progress, CancellationToken token, RemoteAligner? remoteAlign = null,
            bool useAutomaticAlignment = true)
        {
            if (!File.Exists(audioPath))
                return (LyricImportResult.Fail($"audio file not found: {audioPath}"), null);

            // Empty lyrics is its own outcome — before this it fell all the way through to the
            // confusing "no aligner available" message.
            if (string.IsNullOrWhiteSpace(lyricsContent))
                return (LyricImportResult.Fail(
                    "the lyrics are empty — add the song's words (ideally with [mm:ss.xx] line "
                    + "timestamps) before importing."), null);

            // Automatic alignment (the aligner, local or server) is opt-in: off by default so an
            // import uses the user's own line stamps without a slow round-trip. When off, jump
            // straight to the LRC line-stamp path below.
            if (!useAutomaticAlignment)
            {
                progress("automatic alignment off — using your line timestamps");

                if (!HasLineStamps(lyricsContent))
                    return (LyricImportResult.Fail(
                        "these lyrics have no [mm:ss.xx] line timestamps. Add line stamps, or turn on "
                        + "\"automatic alignment\" to have the words timed for you."), null);

                return synthesizeFromLrc(lyricsContent, progress);
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
                        return (LyricImportResult.Ok(string.Empty), timingJson);
                    }

                    if (token.IsCancellationRequested)
                        return (alignerResult, null);

                    progress($"aligner unavailable ({alignerResult.Error}) — trying next option");
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
                    : "local aligner environment not set up (run lyriclab/setup.ps1 for word timing)");
            }

            // Server-side alignment: same word-level quality, no local Python/torch needed.
            if (remoteAlign != null)
            {
                RemoteAlignOutcome outcome = await remoteAlign(audioPath, lyricsContent, artist, title, progress, token).ConfigureAwait(false);

                if (outcome.Success && outcome.TimingJson != null)
                {
                    progress("server alignment complete");
                    return (LyricImportResult.Ok(string.Empty), outcome.TimingJson);
                }

                token.ThrowIfCancellationRequested();
                progress($"server alignment unavailable ({outcome.Error}) — trying line-timed fallback");
            }

            // LRC-only fallback: line-granularity timing straight from the line stamps. The
            // failure hint depends on which aligners were even eligible: a dev build (no remote
            // delegate) points at the local lyriclab setup; a shipped build points at the server.
            if (!HasLineStamps(lyricsContent))
            {
                string reason = remoteAlign != null
                    ? "no aligner is available (locally or on the server) and the lyrics have no "
                      + "[mm:ss.xx] line timestamps to fall back on. Sign in to type!beat for "
                      + "server-side alignment, or add line stamps to the lyrics."
                    : "no local aligner is available and the lyrics have no [mm:ss.xx] line "
                      + "timestamps to fall back on. Set up the aligner (lyriclab/setup.ps1) or "
                      + "add line stamps to the lyrics.";

                return (LyricImportResult.Fail(reason), null);
            }

            return synthesizeFromLrc(lyricsContent, progress);
        }

        /// <summary>Line-granularity timing straight from [mm:ss.xx] line stamps (no word timing).</summary>
        private static (LyricImportResult Result, string? TimingJson) synthesizeFromLrc(string lyricsContent, Action<string> progress)
        {
            string? fallbackTiming = SynthesizeTimingJsonFromLrc(lyricsContent);

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

            psi.ArgumentList.Add(aligner_script);
            psi.ArgumentList.Add(audioPath);
            psi.ArgumentList.Add(lyricsPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outDir);

            if (!HasLineStamps(lyricsContent))
            {
                psi.ArgumentList.Add("--anchors");
                psi.ArgumentList.Add("auto");
                progress("no line stamps found — using fully automatic alignment (less accurate)");
            }

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
        /// </summary>
        public static string? SynthesizeTimingJsonFromLrc(string lyricsContent)
        {
            var lines = LrcParser.Parse(lyricsContent);

            if (lines.Count == 0)
                return null;

            var payload = new
            {
                version = TimingJsonLoader.SUPPORTED_VERSION,
                song_end_ms = lines[^1].EndTime,
                lines = lines.Select(l => new
                {
                    text = l.RawText,
                    start_ms = l.StartTime,
                    end_ms = l.SingEndTime,
                }).ToArray()
            };

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Zips a self-contained .osz: generated .osu (with computed preview/lead-in), the original
        /// audio, and provenance (timing.json + lyrics.txt). Overwrites <paramref name="oszPath"/>.
        /// </summary>
        public static LyricImportResult PackageOsz(string oszPath, string artist, string title, string audioSourcePath, string timingJson, string lyricsContent)
        {
            try
            {
                string audioFilename = Path.GetFileName(audioSourcePath);
                (double previewTime, double audioLeadIn) = computePolish(timingJson);

                // A video container in the audio slot doubles as the map's background video:
                // the same file is referenced from both AudioFilename and the [Events] video.
                string? videoFilename = LyricImportExtensions.IsVideo(audioSourcePath) ? audioFilename : null;

                string osuText = LyricOsuFormat.GenerateOsu(artist, title, audioFilename, CREATOR, timingJson, previewTime, audioLeadIn,
                    videoFilename: videoFilename);

                if (File.Exists(oszPath))
                    File.Delete(oszPath);

                using (var archive = ZipFile.Open(oszPath, ZipArchiveMode.Create))
                {
                    string osuName = $"{SanitizeFolderName(artist)} - {SanitizeFolderName(title)} ({CREATOR}) [typebeat].osu";

                    var osuEntry = archive.CreateEntry(osuName);
                    using (var writer = new StreamWriter(osuEntry.Open()))
                        writer.Write(osuText);

                    archive.CreateEntryFromFile(audioSourcePath, audioFilename);

                    // Provenance: original inputs travel inside the set (ignored by the game, kept for re-alignment).
                    using (var writer = new StreamWriter(archive.CreateEntry("timing.json").Open()))
                        writer.Write(timingJson);

                    using (var writer = new StreamWriter(archive.CreateEntry("lyrics.txt").Open()))
                        writer.Write(lyricsContent);
                }

                return LyricImportResult.Ok(oszPath);
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
        /// </summary>
        private static (double PreviewTime, double AudioLeadIn) computePolish(string timingJson)
        {
            const double lead_in_threshold_ms = 2000;

            double firstLineStart = 0;
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
                            break;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Defaults below.
            }

            double previewTime = songEndMs is > 0 ? songEndMs.Value * 0.4 : firstLineStart;
            double audioLeadIn = firstLineStart < lead_in_threshold_ms ? lead_in_threshold_ms : 0;
            return (previewTime, audioLeadIn);
        }

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

                progress(line.Trim());
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
