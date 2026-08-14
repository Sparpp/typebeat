// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using osu.Framework.Extensions;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps;

namespace typebeat.Game.Screens.Menu
{
    /// <summary>
    /// The editor's "demo the beatdrop" button. A beatdrop is otherwise only observable by quitting and
    /// relaunching the game, so it gets stamped blind; this is the feedback loop that lets it be heard.
    /// The button REBOOTS the game and leaves behind a handoff naming the map being edited, which the next
    /// startup's <see cref="IntroScreen"/> picks up so the intro it plays is a real startup intro on this
    /// map's beatdrop, not an imitation of one.
    /// </summary>
    /// <remarks>
    /// The handoff exists because a plain restart would demo SOMEBODY ELSE'S map: a real startup picks from
    /// <see cref="IntroBeatdropPool"/>, which is every beatdrop-carrying map the user owns, steered by an
    /// anti-repeat history and overridable per map from song select ("Use on game intro",
    /// <see cref="BeatmapUserSettings.IntroPoolInclusion"/>). A consumed handoff overrules all three: the
    /// point is previewing the map in front of you whether or not it has been opted in, and a demo is not a
    /// real intro appearance so it must not steer future ones either. With no beatdrop authored there is
    /// nothing to demo (<see cref="CanDemo"/>) and nothing reboots at all, rather than the game restarting
    /// into an intro on some other map's timestamp that the user would hear as their own.
    /// </remarks>
    public static class IntroBeatdropDemo
    {
        /// <summary>
        /// The demo button's caption while the map has a beatdrop to demo. It names the restart, because
        /// pressing the button really does take the whole game down and bring it back up.
        /// </summary>
        public const string CAPTION = "Restart the game and play its intro on this map's beatdrop";

        /// <summary>
        /// The demo button's caption while the map declares no beatdrop. It replaces the normal caption
        /// rather than sitting alongside it, so the button says why it will not do anything.
        /// </summary>
        public const string NO_BEATDROP_CAPTION = "no beatdrop set!";

        /// <summary>
        /// Whether there is anything to demo, which is exactly whether the map declares an intro beatdrop
        /// (<see cref="IBeatmap.IntroBeatdropTime"/>). Intro pool membership is not consulted.
        /// </summary>
        public static bool CanDemo(double? beatdropTime) => beatdropTime.HasValue;

        /// <summary>
        /// The caption to show on the demo button for a map whose beatdrop is <paramref name="beatdropTime"/>.
        /// </summary>
        public static LocalisableString CaptionFor(double? beatdropTime) => CanDemo(beatdropTime) ? CAPTION : NO_BEATDROP_CAPTION;

        /// <summary>
        /// The demo button's sequence, kept free of any screen or drawable so the ordering it guarantees can
        /// be exercised without a game host.
        /// </summary>
        /// <remarks>
        /// Rebooting discards the editor session for real, so the save prompt comes first and
        /// <paramref name="reboot"/> only ever runs from inside the continuation handed to
        /// <paramref name="promptToSave"/>. A cancelled prompt (or a save that fails) simply never calls
        /// back, and nothing has moved by that point.
        /// </remarks>
        /// <param name="beatdropTime">The beatdrop currently authored on the map being edited.</param>
        /// <param name="promptToSave">Raises the save prompt, invoking its argument once (and only if) the user commits to leaving.</param>
        /// <param name="reboot">Arms the handoff for the given drop time and restarts the game.</param>
        public static void Request(double? beatdropTime, Action<Action> promptToSave, Action<double> reboot)
        {
            if (!CanDemo(beatdropTime))
                return;

            double drop = beatdropTime!.Value;

            promptToSave(() => reboot(drop));
        }

        #region One-shot handoff across the restart

        /// <summary>
        /// Where the handoff is parked. It has to outlive the process (the whole mechanism is a reboot), so
        /// it is a file in the game's storage rather than anything held in memory.
        /// </summary>
        public const string HANDOFF_FILENAME = "intro_beatdrop_demo.txt";

        /// <summary>
        /// Bumped whenever the encoding below changes. A line written by a different version is ignored
        /// rather than guessed at, so an upgrade over a parked handoff degrades to a normal intro.
        /// </summary>
        private const string handoff_version = "1";

        /// <summary>
        /// How long a parked handoff stays honourable. A demo arms it and restarts immediately, so anything
        /// older than this was not a request that was made, it is one that was stranded (nothing consumed it
        /// because the intro never ran), and a demo firing out of nowhere on some later launch would be
        /// baffling. This is the backstop for the one case <see cref="Consume"/> cannot cover by itself.
        /// </summary>
        public static readonly TimeSpan HANDOFF_LIFETIME = TimeSpan.FromMinutes(5);

        /// <summary>
        /// A parked request for the next startup intro to be soundtracked by a specific map.
        /// </summary>
        public sealed class Handoff
        {
            /// <summary>
            /// The beatmap to soundtrack the intro with. Stored by ID rather than by anything richer so a
            /// map deleted in the meantime simply fails to resolve (see <see cref="Resolve{T}"/>).
            /// </summary>
            public Guid BeatmapId { get; }

            /// <summary>
            /// The point in the song (ms) to land on the menu reveal. Carried across rather than re-read
            /// from the map, so the demo plays the timestamp that was on screen in the editor.
            /// </summary>
            public double DropTime { get; }

            public Handoff(Guid beatmapId, double dropTime)
            {
                BeatmapId = beatmapId;
                DropTime = dropTime;
            }
        }

        /// <summary>
        /// Parks a handoff for the next startup. Returns whether it was written: a caller that fails to arm
        /// must NOT go on to restart, since that would throw away the editor session in exchange for an
        /// ordinary intro.
        /// </summary>
        public static bool Arm(Storage storage, Guid beatmapId, double dropTime, DateTimeOffset? now = null)
        {
            try
            {
                using (var stream = storage.CreateFileSafely(HANDOFF_FILENAME))
                using (var writer = new StreamWriter(stream))
                    writer.Write(Encode(beatmapId, dropTime, now ?? DateTimeOffset.UtcNow));

                return true;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not arm the beatdrop demo handoff: {e.Message}", level: LogLevel.Important);
                return false;
            }
        }

        /// <summary>
        /// Discards any parked handoff. Used to back out of an armed demo that is not going to happen.
        /// </summary>
        public static void Clear(Storage storage)
        {
            try
            {
                if (storage.Exists(HANDOFF_FILENAME))
                    storage.Delete(HANDOFF_FILENAME);
            }
            catch (Exception e)
            {
                // A handoff that cannot be deleted still cannot outlive HANDOFF_LIFETIME.
                Logger.Log($"Could not clear the beatdrop demo handoff: {e.Message}", level: LogLevel.Important);
            }
        }

        /// <summary>
        /// Reads the parked handoff WITHOUT clearing it, for code that needs to know a demo is coming before
        /// the intro screen exists (see <c>Loader</c>). Exactly one caller may <see cref="Consume"/>.
        /// </summary>
        public static Handoff? Peek(Storage storage, DateTimeOffset? now = null) => Decode(read(storage), now ?? DateTimeOffset.UtcNow);

        /// <summary>
        /// Takes the parked handoff, if there is a usable one, and clears it.
        /// </summary>
        /// <remarks>
        /// ONE-SHOT, and cleared BEFORE it is even parsed. A handoff that survived its own consumption would
        /// demo the same map on every launch from here on, and clearing only what parsed would leave exactly
        /// the values this code cannot understand parked forever. Deleting first means the worst a bad value
        /// can cost is one odd startup, never a game stuck demoing.
        /// </remarks>
        public static Handoff? Consume(Storage storage, DateTimeOffset? now = null)
        {
            string? contents = read(storage);

            Clear(storage);

            return Decode(contents, now ?? DateTimeOffset.UtcNow);
        }

        /// <summary>
        /// Arms a handoff and takes the game down so that it comes back up on the demo. Returns whether the
        /// restart was actually started.
        /// </summary>
        /// <remarks>
        /// Both failure modes back out completely rather than restarting into an ordinary intro, which would
        /// cost the user their editor session for nothing: a handoff that could not be written, and a
        /// platform that cannot relaunch the game (<see cref="OsuGameBase.RestartAppWhenExited"/> returns
        /// false everywhere except desktop, where <c>OsuGameDesktop</c> overrides it). In the second case the
        /// parked handoff is cleared as well, because a demo waiting on a launch that has to be done by hand,
        /// and may never come, is worse than no demo.
        /// </remarks>
        /// <param name="storage">Game storage, where the handoff is parked.</param>
        /// <param name="beatmapId">The map being edited.</param>
        /// <param name="dropTime">The beatdrop currently authored on it.</param>
        /// <param name="restartWhenExited">Queues the relaunch, returning whether the platform supports one.</param>
        /// <param name="exit">Starts the shutdown that the relaunch follows.</param>
        /// <param name="report">Tells the user why nothing happened, in the cases where nothing happens.</param>
        /// <param name="now">Overridable clock, for tests.</param>
        public static bool Reboot(Storage storage, Guid beatmapId, double dropTime, Func<bool> restartWhenExited, Action exit, Action<string> report, DateTimeOffset? now = null)
        {
            if (!Arm(storage, beatmapId, dropTime, now))
            {
                report("Couldn't set the beatdrop demo up, so the game has not been restarted.");
                return false;
            }

            if (!restartWhenExited())
            {
                Clear(storage);
                report("Demoing the beatdrop restarts the game, which isn't supported on this platform.");
                return false;
            }

            exit();
            return true;
        }

        /// <summary>
        /// Looks up what a handoff names, through <paramref name="lookup"/>, and degrades to <c>null</c>
        /// when it names nothing usable. The map can have been deleted or made unreadable between the demo
        /// being asked for and the game coming back up, and a startup must survive that as a normal intro
        /// rather than as a crash.
        /// </summary>
        public static T? Resolve<T>(Handoff? handoff, Func<Guid, T?> lookup) where T : class
        {
            if (handoff == null)
                return null;

            try
            {
                return lookup(handoff.BeatmapId);
            }
            catch (Exception e)
            {
                Logger.Log($"Beatdrop demo target could not be loaded: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// The parked line: version, beatmap, drop time, and when it was armed.
        /// </summary>
        public static string Encode(Guid beatmapId, double dropTime, DateTimeOffset armedAt)
            => string.Join('|',
                handoff_version,
                beatmapId.ToString("D"),
                dropTime.ToString("0.###", CultureInfo.InvariantCulture),
                armedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Reads back a parked line. Anything unrecognised, malformed, impossible or older than
        /// <see cref="HANDOFF_LIFETIME"/> is <c>null</c>, which every caller treats as "no demo pending".
        /// </summary>
        public static Handoff? Decode(string? contents, DateTimeOffset now)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(contents))
                    return null;

                string[] parts = contents.Trim().Split('|');

                if (parts.Length != 4 || parts[0] != handoff_version)
                    return null;

                if (!Guid.TryParse(parts[1], out var beatmapId) || beatmapId == Guid.Empty)
                    return null;

                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double dropTime)
                    || !double.IsFinite(dropTime) || dropTime < 0)
                {
                    return null;
                }

                if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long armedAtMillis))
                    return null;

                if (now - DateTimeOffset.FromUnixTimeMilliseconds(armedAtMillis) > HANDOFF_LIFETIME)
                    return null;

                return new Handoff(beatmapId, dropTime);
            }
            catch (Exception e)
            {
                // Degrading to a normal intro is the whole contract here, so nothing thrown while reading a
                // value the user never sees is worth failing startup over.
                Logger.Log($"Ignoring an unreadable beatdrop demo handoff: {e.Message}");
                return null;
            }
        }

        private static string? read(Storage storage)
        {
            try
            {
                if (!storage.Exists(HANDOFF_FILENAME))
                    return null;

                using (var stream = storage.GetStream(HANDOFF_FILENAME))
                {
                    if (stream == null)
                        return null;

                    using (var reader = new StreamReader(stream))
                        return reader.ReadToEnd();
                }
            }
            catch (Exception e)
            {
                Logger.Log($"Could not read the beatdrop demo handoff: {e.Message}");
                return null;
            }
        }

        #endregion
    }
}
