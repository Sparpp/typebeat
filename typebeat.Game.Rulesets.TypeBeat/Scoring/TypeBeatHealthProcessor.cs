// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Utils;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// type!beat health is a genuine osu HP pool. Two independent things drain it:
    ///
    /// <list type="bullet">
    /// <item><b>Not typing it right.</b> Every cell that scrolls past untyped seals as a
    /// <see cref="HitResult.Miss"/>, and every wrong character typed into a cell drains at the
    /// KEYPRESS (<see cref="ApplyTypoDrain"/>, refunded by <see cref="RefundTypoDrain"/> if it is
    /// backspaced away); both cost <see cref="MISS_HEALTH_DRAIN"/>. Typing recovers HP
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
        //     Death is quantized to line seals, so it lands on the second seal of Spectator (t~16.8s
        //     of 168.3s) for any drain from about 0.0176 up to 0.0322: seal 1 banks 30 missed cells
        //     and seal 2 banks 27 more, and nothing in between can kill. Backlog 166 moved only the
        //     TYPO drain off the seal: a cell nobody has typed cannot be known missed until its time
        //     has passed, so misses stay seal-quantized and this target is untouched by it.
        //   * Perfect play stays pinned at full (recovery caps at 1).
        //   * ~12% misses spread through otherwise-correct play never approaches 0 (Great recovery
        //     refills to the cap between misses, so the bar only ever dips one miss deep).

        /// <summary>HP restored by a well-timed correct char (the engine's Great tier).</summary>
        public const double GREAT_HEALTH_INCREASE = 0.03;

        /// <summary>HP restored by a correct char in the engine's Ok window.</summary>
        public const double OK_HEALTH_INCREASE = 0.025;

        /// <summary>HP restored by a correct char in the engine's widest scoring window, Meh.</summary>
        public const double MEH_HEALTH_INCREASE = 0.02;

        /// <summary>
        /// HP drained by one untyped cell sealing as a miss (also mistimed/wrong-char misses, and
        /// since backlog 166 one wrong character typed into a cell).
        /// Deliberately BELOW <see cref="GREAT_HEALTH_INCREASE"/>, so one perfect char more than pays
        /// back one miss and a player who resumes typing climbs back to full.
        /// </summary>
        public const double MISS_HEALTH_DRAIN = 0.0225;

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

        /// <summary>
        /// A wrong character was typed INTO a cell (backlog 166): drain the bar NOW, at the
        /// keypress, instead of waiting for the line to seal. HP is the one account a typist reads
        /// while typing, and a typo whose cost only appeared a line later read as unresponsive.
        ///
        /// <para>The typed-through typo is the only judgement that ever had to wait: an accepted
        /// character already recovers at its keypress (its Great/Ok/Meh result is applied there) and
        /// a REJECTED key already drains at its keypress (<see cref="ApplyWrongKeyStreak"/>). What
        /// held the typo back is that its cell's osu RESULT is deferred, because the player may
        /// still backspace and fix it, and the result is what carries HP. So the HP is settled
        /// separately from the result: charged here, given back by <see cref="RefundTypoDrain"/>
        /// when the character is erased.</para>
        ///
        /// <para><b>No double drain.</b> The cell later seals as
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/> if it is still wrong, and that result is
        /// HP-INERT (see <see cref="GetHealthIncreaseFor"/>): a typo's whole HP cost is taken here.
        /// The two sides cannot disagree because there is no per-cell bookkeeping between them, only
        /// the invariant that a cell holds a wrong character for exactly as long as one drain is
        /// outstanding (charged when it enters that state, refunded when it leaves it).</para>
        ///
        /// <para>The drain can EMPTY the bar, so it re-checks the same default fail condition
        /// <c>HealthProcessor.ApplyResultInternal</c> checks after a result moves the bar. That is
        /// the player-visible consequence of the whole change: death can now land mid-line, on the
        /// keypress that empties the bar, rather than only at a seal.</para>
        /// </summary>
        public void ApplyTypoDrain()
        {
            // Mirrors ApplyResultInternal: once the play has failed, nothing moves the bar again.
            if (HasFailed)
                return;

            Health.Value -= MISS_HEALTH_DRAIN;

            // CheckDefaultFailCondition's test, which cannot be called directly here because it
            // takes the JudgementResult this drain deliberately does not have.
            if (Precision.AlmostBigger(Health.MinValue, Health.Value))
                TriggerFailure();
        }

        /// <summary>
        /// The player backspaced a wrong character away (backlog 166): give back what
        /// <see cref="ApplyTypoDrain"/> took for it. The refund is the drain and nothing more, so a
        /// typo, a backspace and a correct retype leave the bar exactly where typing the character
        /// correctly first time would have: the retype earns the ordinary recovery its own
        /// Great/Ok/Meh result carries, and this pays back the detour rather than paying a bonus for
        /// having taken it.
        ///
        /// <para>Erasing a typo and then leaving the cell EMPTY is not rewarded either: the cell is
        /// then one the player did not finish, and it seals as an ordinary miss, so the play ends up
        /// paying exactly one drain for it, as it always did.</para>
        /// </summary>
        public void RefundTypoDrain()
        {
            // The bar is frozen after a fail, so an erase cannot resurrect a dead play. Health is
            // clamped to [0, 1], so a refund into a bar already refilled by later correct characters
            // stops at full rather than banking credit.
            if (HasFailed)
                return;

            Health.Value += MISS_HEALTH_DRAIN;
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

                // An uncorrected typo, which is an osu HIT and would therefore RECOVER health on the
                // stock table (backlog 125: between 124 and 126 it did, so a player who typed
                // nothing but wrong characters healed their way through a map they never typed). It
                // costs exactly what a miss costs, and deliberately reuses that constant: the cell
                // was not typed, and health is the one account that has never cared WHY.
                //
                // ZERO here, not -MISS_HEALTH_DRAIN, because since backlog 166 that cost was
                // already charged, at the KEYPRESS that typed the wrong character
                // (ApplyTypoDrain). This result is the same typo arriving a second time, at the
                // seal, and paying it twice is the one way the two halves could disagree. Every
                // cell this arm can reach is one holding a wrong character nobody erased, which is
                // exactly the set with an outstanding drain, so the whole rule is "the seal takes
                // nothing": there is no per-cell state to consult and none to get wrong.
                case TypeBeatResultMapping.UNFIXED_TYPO:
                    return 0;

                default:
                    // Line containers (IgnoreHit) and anything else are HP-inert.
                    return 0;
            }
        }
    }
}
