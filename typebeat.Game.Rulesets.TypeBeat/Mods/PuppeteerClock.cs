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
    /// down instead of cutting. MODE-SPECIFIC since backlog 258: see <see cref="PuppeteerTuning.For"/>.
    /// </param>
    /// <param name="MinVelocity">
    /// The velocity floor, and NEVER zero. A published frequency of exactly zero makes the
    /// framework stop the track outright (the same reasoning as
    /// <see cref="TypeBeatModConductor.MIN_FREQUENCY_SCALE"/>), and a stopped track is a one-way
    /// door: the mixer flushes and the restart is audible. A floor of 1/512 crawls instead.
    /// </param>
    /// <param name="MaxVelocity">
    /// The velocity ceiling on a TYPING arm, and MODE-SPECIFIC since backlog 258. In FREQUENCY mode
    /// it is hardware's number rather than a taste call: that path resamples, and BASS refuses an
    /// absolute frequency above 100 kHz, so a 44.1 kHz song stops tracking at about 2.27x (see
    /// <see cref="TypeBeatModConductor.PITCH_ABSOLUTE_MAX_RATE"/>, which
    /// <see cref="TypeBeatModPuppeteer.V_MAX"/> is). In TEMPO mode it is the time-stretcher's clean
    /// ceiling instead, <see cref="TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY"/>. See
    /// <see cref="PuppeteerTuning.For"/>.
    /// </param>
    /// <param name="PaceReleaseMsPerTick">
    /// The most caret travel ONE tick may contribute to the pace estimate, in track milliseconds.
    /// See <see cref="TypeBeatModPuppeteer.PACE_RELEASE_MS_PER_TICK"/>.
    /// </param>
    /// <param name="PaceStepMaxMs">
    /// A forward caret step larger than this is a DISCONTINUITY rather than typing, and is credited
    /// as nothing. See <see cref="TypeBeatModPuppeteer.PACE_STEP_MAX_MS"/>.
    /// </param>
    /// <param name="PaceHeadroom">
    /// How much faster than the measured typing pace the tape is allowed to run, which is the whole
    /// of its authority to CLOSE a gap. See <see cref="TypeBeatModPuppeteer.PACE_HEADROOM"/>.
    /// </param>
    public readonly record struct PuppeteerTuning(
        double ChaseMs,
        double SmoothingTauMs,
        double MinVelocity,
        double MaxVelocity,
        double PaceReleaseMsPerTick,
        double PaceStepMaxMs,
        double PaceHeadroom)
    {
        /// <summary>
        /// THE TWO SHIPPING PRESETS, keyed off <see cref="TypeBeatModPuppeteer.AdjustPitch"/>: the
        /// TEMPO preset when it is off (the default) and the FREQUENCY preset when it is on.
        ///
        /// <para>There is deliberately no <c>Default</c> to fall back on. The preset is part of what
        /// a stored run MEANS (a Puppeteer replay re-derives its track times by re-running this
        /// model, see <c>PuppeteerReplayTransform</c>), so every caller has to state which mode it is
        /// re-deriving under rather than inheriting one silently. A misread toggle would put a
        /// watcher on a tape the player never heard, and that is precisely the failure a named
        /// fallback would hide.</para>
        ///
        /// <para>The two differ in exactly two numbers, and both differences are the TIME-STRETCHER's
        /// physics rather than taste. A stretcher is windowed, so it answers a rate change a window
        /// late and smears under rapid modulation: the tempo preset therefore eases the velocity over
        /// <see cref="TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS"/> instead of
        /// <see cref="TypeBeatModPuppeteer.SMOOTHING_TAU_MS"/>. And it is only clean in roughly 0.6x
        /// to 1.6x, so the tempo preset caps the velocity at
        /// <see cref="TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY"/> instead of at the resampler's
        /// hardware wall <see cref="TypeBeatModPuppeteer.V_MAX"/>. Everything else, the chase horizon,
        /// the floor, the pace estimate and its headroom, is one set of numbers across both modes, so
        /// the park, the chase law, the pace cap and the coast are one behaviour and only the
        /// VELOCITY TRAJECTORY is gentler.</para>
        /// </summary>
        public static PuppeteerTuning For(bool adjustPitch) => adjustPitch ? Frequency : Tempo;

        /// <summary>
        /// The preset for TEMPO mode, which is the default. See <see cref="For"/>.
        /// </summary>
        public static PuppeteerTuning Tempo => new PuppeteerTuning(
            TypeBeatModPuppeteer.T_CHASE_MS,
            TypeBeatModPuppeteer.SMOOTHING_TAU_TEMPO_MS,
            TypeBeatModPuppeteer.V_EPSILON,
            TypeBeatModPuppeteer.TEMPO_MAX_VELOCITY,
            TypeBeatModPuppeteer.PACE_RELEASE_MS_PER_TICK,
            TypeBeatModPuppeteer.PACE_STEP_MAX_MS,
            TypeBeatModPuppeteer.PACE_HEADROOM);

        /// <summary>
        /// The preset for FREQUENCY mode, which is backlog 256's original tuning unchanged, to the
        /// number.
        ///
        /// <para>It was also the ONLY tuning between backlog 256 and 258, and a run recorded in that
        /// window carries no toggle at all, so it decodes at the new default and re-derives under
        /// <see cref="Tempo"/>: a tape its player never heard. That is the exact hazard the era
        /// warning exists for, and it is accepted here for one reason only, that the era was still
        /// being authored and no build carrying it has been released, so the set of affected runs is
        /// empty. Once one ships, moving this toggle's DEFAULT costs an era bit like any other
        /// re-derivation rule.</para>
        /// </summary>
        public static PuppeteerTuning Frequency => new PuppeteerTuning(
            TypeBeatModPuppeteer.T_CHASE_MS,
            TypeBeatModPuppeteer.SMOOTHING_TAU_MS,
            TypeBeatModPuppeteer.V_EPSILON,
            TypeBeatModPuppeteer.V_MAX,
            TypeBeatModPuppeteer.PACE_RELEASE_MS_PER_TICK,
            TypeBeatModPuppeteer.PACE_STEP_MAX_MS,
            TypeBeatModPuppeteer.PACE_HEADROOM);
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
    /// accepted character. On a COASTING arm it is <see cref="double.PositiveInfinity"/>, always:
    /// off a line the song has no position to be pulled toward, it simply plays (see
    /// <see cref="Coast"/>).
    /// </param>
    /// <param name="VelocityCap">
    /// The most this arm will let the tape run at, in track ms per wall ms.
    /// <see cref="PuppeteerTuning.MaxVelocity"/> while the player is typing (chase them as fast as
    /// the audio path can honour, subject to the typing-sustained cap the model applies on top) and
    /// <see cref="TypeBeatModPuppeteer.COAST_MAX_VELOCITY"/> (1.00x) while coasting, where an
    /// unreachable target makes the cap the WHOLE arm and the song plays at its own speed, or at the
    /// HELD speed when there is one (see <see cref="PuppeteerState.HeldCoastVelocity"/>).
    /// </param>
    /// <param name="ReleasesHold">
    /// Whether a coast on this arm gives the held velocity back rather than carrying it (backlog
    /// 261). FALSE on every ordinary coast, which is what makes an instrumental gap keep the speed
    /// the player earned, and it is the DEFAULT so that any arm built with the two-argument
    /// constructor behaves exactly like <see cref="Coast"/>. TRUE on one arm only,
    /// <see cref="ReleasedCoast"/>, which is the OUTRO: see there for why the map's end is the one
    /// off-line stretch with nothing to hold the speed for.
    ///
    /// <para>Meaningless on a typing arm (a finite target clears the hold outright), and it is left
    /// on the arm rather than made a second static so the two coasts stay one shape and the field
    /// reads as what it is: a property of the stretch of song, which is the only thing that knows
    /// whether another line is coming.</para>
    /// </param>
    public readonly record struct PuppeteerArm(double DesiredPositionMs, double VelocityCap, bool ReleasesHold = false)
    {
        /// <summary>
        /// THE COAST ARM: no line, or a line the caret has finished. An unreachable target and a cap
        /// of exactly 1.00x, which together are "play the song normally", with no position term at
        /// all.
        ///
        /// <para>An infinite target is not a trick, it is the statement: there is nothing on screen
        /// for the tape to be pulled toward, so the request is pinned at the cap and the velocity is
        /// flat. The alternative (aim at the next line's first vocal) was tried and removed in
        /// backlog 257, because it made every instrumental gap end in a park short of the next line
        /// and it forced the arm to know about the engine's line lifecycle, which is where the one
        /// genuinely subtle bug in this file used to live (a finished line has not SEALED, so the
        /// next-unsealed index is still itself). Parking before an untyped vocal is now the ACTIVE
        /// arm's job, and it does it from the line's cue rather than from the gap.</para>
        ///
        /// <para>SINCE BACKLOG 261 the 1.00x here is a FLOOR rather than the whole story: a tape
        /// that was running faster than the song when the line ran out HOLDS that speed across the
        /// coast instead of easing back down to 1.00x. The cap above is still what a cold coast gets
        /// (the hold is <c>max(1, velocity)</c>, so a tape at or below the song's own speed holds
        /// exactly 1.00x and this arm is byte-identical to what it always was). See
        /// <see cref="PuppeteerState.HeldCoastVelocity"/> for the whole of it.</para>
        /// </summary>
        public static PuppeteerArm Coast => new PuppeteerArm(double.PositiveInfinity, TypeBeatModPuppeteer.COAST_MAX_VELOCITY);

        /// <summary>
        /// THE OUTRO ARM (backlog 261): the same coast, except that it gives the held velocity back
        /// and eases the tape down to the song's own speed.
        ///
        /// <para>The hold exists because a fast player found it jarring for the song to sag back to
        /// 1.00x between lines, and the speed is given back the moment the next line takes the caret.
        /// PAST THE LAST LINE there is no next line to give it back to, so a held outro would play
        /// the end of the song fast forever, which is not what was asked for and has no instant at
        /// which it would end. This arm is the one place that difference lives, and it is chosen by
        /// the DRIVER (<see cref="TypeBeatModPuppeteer.ArmFor"/>), which is the half that may read
        /// the engine's line lifecycle; the model still knows nothing about lines.</para>
        /// </summary>
        public static PuppeteerArm ReleasedCoast => new PuppeteerArm(double.PositiveInfinity, TypeBeatModPuppeteer.COAST_MAX_VELOCITY, true);
    }

    /// <summary>
    /// Everything the model carries between ticks: where the tape is, how fast it is moving, and how
    /// fast the player has lately been typing.
    /// </summary>
    /// <param name="PositionMs">The tape's position, in TRACK milliseconds. Monotonic non-decreasing by construction, see <see cref="PuppeteerClock"/>.</param>
    /// <param name="Velocity">Track milliseconds per WALL millisecond. 1 is the song's own speed.</param>
    /// <param name="PaceCursorMs">
    /// A RATE-LIMITED follower of the arm's desired position, in track milliseconds, and the only
    /// reason the pace estimate is not simply an average of <c>dD</c>. See
    /// <see cref="PuppeteerClock.StepPace"/> for what it is for; <see cref="double.PositiveInfinity"/>
    /// means "no baseline yet", so the next real target re-seeds it instead of being read as one
    /// enormous keystroke.
    /// </param>
    /// <param name="PaceVelocity">
    /// The smoothed TYPING PACE, in track milliseconds of caret travel per WALL millisecond, on the
    /// same time constant as the tape's own velocity. 0 while nobody is typing, which is what makes
    /// an untyped approach happen at the song's own speed.
    /// </param>
    /// <param name="HeldCoastVelocity">
    /// THE HELD COAST (backlog 261): the velocity this coast is being run at, or
    /// <see cref="PuppeteerClock.NO_HELD_COAST"/> while there is no hold.
    ///
    /// <para><b>What it is for.</b> A player faster than the song settles the tape above 1.00x, and
    /// before this the moment they finished a line the coast dropped them back to 1.00x for the
    /// whole instrumental and then the next line started them climbing again. That sag is what the
    /// hold removes: when the arm goes from a finite target to a coast, the tape KEEPS the speed the
    /// player was already hearing, and keeps it flat until a line takes the caret again.</para>
    ///
    /// <para><b>The value is <c>max(1, Velocity)</c> at the tick the coast begins</b>, and both
    /// halves matter. It is the SMOOTHED TAPE VELOCITY, which is the speed the player is actually
    /// hearing, rather than their typing pace, which is a different number they never hear. And it
    /// is FLOORED AT 1.00x, so a slow player, or one whose tape was still parked on a cell they
    /// hesitated over, gets the song back at its own speed rather than being made to sit through an
    /// instrumental at a crawl: this is a feature for people who outrun the song and it may never
    /// slow one down. The ceiling needs no clamp of its own, since the velocity can never exceed the
    /// preset's <see cref="PuppeteerTuning.MaxVelocity"/>, but <see cref="PuppeteerClock.Step"/>
    /// applies one anyway so a hand-built state cannot command a rate the audio path would refuse.
    /// </para>
    ///
    /// <para><b>Why it is MODEL state and not the driver's.</b> A Puppeteer replay re-derives its
    /// track times by re-running this model over the run's wall stamps
    /// (<c>PuppeteerReplayTransform</c>), and the transform threads a
    /// <see cref="PuppeteerState"/> through <see cref="PuppeteerClock.Step"/> and nothing else: it
    /// never builds the mod. A hold parked on the driver would therefore be invisible to every
    /// stored run, and a watcher would see a tape the player never heard. Here it re-derives for
    /// free, with no change to the transform at all.</para>
    ///
    /// <para><b>It is dropped by a re-anchor</b>, because <see cref="AnchoredAt"/> starts with no
    /// hold and both seek arms go through it. A seek is a discontinuity: the velocity already reset
    /// to 1 there before this existed, and a speed earned on a stretch of song that is no longer
    /// being played is not one to carry across.</para>
    /// </param>
    public readonly record struct PuppeteerState(double PositionMs, double Velocity, double PaceCursorMs, double PaceVelocity, double HeldCoastVelocity)
    {
        /// <summary>
        /// A tape anchored at <paramref name="positionMs"/>, running at the song's own speed, with no
        /// typing behind it yet and no held coast. Used for the first frame of a play and for both
        /// seek re-anchors, and it is the state a replay's re-derivation starts from, so it has to be
        /// one expression.
        /// </summary>
        public static PuppeteerState AnchoredAt(double positionMs)
            => new PuppeteerState(positionMs, 1, double.PositiveInfinity, 0, PuppeteerClock.NO_HELD_COAST);
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
    /// <para><b>THE CAP IS THE TYPIST'S, not a flat ceiling (backlog 257).</b> The cap the law
    /// clamps into is the LOWEST of three: the arm's own, the tuning's hardware ceiling, and
    /// <c>max(1, Headroom * pace)</c>, where <c>pace</c> is the smoothed speed the CARET has lately
    /// been moving at. That third one is what stops the chase term being a sprint licence: a line is
    /// activated a cue lead (1500 ms) before its first vocal and hands the caret over there, so at
    /// that instant <c>E</c> is 1500 ms and the raw request is <c>1500 / 150</c>, i.e. pinned at the
    /// ceiling. Nobody has typed anything, so the honest answer is the song's own speed, and a pace
    /// of 0 gives exactly that: the song flows up to the vocal, eases in and PARKS on the untyped
    /// cell. Type, and the cap lifts with the typing rather than ahead of it.</para>
    ///
    /// <para><b>THE TAPE NEVER REWINDS, and it needs no special case to say so.</b> The caret is not
    /// monotonic: backspace, ctrl-backspace and a retype selection all move it BACKWARDS, which
    /// makes <c>E</c> negative. The clamp already answers that, because its lower bound is the
    /// velocity floor and not the requested velocity: <c>clamp(negative, MinVelocity, cap)</c> is
    /// <c>MinVelocity</c>. So the tape parks and crawls until the caret has been retyped back past
    /// the playhead, and the position is monotonic non-decreasing over every possible arm schedule.
    /// That is a design fact, not an accident of the arithmetic, and it is why there is no
    /// "handle backspace" branch anywhere in this file.</para>
    ///
    /// <para><b>THE COAST HOLDS THE SPEED IT ARRIVED AT (backlog 261).</b> A coast used to be a flat
    /// 1.00x, which meant a player running the song above its own speed was dropped back to 1.00x for
    /// every instrumental stretch and had to climb again on the next line. The coast now carries the
    /// velocity the tape had when the arm went unreachable, floored at 1.00x and held FLAT until a
    /// finite target takes over (see <see cref="PuppeteerState.HeldCoastVelocity"/>). It is one extra
    /// number of state and one branch in the cap, and it changes nothing at all for a tape that was
    /// at or below the song's own speed when the line ran out.</para>
    /// </summary>
    public static class PuppeteerClock
    {
        /// <summary>The fixed integration step, in WALL milliseconds. See the class remarks: this is a contract, not a tuning knob.</summary>
        public const double TICK_MS = 1;

        /// <summary>
        /// The <see cref="PuppeteerState.HeldCoastVelocity"/> value meaning "no hold", i.e. either a
        /// typing arm or a coast that has given the hold back. Zero, and unambiguous by construction:
        /// a real hold is <c>max(1, velocity)</c>, so it is never under 1.00x.
        ///
        /// <para>A CONTRACT like every other constant the model reads (see the tuning comment on
        /// <see cref="TypeBeatModPuppeteer"/>): a stored Puppeteer run re-derives its track times by
        /// re-running this model, so the sentinel is part of what such a run means. Puppeteer is
        /// unshipped as of backlog 261, so introducing the hold at all needed no replay era; the next
        /// time one of these numbers moves, it will.</para>
        /// </summary>
        public const double NO_HELD_COAST = 0;

        /// <summary>
        /// Advance the model by exactly one <see cref="TICK_MS"/> wall tick under one arm.
        /// </summary>
        public static PuppeteerState Step(PuppeteerState state, PuppeteerArm arm, PuppeteerTuning tuning)
        {
            // First-order ease, shared by the velocity and the pace estimate so that "how fast the
            // reel answers" and "how fast the reel forgets you stopped" are one constant. A
            // non-positive time constant means "no filter at all", which is what the term-isolating
            // tests want.
            double alpha = tuning.SmoothingTauMs > 0
                ? 1 - Math.Exp(-TICK_MS / tuning.SmoothingTauMs)
                : 1;

            (double paceCursor, double pace) = StepPace(state, arm, tuning, alpha);

            // The floor is the LOWER bound of the clamp, so a target behind the tape (a backspaced
            // caret) asks for the floor rather than for a rewind. See the class remarks.
            double gap = arm.DesiredPositionMs - state.PositionMs;

            double held = HoldFor(state, arm);

            // THE CAP, and the coast arm now takes a branch of its own (backlog 261).
            //
            // A HELD COAST is capped at the hold and at nothing else (bar the preset's ceiling): not
            // at the arm's own 1.00x, which is the flat coast the hold exists to replace, and NOT at
            // TypingSustainedCap. That bypass is the load-bearing half. StepPace decays the pace
            // toward 0 across a coast, deliberately (it is what makes a line hand-over read as a blip
            // rather than as a burst of typing), so within about three time constants the sustained
            // cap is back at max(1, 0) = 1.00x and would quietly undo every hold. The decay stays and
            // the coast reads past it instead.
            //
            // Everything else, every arm with a finite target, takes the chain exactly as it was, to
            // the bit: the hold is NO_HELD_COAST there by construction.
            double cap = held > NO_HELD_COAST
                ? Math.Max(tuning.MinVelocity, Math.Min(held, tuning.MaxVelocity))
                : Math.Max(tuning.MinVelocity,
                    Math.Min(Math.Min(arm.VelocityCap, tuning.MaxVelocity), TypingSustainedCap(pace, tuning)));

            double requested = tuning.ChaseMs > 0 ? gap / tuning.ChaseMs : cap;

            double targetVelocity = Math.Clamp(requested, tuning.MinVelocity, cap);

            double velocity = state.Velocity + ((targetVelocity - state.Velocity) * alpha);

            return new PuppeteerState(state.PositionMs + (velocity * TICK_MS), velocity, paceCursor, pace, held);
        }

        /// <summary>
        /// The hold this tick runs under: <see cref="NO_HELD_COAST"/> on any arm with a finite target
        /// and on <see cref="PuppeteerArm.ReleasedCoast"/>, the state's existing hold once a coast is
        /// under way, and <c>max(1, Velocity)</c> on the FIRST tick of a coast, which is where the
        /// speed the player was hearing is captured.
        ///
        /// <para>"First tick of a coast" is read off the state rather than off any edge detection: a
        /// coast arm with no hold recorded IS the first tick of one, because a finite target clears
        /// the hold on every tick it is in effect. So the whole rule is a function of (state, arm),
        /// which is what keeps <see cref="Step"/> a pure map and lets a replay's co-simulation
        /// reproduce a hold it was never told about.</para>
        ///
        /// <para>The floor is 1.00x, the same 1 <see cref="TypingSustainedCap"/> floors at and the
        /// same number as <see cref="TypeBeatModPuppeteer.COAST_MAX_VELOCITY"/> (pinned by test): the
        /// song is always allowed to simply play, so a hesitating player whose tape had dragged down
        /// toward the crawl gets the instrumental back at its own speed rather than at their stall.
        /// </para>
        /// </summary>
        public static double HoldFor(PuppeteerState state, PuppeteerArm arm)
        {
            if (double.IsFinite(arm.DesiredPositionMs) || arm.ReleasesHold)
                return NO_HELD_COAST;

            return state.HeldCoastVelocity > NO_HELD_COAST ? state.HeldCoastVelocity : Math.Max(1, state.Velocity);
        }

        /// <summary>
        /// The ceiling a given typing <paramref name="pace"/> buys: never under 1.00x (the song is
        /// always allowed to simply play), never over the hardware ceiling, and
        /// <see cref="PuppeteerTuning.PaceHeadroom"/> times the pace in between.
        ///
        /// <para>THE HEADROOM IS LOAD-BEARING and is not padding. A tape capped at exactly the
        /// caret's own pace moves exactly as fast as the caret does, so whatever gap it starts with
        /// it keeps FOREVER: the cue lead's 1500 ms would become a permanent lag and the player would
        /// spend the whole song typing over a vocal they have not heard yet. The headroom is the
        /// tape's entire authority to CLOSE a gap, and because it is a multiple of the pace it never
        /// binds in the steady state, which is why the settled lag is still exactly
        /// <c>pace * ChaseMs</c> and the chase horizon still means what it says.</para>
        /// </summary>
        public static double TypingSustainedCap(double pace, PuppeteerTuning tuning)
            => Math.Min(Math.Max(1, tuning.PaceHeadroom * pace), tuning.MaxVelocity);

        /// <summary>
        /// One tick of the typing-pace estimate: how fast the CARET is moving, smoothed on the same
        /// time constant as the tape's velocity.
        ///
        /// <para><b>Why a rate-limited cursor rather than a smoothed <c>dD</c>.</b> The desired
        /// position is a STEP function: a keystroke moves it by a whole cell (a hundred milliseconds
        /// or several hundred) in one tick and then it is still for the next two hundred. Smoothing
        /// the raw per-tick difference and clamping each tick's contribution would throw away
        /// everything above the clamp on the one tick that had any, so the estimate would read the
        /// clamp divided by the gap between keystrokes and a real player would never lift the cap off
        /// 1.00x at all. Smoothing it UNCLAMPED preserves the average but rings: it peaks at one
        /// cell's worth per tick and decays, so the cap would swing between 1.00x and the ceiling
        /// once per keystroke. A cursor that CHASES the target at up to
        /// <see cref="PuppeteerTuning.PaceReleaseMsPerTick"/> per tick spreads each keystroke over
        /// the ticks that follow it, which keeps the average exact and the ripple bounded, and it
        /// costs one number of state.</para>
        ///
        /// <para><b>What is not typing.</b> A step BACKWARDS (backspace, ctrl-backspace, a retype
        /// selection), a step larger than <see cref="PuppeteerTuning.PaceStepMaxMs"/> (no keystroke
        /// moves a caret that far, so crediting it would let one press fund a sprint), and the first
        /// real target after a coast, whose cursor is at infinity because a coasting arm has no caret
        /// to measure. All three take the new position as a BASELINE and credit nothing, which is why
        /// a line hand-over reads as a blip rather than as a burst of typing: the caret leaves for
        /// the next line THROUGH the coast arm, always.</para>
        /// </summary>
        public static (double CursorMs, double Pace) StepPace(PuppeteerState state, PuppeteerArm arm, PuppeteerTuning tuning, double alpha)
        {
            double desired = arm.DesiredPositionMs;
            double cursor = state.PaceCursorMs;
            double advance;

            if (!double.IsFinite(desired))
            {
                // Coasting: no caret, nothing to measure, and the cursor is parked out of reach so
                // that the next real target re-seeds rather than being credited.
                advance = 0;
                cursor = double.PositiveInfinity;
            }
            else if (!(desired >= cursor) || desired - cursor > tuning.PaceStepMaxMs)
            {
                advance = 0;
                cursor = desired;
            }
            else
            {
                advance = Math.Min(desired - cursor, tuning.PaceReleaseMsPerTick);
                cursor += advance;
            }

            return (cursor, state.PaceVelocity + ((advance - state.PaceVelocity) * alpha));
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
