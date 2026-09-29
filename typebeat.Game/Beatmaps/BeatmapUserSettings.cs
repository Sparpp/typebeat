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
        /// OBSOLETE, no longer read or written. This was the per-difficulty intro pool override (realm
        /// schema 53 to 57). Since schema 58 the override is per SET, <see cref="BeatmapSetInfo.IntroPoolInclusion"/>,
        /// and the 58 migration derived each set's value from these (any difficulty <c>true</c> gives
        /// <c>true</c>, every difficulty <c>false</c> gives <c>false</c>, anything else <c>null</c>).
        /// </summary>
        /// <remarks>
        /// Left in the schema rather than removed, because dropping a realm property is a migration of its
        /// own. Not marked <see cref="System.ObsoleteAttribute"/> since the 58 migration still has to read it.
        /// </remarks>
        public bool? IntroPoolInclusion { get; set; }
    }
}
