// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;

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
        ///
        /// <para>"This beatmap" is the CONVERTED one: a caller that converted with mods gets a
        /// profile of what those mods produced, so the Literate mod's punctuation cells count in
        /// the target and average WPM exactly as they do in the play. The peak is the one
        /// exception, and the profile says why.</para>
        /// </summary>
        /// <param name="rate">
        /// The CLOCK to read it at: 1 for no rate mod, 1.5 for DoubleTime, 0.75 for HalfTime, and
        /// whatever a custom rate mod asks for. The peak and the average are WPM, so they scale with
        /// the clock; the target is the map's hardest window re-expressed at a fixed reading DURATION,
        /// and a faster clock shortens that duration too, so it is recomputed rather than multiplied.
        /// </param>
        TypingPaceProfile? GetTypingPace(double rate = 1);
    }

    /// <summary>
    /// Peak, target and average typing pace for a beatmap, plus a WPM curve over its length. All
    /// three are WPM, in the typing-test unit of 5 characters to the word, the same unit the
    /// in-game counter and the results screen use, so every WPM the game ever shows a player means
    /// one thing. They are three different questions, not one ladder: <see cref="PeakWpm"/> is the
    /// fastest rolling window, <see cref="AverageWpm"/> is the whole map's cells over its sung time,
    /// and <see cref="TargetWpm"/> is the map's hardest window by raw speed re-expressed as the
    /// speed an equally demanding thirty-second stretch would ask for. The CPM twins they used
    /// to carry alongside are gone: since the typing-test redefinition a CPM is its WPM times five
    /// exactly, so it said nothing its own row did not.
    /// </summary>
    public sealed class TypingPaceProfile
    {
        /// <summary>Raw (unnormalised) WPM at evenly spaced points from the map's first to its last typed cell.</summary>
        public required IReadOnlyList<double> WpmCurve { get; init; }

        /// <summary>Highest WPM over any rolling window of the map.</summary>
        public required double PeakWpm { get; init; }

        /// <summary>
        /// The pace to sustain: the map's PEAK difficulty window read at a fixed duration, i.e.
        /// <c>LyricPaceStatistics.TargetWpm</c>, which is the same figure the Star Rating Sandbox
        /// prints beside every map. The peak's difficulty is a ratio (WPM over what a typist can
        /// sustain for that many seconds), so reading it at one duration keeps maps comparable; it
        /// replaced a per-line mean over the map's fastest lines, which answered a different
        /// question and is kept as <c>LyricPaceStatistics.LineAverageWpm</c>. Typability and the
        /// rhythm bonus are left out of both the window choice and the conversion, so this figure
        /// does not move when either experiment does.
        /// </summary>
        public required double TargetWpm { get; init; }

        /// <summary>
        /// The whole map's typing pace: every typeable cell over the total time the map is sung,
        /// with pauses wider than the break threshold left out and freestyle slots not counted
        /// (<c>LyricPaceStatistics.AverageWpm</c>). A supported punctuation mark counts here when
        /// the mods this profile was asked of turned it into a typed cell.
        /// </summary>
        public required double AverageWpm { get; init; }

    }
}
