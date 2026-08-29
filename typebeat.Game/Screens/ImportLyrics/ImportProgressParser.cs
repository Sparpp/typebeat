// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// The coarse steps an import walks through, in the order a local run reaches them. The import
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

        Uploading,
        WaitingForServer,
        SeparatingVocals,
        LoadingModel,
        AligningLyrics,

        /// <summary>
        /// A momentary notice that the aligner that was running gave up and the next option is being
        /// tried (local aligner -> server -> line stamps). Never a long-lived stage.
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
    public readonly record struct ImportProgressUpdate(ImportStage? Stage, float? Progress);

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

            if (has(l, "uploading"))
                return ImportStage.Uploading;

            if (has(l, "waiting for the server") || has(l, "server aligner") || has(l, "queued"))
                return ImportStage.WaitingForServer;

            // "aligner unavailable (...), trying next option" / "server alignment unavailable (...)".
            if (has(l, "unavailable") || has(l, "trying next option") || has(l, "trying line-timed fallback"))
                return ImportStage.FallingBack;

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
