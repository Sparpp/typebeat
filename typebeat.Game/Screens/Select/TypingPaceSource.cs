// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Overlays.BeatmapSet;

namespace typebeat.Game.Screens.Select
{
    /// <summary>
    /// Which typing pace song select shows for the selected difficulty. The server's figures win when it has
    /// them, so song select, the set overlay and the website print one set of numbers for a map even while
    /// the two pace implementations sit at different versions. The local figures stand in whenever the
    /// server's do not apply: offline, before or without the set lookup, on a row the server has not
    /// measured yet, on a local copy that differs from the online version, and under a mod that changes the
    /// pace (a rate mod or Literate), since the server stores the unmodded map only.
    /// </summary>
    public static class TypingPaceSource
    {
        /// <summary>
        /// The server's pace for <paramref name="beatmap"/>, or null when it does not apply (see the class
        /// remarks). <paramref name="online"/> is the difficulty from song select's own set lookup, which is
        /// the request that already serves the set overlay's figures; nothing extra is fetched for this.
        /// </summary>
        public static TypingPaceProfile? ServerFigures(APIBeatmap? online, BeatmapInfo beatmap, bool modsChangePace)
        {
            if (modsChangePace || online == null || beatmap.OnlineID <= 0 || online.OnlineID != beatmap.OnlineID)
                return null;

            // Only the exact version the server measured: a locally edited or outdated copy is paced locally.
            if (string.IsNullOrEmpty(online.MD5Hash) || online.MD5Hash != beatmap.MD5Hash)
                return null;

            if (online.PeakWpm is not double peak || online.TargetWpm is not double target || online.AverageWpm is not double average)
                return null;

            // The same test the set overlay applies before it draws the graph.
            if (TypingStatsFormat.NormalisedCurve(online.WpmCurve) == null)
                return null;

            return new TypingPaceProfile
            {
                WpmCurve = online.WpmCurve!.Select(v => (double)v).ToArray(),
                PeakWpm = peak,
                TargetWpm = target,
                AverageWpm = average,
            };
        }

        /// <summary>The figures to show: the server's when they apply, otherwise <paramref name="local"/>.</summary>
        public static TypingPaceProfile? Choose(TypingPaceProfile? local, APIBeatmap? online, BeatmapInfo beatmap, bool modsChangePace)
            => ServerFigures(online, beatmap, modsChangePace) ?? local;
    }
}
