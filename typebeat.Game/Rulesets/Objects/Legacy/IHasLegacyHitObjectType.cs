// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Beatmaps.Legacy;

namespace typebeat.Game.Rulesets.Objects.Legacy
{
    /// <summary>
    /// A hit object from a legacy beatmap representation.
    /// </summary>
    public interface IHasLegacyHitObjectType
    {
        /// <summary>
        /// The hit object type.
        /// </summary>
        LegacyHitObjectType LegacyType { get; }
    }
}
