// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Screens.Play;

namespace typebeat.Game.Screens.Edit.GameplayTest
{
    /// <summary>The editor tempo inherited by one Test Play, without adding a gameplay mod.</summary>
    public sealed class EditorTestPlayback
    {
        public double Rate { get; }

        /// <summary>The editor tempo applied to WPM and pp previews.</summary>
        public double StatisticsRate => Rate;

        /// <summary>Compose the editor statistics tempo with the live mod rate, including ramp mods.</summary>
        public double GetRateForStatistics(IGameplayClock? clock)
            => (clock?.GetTrueGameplayRate() ?? 1) * StatisticsRate;

        public EditorTestPlayback(double rate)
        {
            Rate = rate;
        }
    }
}
