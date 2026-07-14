// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// type!beat's only fail condition is mashing: <see cref="WRONG_KEY_FAIL_STREAK"/>
    /// consecutive rejected wrong keys fail the play. Health mirrors the current streak
    /// (full at 0, empty at the fail threshold) so the HUD health bar doubles as the
    /// "stop mashing" warning; any accepted char restores it instantly. Judgements
    /// (including seal misses) never affect health.
    /// </summary>
    public partial class TypeBeatHealthProcessor : HealthProcessor
    {
        public const int WRONG_KEY_FAIL_STREAK = 13;

        /// <summary>Reflects the engine's consecutive wrong-key streak; fails at the threshold.</summary>
        public void ApplyWrongKeyStreak(int streak)
        {
            Health.Value = Math.Max(0, 1 - (double)streak / WRONG_KEY_FAIL_STREAK);

            if (streak >= WRONG_KEY_FAIL_STREAK)
                TriggerFailure();
        }

        /// <summary>An accepted char ends the streak: health snaps back to full.</summary>
        public void ResetWrongKeyStreak() => Health.Value = 1;

        protected override double GetHealthIncreaseFor(JudgementResult result) => 0;

        /// <summary>Failure comes exclusively from the wrong-key streak, never from results.</summary>
        protected override bool CheckDefaultFailCondition(JudgementResult result) => false;
    }
}
