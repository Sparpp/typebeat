// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// The redirected process runs of the lyric import (the aligner, its setup script, the vocals
    /// separation and the stem encode), streamed line by line to a progress callback.
    /// </summary>
    internal static class ImportProcessRunner
    {
        /// <summary>The exit code <see cref="RunProcessAsync"/> returns when its token fired.</summary>
        internal const int CANCELLED_EXIT_CODE = int.MinValue;


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
        /// (background thread!). Returns the exit code (<see cref="CANCELLED_EXIT_CODE"/> when the
        /// token fired and the process tree was killed) and the last few output lines.
        /// </summary>
        internal static async Task<(int ExitCode, string Tail)> RunProcessAsync(ProcessStartInfo psi, Action<string> progress, CancellationToken token)
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

                return (CANCELLED_EXIT_CODE, tailString());
            }

            return (process.ExitCode, tailString());
        }
    }
}
