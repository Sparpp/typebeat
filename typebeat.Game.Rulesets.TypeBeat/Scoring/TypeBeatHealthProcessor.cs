// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// type!beat health is a genuine osu HP pool. Two independent things drain it:
    ///
    /// <list type="bullet">
    /// <item><b>Not typing.</b> Every cell that scrolls past untyped seals as a
    /// <see cref="HitResult.Miss"/> and drains <see cref="MISS_HEALTH_DRAIN"/>. Typing recovers HP
    /// (<see cref="GREAT_HEALTH_INCREASE"/>/<see cref="OK_HEALTH_INCREASE"/>/<see cref="MEH_HEALTH_INCREASE"/>),
    /// so imperfect play with scattered misses stays healthy, but sustained AFK never recovers and
    /// dies once the accumulated drain empties the bar (default fail condition: health &lt;= 0).</item>
    /// <item><b>Mashing.</b> Each rejected wrong key drains <see cref="WRONG_KEY_HP_DRAIN"/>; an
    /// uninterrupted mash from full empties the bar exactly as the streak reaches
    /// <see cref="WRONG_KEY_FAIL_STREAK"/>, so the HUD bar doubles as the "stop mashing" warning,
    /// and the streak fails the play outright at the threshold.</item>
    /// </list>
    ///
    /// Health is HP only; it never touches score, accuracy or combo (those live in
    /// <see cref="TypeBeatScoreProcessor"/> / the engine and are unchanged).
    /// </summary>
    public partial class TypeBeatHealthProcessor : HealthProcessor
    {
        /// <summary>Consecutive rejected wrong keys that fail the play (mashing guard).</summary>
        public const int WRONG_KEY_FAIL_STREAK = 13;

        // HP deltas as a fraction of the full bar (Health is clamped to [0, 1]).
        //
        // These are DELIBERATELY gentler than osu!'s per-object convention (Great +0.05, Miss
        // -0.10): a type!beat "hit"/"miss" is a single CHARACTER, and a line seals a whole run of
        // untyped characters at once (the real map averages ~23 cells/line, up to 39). Per-object
        // magnitudes would let one fumbled line insta-kill from full. Instead each miss drains a
        // little and any correct key recovers, so death comes from SUSTAINED not-typing, never from
        // one bad line or from sloppy-but-complete timing (the completion-based grading philosophy:
        // typing every cell, even all-Meh, should survive and score an SS).
        //
        // Balance targets (verified in TypeBeatHealthTest):
        //   * Full AFK on the real ~905-cell map empties the bar within the first few line seals
        //     (well under half the map) - clearly "sustained not typing", well before the map ends.
        //   * Perfect play stays pinned at full (recovery caps at 1).
        //   * ~12% misses spread through otherwise-correct play never approaches 0 (Great recovery
        //     refills to the cap between misses, so the bar only ever dips one miss deep).

        /// <summary>HP restored by a perfectly-timed correct char (engine Perfect -> osu Great).</summary>
        public const double GREAT_HEALTH_INCREASE = 0.03;

        /// <summary>HP restored by a correct char in the Good window (engine Good -> osu Ok).</summary>
        public const double OK_HEALTH_INCREASE = 0.025;

        /// <summary>HP restored by a correct char in the Ok window (engine Ok -> osu Meh).</summary>
        public const double MEH_HEALTH_INCREASE = 0.02;

        /// <summary>HP drained by one untyped cell sealing as a miss (also mistimed/wrong-char misses).</summary>
        public const double MISS_HEALTH_DRAIN = 0.04;

        /// <summary>
        /// HP drained by a single rejected wrong key. Sized so an uninterrupted mash from full empties
        /// the bar exactly at <see cref="WRONG_KEY_FAIL_STREAK"/>; the bar IS the mash warning.
        /// </summary>
        public const double WRONG_KEY_HP_DRAIN = 1.0 / WRONG_KEY_FAIL_STREAK;

        /// <summary>
        /// A wrong key was rejected: drain the bar (mash warning) and fail outright once the engine's
        /// consecutive-wrong-key streak reaches the threshold. Rejected keys produce no
        /// <see cref="JudgementResult"/>, so this is the only place they reach health.
        /// </summary>
        public void ApplyWrongKeyStreak(int streak)
        {
            Health.Value -= WRONG_KEY_HP_DRAIN;

            if (streak >= WRONG_KEY_FAIL_STREAK)
                TriggerFailure();
        }

        protected override double GetHealthIncreaseFor(JudgementResult result)
        {
            switch (result.Type)
            {
                case HitResult.Great:
                    return GREAT_HEALTH_INCREASE;

                case HitResult.Ok:
                    return OK_HEALTH_INCREASE;

                case HitResult.Meh:
                    return MEH_HEALTH_INCREASE;

                case HitResult.Miss:
                    return -MISS_HEALTH_DRAIN;

                default:
                    // Line containers (IgnoreHit) and anything else are HP-inert.
                    return 0;
            }
        }
    }
}
