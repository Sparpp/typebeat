// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>Validates mapper-entered ending times.</summary>
    public static class MapEndTime
    {
        /// <summary>The latest line end in the map, 0 when it has no lines.</summary>
        public static double LastLineEnd(IBeatmap beatmap)
            => beatmap.HitObjects.OfType<TypeBeatHitObject>().Select(h => h.Line.EndTime).DefaultIfEmpty(0).Max();

        /// <summary>
        /// Rounds to milliseconds, clamps to a known track length, and requires a time after the last line.
        /// </summary>
        public static bool TryNormalise(double requestedMs, double lastLineEnd, double trackLength, out double endTime)
        {
            endTime = 0;

            if (!double.IsFinite(requestedMs))
                return false;

            double rounded = Math.Round(requestedMs);

            if (trackLength > 0)
                rounded = Math.Min(rounded, Math.Round(trackLength));

            if (rounded <= lastLineEnd)
                return false;

            endTime = rounded;
            return true;
        }
    }
}
