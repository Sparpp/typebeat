// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;

namespace typebeat.Game.Screens.Menu
{
    /// <summary>
    /// A request from the editor to replay the game startup intro (<see cref="IntroScreen"/>) soundtracked
    /// by the map being edited, at the beatdrop timestamp currently authored on it. A beatdrop is otherwise
    /// only observable by quitting and relaunching the game, so it gets stamped blind; this is the feedback
    /// loop that lets it be heard in place.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT routed through <see cref="IntroBeatdropPool"/>. The pool decides which of the user's
    /// maps may soundtrack a real startup, and a map can be kept out of it from song select ("Use on game
    /// intro", <see cref="BeatmapUserSettings.IntroPoolInclusion"/>). A demo previews the map you are editing
    /// whether or not it has been opted in, so this path neither reads that override nor writes it, and it
    /// has no preview-point fallback either: with no beatdrop there is nothing to demo (<see cref="CanDemo"/>)
    /// and the intro is not played at all, rather than played silent or on some other map's timestamp, which
    /// the user would read as their own.
    /// </remarks>
    public class IntroBeatdropDemo
    {
        /// <summary>
        /// The demo button's caption while the map has a beatdrop to demo.
        /// </summary>
        public const string CAPTION = "Play the game intro on this map's beatdrop";

        /// <summary>
        /// The demo button's caption while the map declares no beatdrop. It replaces the normal caption
        /// rather than sitting alongside it, so the button says why it will not do anything.
        /// </summary>
        public const string NO_BEATDROP_CAPTION = "no beatdrop set!";

        /// <summary>
        /// The map to soundtrack the intro with, resolved to a working beatmap when the intro loads.
        /// </summary>
        public BeatmapInfo BeatmapInfo { get; }

        /// <summary>
        /// The point in the song (ms) to land on the menu reveal. Always the authored beatdrop: a demo is
        /// only ever raised for a map that has one.
        /// </summary>
        public double DropTime { get; }

        public IntroBeatdropDemo(BeatmapInfo beatmapInfo, double dropTime)
        {
            BeatmapInfo = beatmapInfo;
            DropTime = dropTime;
        }

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
        /// Playing the intro takes the user out of the editor, so the save prompt comes first and
        /// <paramref name="play"/> only ever runs from inside the continuation handed to
        /// <paramref name="promptToSave"/>. A prompt that is cancelled simply never calls back, and nothing
        /// has moved by that point.
        /// </remarks>
        /// <param name="beatdropTime">The beatdrop currently authored on the map being edited.</param>
        /// <param name="promptToSave">Raises the save prompt, invoking its argument once (and only if) the user commits to leaving.</param>
        /// <param name="play">Starts the demo for the given drop time.</param>
        public static void Request(double? beatdropTime, Action<Action> promptToSave, Action<double> play)
        {
            if (!CanDemo(beatdropTime))
                return;

            double drop = beatdropTime!.Value;

            promptToSave(() => play(drop));
        }
    }
}
