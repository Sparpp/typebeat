// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The rhythmic-complexity bonus: stars for the extent to which keeping perfect timing means
    /// CHANGING typing pace, weighted toward the map's demanding passages.
    ///
    /// <para>THE QUESTION IT ASKS. Not "how fast is this map" (the envelope already prices that)
    /// but "how much timing does one constant pace <c>t = a + b*key</c> fail to reach". A stretch
    /// a player can cover at a single constant speed pays EXACTLY nothing, however much its word
    /// durations wobble, because the game's judgement windows absorb the wobble. Only timing
    /// outside the allowed intervals is charged, and it is charged through a WEIGHTED MINIMAX: the
    /// smallest <c>E</c> for which one pace fits every interval once each is widened by
    /// <c>E / weight</c>, with <c>weight</c> fixed before the search as that keypress's own demand
    /// over the map's peak. An easy keypress therefore gives up its own timing cheaply, and no
    /// single hardest word can price a whole passage.</para>
    ///
    /// <para>THE ROLLING METHOD (the shipped one). Every timed character is visited, and the
    /// constraint at index <c>i</c> asks whether a STRAIGHT pace line can pass through three
    /// Great intervals at <c>i - h</c>, <c>i</c>, <c>i + h</c>. For evenly spaced indices
    /// straightness is <c>tL - 2*tM + tR = 0</c>, so the constraint's own residual is the best a
    /// constant pace can do, and the weighted minimax residual of that three-point constraint is
    /// <c>residual / (1/wL + 2/wM + 1/wR)</c>. Four distances (h, 2h, 4h, 8h) accumulate short and
    /// long deviations, each divided by the local typing period so the same absolute error costs
    /// the same share of the local typing time, and by <c>h^2</c> so overlapping checks average
    /// rather than multiply. There are no sections and no split threshold: the scan is continuous,
    /// so a rhythmically complex map with no rests is not underweighted.</para>
    ///
    /// <para>RESTS. A pause BETWEEN two lyric lines is deleted from the scan outright: the next
    /// line cues its own first word, so the silence is neither a rest the pace recovers inside nor
    /// a rhythm the pace has to cover. A pause INSIDE a line is sung time and stays in as idle
    /// recovery, which dilutes the local contribution continuously (squared, so an arbitrarily
    /// long rest tends to nothing) rather than through a threshold.</para>
    ///
    /// <para>WHAT IT IS NOT. A calibrated measurement, and not a solution to the minimum total
    /// pace variation. It is a lower bound on the pace work a passage forces, against a chosen
    /// experimental curve, and the strength is a dial rather than a fitted constant.</para>
    /// </summary>
    public static class RhythmicComplexity
    {
        /// <summary>How much forced pace change raises the rating; 0 leaves the rating alone.</summary>
        public const double Strength = 0.3;

        /// <summary>
        /// The Great window the scan judges with, in milliseconds: 150 is the shipped ladder's own
        /// Great rung, so the proxy charges only the pace changes the engine cannot absorb.
        /// </summary>
        public const double Tolerance = 150;

        /// <summary>Rolling distance in characters; checks run at this distance and 2x, 4x and 8x it.</summary>
        public const int Horizon = 3;

        /// <summary>Rolling load that fills 63% of the available bonus.</summary>
        public const double RollingScale = 0.2;

        /// <summary>Never zero, so the weighted bracket stays finite.</summary>
        public const double WeightFloor = 0.02;

        /// <summary>The bonus's own cap, the multiplier it approaches as forced change grows.</summary>
        public const double MaxMultiplier = 2.0;

        /// <summary>
        /// One keypress of the scan: the interval a Great judgement allows, its own target time,
        /// the idle time accumulated before it inside its line, its demand weight, and (for a caller
        /// that reads the load per WORD rather than per press) the word it came from.
        /// </summary>
        public readonly record struct Press(double Lo, double Hi, double Target, double Idle, double Weight, int WordIndex = -1);

        /// <summary>What a scan found: the load, how many checks forced something, and the worst margin.</summary>
        public readonly record struct Report(double Load, int Checks, int Changed, double Worst, double Multiplier);

        /// <summary>
        /// The scan's load AND the same load split back onto the keypresses it was measured on: each
        /// check's contribution is shared equally by its three checkpoints and by the four distances,
        /// so <see cref="Press"/> sums to <see cref="Load"/> and a caller can read the load at a POINT
        /// rather than only across the whole map. The combined model uses that to place the load on
        /// the timeline (see <see cref="ChunkedEndurance"/>); the sections/rolling bonus ignores it.
        /// </summary>
        public readonly record struct Split(double Load, double[] Press, int Checks, int Changed, double Worst);

        /// <summary>
        /// The rolling load of a press stream (see the type's own remarks), split onto the presses.
        /// <see cref="Compute"/> reads this and applies the multiplier; callers that need the local
        /// load read the split directly.
        /// </summary>
        public static Split Scan(IReadOnlyList<Press> presses, int horizon = Horizon)
        {
            var split = new double[presses.Count];
            double load = 0;
            int checks = 0, changed = 0;
            double worst = 0;

            if (presses.Count == 0)
                return new Split(0, split, 0, 0, 0);

            int step = Math.Max(1, Math.Min(16, horizon));
            int[] distances = { step, 2 * step, 4 * step, 8 * step };

            foreach (int h in distances)
            {
                double scaleLoad = 0;

                for (int i = h; i + h < presses.Count; i++)
                {
                    Press a = presses[i - h], b = presses[i], c = presses[i + h];
                    checks++;

                    // For evenly spaced indices a straight pace line requires
                    // tL - 2*tM + tR = 0; the smaller of the two bracket violations is how
                    // far the three intervals are from admitting one.
                    double residual = Math.Max(0, Math.Max(a.Lo - 2 * b.Hi + c.Lo, 2 * b.Lo - a.Hi - c.Hi));

                    if (residual <= 1e-9)
                        continue;

                    double error = residual / (1 / a.Weight + 2 / b.Weight + 1 / c.Weight);

                    // Idle time inside a line dilutes the local pace, squared so a long rest
                    // tends to nothing. A pause across a line boundary is not in `Idle` at all:
                    // the caller deletes it from the timeline.
                    double elapsed = Math.Max(1e-9, c.Target - a.Target);
                    double active = Math.Max(1e-9, elapsed - (c.Idle - a.Idle));
                    double recovery = Math.Pow(Math.Min(1, active / elapsed), 2);
                    double period = active / (2 * h);
                    double contribution = error / Math.Max(1e-9, period) / ((double)h * h) * recovery;

                    scaleLoad += contribution;

                    // Each check's contribution is shared by its three checkpoints AND by the
                    // four scales it is averaged over, so the split adds up to the reported
                    // load exactly rather than to the unscaled per-scale total.
                    double share = contribution / 3 / distances.Length;
                    split[i - h] += share;
                    split[i] += share;
                    split[i + h] += share;
                    changed++;
                    worst = Math.Max(worst, error);
                }

                // A fixed scale count, including scales too wide for this map, so a longer map
                // does not renormalise every existing contribution.
                load += scaleLoad / distances.Length;
            }

            return new Split(load, split, checks, changed, worst);
        }

        /// <summary>
        /// The rolling load of a press stream (see the type's own remarks), and the multiplier for
        /// the strength and saturation the caller chose.
        /// </summary>
        public static Report Compute(IReadOnlyList<Press> presses, double strength = Strength, int horizon = Horizon, double scale = RollingScale)
        {
            Split scan = Scan(presses, horizon);

            return new Report(scan.Load, scan.Checks, scan.Changed, scan.Worst, Multiplier(scan.Load, strength, scale));
        }

        /// <summary>
        /// The rating multiplier the load earns: <c>1 + strength * (1 - exp(-load / scale))</c>,
        /// clamped between 1 and the bonus's own cap. At strength 0 this is exactly 1, so the
        /// rating is what the envelope (and typability) computed.
        /// </summary>
        public static double Multiplier(double load, double strength = Strength, double scale = RollingScale)
        {
            if (!(strength > 0) || !(scale > 0) || double.IsNaN(load) || double.IsInfinity(load))
                return 1;

            double raw = 1 + strength * (1 - Math.Exp(-load / scale));
            return Math.Min(Math.Min(MaxMultiplier, 1 + strength), Math.Max(1, raw));
        }
    }
}
