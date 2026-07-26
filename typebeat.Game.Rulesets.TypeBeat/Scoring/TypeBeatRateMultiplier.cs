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
    ///              : 1 - 1.80 * (1 - r)                    // slowing down
    /// multiplier = round(max(0.10, raw), 4)
    /// </code>
    ///
    /// <para>
    /// Properties this buys, all of them load-bearing:
    /// <list type="bullet">
    /// <item>CONTINUOUS AND EQUAL TO 1.0 AT r = 1. A rate mod dialled all the way back toward
    /// no-mod pays exactly what no-mod pays, from both sides. osu's own V2 curve jumps (Half Time
    /// at 0.99x pays 0.886x there), which is indefensible once the setting is ranked.</item>
    /// <item>STRICTLY MONOTONIC over the whole reachable domain [0.50, 2.00]. Faster always pays
    /// strictly more, slower always pays strictly less; there is never a rate you can pick for free.
    /// osu's V2 curve floors the rate to 0.1 / 0.05 buckets, so 1.50x and 1.59x pay the same.</item>
    /// <item>EXACT AT THE DEFAULTS. Default Double Time / Nightcore (1.50x) is 1.23x and default
    /// Half Time (0.75x) is 0.55x, the same numbers the flat-default policy paid, so every existing
    /// default-speed score keeps its value and no leaderboard is re-based by this change.</item>
    /// <item>DETERMINISTIC AND CHEAP TO MIRROR. Two rounding steps with fixed decimal counts, so an
    /// independent implementation (the web backend) lands on the same double, and the contract can
    /// be stated as exact decimal values rather than "within some epsilon".</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The slopes are chosen by the defaults, not by taste: 0.46 is (1.23 - 1) / (1.50 - 1) and 1.80
    /// is (1 - 0.55) / (1 - 0.75). The reward side is deliberately shallower than the penalty side,
    /// which is the same asymmetry osu ships; slowing a lyric down is worth far more to a typist
    /// than speeding it up costs.
    /// </para>
    /// </summary>
    public static class TypeBeatRateMultiplier
    {
        /// <summary>Multiplier gained per +1.0x of rate above 1.0x. Fixed by the 1.50x → 1.23x anchor.</summary>
        public const double INCREASE_SLOPE = 0.46;

        /// <summary>Multiplier lost per -1.0x of rate below 1.0x. Fixed by the 0.75x → 0.55x anchor.</summary>
        public const double DECREASE_SLOPE = 1.8;

        /// <summary>
        /// Floor on the returned multiplier. The reachable rate floor is 0.50x (both the Half Time
        /// slider and the Wind Down ramp stop there), which lands exactly on 0.10; the clamp only
        /// exists so a future lower bound can never produce a zero or negative multiplier.
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
