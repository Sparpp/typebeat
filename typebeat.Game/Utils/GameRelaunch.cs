// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using osu.Framework;
using osu.Framework.Logging;

namespace typebeat.Game.Utils
{
    /// <summary>
    /// How, or whether, the running process is able to bring itself back up after it exits.
    /// </summary>
    public enum RelaunchMethod
    {
        /// <summary>
        /// Nothing available here can restart the game. A caller must NOT exit expecting to come back.
        /// </summary>
        None,

        /// <summary>
        /// This build was laid down by the updater, so the updater's own launcher does the restart
        /// (including waiting for this process to be gone first).
        /// </summary>
        Updater,

        /// <summary>
        /// There is no updater to lean on, so the game starts its own executable again and that new
        /// process waits for this one to exit before it touches anything.
        /// </summary>
        OwnExecutable,
    }

    /// <summary>
    /// Deciding whether the game can restart itself, and doing it when the updater cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The DECISION is kept separate from the ACT on purpose. Whether a restart will really happen is
    /// what <see cref="OsuGameBase.RestartAppWhenExited"/> promises to its callers, and callers throw
    /// away real state on the strength of that promise (the editor session behind the beatdrop demo, the
    /// data path behind the migration screen). A promise that cannot be checked is how this went wrong
    /// once already: the updater path was reported as queued unconditionally, and in a build the updater
    /// never installed it silently did nothing, so the game went down and never came back.
    /// </para>
    /// <para>
    /// The relaunch itself cannot be exercised in-process, so everything above it (<see cref="Decide"/>,
    /// the argument round trip) is pure and covered, and only the two lines that spawn and wait are not.
    /// </para>
    /// </remarks>
    public static class GameRelaunch
    {
        /// <summary>
        /// Handed to the relaunched process, naming the process it must outlive.
        /// </summary>
        /// <remarks>
        /// The game holds a realm database and a storage lock for as long as it is alive, so two instances
        /// overlapping is worse than no restart at all: the new one would find the data directory still
        /// held. The waiting is done by the NEW process rather than by some intermediary because the old
        /// one has to be alive to start it, which is the same reason the updater is passed a wait pid.
        /// </remarks>
        public const string WAIT_FOR_EXIT_ARGUMENT = @"--wait-for-process-exit";

        /// <summary>
        /// How long the incoming process will wait for the outgoing one before giving up on it and
        /// starting anyway. A shutdown takes a moment, not a minute; anything past this is a process that
        /// is not going to exit, and a game that never appears is a worse outcome than one that reports
        /// the data directory as busy.
        /// </summary>
        public static readonly TimeSpan WAIT_TIMEOUT = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Whether the game can restart itself, and by which route.
        /// </summary>
        /// <remarks>
        /// The generic route is deliberately Windows only. Elsewhere the executable path is not reliably
        /// the thing to start again (a macOS build runs from inside an app bundle, and a Linux AppImage
        /// runs from a mount point that disappears with the process), and half a restart is exactly the
        /// failure being fixed here, so those platforms answer <see cref="RelaunchMethod.None"/> and let
        /// the caller take its "no restart" path.
        /// </remarks>
        /// <param name="updaterAvailable">Whether an updater-managed install is present to restart through.</param>
        /// <param name="platform">The running platform.</param>
        /// <param name="executablePath">The current process's executable, normally <see cref="Environment.ProcessPath"/>.</param>
        /// <param name="expectedExecutableName">
        /// The game's own executable name (no extension), normally the entry assembly's name. The process
        /// path is only ours to start again when it matches: launched through a toolchain host the process
        /// is something like <c>dotnet</c>, and starting THAT again would not bring the game back.
        /// </param>
        /// <param name="executableExists">Overridable existence check, for tests.</param>
        public static RelaunchMethod Decide(bool updaterAvailable, RuntimeInfo.Platform platform, string? executablePath, string? expectedExecutableName,
                                            Func<string, bool>? executableExists = null)
        {
            if (updaterAvailable)
                return RelaunchMethod.Updater;

            if (platform != RuntimeInfo.Platform.Windows)
                return RelaunchMethod.None;

            if (string.IsNullOrEmpty(executablePath) || string.IsNullOrEmpty(expectedExecutableName))
                return RelaunchMethod.None;

            if (!string.Equals(Path.GetFileNameWithoutExtension(executablePath), expectedExecutableName, StringComparison.OrdinalIgnoreCase))
                return RelaunchMethod.None;

            if (!(executableExists ?? File.Exists)(executablePath))
                return RelaunchMethod.None;

            return RelaunchMethod.OwnExecutable;
        }

        /// <summary>
        /// The argument telling a freshly started game to wait for <paramref name="processId"/> to exit.
        /// </summary>
        public static string ArgumentFor(int processId) => $"{WAIT_FOR_EXIT_ARGUMENT}={processId.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>
        /// Strips <see cref="WAIT_FOR_EXIT_ARGUMENT"/> out of a command line, reporting the process id it
        /// named (or <c>0</c> when it named none).
        /// </summary>
        /// <remarks>
        /// It is taken out rather than left in place because everything downstream reads the command line
        /// for its own purposes: an argument present at all suppresses updater setup, and the first one is
        /// sniffed for a file import. This is an internal detail of a restart and none of their business.
        /// </remarks>
        public static string[] TakeWaitTarget(string[] args, out int processId)
        {
            processId = 0;

            if (args.Length == 0)
                return args;

            List<string> remaining = new List<string>(args.Length);

            foreach (string arg in args)
            {
                if (!arg.StartsWith(WAIT_FOR_EXIT_ARGUMENT, StringComparison.Ordinal))
                {
                    remaining.Add(arg);
                    continue;
                }

                string[] split = arg.Split('=');

                if (split.Length == 2 && int.TryParse(split[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                    processId = parsed;
            }

            return remaining.ToArray();
        }

        /// <summary>
        /// Blocks until <paramref name="processId"/> is gone, so that nothing this process does afterwards
        /// races the instance it is replacing. Must be called before the game host, its storage or realm
        /// are touched.
        /// </summary>
        public static void WaitForProcessExit(int processId, TimeSpan? timeout = null)
        {
            // Waiting on ourselves would never return, and a process id we were never given is nothing to wait for.
            if (processId <= 0 || processId == Environment.ProcessId)
                return;

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (!process.WaitForExit((int)(timeout ?? WAIT_TIMEOUT).TotalMilliseconds))
                        Logger.Log($"The previous instance ({processId}) has not exited; starting anyway.", LoggingTarget.Runtime, LogLevel.Important);
                }
            }
            catch (ArgumentException)
            {
                // Already gone, which is the state being waited for.
            }
            catch (Exception e)
            {
                Logger.Log($"Could not wait on the previous instance ({processId}): {e.Message}", LoggingTarget.Runtime, LogLevel.Important);
            }
        }

        /// <summary>
        /// Starts <paramref name="executablePath"/> again, telling it to wait for
        /// <paramref name="waitForProcessId"/> to exit first. Returns whether the new process was started.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Run from the tail of this process's shutdown, so it swallows its own failures: an exception
        /// thrown while the game is being disposed helps nobody.
        /// </para>
        /// <para>
        /// The one argument is passed through <c>ArgumentList</c> so that .NET does the quoting: the game's
        /// executable name is not a tame one and its path is not either. Losing that argument is the one
        /// failure here that would be silent AND harmful, since the new process would then start straight
        /// away and race the one still holding realm.
        /// </para>
        /// </remarks>
        public static bool StartOwnExecutable(string executablePath, int waitForProcessId)
        {
            try
            {
                var info = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
                };

                info.ArgumentList.Add(ArgumentFor(waitForProcessId));

                Process.Start(info)?.Dispose();
                return true;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not restart the game: {e.Message}", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }
        }
    }
}
