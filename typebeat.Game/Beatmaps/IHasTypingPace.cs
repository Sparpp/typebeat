// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// A playable beatmap that can describe its own typing pace. Implemented by the typebeat
    /// ruleset's beatmap; song select's metadata wedge consumes it through this interface for the
    /// same reason it consumes <see cref="BeatmapStatistic"/>s that way, the game project cannot
    /// reference the ruleset project (the dependency runs the other way).
    /// </summary>
    public interface IHasTypingPace
    {
        /// <summary>
        /// The pace profile of this beatmap, or null when it carries nothing typeable to measure.
        /// Potentially expensive; call it off the update thread.
        /// </summary>
        TypingPaceProfile? GetTypingPace();
    }

    /// <summary>
    /// Peak and average typing pace for a beatmap, plus a WPM curve over its length. WPM is in real
    /// words (no "1 word = 5 characters" estimate), so the peak and the average are directly
    /// comparable with each other.
    /// </summary>
    public sealed class TypingPaceProfile
    {
        /// <summary>Raw (unnormalised) WPM at evenly spaced points from the map's first to its last typed cell.</summary>
        public required IReadOnlyList<double> WpmCurve { get; init; }

        /// <summary>Highest WPM over any rolling window of the map.</summary>
        public required double PeakWpm { get; init; }

        /// <summary>Highest characters-per-minute over any rolling window; maximised independently of <see cref="PeakWpm"/>.</summary>
        public required double PeakCpm { get; init; }

        /// <summary>Mean per-line WPM across the map.</summary>
        public required double AverageWpm { get; init; }

        /// <summary>Mean per-line CPM across the map.</summary>
        public required double AverageCpm { get; init; }
    }
}
