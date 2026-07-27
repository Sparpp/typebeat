// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// The score multiplier awarded for playing at a track rate other than 1.0x (Half Time, Double
    /// Time, Nightcore, and the Wind Up / Wind Down ramps).
    ///
    /// <para>
    /// type!beat ranks rate mods at EVERY speed, not just at their default, so the multiplier has to
    /// be a real function of the rate rather than a flat per-mod constant: two players who both wear
    /// "DT" must not be paid the same when one ran 1.01x and the other 2.00x. The function is:
    /// </para>
    ///
    /// <code>
    /// r = round(rate, 2)                                   // the sliders step by 0.01
    /// raw = r >= 1 ? 1 + 0.46 * (r - 1)                    // speeding up
    ///              : 1 - 3.00 * (1 - r)                    // slowing down
    /// multiplier = round(max(0.10, raw), 4)
    /// </code>
    ///
    /// <para>
    /// Properties this buys, all of them load-bearing:
    /// <list type="bullet">
    /// <item>CONTINUOUS AND EQUAL TO 1.0 AT r = 1. A rate mod dialled all the way back toward
    /// no-mod pays exactly what no-mod pays, from both sides. osu's own V2 curve jumps (Half Time
    /// at 0.99x pays 0.886x there), which is indefensible once the setting is ranked.</item>
    /// <item>MONOTONIC ABOVE THE FLOOR. Faster always pays strictly more; slower pays strictly less
    /// down to r = 0.70, below which the 0.10 floor clamps the whole [0.50, 0.70] tail flat (the
    /// increase side, [1.00, 2.00], stays strictly monotonic throughout). osu's V2 curve floors the
    /// rate to 0.1 / 0.05 buckets over its whole domain instead, so 1.50x and 1.59x pay the same.</item>
    /// <item>EXACT AT THE DEFAULTS. Default Double Time / Nightcore (1.50x) is 1.23x and default
    /// Half Time (0.75x) is 0.25x; both defaults are still exact anchor points of the curve, they
    /// just no longer match the pre-nerf flat-default HT payout of 0.55x.</item>
    /// <item>DETERMINISTIC AND CHEAP TO MIRROR. Two rounding steps with fixed decimal counts, so an
    /// independent implementation (the web backend) lands on the same double, and the contract can
    /// be stated as exact decimal values rather than "within some epsilon".</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The slopes are chosen by the defaults, not by taste: 0.46 is (1.23 - 1) / (1.50 - 1) and 3.00
    /// is (1 - 0.25) / (1 - 0.75). The reward side is deliberately shallower than the penalty side,
    /// which is the same asymmetry osu ships; slowing a lyric down is worth far more to a typist
    /// than speeding it up costs.
    /// </para>
    /// </summary>
    public static class TypeBeatRateMultiplier
    {
        /// <summary>Multiplier gained per +1.0x of rate above 1.0x. Fixed by the 1.50x → 1.23x anchor.</summary>
        public const double INCREASE_SLOPE = 0.46;

        /// <summary>Multiplier lost per -1.0x of rate below 1.0x. Fixed by the 0.75x → 0.25x anchor.</summary>
        public const double DECREASE_SLOPE = 3.0;

        /// <summary>
        /// Floor on the returned multiplier. Reached at r = 0.70 and clamped flat the rest of the way
        /// down to the reachable rate floor of 0.50x (both the Half Time slider and the Wind Down ramp
        /// stop there), so every rate in [0.50, 0.70] pays the same 0.10.
        /// </summary>
        public const double MINIMUM = 0.1;

        /// <summary>Decimal places the rate is snapped to before use (the slider's <c>Precision</c>).</summary>
        public const int RATE_DECIMALS = 2;

        /// <summary>Decimal places the returned multiplier is snapped to.</summary>
        public const int MULTIPLIER_DECIMALS = 4;

        /// <summary>
        /// The multiplier for a given track rate. 1.0 in, 1.0 out.
        /// </summary>
        public static double For(double rate)
        {
            double snapped = Math.Round(rate, RATE_DECIMALS, MidpointRounding.AwayFromZero);

            double raw = snapped >= 1
                ? 1 + INCREASE_SLOPE * (snapped - 1)
                : 1 - DECREASE_SLOPE * (1 - snapped);

            return Math.Round(Math.Max(MINIMUM, raw), MULTIPLIER_DECIMALS, MidpointRounding.AwayFromZero);
        }
    }
}
