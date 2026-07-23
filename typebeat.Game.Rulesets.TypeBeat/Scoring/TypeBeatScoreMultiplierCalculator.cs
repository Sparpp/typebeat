// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// Score multipliers for type!beat mods. Values mirror osu!'s current (V2) multipliers so
    /// modded scores read consistently with the rest of lazer; the rate helpers below are copied
    /// verbatim from <c>OsuScoreMultiplierCalculatorV2</c>. Mods not listed here stay at 1.0x
    /// (Sudden Death). Mashing is unranked, but still carries the Relax 0.1x for display parity.
    /// </summary>
    public class TypeBeatScoreMultiplierCalculator : ScoreMultiplierCalculator
    {
        public TypeBeatScoreMultiplierCalculator(ScoreMultiplierContext context)
            : base(context)
        {
            // Difficulty reduction.
            Single<TypeBeatModNoFail>(hasMultiplier: 0.5);
            Single<TypeBeatModHalfTime>(hasMultiplier: halfTime => halfTimeMultiplier(halfTime.SpeedChange.Value));

            // Difficulty increase.
            // Sudden Death (1.0x)
            Single<TypeBeatModDoubleTime>(hasMultiplier: doubleTime => doubleTimeMultiplier(doubleTime.SpeedChange.Value));
            Single<TypeBeatModNightcore>(hasMultiplier: nightcore => doubleTimeMultiplier(nightcore.SpeedChange.Value));
            Single<TypeBeatModFlashlight>(hasMultiplier: flashlightMultiplier);
            Single<TypeBeatModLiterate>(hasMultiplier: 1.05);

            // Automation.
            Single<TypeBeatModMashing>(hasMultiplier: 0.1);

            // Fun.
            Single<ModWindUp>(hasMultiplier: timeRampMultiplier);
            Single<ModWindDown>(hasMultiplier: timeRampMultiplier);
        }

        private static double halfTimeMultiplier(double speedChange)
        {
            // 0.2x at 0.5x speed, +0.07x per 0.05x speed increment.
            // Default HT (0.75x) = 0.55
            return (int)(speedChange * 20) / 20.0 * 1.4 - 0.5;
        }

        private static double doubleTimeMultiplier(double speedChange)
        {
            // Floor to the nearest multiple of 0.1.
            double value = (int)(speedChange * 10) / 10.0;

            // 0.01 penalty for non-default rates.
            double penalty = value != 1.5 && value != 1.0 ? 0.01 : 0.0;

            // Linear from 1.0 to 1.46, minus the penalty.
            // Default DT (1.5x) = 1.23
            return (value - 1) * 0.46 + 1 - penalty;
        }

        private static double flashlightMultiplier(ModFlashlight flashlight)
        {
            // Multiplier of 1.2x, reduced by 0.02 per 0.1 increase in flashlight size.
            double value = Math.Max(1.02, Math.Min(1.2, 1.2 - 0.2 * (flashlight.SizeMultiplier.Value - 1)));

            if (!flashlight.ComboBasedSize.Value)
                value = 1 + (value - 1) / 5;

            return value;
        }

        private static double timeRampMultiplier(ModTimeRamp timeRamp)
        {
            double minSpeed = Math.Min(timeRamp.InitialRate.Value, timeRamp.FinalRate.Value);
            double maxSpeed = Math.Max(timeRamp.InitialRate.Value, timeRamp.FinalRate.Value);

            double minMultiplier = minSpeed < 1 ? halfTimeMultiplier(minSpeed) : doubleTimeMultiplier(minSpeed);
            double maxMultiplier = maxSpeed < 1 ? halfTimeMultiplier(maxSpeed) : doubleTimeMultiplier(maxSpeed);

            return 0.8 * minMultiplier + 0.2 * maxMultiplier;
        }
    }
}
