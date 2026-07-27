// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Realms;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// User settings overrides that are attached to a beatmap.
    /// </summary>
    public class BeatmapUserSettings : EmbeddedObject
    {
        /// <summary>
        /// An audio offset that can be used for timing adjustments.
        /// </summary>
        public double Offset { get; set; }

        /// <summary>
        /// Whether this beatmap may be picked to soundtrack the game intro, overriding the default
        /// (which is "yes if the map declares an intro beatdrop", see <see cref="IBeatmap.IntroBeatdropTime"/>).
        /// <c>null</c> means no override: follow the beatdrop. <c>false</c> keeps a beatdrop-carrying map
        /// out of the pool without touching the authored timestamp; <c>true</c> opts a map with no
        /// beatdrop in. Decided by the "Use on game intro" song select context menu toggle and read by
        /// <see cref="Screens.Menu.IntroBeatdropPool"/>.
        /// </summary>
        /// <remarks>
        /// This is user data, not map content: it lives here rather than in the beatmap file so that
        /// toggling it never re-encodes (and never un-ranks) the map, and so that an accidental untick
        /// cannot destroy a hand-found beatdrop timestamp.
        /// </remarks>
        public bool? IntroPoolInclusion { get; set; }
    }
}
