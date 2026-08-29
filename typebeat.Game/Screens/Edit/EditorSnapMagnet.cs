// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Screens.Edit
{
    /// <summary>
    /// The one rule behind every editor drag snap: a MAGNET, not a grid. A dragged time stays
    /// exactly where the cursor is unless it comes within <see cref="RADIUS_PX"/> SCREEN PIXELS of
    /// the thing it can snap to, at which point it lands on it exactly. Because the radius is in
    /// pixels rather than milliseconds, the pull feels identical at every zoom level and the drag
    /// stays fully continuous everywhere else.
    /// </summary>
    public static class EditorSnapMagnet
    {
        /// <summary>How close (in screen pixels) a drag has to come before the magnet takes it.</summary>
        public const float RADIUS_PX = 9;

        /// <summary>
        /// <paramref name="candidate"/> when it sits within <paramref name="toleranceMs"/> of
        /// <paramref name="time"/>, otherwise <paramref name="time"/> untouched. Callers convert
        /// <see cref="RADIUS_PX"/> into the tolerance using their own time-per-pixel scale.
        /// </summary>
        public static double Magnet(double time, double candidate, double toleranceMs)
            => toleranceMs > 0 && Math.Abs(candidate - time) <= toleranceMs ? candidate : time;
    }
}
