// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Screens.Edit.Submission
{
    /// <summary>
    /// The largest beatmap upload the server takes, checked before uploading so an oversized beatmap is
    /// refused with a readable message instead of the server's raw rejection and a doomed direct retry.
    /// </summary>
    public static class UploadSizeLimit
    {
        /// <summary>
        /// Mirrors typebeat-web's <c>BssEndpoints.MaxUploadBodyBytes</c> (100 MiB), which stays the authority:
        /// a body just under this that the multipart framing pushes over is still refused by the server,
        /// whose message reads the same way.
        /// </summary>
        public const long MAX_BYTES = 100L * 1024 * 1024;

        /// <summary>The message for an upload of <paramref name="bytes"/>, or null when it fits.</summary>
        public static string? TooLargeMessage(long bytes)
            => bytes > MAX_BYTES ? $"Beatmap is too large ({FormatMegabytes(bytes)}). It must be {FormatMegabytes(MAX_BYTES)} or smaller." : null;

        /// <summary>Bytes as binary megabytes, one decimal at most ("104.2 MB", "100 MB").</summary>
        public static string FormatMegabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }
}
