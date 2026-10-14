// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    public partial class LyricLineDisplay
    {
        /// <summary>Approach-only timing: holds own their span, and their syllable's suffix waits for the release.</summary>
        private (double start, double end) approachBarSpan(int index)
        {
            if (holdDurations[index] > 0)
                return (Line.Cells[index].HoldStart!.Value, Line.Cells[index].HoldEnd!.Value);

            var span = sungSpan(index);
            int group = grouping.IndexOf(index);
            int word = Line.WordIndexOf(index);
            if (group >= 0 && word >= 0)
            {
                for (int i = index - 1; i >= 0 && grouping.IndexOf(i) == group && Line.WordIndexOf(i) == word; i--)
                {
                    if (holdDurations[i] <= 0)
                        continue;

                    span.start = Math.Max(span.start, Line.Cells[i].HoldEnd!.Value);
                    break;
                }
            }

            return (span.start, Math.Max(span.start, span.end));
        }

        /// <summary>
        /// <paramref name="first"/> is the candidate bar owner; <paramref name="second"/> is its preceding
        /// duplicate owner or a cell to extend across. Keep the original owner-first character-timing
        /// restrictions, including coincident spans, for ordinary cells.
        /// </summary>
        private bool sharesApproachBar(int first, int second)
        {
            if (holdDurations[first] > 0 || holdDurations[second] > 0
                || characterTiming || grouping.IndexOf(first) < 0
                || charTimedStretch && grouping.IsCharTimedStretch(first))
                return false;

            return Line.Cells[second].IsCountable && approachBarSpan(first) == approachBarSpan(second)
                                                    && windowMultiplier(first) == windowMultiplier(second);
        }

        public (Vector2 Position, float Alpha) FloatingHoldBarSample(int index, float trackFraction) =>
            index >= 0 && index < floatingBars.Length ? floatingBars[index].HoldSample(trackFraction) : default;

        private sealed partial class FloatingIndicatorBar : Container
        {
            private readonly LyricLineDisplay owner;
            private readonly int index;
            private readonly HoldApproachRamp? ramp;

            public float BarAlpha => ramp == null ? Alpha : Alpha * (ramp.Vertices.Length > 0 ? ramp.Vertices[0].Alpha : 0);

            public (Vector2 Position, float Alpha) HoldSample(float fraction) => ramp?.ScreenSample(fraction) ?? default;

            public FloatingIndicatorBar(LyricLineDisplay owner, int index)
            {
                this.owner = owner;
                this.index = index;
                AlwaysPresent = true;
                BypassAutoSizeAxes = Axes.Both;
                Depth = -0.5f;
                Alpha = 0;
                if (owner.holdDurations[index] > 0)
                    Add(ramp = new HoldApproachRamp());
                else
                    Add(new Box { RelativeSizeAxes = Axes.Both });
            }

            protected override void Update()
            {
                base.Update();
                Alpha = 0;
                if (!owner.approachBarsEnabled || index >= owner.cells.Length || !owner.Line.Cells[index].IsCountable)
                    return;
                var span = owner.approachBarSpan(index);
                // Bars always land on the sung target, independently of text indicator alignment.
                double time = owner.sungTime;
                if (ramp != null)
                {
                    float trackWidth = owner.HoldTrackWidthAt(index);
                    Position = new Vector2(owner.cellX[index], owner.glyphHeight + SWEEP_RAIL_OFFSET * owner.SizeRatio);
                    Size = new Vector2(owner.cellX[index + 1] - owner.cellX[index], SWEEP_RAIL_HEIGHT * owner.SizeRatio);
                    Colour = owner.paceColourFor(index);
                    Alpha = owner.cellVisibleAlpha(index);
                    ramp.UpdateRamp(time, span.start, span.end, Width - trackWidth, trackWidth,
                        owner.syllableFadeInDuration, span.end + owner.sungGreatLate * owner.windowMultiplier(index),
                        24 * owner.SizeRatio, Height);
                    return;
                }
                float release = IndicatorRelease(time, span.end + owner.sungGreatLate * owner.windowMultiplier(index));
                if (double.IsNaN(time) || time < span.start - owner.syllableFadeInDuration || release <= 0)
                    return;
                float progress = SyllableFadeInProgress(time, span.start, owner.syllableFadeInDuration);
                float fadeIn = SyllableFadeInProgress(time, span.start - owner.syllableFadeInDuration + approach_bar_fade_in_duration_ms,
                    approach_bar_fade_in_duration_ms);
                if (index > 0 && owner.sharesApproachBar(index, index - 1))
                    return;
                int end = index + 1;
                while (end < owner.cells.Length && owner.sharesApproachBar(index, end))
                    end++;
                float visible = 1;
                for (int i = index; i < end; i++)
                    visible = Math.Min(visible, owner.cellVisibleAlpha(i));
                Alpha = fadeIn * release * visible;
                Position = new Vector2(owner.cellX[index], owner.glyphHeight + SWEEP_RAIL_OFFSET * owner.SizeRatio
                    + 24 * owner.SizeRatio * (1 - progress));
                Size = new Vector2(owner.cellX[end] - owner.cellX[index], SWEEP_RAIL_HEIGHT * owner.SizeRatio);
                Colour = owner.paceColourFor(index);
            }
        }
    }
}
