// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// The coarse steps an import walks through, in the order a run reaches them. The import
    /// pipeline (<c>ILyricMapImporter</c> plus the vendored lyriclab python) only speaks a stream of
    /// free-form text lines, so the presentation layer classifies each line into one of these.
    /// </summary>
    public enum ImportStage
    {
        Preparing,

        /// <summary>
        /// Pulling a dropped video container's audio track out to a file of its own, so the map
        /// carries a real audio file and the container is only its background video. Skipped
        /// entirely for an audio import, and for a video import on a machine with no extractor.
        /// </summary>
        ExtractingAudio,

        /// <summary>
        /// Sending the audio and lyrics to the server aligner (backlog 413). Only an import that
        /// opted in to the server reaches it, and the three server stages replace the local ones
        /// below rather than preceding them.
        /// </summary>
        Uploading,

        /// <summary>The server has the job and it is queued behind others; the row's note carries the queue position.</summary>
        WaitingForServer,

        /// <summary>
        /// The server's aligner is running the job. Its own log lines are relayed under
        /// <see cref="ImportProgressParser.SERVER_PROGRESS_PREFIX"/>, so they drive this one row's bar
        /// and note instead of growing the local stages as if they ran here.
        /// </summary>
        ServerAligning,

        SeparatingVocals,
        LoadingModel,
        AligningLyrics,

        /// <summary>
        /// A momentary notice that the aligner that was running gave up and the next option is being
        /// tried (local aligner -> line stamps). Never a long-lived stage. The server aligner never
        /// falls back: a failed server job fails the import with the server's own reason.
        /// </summary>
        FallingBack,

        Packaging,
        Importing,
    }

    /// <summary>Outcome of classifying one raw progress line.</summary>
    /// <param name="Stage">
    /// The stage the line belongs to, or null when the line carries no stage information (a bare
    /// tqdm bar, a python warning, a torch deprecation notice). Callers keep the stage they were on.
    /// </param>
    /// <param name="Progress">A 0-1 fraction when the line carries one, else null.</param>
    /// <param name="Note">
    /// A short note for the stage's row when the line carries one: the queue position while waiting
    /// for the server, or which step the server's aligner is on. Null leaves the row's note alone.
    /// </param>
    public readonly record struct ImportProgressUpdate(ImportStage? Stage, float? Progress, string? Note = null);

    /// <summary>
    /// Turns the import pipeline's raw progress chatter into a stage plus an optional completion
    /// fraction, so the UI can show a short human label and a bar instead of relaying internals
    /// ("separation: running demucs (htdemucs) on cpu ...", "emissions: chunk 7/31 frames=1490",
    /// "loading MMS_FA aligner model (first run downloads ~1.2 GB)..."). Pure and side-effect free:
    /// the raw lines still go to the log, only the display is summarised.
    /// </summary>
    public static class ImportProgressParser
    {
        /// <summary>Short, lowercase summary of a stage, matching the import screen's copy.</summary>
        public static string LabelFor(ImportStage stage)
        {
            switch (stage)
            {
                case ImportStage.Preparing:
                    return "preparing";

                case ImportStage.ExtractingAudio:
                    return "extracting the audio";

                case ImportStage.Uploading:
                    return "uploading to the server";

                case ImportStage.WaitingForServer:
                    return "waiting for the server";

                case ImportStage.ServerAligning:
                    return "aligning on the server";

                case ImportStage.SeparatingVocals:
                    return "separating vocals";

                case ImportStage.LoadingModel:
                    return "loading model";

                case ImportStage.AligningLyrics:
                    return "aligning lyrics";

                case ImportStage.FallingBack:
                    return "trying another aligner";

                case ImportStage.Packaging:
                    return "packaging map";

                case ImportStage.Importing:
                    return "importing beatmap";

                default:
                    return "working";
            }
        }

        /// <summary>The server aligner client's line for the upload (backlog 413).</summary>
        public const string SERVER_UPLOADING = "uploading to the server aligner...";

        /// <summary>
        /// Prefix of every line the server aligner client relays from the server's own aligner log
        /// (the job's last progress line). Everything after it is classified as a local line would
        /// be, but only to pick the row's note and bar: the row stays <see cref="ImportStage.ServerAligning"/>.
        /// </summary>
        public const string SERVER_PROGRESS_PREFIX = "server aligner: ";

        /// <summary>The line for a job waiting in the server's queue; <paramref name="position"/> is 1-based, null when the server did not say.</summary>
        public static string ServerQueueLine(int? position)
            => position is int p && p > 0 ? $"waiting for the server aligner, position {p} in the queue" : "waiting for the server aligner";

        /// <summary>The line for a running server job, relaying the server aligner's own last log line when it has one.</summary>
        public static string ServerProgressLine(string? serverLine)
            => SERVER_PROGRESS_PREFIX + (string.IsNullOrWhiteSpace(serverLine) ? "running" : serverLine.Trim());

        /// <summary>The note a waiting row shows for a 1-based queue position.</summary>
        public static string QueueNote(int position) => position == 1 ? "next in the queue" : $"position {position} in the queue";

        private static readonly Regex queue_position = new Regex(@"^waiting for the server aligner, position (\d+) in the queue$", RegexOptions.Compiled);

        // lyriclab's log() prefixes every line with a wall-clock stamp; strip it before matching so
        // it can never reach the display.
        private static readonly Regex log_timestamp = new Regex(@"^\[\d{1,2}:\d{2}(?::\d{2})?\]\s*", RegexOptions.Compiled);

        // tqdm (demucs, torch hub downloads) repaints with ANSI cursor moves on some terminals.
        private static readonly Regex ansi_escape = new Regex(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

        private static readonly Regex percentage = new Regex(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);

        // "chunk 7/31", "121.1/269.1". Guarded so path separators and dates cannot masquerade as one.
        private static readonly Regex ratio = new Regex(@"(?<![\w.])(\d+(?:\.\d+)?)\s*/\s*(\d+(?:\.\d+)?)(?![\w.])", RegexOptions.Compiled);

        /// <summary>Removes the pipeline's own timestamp prefix and any terminal escapes.</summary>
        public static string Clean(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;

            return log_timestamp.Replace(ansi_escape.Replace(line, string.Empty), string.Empty).Trim();
        }

        public static ImportProgressUpdate Parse(string? line)
        {
            string cleaned = Clean(line);

            if (cleaned.Length == 0)
                return new ImportProgressUpdate(null, null);

            // The server's own aligner chatter: one row, whatever step the server is on. The step
            // becomes the row's note (its fixed label, never the raw line) and its fraction the bar.
            if (cleaned.StartsWith(SERVER_PROGRESS_PREFIX, StringComparison.Ordinal))
            {
                string inner = Clean(cleaned[SERVER_PROGRESS_PREFIX.Length..]);
                ImportStage? step = inner.Length == 0 ? null : stageFor(inner);
                string? note = step is ImportStage.SeparatingVocals or ImportStage.LoadingModel or ImportStage.AligningLyrics ? LabelFor(step.Value) : null;
                return new ImportProgressUpdate(ImportStage.ServerAligning, ParseProgress(inner), note);
            }

            var queued = queue_position.Match(cleaned);

            if (queued.Success && int.TryParse(queued.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int position))
                return new ImportProgressUpdate(ImportStage.WaitingForServer, null, QueueNote(position));

            return new ImportProgressUpdate(stageFor(cleaned), ParseProgress(cleaned));
        }

        /// <summary>
        /// The 0-1 fraction a line carries, if any. A percentage wins over a "done/total" ratio
        /// (tqdm prints both, and the percentage is the authoritative one).
        /// </summary>
        public static float? ParseProgress(string line)
        {
            if (string.IsNullOrEmpty(line))
                return null;

            var percent = percentage.Match(line);

            if (percent.Success && double.TryParse(percent.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                return (float)Math.Clamp(p / 100, 0, 1);

            var fraction = ratio.Match(line);

            if (fraction.Success
                && double.TryParse(fraction.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double done)
                && double.TryParse(fraction.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double total)
                && total > 0
                && done >= 0
                && done <= total)
            {
                return (float)(done / total);
            }

            return null;
        }

        private static ImportStage? stageFor(string line)
        {
            string l = line.ToLowerInvariant();

            // Order matters: the later, broader keyword tests would otherwise swallow lines that
            // merely mention "align" or "model" in passing.
            if (has(l, "importing beatmap") || has(l, "packaging done") || has(l, "imported"))
                return ImportStage.Importing;

            if (has(l, "packaging") || has(l, "packaged"))
                return ImportStage.Packaging;

            // "aligner unavailable (aligner exited with code 1: ...), trying next option".
            if (has(l, "unavailable") || has(l, "trying next option"))
                return ImportStage.FallingBack;

            // The server aligner's own lines (backlog 413). Ahead of the broad "align" arm below,
            // which every one of them would otherwise fall into.
            if (has(l, "uploading to the server"))
                return ImportStage.Uploading;

            if (has(l, "waiting for the server"))
                return ImportStage.WaitingForServer;

            // The split step. Deliberately matched on the ACT, not on the word "extractor", so the
            // "no audio extractor available, keeping the video file as the map's audio" degrade
            // claims no stage at all: nothing was extracted, and the display holds where it was.
            if (has(l, "extracting audio") || has(l, "extracted audio"))
                return ImportStage.ExtractingAudio;

            // Setup-shaped notices that arrive before any real work starts. These carry paths and
            // package sizes, which is exactly what must not reach the display.
            if (has(l, "starting import")
                || has(l, "automatic alignment off")
                || has(l, "no line stamps")
                || has(l, "some lines are stamped")
                || has(l, "aligner environment")
                || has(l, "anchor mode")
                || l.StartsWith("lyrics:", StringComparison.Ordinal)
                || l.StartsWith("audio:", StringComparison.Ordinal))
            {
                return ImportStage.Preparing;
            }

            if (has(l, "separation") || has(l, "separating") || has(l, "demucs"))
                return ImportStage.SeparatingVocals;

            if ((has(l, "loading") || has(l, "downloading")) && has(l, "model"))
                return ImportStage.LoadingModel;

            if (has(l, "emission"))
                return ImportStage.AligningLyrics;

            if (has(l, "align") || has(l, "anchor"))
                return ImportStage.AligningLyrics;

            return null;
        }

        private static bool has(string line, string token) => line.Contains(token, StringComparison.Ordinal);
    }
}
