// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// Score multipliers for type!beat mods. The flat values mirror osu!'s current (V2) multipliers
    /// so modded scores read consistently with the rest of lazer. Mods not listed here stay at 1.0x
    /// (Sudden Death, Muted). Mashing is unranked, but still carries the Relax 0.1x for display parity.
    ///
    /// <para>
    /// The rate mods are the exception: type!beat ranks them at every speed, so they are paid by
    /// <see cref="TypeBeatRateMultiplier"/> (a continuous, strictly monotonic function of the rate)
    /// rather than by osu's bucketed V2 curve. It agrees with the old curve exactly at the default
    /// speeds, so no default-speed score changes value.
    /// </para>
    /// </summary>
    public class TypeBeatScoreMultiplierCalculator : ScoreMultiplierCalculator
    {
        public TypeBeatScoreMultiplierCalculator(ScoreMultiplierContext context)
            : base(context)
        {
            // Difficulty reduction.
            Single<TypeBeatModNoFail>(hasMultiplier: 0.5);
            Single<TypeBeatModHalfTime>(hasMultiplier: halfTime => TypeBeatRateMultiplier.For(halfTime.SpeedChange.Value));

            // Difficulty increase.
            // Sudden Death (1.0x)
            Single<TypeBeatModDoubleTime>(hasMultiplier: doubleTime => TypeBeatRateMultiplier.For(doubleTime.SpeedChange.Value));
            Single<TypeBeatModNightcore>(hasMultiplier: nightcore => TypeBeatRateMultiplier.For(nightcore.SpeedChange.Value));
            // Flashlight is a fixed character-window reveal (no size setting). 1.05x, trimmed from
            // the old circular flashlight's 1.2x: the character window is a far milder handicap.
            // Mirrored by the mod's own ScoreMultiplier self-report and the server's ModMultiplier.
            Single<TypeBeatModFlashlight>(hasMultiplier: 1.05);
            Single<TypeBeatModLiterate>(hasMultiplier: 1.05);

            // Conversion.
            // Fletcher unpins the caret from the playhead: still ranked, and only a shade easier than
            // a cue-locked run (every char is still typed and still judged against its own target),
            // so it takes a small trim rather than a penalty.
            Single<TypeBeatModFletcher>(hasMultiplier: 0.98);

            // Automation.
            Single<TypeBeatModMashing>(hasMultiplier: 0.1);

            // Fun.
            // Muted (1.0x) is deliberately absent: it is a flex, not a difficulty adjustment, so it is
            // ranked and carries no bonus and no penalty. Pinned by TypeBeatModMutedTest.
            Single<ModWindUp>(hasMultiplier: timeRampMultiplier);
            Single<ModWindDown>(hasMultiplier: timeRampMultiplier);
        }

        /// <summary>
        /// A ramp is paid mostly for the rate it spends the most map at (osu weights 80/20 toward
        /// the slower end). Both ends go through the same rate curve as the static rate mods, so a
        /// ramp that starts or ends at a rate a rate mod could have been set to is priced the same
        /// way. Wind Up / Wind Down stay unranked regardless; this is display-only.
        /// </summary>
        private static double timeRampMultiplier(ModTimeRamp timeRamp)
        {
            double minSpeed = Math.Min(timeRamp.InitialRate.Value, timeRamp.FinalRate.Value);
            double maxSpeed = Math.Max(timeRamp.InitialRate.Value, timeRamp.FinalRate.Value);

            return 0.8 * TypeBeatRateMultiplier.For(minSpeed) + 0.2 * TypeBeatRateMultiplier.For(maxSpeed);
        }
    }
}
