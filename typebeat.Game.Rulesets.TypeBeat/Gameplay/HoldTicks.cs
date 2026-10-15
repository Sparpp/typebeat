// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>Which of a hold's authored ticks a play honours, shared by judging, display and the editor.</summary>
    public static class HoldTicks
    {
        /// <summary>The closest two ticks may sit.</summary>
        public const double MIN_GAP_MS = 20;

        /// <summary>The earliest a tick may sit: clear of the press.</summary>
        public static double WindowStart(double start) => start + HoldTailRule.MIN_SLACK_MS;

        /// <summary>A tick must sit before this: clear of the Great release window.</summary>
        public static double WindowEnd(double start, double end) => end - HoldTailRule.Slack(start, end);

        /// <summary>The hold's ticks rounded to whole ms, inside [<see cref="WindowStart"/>, <see cref="WindowEnd"/>), distinct and ascending.</summary>
        public static double[] UsableTicks(WordHold hold)
        {
            var authored = hold.Ticks;

            if (authored.Count == 0 || !double.IsFinite(hold.StartTime) || !double.IsFinite(hold.EndTime) || hold.EndTime <= hold.StartTime)
                return Array.Empty<double>();

            double lo = WindowStart(hold.StartTime);
            double hi = WindowEnd(hold.StartTime, hold.EndTime);
            var kept = new SortedSet<double>();

            foreach (double tick in authored)
            {
                if (!double.IsFinite(tick))
                    continue;

                double t = Math.Round(tick);

                if (t >= lo && t < hi)
                    kept.Add(t);
            }

            return kept.Count == 0 ? Array.Empty<double>() : kept.ToArray();
        }

        /// <summary>How many evenly spaced ticks fit with every gap at least <see cref="MIN_GAP_MS"/> after rounding.</summary>
        public static int MaxCount(double start, double end)
        {
            double span = WindowEnd(start, end) - WindowStart(start);

            if (!(span > 0))
                return 0;

            return Math.Max(0, (int)Math.Floor(span / (MIN_GAP_MS + 1)) - 1);
        }

        /// <summary>Up to <paramref name="count"/> ticks spread evenly over the window, whole ms (at most <see cref="MaxCount"/>).</summary>
        public static double[] Even(double start, double end, int count)
        {
            int n = Math.Min(count, MaxCount(start, end));

            if (n <= 0)
                return Array.Empty<double>();

            double lo = WindowStart(start);
            double step = (WindowEnd(start, end) - lo) / (n + 1);
            var ticks = new double[n];

            for (int k = 0; k < n; k++)
                ticks[k] = Math.Round(lo + (k + 1) * step);

            return ticks;
        }

        /// <summary><paramref name="hold"/> re-spanned, each tick keeping its share; ticks squeezed out are dropped.</summary>
        public static WordHold Stretched(WordHold hold, double newStart, double newEnd)
        {
            var moved = hold with { StartTime = newStart, EndTime = newEnd };

            if (hold.Ticks.Count == 0)
                return moved;

            double scale = hold.EndTime > hold.StartTime ? (newEnd - newStart) / (hold.EndTime - hold.StartTime) : 1;
            double[] ticks = hold.Ticks.Select(t => newStart + (t - hold.StartTime) * scale).ToArray();

            return moved with { Ticks = UsableTicks(moved with { Ticks = ticks }) };
        }
    }
}
