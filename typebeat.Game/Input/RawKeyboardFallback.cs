// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using typebeat.Game.Configuration;

namespace typebeat.Game.Input
{
    /// <summary>
    /// The startup half of <see cref="OsuSetting.DisableRawKeyboard"/>: turns SDL's Windows raw keyboard
    /// input off for this process when the player has asked for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The framework calls <c>SDL_SetHint(SDL_HINT_WINDOWS_RAW_KEYBOARD, "1")</c> unconditionally while it
    /// creates the window. That is a NORMAL priority hint, and SDL refuses a hint below OVERRIDE priority
    /// while an environment variable of the same name exists (<c>SDL_SetHintWithPriority</c>), and
    /// <c>SDL_GetHint</c> answers with the environment variable in that case. So the one lever the game has
    /// is the process environment, and it has to be set before SDL's first call: SDL takes ONE snapshot
    /// of the process environment (<c>SDL_GetEnvironment</c>, on the first hint or init call) and never
    /// reads the real environment again.
    /// </para>
    /// <para>
    /// game.ini is normally loaded long after the window exists, so this reads the one stored value
    /// straight from the file, the same way <c>IniConfigManager</c> would. Anything unexpected (no file,
    /// no line, an unreadable value, an IO error) leaves raw keyboard input on, which is the default.
    /// </para>
    /// </remarks>
    public static class RawKeyboardFallback
    {
        /// <summary>
        /// The environment variable SDL reads for <c>SDL_HINT_WINDOWS_RAW_KEYBOARD</c>.
        /// </summary>
        public const string ENVIRONMENT_VARIABLE = @"SDL_WINDOWS_RAW_KEYBOARD";

        /// <summary>
        /// Set alongside <see cref="ENVIRONMENT_VARIABLE"/> when the game set it, so that a restarted game
        /// (which inherits this process's environment) can tell its own leftover apart from a value the
        /// player set themselves, and re-decide from the setting rather than keeping the old one forever.
        /// </summary>
        public const string MARKER_VARIABLE = @"TYPEBEAT_RAW_KEYBOARD_FROM_SETTING";

        private const string game_config_filename = @"game.ini";
        private const string storage_config_filename = @"storage.ini";

        // FrameworkConfigManager.FILENAME, which is internal to the framework. The desktop host treats this
        // file beside the executable as the mark of a portable install.
        private const string framework_config_filename = @"framework.ini";

        /// <summary>
        /// Applies the stored setting to this process's environment. Call before anything touches SDL.
        /// </summary>
        /// <param name="defaultStoragePath">The game's default data directory, see <see cref="DefaultStoragePath"/>.</param>
        /// <returns>Whether raw keyboard input was turned off by the setting.</returns>
        public static bool Apply(string defaultStoragePath)
        {
            if (!OperatingSystem.IsWindows())
                return false;

            return Apply(defaultStoragePath, Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);
        }

        /// <summary>
        /// <see cref="Apply(string)"/> against an arbitrary environment, for tests.
        /// </summary>
        public static bool Apply(string defaultStoragePath, Func<string, string?> getVariable, Action<string, string?> setVariable)
        {
            // A previous instance of the game (a restart) left its own value behind: drop it, so the
            // stored setting decides again exactly as it would on a fresh start.
            if (getVariable(MARKER_VARIABLE) != null)
            {
                if (getVariable(ENVIRONMENT_VARIABLE) == "0")
                    setVariable(ENVIRONMENT_VARIABLE, null);

                setVariable(MARKER_VARIABLE, null);
            }

            // A value the player set themselves always wins, whichever way it points.
            if (getVariable(ENVIRONMENT_VARIABLE) != null)
                return false;

            if (!IsDisabledInConfig(defaultStoragePath))
                return false;

            setVariable(ENVIRONMENT_VARIABLE, "0");
            setVariable(MARKER_VARIABLE, "1");
            return true;
        }

        /// <summary>
        /// Where the framework's desktop host keeps the game's files before the game redirects them: the
        /// startup directory for a portable install (a framework.ini beside the executable), otherwise
        /// <paramref name="applicationDataPath"/>/<paramref name="gameName"/>.
        /// </summary>
        public static string DefaultStoragePath(string startupDirectory, string applicationDataPath, string gameName)
        {
            if (File.Exists(Path.Combine(startupDirectory, framework_config_filename)))
                return startupDirectory;

            return Path.Combine(applicationDataPath, gameName);
        }

        /// <summary>
        /// Reads <see cref="OsuSetting.DisableRawKeyboard"/> from the game.ini the game itself will load:
        /// the one in the custom data directory from storage.ini when one is chosen, otherwise the one in
        /// <paramref name="defaultStoragePath"/>. Never throws; anything unexpected reads as false.
        /// </summary>
        public static bool IsDisabledInConfig(string defaultStoragePath)
        {
            try
            {
                string dataPath = defaultStoragePath;

                string? customPath = readValue(Path.Combine(defaultStoragePath, storage_config_filename), nameof(StorageConfig.FullPath));
                if (!string.IsNullOrEmpty(customPath))
                    dataPath = customPath;

                string? stored = readValue(Path.Combine(dataPath, game_config_filename), nameof(OsuSetting.DisableRawKeyboard));

                // BindableBool.Parse accepts "1"/"0" as well as bool.Parse's spellings.
                switch (stored)
                {
                    case null:
                        return false;

                    case "1":
                        return true;

                    case "0":
                        return false;

                    default:
                        return bool.TryParse(stored, out bool value) && value;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The value of <paramref name="key"/> in an ini file, parsed the way <c>IniConfigManager</c> parses
        /// it (split at the first '=', both halves trimmed, '#' comments skipped, the last line wins).
        /// </summary>
        private static string? readValue(string path, string key)
        {
            if (!File.Exists(path))
                return null;

            string? result = null;

            foreach (string line in File.ReadLines(path))
            {
                int equalsIndex = line.IndexOf('=');

                if (line.Length == 0 || line[0] == '#' || equalsIndex < 0)
                    continue;

                if (line.AsSpan(0, equalsIndex).Trim().SequenceEqual(key))
                    result = line.AsSpan(equalsIndex + 1).Trim().ToString();
            }

            return result;
        }
    }
}
