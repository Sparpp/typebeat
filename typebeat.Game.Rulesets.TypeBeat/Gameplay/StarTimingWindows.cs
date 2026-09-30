// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>Extra judgement tolerance for easier maps, before mod window scales are applied.</summary>
    public static class StarTimingWindows
    {
        /// <summary>Twice the normal width through one star, linearly reaching normal at five stars.</summary>
        public static double ScaleForStars(double stars)
        {
            if (!double.IsFinite(stars))
                return 1;

            return 2 - Math.Clamp((stars - 1) / 4, 0, 1);
        }
    }
}
