// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Screens.Play
{
    /// <summary>Timing rules for waiting at and skipping a map's ending.</summary>
    public static class EndingHold
    {
        public static bool IsHeld(double? intendedEndTime, double currentTime, bool autoSkipEnding, bool allowSkipping)
            => !autoSkipEnding && allowSkipping && intendedEndTime is double end && currentTime < end;

        /// <summary>
        /// Offsets the ending by <see cref="MasterGameplayClockContainer.MINIMUM_SKIP_TIME"/> so the
        /// overlay stays skippable until then. A later storyboard still wins.
        /// </summary>
        public static double OverlayStartTime(double? storyboardLatestEventTime, double? intendedEndTime)
        {
            double storyboard = storyboardLatestEventTime ?? 0;

            return intendedEndTime is double end
                ? Math.Max(storyboard, end + MasterGameplayClockContainer.MINIMUM_SKIP_TIME)
                : storyboard;
        }
    }
}
