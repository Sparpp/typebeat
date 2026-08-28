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
    /// <param name="SlewPerSecond">Maximum change in rate per second of TRACK time.</param>
    /// <param name="SupplyTauSeconds">Time constant of the typing-speed EMA, in TRACK seconds.</param>
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
    /// One fixed step's worth of observation of the play. Everything here is read off the
    /// <c>TypingEngine</c> and the gameplay clock, never off wall time, so a replay fed the same
    /// keystrokes reproduces the same sequence of inputs.
    /// </summary>
    /// <param name="AcceptedCells">
    /// Accepted (correct) keypresses observed since the previous step. Fractional so a test can
    /// drive a perfectly smooth typist; live it is a whole number of <c>CharJudged</c> events.
    /// </param>
    /// <param name="DemandCellsPerSecond">
    /// How fast the map is asking the player to type right here: the countable-character density of
    /// the active line. 0 when there is nothing to measure, which disables the feed-forward term.
    /// </param>
    /// <param name="PhaseErrorMs">
    /// Signed phase error in milliseconds, POSITIVE when the player is AHEAD of the song (the caret
    /// sits on a character the song has not reached yet). This is
    /// <c>-TypingEngine.CurrentLeadLag(time)</c>; null when the caret has no judgeable cell.
    /// </param>
    /// <param name="HasActiveLine">Whether a lyric line is active at all (false in the intro, an instrumental gap, the outro).</param>
    public readonly record struct ConductorInputs(
        double AcceptedCells,
        double DemandCellsPerSecond,
        double? PhaseErrorMs,
        bool HasActiveLine);

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
    /// The Conductor mod's control law, as a pure function so it can be driven headlessly with no
    /// drawables, no clock and no audio (see <c>TypeBeatModConductorTest</c>). It is called at a
    /// FIXED step of TRACK time, which is what keeps a live play and a replay of it on nearly the
    /// same rate curve: track time is the only axis both agree on.
    ///
    /// <para>Three terms, in the order they are combined:</para>
    /// <list type="number">
    /// <item>FEED-FORWARD, <c>supply / demand</c>: how fast the player is typing over how fast the
    /// map is asking them to. This sets the frequency, and on its own would let the player hold any
    /// phase offset they liked.</item>
    /// <item>PROPORTIONAL, <c>Kp * e</c> on the phase error in milliseconds, positive when the
    /// player is ahead: this pulls the song onto the player rather than merely alongside them. A
    /// deadband makes the term exactly zero for small errors and eases in from the deadband edge
    /// (rather than stepping off it), so ordinary human jitter does not modulate the music.</item>
    /// <item>SLEW, applied last: the rate may not move faster than
    /// <see cref="ConductorTuning.SlewPerSecond"/>, so corrections glide. This is also what keeps
    /// <c>MasterGameplayClockContainer.checkPlaybackValidity</c> quiet, since that compares
    /// accumulated gameplay time against rate * elapsed with a 300 ms tolerance.</item>
    /// </list>
    ///
    /// <para>With NO ACTIVE LINE the whole law is bypassed and the target is simply 1.00x (clamped
    /// into the user's band), so intros, instrumental gaps and outros sound normal and the skip
    /// overlays behave exactly as they do unmodded. The supply filter relaxes toward "no evidence"
    /// over the same time constant while that lasts, so a short gap between two lines barely
    /// disturbs it and a long one starts the next line fresh.</para>
    /// </summary>
    public static class ConductorController
    {
        /// <summary>The fixed integration step, in TRACK milliseconds (50 Hz).</summary>
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
        /// <param name="dtSeconds">The step length in TRACK seconds; normally <see cref="STEP_SECONDS"/>.</param>
        public static ConductorState Step(ConductorState state, ConductorInputs inputs, ConductorTuning tuning, double dtSeconds)
        {
            // The two settings are independent sliders, so nothing stops a player putting the
            // minimum above the maximum. Read them as an unordered pair rather than throwing out of
            // Math.Clamp in the middle of gameplay.
            double lo = Math.Min(tuning.MinRate, tuning.MaxRate);
            double hi = Math.Max(tuning.MinRate, tuning.MaxRate);

            // Exponential blend for a step of this length. A non-positive time constant means "no
            // filter at all", which is what the term-isolating tests want.
            double alpha = tuning.SupplyTauSeconds > 0
                ? 1 - Math.Exp(-dtSeconds / tuning.SupplyTauSeconds)
                : 1;

            double supply = state.SupplyCellsPerSecond;
            double authority = state.Authority;
            double target;

            if (!inputs.HasActiveLine)
            {
                // Nothing to follow. Relax the filter toward "no evidence" so the next line is not
                // entered holding a stale reading, and aim at the normal rate.
                supply += (0 - supply) * alpha;
                authority += (0 - authority) * alpha;

                target = Math.Clamp(1d, lo, hi);
            }
            else
            {
                double instant = dtSeconds > 0 ? inputs.AcceptedCells / dtSeconds : 0;

                supply += (instant - supply) * alpha;
                authority += (1 - authority) * alpha;

                double feedForward = 1;

                if (inputs.DemandCellsPerSecond > MIN_MEANINGFUL_DEMAND)
                {
                    // supply / demand, with the share of the window that has NOT been observed yet
                    // credited at the map's own pace. An EMA reads low until it fills, and reading
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
