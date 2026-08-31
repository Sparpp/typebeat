// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// The bounds and time constants <see cref="PuppeteerClock"/> runs on. Carried in a struct
    /// rather than baked into the maths so a test can drive a degenerate clock (no smoothing, a
    /// different chase horizon) and read one term at a time.
    /// </summary>
    /// <param name="ChaseMs">
    /// The position-to-velocity horizon, in WALL milliseconds: the tape aims to close the whole
    /// remaining gap in this long, so a gap of <c>E</c> asks for a velocity of <c>E / ChaseMs</c>.
    /// It is therefore also the STEADY-STATE LAG: a player holding a pace of <c>p</c> track ms per
    /// wall ms settles with the tape <c>p * ChaseMs</c> behind their caret, which is exactly why
    /// Puppeteer forgives timing rather than judging it.
    /// </param>
    /// <param name="SmoothingTauMs">
    /// Time constant of the first-order velocity filter, in WALL milliseconds. The velocity never
    /// jumps to its target: it eases, so a keypress spins the reel up and silence lets it wind
    /// down instead of cutting.
    /// </param>
    /// <param name="MinVelocity">
    /// The velocity floor, and NEVER zero. A published frequency of exactly zero makes the
    /// framework stop the track outright (the same reasoning as
    /// <see cref="TypeBeatModConductor.MIN_FREQUENCY_SCALE"/>), and a stopped track is a one-way
    /// door: the mixer flushes and the restart is audible. A floor of 1/512 crawls instead.
    /// </param>
    /// <param name="MaxVelocity">
    /// The velocity ceiling on a TYPING arm. Hardware's number rather than a taste call: this mod
    /// publishes frequency only, that path resamples, and BASS refuses an absolute frequency above
    /// 100 kHz, so a 44.1 kHz song stops tracking at about 2.27x. See
    /// <see cref="TypeBeatModConductor.PITCH_ABSOLUTE_MAX_RATE"/>, which this is.
    /// </param>
    public readonly record struct PuppeteerTuning(
        double ChaseMs,
        double SmoothingTauMs,
        double MinVelocity,
        double MaxVelocity)
    {
        /// <summary>The shipping tuning. See <see cref="TypeBeatModPuppeteer"/> for the reasoning behind each number.</summary>
        public static PuppeteerTuning Default => new PuppeteerTuning(
            TypeBeatModPuppeteer.T_CHASE_MS,
            TypeBeatModPuppeteer.SMOOTHING_TAU_MS,
            TypeBeatModPuppeteer.V_EPSILON,
            TypeBeatModPuppeteer.V_MAX);
    }

    /// <summary>
    /// One tick's worth of "where the tape is being pulled towards, and how hard it may be pulled".
    /// This is the whole of what the model reads off the play: two numbers, both of them derived
    /// from engine state that a stored replay re-derives exactly.
    ///
    /// <para>The DESIRED POSITION is a position and not a velocity on purpose. A velocity command
    /// would need the driver to differentiate a caret that moves in discrete jumps; a position
    /// command lets the model do the smoothing, and lets a frozen caret express itself as a frozen
    /// target rather than as a special case.</para>
    /// </summary>
    /// <param name="DesiredPositionMs">
    /// Where the tape wants to be, in TRACK milliseconds. On a typing arm this is the caret cell's
    /// target time, so it FREEZES the instant the player stops and steps forward with every
    /// accepted character. On a coasting arm it is the next line's first vocal, or
    /// <see cref="double.PositiveInfinity"/> when there is no next line (the cap is what bounds the
    /// tape then, not the target).
    /// </param>
    /// <param name="VelocityCap">
    /// The most this arm will let the tape run at, in track ms per wall ms.
    /// <see cref="PuppeteerTuning.MaxVelocity"/> while the player is typing (chase them as fast as
    /// the audio path can honour) and <see cref="TypeBeatModPuppeteer.COAST_MAX_VELOCITY"/> (1.00x)
    /// while coasting, so a finished line's tail and the instrumental gap behind it play at the
    /// song's own speed instead of being sprinted through toward a target seconds away.
    /// </param>
    public readonly record struct PuppeteerArm(double DesiredPositionMs, double VelocityCap);

    /// <summary>
    /// Everything the model carries between ticks: where the tape is, and how fast it is moving.
    /// </summary>
    /// <param name="PositionMs">The tape's position, in TRACK milliseconds. Monotonic non-decreasing by construction, see <see cref="PuppeteerClock"/>.</param>
    /// <param name="Velocity">Track milliseconds per WALL millisecond. 1 is the song's own speed.</param>
    public readonly record struct PuppeteerState(double PositionMs, double Velocity)
    {
        /// <summary>A tape anchored at <paramref name="positionMs"/> and running at the song's own speed.</summary>
        public static PuppeteerState AnchoredAt(double positionMs) => new PuppeteerState(positionMs, 1);
    }

    /// <summary>
    /// The Puppeteer mod's clock model: a deterministic map from wall time to the track position the
    /// song should be at. Pure, so it can be driven headlessly with no drawables, no clock and no
    /// audio, and so that a stored replay can re-derive the identical curve by re-running it over
    /// wall-stamped frames.
    ///
    /// <para><b>THE CANONICAL SEMANTICS, which are load-bearing.</b> The model is integrated in
    /// FIXED ONE MILLISECOND WALL TICKS, and an arm change takes effect at its own integral wall
    /// millisecond. That is what makes a trajectory a function of the (wall ms -&gt; arm) schedule
    /// ALONE, rather than of how a particular machine's frames happened to chop that schedule up:
    /// two runs of the same key schedule agree bit for bit whatever the frame rate was, which is
    /// the whole foundation a replay era is built on. <see cref="Run"/> is exactly its own tick
    /// count in calls to <see cref="Step"/> and must never become one call with a longer <c>dt</c>:
    /// an exponential filter is not linear in <c>dt</c>, so that would make the curve depend on
    /// frame boundaries.</para>
    ///
    /// <para><b>The law, per tick.</b> Read the gap <c>E = D - P</c> to the arm's desired position,
    /// ask for the velocity that closes it over <see cref="PuppeteerTuning.ChaseMs"/>, clamp that
    /// into <c>[MinVelocity, cap]</c>, ease the actual velocity toward it over
    /// <see cref="PuppeteerTuning.SmoothingTauMs"/>, and advance the position by one tick of it.
    /// A player typing steadily at pace <c>p</c> settles at <c>v = p</c> with the tape a constant
    /// <c>p * ChaseMs</c> behind their caret; stop and the target freezes, so the gap is eaten, the
    /// velocity eases to the floor and the reel drags to a stop; type again and it spins back up
    /// over the smoothing constant.</para>
    ///
    /// <para><b>THE TAPE NEVER REWINDS, and it needs no special case to say so.</b> The caret is not
    /// monotonic: backspace, ctrl-backspace and a retype selection all move it BACKWARDS, which
    /// makes <c>E</c> negative. The clamp already answers that, because its lower bound is the
    /// velocity floor and not the requested velocity: <c>clamp(negative, MinVelocity, cap)</c> is
    /// <c>MinVelocity</c>. So the tape parks and crawls until the caret has been retyped back past
    /// the playhead, and the position is monotonic non-decreasing over every possible arm schedule.
    /// That is a design fact, not an accident of the arithmetic, and it is why there is no
    /// "handle backspace" branch anywhere in this file.</para>
    /// </summary>
    public static class PuppeteerClock
    {
        /// <summary>The fixed integration step, in WALL milliseconds. See the class remarks: this is a contract, not a tuning knob.</summary>
        public const double TICK_MS = 1;

        /// <summary>
        /// Advance the model by exactly one <see cref="TICK_MS"/> wall tick under one arm.
        /// </summary>
        public static PuppeteerState Step(PuppeteerState state, PuppeteerArm arm, PuppeteerTuning tuning)
        {
            // The floor is the LOWER bound of the clamp, so a target behind the tape (a backspaced
            // caret) asks for the floor rather than for a rewind. See the class remarks.
            double gap = arm.DesiredPositionMs - state.PositionMs;

            double cap = Math.Max(tuning.MinVelocity, Math.Min(arm.VelocityCap, tuning.MaxVelocity));
            double requested = tuning.ChaseMs > 0 ? gap / tuning.ChaseMs : cap;

            double targetVelocity = Math.Clamp(requested, tuning.MinVelocity, cap);

            // First-order ease. A non-positive time constant means "no filter at all", which is what
            // the term-isolating tests want.
            double alpha = tuning.SmoothingTauMs > 0
                ? 1 - Math.Exp(-TICK_MS / tuning.SmoothingTauMs)
                : 1;

            double velocity = state.Velocity + ((targetVelocity - state.Velocity) * alpha);

            return new PuppeteerState(state.PositionMs + (velocity * TICK_MS), velocity);
        }

        /// <summary>
        /// Advance the model by <paramref name="ticks"/> whole wall milliseconds under one arm.
        /// Exactly <paramref name="ticks"/> calls to <see cref="Step"/>, deliberately: see the class
        /// remarks for why this may not be collapsed into a single longer step.
        /// </summary>
        public static PuppeteerState Run(PuppeteerState state, PuppeteerArm arm, PuppeteerTuning tuning, int ticks)
        {
            for (int i = 0; i < ticks; i++)
                state = Step(state, arm, tuning);

            return state;
        }
    }
}
