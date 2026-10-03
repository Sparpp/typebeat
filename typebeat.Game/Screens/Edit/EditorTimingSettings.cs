// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Bindables;
using typebeat.Game.Beatmaps.ControlPoints;

namespace typebeat.Game.Screens.Edit
{
    /// <summary>Editor-only view/input preferences. Visibility and snapping are independent.</summary>
    public class EditorTimingSettings
    {
        public readonly BindableBool ShowGrid = new BindableBool();
        public readonly BindableBool SnapToGrid = new BindableBool();
        public readonly BindableBool SnapToCaret = new BindableBool(true);
        public readonly BindableBool Metronome = new BindableBool();

        /// <summary>
        /// Whether the timeline waveform draws the map's ISOLATED VOCALS STEM instead of the full
        /// mix (backlog 392), so a mapper can see where the vocals actually start. View-only: the
        /// editor clock keeps playing the full mix, and timing, judgement and save are untouched.
        /// A map with no stem has no toggle to set this, and the value is ignored when the stem's
        /// waveform cannot be obtained.
        /// </summary>
        public readonly BindableBool VocalsWaveform = new BindableBool();

        // Whole and half notes span multiple quarter-note beats.
        public readonly BindableInt BeatMultiplier = new BindableInt(1) { MinValue = 1, MaxValue = 4 };

        public double Snap(double time, ControlPointInfo points, int divisor)
        {
            if (!SnapToGrid.Value || points.TimingPoints.Count == 0)
                return time;
            if (BeatMultiplier.Value == 1)
                return points.GetClosestSnappedTime(time, divisor);

            var point = points.TimingPointAt(time);
            double step = point.BeatLength * BeatMultiplier.Value;
            double candidate = point.Time + Math.Round((Math.Max(0, time) - point.Time) / step, MidpointRounding.AwayFromZero) * step;
            if (candidate < 0)
                candidate += step;
            var next = points.TimingPointAfter(time);
            return next != null && Math.Abs(next.Time - time) <= Math.Abs(candidate - time) ? next.Time : candidate;
        }
    }
}
