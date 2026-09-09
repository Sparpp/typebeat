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
    /// Peak, target and average typing pace for a beatmap, plus a WPM curve over its length. All
    /// three are WPM, in the typing-test unit of 5 characters to the word, the same unit the
    /// in-game counter and the results screen use, so every WPM the game ever shows a player means
    /// one thing and the three read as a ladder. The CPM twins they used to carry alongside are
    /// gone: since the typing-test redefinition a CPM is its WPM times five exactly, so it said
    /// nothing its own row did not.
    /// </summary>
    public sealed class TypingPaceProfile
    {
        /// <summary>Raw (unnormalised) WPM at evenly spaced points from the map's first to its last typed cell.</summary>
        public required IReadOnlyList<double> WpmCurve { get; init; }

        /// <summary>Highest WPM over any rolling window of the map.</summary>
        public required double PeakWpm { get; init; }

        /// <summary>
        /// The pace to sustain: the average WPM across the fastest fifth of the map's lyric lines
        /// (<c>LyricPaceStatistics.TargetWpm</c>). The same per-line mean <see cref="AverageWpm"/>
        /// is, over the demanding lines alone, so it never reads below it.
        /// </summary>
        public required double TargetWpm { get; init; }

        /// <summary>Mean per-line WPM across the map.</summary>
        public required double AverageWpm { get; init; }
    }
}
