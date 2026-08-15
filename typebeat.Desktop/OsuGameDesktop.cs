// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;
using typebeat.Desktop.IPC;
using typebeat.Desktop.Performance;
using typebeat.Desktop.Security;
using osu.Framework.Platform;
using typebeat.Game;
using typebeat.Desktop.Updater;
using osu.Framework;
using osu.Framework.Logging;
using typebeat.Game.Updater;
using typebeat.Desktop.MacOS;
using typebeat.Desktop.Windows;
using osu.Framework.Allocation;
using typebeat.Game.Configuration;
using typebeat.Game.IO;
using typebeat.Game.IPC;
using typebeat.Game.Performance;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game.Utils;
using Velopack.Locators;

namespace typebeat.Desktop
{
    internal partial class OsuGameDesktop : OsuGame
    {
        private OsuSchemeLinkIPCChannel? osuSchemeLinkIPCChannel;
        private ArchiveImportIPCChannel? archiveImportIPCChannel;

        [Cached(typeof(IHighPerformanceSessionManager))]
        private readonly HighPerformanceSessionManager highPerformanceSessionManager = new HighPerformanceSessionManager();

        // The lyric-map import pipeline lives in the ruleset; typebeat.Desktop is the one project that
        // references both it and typebeat.Game, so it bridges the shell's ILyricMapImporter seam here
        // (and the local auto-aligner installer seam the first-run setup / settings UI drive).
        [Cached(typeof(ILyricMapImporter))]
        [Cached(typeof(ILocalAlignerManager))]
        private readonly LyricMapImportService lyricMapImporter = new LyricMapImportService();

        public bool IsFirstRun { get; init; }

        public bool EnableWebSocketServer { get; init; }

        public OsuGameDesktop(string[]? args = null)
            : base(args)
        {
        }

        public override StableStorage? GetStorageForStableInstall()
        {
            try
            {
                if (Host is DesktopGameHost desktopHost)
                {
                    string? stablePath = getStableInstallPath();
                    if (!string.IsNullOrEmpty(stablePath))
                        return new StableStorage(stablePath, desktopHost);
                }
            }
            catch (Exception)
            {
                Logger.Log("Could not find a stable install", LoggingTarget.Runtime, LogLevel.Important);
            }

            return null;
        }

        private string? getStableInstallPath()
        {
            static bool checkExists(string p) => Directory.Exists(Path.Combine(p, "Songs")) || File.Exists(Path.Combine(p, "type!beat.cfg"));

            string? stableInstallPath;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    stableInstallPath = getStableInstallPathFromRegistry("osustable.File.osz");

                    if (!string.IsNullOrEmpty(stableInstallPath) && checkExists(stableInstallPath))
                        return stableInstallPath;

                    stableInstallPath = getStableInstallPathFromRegistry("type!beat");

                    if (!string.IsNullOrEmpty(stableInstallPath) && checkExists(stableInstallPath))
                        return stableInstallPath;
                }
                catch
                {
                }
            }

            stableInstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"type!beat");
            if (checkExists(stableInstallPath))
                return stableInstallPath;

            stableInstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".osu");
            if (checkExists(stableInstallPath))
                return stableInstallPath;

            return null;
        }

        [SupportedOSPlatform("windows")]
        private string? getStableInstallPathFromRegistry(string progId)
        {
            using (RegistryKey? key = Registry.ClassesRoot.OpenSubKey(progId))
                return key?.OpenSubKey(WindowsAssociationManager.SHELL_OPEN_COMMAND)?.GetValue(string.Empty)?.ToString()?.Split('"')[1].Replace("type!beat.exe", "");
        }

        public static bool IsPackageManaged => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OSU_EXTERNAL_UPDATE_PROVIDER"));

        protected override UpdateManager CreateUpdateManager()
        {
            // If this is the first time we've run the game, ie it is being installed,
            // reset the user's release stream to "lazer".
            //
            // This ensures that if a user is trying to recover from a failed startup on an unstable release stream,
            // the game doesn't immediately try and update them back to the release stream after starting up.
            if (IsFirstRun)
                LocalConfig.SetValue(OsuSetting.ReleaseStream, ReleaseStream.Lazer);

            if (IsPackageManaged)
                return new NoActionUpdateManager();

            return new VelopackUpdateManager();
        }

        /// <summary>
        /// Queues a restart, and answers HONESTLY whether the game is really going to come back.
        /// </summary>
        /// <remarks>
        /// This used to hand back <c>true</c> unconditionally while queueing the updater's restart, which
        /// is a lie in any build the updater did not install (a development build, or the plain zip the
        /// game ships as): <see cref="Velopack.UpdateExe.Start"/> needs an <c>Update.exe</c> beside the
        /// app, finds none, and does nothing at all. Callers take that answer as licence to discard
        /// something, so the game would exit, keep its promise to nobody, and never return. Every caller
        /// has a sane path for <c>false</c>, so reporting it correctly is worth more than claiming a
        /// restart that will not happen.
        /// </remarks>
        public override bool RestartAppWhenExited()
        {
            string? executablePath = Environment.ProcessPath;
            int processId = Environment.ProcessId;

            switch (GameRelaunch.Decide(updaterCanRestart(), RuntimeInfo.OS, executablePath, Assembly.GetEntryAssembly()?.GetName().Name))
            {
                case RelaunchMethod.Updater:
                    RestartOnExitAction = () => Velopack.UpdateExe.Start(waitPid: (uint)processId);
                    return true;

                case RelaunchMethod.OwnExecutable:
                    // No installer to restart through, so the game starts itself: the new process is handed
                    // this one's id and does not touch the data directory until it is gone, which is the
                    // same guarantee the updater's waitPid buys above. Two overlapping instances would
                    // fight over realm and the storage lock, so it is a wait, not a delay.
                    RestartOnExitAction = () => GameRelaunch.StartOwnExecutable(executablePath!, processId);
                    return true;

                default:
                    Logger.Log("This build has no way to restart itself, so no restart has been queued.", LoggingTarget.Runtime, LogLevel.Important);
                    return false;
            }
        }

        /// <summary>
        /// Whether an updater-managed install is actually present to restart through. Version alone does
        /// not answer this: <see cref="OsuGameBase.IsDeployedBuild"/> is about the assembly version and
        /// <see cref="IsPackageManaged"/> is about somebody else owning updates, while what matters here is
        /// only whether the updater laid this copy down and left its launcher next to it.
        /// </summary>
        private static bool updaterCanRestart()
        {
            // An external provider owns the install, so its updater binary is not ours to drive.
            if (IsPackageManaged)
                return false;

            try
            {
                var locator = VelopackLocator.IsCurrentSet ? VelopackLocator.Current : VelopackLocator.CreateDefaultForPlatform(null!);

                return locator.CurrentlyInstalledVersion != null
                       && !string.IsNullOrEmpty(locator.UpdateExePath)
                       && File.Exists(locator.UpdateExePath);
            }
            catch (Exception e)
            {
                Logger.Log($"Could not tell whether this is an updater-managed install: {e.Message}", LoggingTarget.Runtime, LogLevel.Important);
                return false;
            }
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Added so its BDL runs (resolves the ruleset config cache); it is also cached above.
            Add(lyricMapImporter);

            LoadComponentAsync(new DiscordRichPresence(), Add);

            switch (RuntimeInfo.OS)
            {
                case RuntimeInfo.Platform.Windows:
                    LoadComponentAsync(new GameplayWinKeyBlocker(), Add);
                    break;

                case RuntimeInfo.Platform.macOS when !IsPackageManaged && IsDeployedBuild:
                    if (!IsPackageManaged && IsDeployedBuild)
                        LoadComponentAsync(new MacOSAppLocationChecker(), Add);
                    break;
            }

            LoadComponentAsync(new ElevatedPrivilegesChecker(), Add);

            osuSchemeLinkIPCChannel = new OsuSchemeLinkIPCChannel(Host, this);
            archiveImportIPCChannel = new ArchiveImportIPCChannel(Host, this);

            if (EnableWebSocketServer)
                Add(new OsuWebSocketProvider());
        }

        public override void SetHost(GameHost host)
        {
            base.SetHost(host);

            // Apple operating systems use a better icon provided via external assets.
            if (!RuntimeInfo.IsApple)
            {
                // This resource is a PNG, and must stay one. The framework's SetIconFromStream first
                // tries ImageSharp, and the ImageSharp version osu.Framework pins (3.1.11, see the
                // comment in typebeat.Game.csproj) ships no ICO decoder at all: ICO support only
                // arrived in ImageSharp 4.0. ImageSharp is fully managed, so a .ico here fails to
                // decode on every platform, Windows included.
                //
                // What differs by platform is the fallback. On the failure the framework parses the
                // .ico itself and calls SetIconFromGroup. Windows overrides that to build the icon
                // through the Win32 CreateIconFromResourceEx, which needs no decoder and works, so
                // the bug stays invisible there. Everywhere else the base implementation just feeds
                // the container's largest entry back to ImageSharp, and in our .ico files that entry
                // is a headerless DIB rather than a PNG, so it fails a second time. That second
                // throw happens inside the framework's own catch block, so nothing catches it and it
                // escapes this method, taking the client down at startup. Linux is where a user hit
                // it (backlog 163).
                //
                // Handing over a PNG means the very first ImageSharp load succeeds and the .ico
                // fallback is never entered on any platform.
                var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(GetType(), "lazer.png");
                if (iconStream != null)
                    host.Window.SetIconFromStream(iconStream);
            }

            host.Window.Title = Name;
        }

        protected override BatteryInfo CreateBatteryInfo() => FrameworkEnvironment.UseSDL3 ? new SDL3BatteryInfo() : new SDL2BatteryInfo();

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            osuSchemeLinkIPCChannel?.Dispose();
            archiveImportIPCChannel?.Dispose();
        }
    }
}
