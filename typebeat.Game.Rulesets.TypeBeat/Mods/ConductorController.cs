// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// The bounds and gains <see cref="ConductorController"/> runs on. The two rate bounds come
    /// from <see cref="TypeBeatModConductor"/>'s user settings; the rest are the mod's own
    /// constants, carried here rather than baked into the maths so a test can drive a degenerate
    /// controller (no deadband, no slew) and read one term at a time.
    /// </summary>
    /// <param name="MinRate">Slowest playback the controller may ask for.</param>
    /// <param name="MaxRate">Fastest playback the controller may ask for.</param>
    /// <param name="ProportionalGainPerMs">Rate added per millisecond of phase error outside the deadband.</param>
    /// <param name="DeadbandMs">Phase error inside which the proportional term is exactly zero.</param>
    /// <param name="SlewPerSecond">Maximum change in rate per second of REAL time.</param>
    /// <param name="SupplyTauSeconds">Time constant of the typing-speed EMA, in REAL seconds.</param>
    public readonly record struct ConductorTuning(
        double MinRate,
        double MaxRate,
        double ProportionalGainPerMs,
        double DeadbandMs,
        double SlewPerSecond,
        double SupplyTauSeconds)
    {
        /// <summary>The shipping tuning at the mod's default rate band. See the mod for the reasoning.</summary>
        public static ConductorTuning Default => new ConductorTuning(
            TypeBeatModConductor.DEFAULT_MIN_RATE,
            TypeBeatModConductor.DEFAULT_MAX_RATE,
            TypeBeatModConductor.PROPORTIONAL_GAIN_PER_MS,
            TypeBeatModConductor.DEADBAND_MS,
            TypeBeatModConductor.SLEW_PER_SECOND,
            TypeBeatModConductor.SUPPLY_TAU_SECONDS);

        /// <summary>The same tuning with the user's rate band substituted in.</summary>
        public ConductorTuning WithRateBand(double minRate, double maxRate)
            => this with { MinRate = minRate, MaxRate = maxRate };
    }

    /// <summary>
    /// One fixed step's worth of observation of the play. Every VALUE here is read off the
    /// <c>TypingEngine</c> and the beatmap rather than off wall time; what the wall clock decides
    /// (see <see cref="ConductorPacing"/>) is only how often a step happens.
    /// </summary>
    /// <param name="AcceptedCells">
    /// Accepted (correct) keypresses observed since the previous step. Fractional so a test can
    /// drive a perfectly smooth typist; live it is a whole number of <c>CharJudged</c> events.
    /// Divided by the step's REAL length it is the player's real-world typing speed.
    /// </param>
    /// <param name="DemandCellsPerSecond">
    /// How fast the map is asking the player to type right here: the countable-character density of
    /// the active line. 0 when there is nothing to measure, which disables the feed-forward term.
    ///
    /// <para>This is characters per second of MAP time, and the supply it is divided by is
    /// characters per second of REAL time. That asymmetry is the point rather than a bug: the ratio
    /// of the two is dimensionally a RATE, and it is exactly the rate at which this player can keep
    /// up with this line.</para>
    /// </param>
    /// <param name="PhaseErrorMs">
    /// Signed phase error in MAP milliseconds, POSITIVE when the player is AHEAD of the song (the
    /// caret sits on a character the song has not reached yet). The one quantity in the law that is
    /// properly measured in song time, and it stays that way. This is
    /// <c>-TypingEngine.CurrentLeadLag(time)</c>; null when the caret has no judgeable cell.
    /// </param>
    /// <param name="HasActiveLine">Whether a lyric line is active at all (false in the intro, an instrumental gap, the outro).</param>
    /// <param name="LineComplete">
    /// Whether the caret has nothing left to type on the active line, i.e.
    /// <c>TypingEngine.IsLineComplete</c>. That is a CARET predicate (the caret sits past the last
    /// cell), not a "every cell was typed correctly" one, and the caret is what this controller
    /// follows: a line the player finished early, one they gave up with the line skip and one they
    /// walked away from all park the caret in the same place, and in every one of them the player is
    /// waiting for the song rather than being late for it.
    /// </param>
    public readonly record struct ConductorInputs(
        double AcceptedCells,
        double DemandCellsPerSecond,
        double? PhaseErrorMs,
        bool HasActiveLine,
        bool LineComplete);

    /// <summary>
    /// Everything the controller carries between steps.
    /// </summary>
    /// <param name="Rate">The playback rate the mod is currently asking the track for.</param>
    /// <param name="SupplyCellsPerSecond">The typing-speed EMA, in countable characters per second.</param>
    /// <param name="Authority">
    /// How far the EMA has actually filled, in [0, 1]. The share that has NOT filled is credited to
    /// the feed-forward at the map's own pace, so a controller that has just engaged (or has just
    /// come back from a gap) asks for 1.00x instead of reading its own empty filter as "the player
    /// has stopped typing" and stalling the song.
    /// </param>
    public readonly record struct ConductorState(double Rate, double SupplyCellsPerSecond, double Authority)
    {
        /// <summary>A controller that has observed nothing yet, playing at the normal rate.</summary>
        public static ConductorState Initial => new ConductorState(1, 0, 0);

        /// <summary>The same state with the filter emptied but the rate left where it is (a seek or a rewind).</summary>
        public ConductorState WithFilterCleared() => new ConductorState(Rate, 0, 0);
    }

    /// <summary>
    /// The per-frame PACING decision for <see cref="TypeBeatModConductor"/>'s driver: how far to
    /// advance the fixed-step accumulator, and whether what just happened means the filter is
    /// describing a run that is no longer being played. Pure, like the law itself, so it is pinned
    /// with no playfield, no clock and no track.
    ///
    /// <para>The controller integrates on REAL time, uniformly, at every rate (backlog 254). The law
    /// is a servo on two REAL-WORLD quantities: how fast this player is actually typing, and how far
    /// the song has drifted from the character their caret is on. Neither is measured in song time,
    /// so integrating them in song time put a factor of the rate through both halves of the law:</para>
    /// <list type="number">
    /// <item>THE FEED-FORWARD SELF-INFLATED as 1/rate. Supply is an EMA of accepted cells per unit
    /// of the integration clock, so a player typing s cells a real second while the song ran at rate
    /// r read as s/r, and <c>supply / demand</c> came out (s/d)/r: the slower the song got, the
    /// faster the player appeared to be, and the more the feed-forward asked for. A player holding a
    /// steady pace some way behind the playhead therefore settled at whatever rate balanced that
    /// inflation against the proportional term instead of closing the gap, which is the reported
    /// "the song keeps the gap instead of coming back to me".</item>
    /// <item>THE SLEW BECAME EXPONENTIAL in the rate. A bound of 0.8 per second of TRACK time is
    /// 0.8 * r per second of REAL time, i.e. dr/dt = -0.8r near the bottom of the band: a decay that
    /// approaches 0 and never arrives. A player who stopped typing altogether kept hearing the song,
    /// and only the sub-floor real-time pacing backlog 252 had added for a different reason rescued
    /// the park at all, seconds late. On real time the same bound is linear and lands on an exact 0.</item>
    /// </list>
    ///
    /// <para>Both deadlocks backlog 252 fixed stay fixed here by construction rather than by a
    /// special case: real time never freezes with the song, so a parked controller still takes the
    /// step the reviving keypress lands on, and real time never runs 51x, so the stall guard below
    /// cannot be tripped by a fast song.</para>
    /// </summary>
    /// <param name="AdvanceMs">Milliseconds to add to the driver's fixed-step accumulator.</param>
    /// <param name="ClearFilter">Whether to empty the filter (the rate itself is always kept).</param>
    public readonly record struct ConductorPacing(double AdvanceMs, bool ClearFilter)
    {
        /// <summary>A frame longer than this in REAL time is a seek, a hitch or a stall, not a frame.</summary>
        public const double MAX_REAL_FRAME_MS = 250;

        /// <summary>
        /// Decide how one frame is paced.
        /// </summary>
        /// <param name="trackElapsedMs">Gameplay-clock time since the previous frame. Read only to
        /// spot a REWIND: a forward gap-skip is an ordinary jump the filter survives, but a backwards
        /// step means the run the filter describes did not happen.</param>
        /// <param name="realElapsedMs">Wall time since the previous frame, and the whole of the
        /// advance. Nearly zero for the catch-up iterations a frame-stability container runs, which
        /// is correct: they are one frame's worth of real time between them.</param>
        public static ConductorPacing Decide(double trackElapsedMs, double realElapsedMs)
        {
            // A backwards step in track time is a seek or a rewind: the run the filter describes did
            // not happen.
            if (trackElapsedMs < 0)
                return new ConductorPacing(0, true);

            if (realElapsedMs > MAX_REAL_FRAME_MS)
                return new ConductorPacing(0, true);

            return new ConductorPacing(Math.Max(realElapsedMs, 0), false);
        }
    }

    /// <summary>
    /// The Conductor mod's control law, as a pure function so it can be driven headlessly with no
    /// drawables, no clock and no audio (see <c>TypeBeatModConductorTest</c>). It is called at a
    /// FIXED step of REAL time (backlog 254), the axis both of its measurements are already in; see
    /// <see cref="ConductorPacing"/> for why stepping it in track time put a factor of the rate
    /// through the law and broke both of its terms.
    ///
    /// <para>Three terms, in the order they are combined:</para>
    /// <list type="number">
    /// <item>FEED-FORWARD, <c>supply / demand</c>: how fast the player is typing (real cells a real
    /// second) over how fast the map is asking them to (map cells a map second). That ratio is
    /// dimensionally a RATE and is RATE-INVARIANT: it is the speed the song would have to run at for
    /// this player to keep up, and it does not move when the song's speed does. This sets the
    /// frequency, and on its own would let the player hold any phase offset they liked.</item>
    /// <item>PROPORTIONAL, <c>Kp * e</c> on the phase error in MAP milliseconds, positive when the
    /// player is ahead: this pulls the song onto the player rather than merely alongside them. With
    /// a rate-invariant feed-forward it is the term that actually CLOSES a gap, because it is the
    /// only one that is not satisfied by merely matching the player's pace: any rate above the pace
    /// they can sustain grows the error, and the growing error pushes the rate back down, so the
    /// loop is a type-1 servo whose steady-state lag is bounded by the deadband. A deadband makes
    /// the term exactly zero for small errors and eases in from the deadband edge (rather than
    /// stepping off it), so ordinary human jitter does not modulate the music.</item>
    /// <item>SLEW, applied last: the rate may not move faster than
    /// <see cref="ConductorTuning.SlewPerSecond"/> per REAL second, so corrections glide at a
    /// constant, audible speed whatever the song is doing. This is also what keeps
    /// <c>MasterGameplayClockContainer.checkPlaybackValidity</c> quiet, since that compares
    /// accumulated gameplay time against rate * elapsed with a 300 ms tolerance.</item>
    /// </list>
    ///
    /// <para>WHEN THERE IS NOTHING TYPEABLE between the caret and the playhead the whole law is
    /// bypassed and the target is simply 1.00x (clamped into the user's band), so intros,
    /// instrumental gaps and outros sound normal and the skip overlays behave exactly as they do
    /// unmodded. That is two states, not one: no active line at all
    /// (<see cref="ConductorInputs.HasActiveLine"/>), and an active line whose caret has run off the
    /// end (<see cref="ConductorInputs.LineComplete"/>), which is where a player who FINISHED EARLY
    /// waits out the rest of the line.</para>
    ///
    /// <para>Through both of them the supply EMA and the authority are FROZEN, not relaxed. Letting
    /// them decay makes the controller punish the player for being ahead: the phase error is null
    /// there and no cell is accepted, so the filter would empty toward "this player has stopped
    /// typing" and drag the rate to the band floor for exactly the player who got in front of the
    /// song. Freezing means the next line opens on the pace that was actually measured, instead of
    /// dipping at every line start that follows a gap while the EMA refills. The two freeze
    /// TOGETHER: authority is the share of the filter credited back to the map's own pace, so
    /// freezing supply while authority decayed would add a spurious +1 to the feed-forward and
    /// overshoot the moment the next line opened.</para>
    ///
    /// <para>On a real-time filter that freeze is strictly more meaningful than it was: a frozen
    /// real-world pace stays true at whatever rate the song comes back at, whereas a frozen
    /// track-time pace was only ever true at the rate it happened to be measured at, so the reading
    /// the next line opened on was silently scaled by whatever the song had been doing during the
    /// gap.</para>
    /// </summary>
    public static class ConductorController
    {
        /// <summary>The fixed integration step, in REAL milliseconds (50 Hz).</summary>
        public const double STEP_MS = 20;

        /// <summary><see cref="STEP_MS"/> in seconds, the unit every rate in the tuning is per.</summary>
        public const double STEP_SECONDS = STEP_MS / 1000d;

        /// <summary>
        /// Below this density the feed-forward term is meaningless (a one-character line, a line
        /// whose characters all share a target) and is dropped in favour of 1.00x, leaving the
        /// proportional term to do the whole job.
        /// </summary>
        public const double MIN_MEANINGFUL_DEMAND = 0.2;

        /// <summary>
        /// Advance the controller by one fixed step.
        /// </summary>
        /// <param name="state">The state the previous step returned.</param>
        /// <param name="inputs">What the engine looked like over this step.</param>
        /// <param name="tuning">Bounds and gains.</param>
        /// <param name="dtSeconds">The step length in REAL seconds; normally <see cref="STEP_SECONDS"/>.</param>
        public static ConductorState Step(ConductorState state, ConductorInputs inputs, ConductorTuning tuning, double dtSeconds)
        {
            // The two settings are independent sliders, so nothing stops a player putting the
            // minimum above the maximum. Read them as an unordered pair rather than throwing out of
            // Math.Clamp in the middle of gameplay.
            double lo = Math.Min(tuning.MinRate, tuning.MaxRate);
            double hi = Math.Max(tuning.MinRate, tuning.MaxRate);

            double supply = state.SupplyCellsPerSecond;
            double authority = state.Authority;
            double target;

            if (!inputs.HasActiveLine || inputs.LineComplete)
            {
                // Nothing typeable between the caret and the playhead: no line at all, or a line
                // whose caret has run off the end. Aim at the normal rate and FREEZE the filter.
                //
                // Freezing rather than relaxing is the whole point. A player who finished a line
                // early sits here with no phase error and no accepted cells, so a decaying filter
                // would read them as silent and haul the rate down to the band floor, punishing the
                // one player who is ahead. Held, the filter still describes the pace they actually
                // typed at, which is the reading the next line has to open on. Supply and authority
                // freeze as a pair: authority is the unobserved share credited at the map's own
                // pace, so decaying it alone would add a spurious +1 to the next line's feed-forward.
                target = Math.Clamp(1d, lo, hi);
            }
            else
            {
                // Exponential blend for a step of this length. A non-positive time constant means
                // "no filter at all", which is what the term-isolating tests want.
                double alpha = tuning.SupplyTauSeconds > 0
                    ? 1 - Math.Exp(-dtSeconds / tuning.SupplyTauSeconds)
                    : 1;

                double instant = dtSeconds > 0 ? inputs.AcceptedCells / dtSeconds : 0;

                supply += (instant - supply) * alpha;
                authority += (1 - authority) * alpha;

                double feedForward = 1;

                if (inputs.DemandCellsPerSecond > MIN_MEANINGFUL_DEMAND)
                {
                    // supply / demand: real cells a real second over map cells a map second, which
                    // is the rate this player can sustain and does not move when the song's rate
                    // does. Plus the share of the window that has NOT been observed yet, credited
                    // at the map's own pace. An EMA reads low until it fills, and reading
                    // that emptiness as "the player has stopped typing" would dive the rate to the
                    // floor at the start of every play. Crediting the unobserved share instead makes
                    // a player who is exactly on pace read as exactly 1.00x from the very first
                    // step, while a player who really is silent still falls away as the window fills.
                    feedForward = (supply / inputs.DemandCellsPerSecond) + (1 - authority);
                }

                double proportional = 0;

                if (inputs.PhaseErrorMs is double error)
                {
                    // Soft deadband: zero inside it, and continuous across its edge, so the term
                    // eases in rather than stepping to Kp * DeadbandMs the instant it engages.
                    double outside = Math.Sign(error) * Math.Max(0, Math.Abs(error) - tuning.DeadbandMs);
                    proportional = outside * tuning.ProportionalGainPerMs;
                }

                target = Math.Clamp(feedForward + proportional, lo, hi);
            }

            double maxMove = tuning.SlewPerSecond * dtSeconds;
            double rate = state.Rate + Math.Clamp(target - state.Rate, -maxMove, maxMove);

            return new ConductorState(rate, supply, authority);
        }
    }
}
