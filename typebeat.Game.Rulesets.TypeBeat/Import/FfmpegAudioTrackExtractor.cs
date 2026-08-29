// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// The one extractor that can exist on a player's machine: an external ffmpeg binary.
    ///
    /// <para>WHY NOTHING IN-PROCESS DOES THIS. The framework's bundled ffmpeg is built
    /// <c>--disable-all</c> with video demuxers and decoders only (no audio decoders, no encoders,
    /// no muxers, pipe protocol only), so it can play a video and nothing else; BASS ships with no
    /// AAC/MP4 plugin at all on Linux. So the probe order is the aligner venv's provisioned ffmpeg
    /// (the static imageio-ffmpeg build <c>lyriclab/setup</c> copies beside the venv python, present
    /// only when the optional local aligner was installed), then a plain <c>ffmpeg</c> on PATH.
    /// Neither is guaranteed, which is why "no extractor" is a first-class outcome.</para>
    ///
    /// <para>WHY IT RE-ENCODES. Stream-copying the container's AAC into an .m4a is the cheapest
    /// ffmpeg command and the most expensive everywhere else: BASS decodes no AAC on Linux, the
    /// editor's audio chooser and the browser player's content types both know only mp3/ogg/wav.
    /// So the output is mp3 (libmp3lame at 192k), falling back to ogg (libvorbis) when the binary
    /// carries no mp3 encoder. Both are already in every allow-list, so nothing downstream moves.
    /// Encoder availability is PROBED by attempting the encode and requiring a non-empty file, not
    /// assumed: the imageio-ffmpeg build's encoder set is not something the client can promise.</para>
    /// </summary>
    public class FfmpegAudioTrackExtractor : IAudioTrackExtractor
    {
        /// <summary>
        /// Output formats in preference order. Both extensions are already accepted by the import
        /// allow-list, the editor's audio chooser and the site's player, so choosing between them
        /// changes nothing downstream.
        /// </summary>
        private static readonly (string Extension, string[] CodecArgs)[] output_formats =
        {
            (".mp3", new[] { "-c:a", "libmp3lame", "-b:a", "192k" }),
            (".ogg", new[] { "-c:a", "libvorbis", "-q:a", "5" }),
        };

        /// <summary>
        /// The progress line announcing the split, emitted once an ffmpeg has been found (so a
        /// machine with none never claims to be extracting). Public because
        /// <c>ImportProgressParser</c> lives in the shell assembly and cannot see this one: the
        /// keyword it matches on is pinned against this constant rather than a copy of it.
        /// </summary>
        public const string EXTRACTING_NOTICE = "extracting audio from the video";

        private const int cancelled_exit_code = int.MinValue;

        private readonly string? configuredLyricLabPath;
        private readonly IEnumerable<string> startDirectories;

        public FfmpegAudioTrackExtractor(string? configuredLyricLabPath, IEnumerable<string> startDirectories)
        {
            this.configuredLyricLabPath = configuredLyricLabPath;
            this.startDirectories = startDirectories;
        }

        /// <summary>The provisioned ffmpeg beside an aligner venv's python (may not exist).</summary>
        public static string FfmpegExeFor(string lyricLabDir)
            => OperatingSystem.IsWindows()
                ? Path.Combine(lyricLabDir, ".venv", "Scripts", "ffmpeg.exe")
                : Path.Combine(lyricLabDir, ".venv", "bin", "ffmpeg");

        /// <summary>
        /// The ffmpeg an extraction would run, or null when there is none. The aligner venv's copy
        /// wins (it is the one the game itself provisioned), then a system-wide install.
        /// </summary>
        public static string? Resolve(string? configuredLyricLabPath, IEnumerable<string> startDirectories)
        {
            string? lyricLabDir = LyricMapImporter.ResolveLyricLabDir(configuredLyricLabPath, startDirectories);

            if (lyricLabDir != null)
            {
                string venvFfmpeg = FfmpegExeFor(lyricLabDir);

                if (File.Exists(venvFfmpeg))
                    return venvFfmpeg;
            }

            return runsOnPath("ffmpeg") ? "ffmpeg" : null;
        }

        public async Task<AudioExtractionResult> ExtractAsync(string videoPath, string outputDirectory, Action<string> progress, CancellationToken token)
        {
            string? ffmpeg = Resolve(configuredLyricLabPath, startDirectories);

            if (ffmpeg == null)
                return AudioExtractionResult.Unavailable("no ffmpeg found on this machine");

            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception e)
            {
                return AudioExtractionResult.Unavailable(e.Message);
            }

            progress(EXTRACTING_NOTICE);

            string stem = Path.GetFileNameWithoutExtension(videoPath);
            string? lastError = null;

            foreach ((string extension, string[] codecArgs) in output_formats)
            {
                if (token.IsCancellationRequested)
                    return AudioExtractionResult.Unavailable("cancelled");

                string destination = Path.Combine(outputDirectory, stem + extension);

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                // -nostdin: never let ffmpeg consume the host's stdin. -map 0:a:0 (not the optional
                // "0:a:0?") so a container with NO audio track fails loudly here and degrades to the
                // old behaviour, rather than silently packaging a zero-length audio file.
                psi.ArgumentList.Add("-nostdin");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(videoPath);
                psi.ArgumentList.Add("-vn");
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a:0");

                foreach (string arg in codecArgs)
                    psi.ArgumentList.Add(arg);

                psi.ArgumentList.Add(destination);

                (int exitCode, string tail) = await runAsync(psi, token).ConfigureAwait(false);

                if (exitCode == cancelled_exit_code)
                    return AudioExtractionResult.Unavailable("cancelled");

                if (exitCode == 0 && fileHasContent(destination))
                {
                    progress($"extracted audio to {Path.GetFileName(destination)}");
                    return AudioExtractionResult.Ok(destination);
                }

                lastError = tail.Length > 0 ? tail : $"ffmpeg exited with code {exitCode}";
                deleteQuietly(destination);
            }

            return AudioExtractionResult.Unavailable(lastError ?? "ffmpeg produced no audio");
        }

        /// <summary>Runs ffmpeg, returning its exit code and a short tail of what it complained about.</summary>
        private static async Task<(int ExitCode, string Tail)> runAsync(ProcessStartInfo psi, CancellationToken token)
        {
            using var process = new Process { StartInfo = psi };

            try
            {
                if (!process.Start())
                    return (-1, "failed to start ffmpeg");

                // Read both pipes concurrently: a full stderr buffer would deadlock the wait.
                Task<string> error = process.StandardError.ReadToEndAsync();
                Task<string> output = process.StandardOutput.ReadToEndAsync();

                await process.WaitForExitAsync(token).ConfigureAwait(false);

                string tail = summarise(await error.ConfigureAwait(false));

                if (tail.Length == 0)
                    tail = summarise(await output.ConfigureAwait(false));

                return (process.ExitCode, tail);
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

                return (cancelled_exit_code, string.Empty);
            }
            catch (Exception e)
            {
                // A missing or unrunnable binary is a degrade, never an import failure.
                return (-1, e.Message);
            }
        }

        /// <summary>The last non-empty line, clipped: this ends up inside one progress line.</summary>
        private static string summarise(string text)
        {
            string tail = string.Empty;

            foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();

                if (line.Length > 0)
                    tail = line;
            }

            return tail.Length <= 160 ? tail : tail.Substring(0, 160);
        }

        private static bool runsOnPath(string fileName)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-version");

            try
            {
                using var process = Process.Start(psi);

                if (process == null)
                    return false;

                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool fileHasContent(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && info.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void deleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // best-effort cleanup of a half-written attempt
            }
        }
    }
}
