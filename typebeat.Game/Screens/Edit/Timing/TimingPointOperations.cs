// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Beatmaps.Timing;

namespace typebeat.Game.Screens.Edit.Timing
{
    /// <summary>Uses lazer's BPM conversion and remove/reinsert approach for changing offsets.</summary>
    public static class TimingPointOperations
    {
        public static TimingControlPoint Set(EditorBeatmap beatmap, TimingControlPoint? point, double offset, double bpm, int meter)
        {
            if (!double.IsFinite(offset) || !double.IsFinite(bpm) || bpm < 1 || bpm > 10000 || meter < 1 || meter > 32)
                throw new ArgumentOutOfRangeException(nameof(bpm));

            if (beatmap.ControlPointInfo.TimingPoints.Any(p => !ReferenceEquals(p, point) && p.Time == offset))
                throw new InvalidOperationException("A timing point already exists at this offset.");

            beatmap.BeginChange();
            try
            {
                if (point != null)
                    beatmap.ControlPointInfo.GroupAt(point.Time)?.Remove(point);

                point = new TimingControlPoint
                {
                    BeatLength = 60000 / bpm,
                    TimeSignature = new TimeSignature(meter),
                    OmitFirstBarLine = point?.OmitFirstBarLine ?? false,
                };
                beatmap.ControlPointInfo.Add(offset, point);
            }
            finally
            {
                beatmap.EndChange();
            }

            return point;
        }

        public static void Delete(EditorBeatmap beatmap, TimingControlPoint point)
        {
            beatmap.BeginChange();
            try
            {
                beatmap.ControlPointInfo.GroupAt(point.Time)?.Remove(point);
            }
            finally
            {
                beatmap.EndChange();
            }
        }
    }
}
