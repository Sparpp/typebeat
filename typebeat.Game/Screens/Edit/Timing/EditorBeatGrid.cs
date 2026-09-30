// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Beatmaps.ControlPoints;

namespace typebeat.Game.Screens.Edit.Timing
{
    /// <summary>
    /// Lazer's timeline tick indexing, calculated from the section origin instead of repeated
    /// floating-point addition. Every timing change starts a new grid and a new measure.
    /// </summary>
    public static class EditorBeatGrid
    {
        public readonly record struct Tick(double Time, int Divisor, bool IsBar);

        public static IEnumerable<Tick> Ticks(ControlPointInfo points, int divisor, double start, double end, double minimumSpacing = 0, int beatMultiplier = 1)
        {
            if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start || divisor < 1)
                yield break;

            for (int p = 0; p < points.TimingPoints.Count; p++)
            {
                var point = points.TimingPoints[p];
                double until = p + 1 < points.TimingPoints.Count ? Math.Min(end, points.TimingPoints[p + 1].Time) : end;
                // The first point supplies timing before its offset too, matching lazer snapping.
                double from = Math.Max(0, p == 0 ? start : Math.Max(start, point.Time));
                if (until < from)
                    continue;

                double step = point.BeatLength * beatMultiplier / divisor;
                // Bound drawing at high BPM / deep zoom-out. Retain beats or bars when subdivisions
                // are too dense to distinguish; snapping still uses the exact selected divisor.
                int stride = step >= minimumSpacing ? 1 : divisor;
                if (step * stride < minimumSpacing)
                    stride *= point.TimeSignature.Numerator * Math.Max(1, (int)Math.Ceiling(minimumSpacing / (point.BeatLength * point.TimeSignature.Numerator)));

                long first = (long)Math.Ceiling((from - point.Time) / (step * stride));
                for (long index = first * stride; ; index += stride)
                {
                    double time = point.Time + index * step;
                    if (time > until || (p + 1 < points.TimingPoints.Count && time >= points.TimingPoints[p + 1].Time))
                        break;

                    bool bar = (index * beatMultiplier) % (point.TimeSignature.Numerator * divisor) == 0 && !(index == 0 && point.OmitFirstBarLine);
                    int colourDivisor = BindableBeatDivisor.GetDivisorForBeatIndex((int)(index % divisor), divisor);
                    yield return new Tick(time, colourDivisor, bar);
                }
            }
        }
    }
}
