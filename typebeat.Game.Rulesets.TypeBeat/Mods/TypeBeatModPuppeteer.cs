// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// UNRANKED. The song STRICTLY FOLLOWS the typing. Not a servo that meets the player halfway
    /// like <see cref="TypeBeatModConductor"/>: a TAPE REEL that the caret drags. Type and the reel
    /// spins up, hesitate and it drags to a stop, walk away and it parks.
    ///
    /// <para><b>THE DISPLAY NAME IS "Conductor" AND THE CLASS NAME IS NOT (backlog 257).</b> This is
    /// the only follower a player can pick: the older rate-follower it replaced
    /// (<see cref="TypeBeatModConductor"/>) is retired to <see cref="ModType.System"/>, keeps its
    /// class name and its "CT" acronym forever so its stored scores and replays go on resolving, and
    /// has "(retired)" appended to its own display name. The two identities are deliberately kept
    /// apart. The DISPLAY name is what the owner wants the one surviving follower called; the CODE
    /// name is what every file, test and doc in this ruleset already says; and the ACRONYM is a WIRE
    /// identity that can never be re-pointed, because "PT" is stamped into scores already recorded
    /// and "CT" belongs to the retired mod. Renaming the class to match the label would either
    /// collide with the retired type or quietly change which mod a stored acronym means, so the
    /// label moved and nothing else did.</para>
    ///
    /// <para><b>Where the model lives.</b> Entirely in <see cref="PuppeteerClock"/>, a pure function
    /// integrated in fixed one millisecond WALL ticks; this class is only the driver that feeds it
    /// (an arm read off the engine each frame, a commanded frequency out). The law, the arms and the
    /// reason the tape can never rewind are all documented there.</para>
    ///
    /// <para><b>TWO MODES, and TEMPO IS THE DEFAULT (backlog 258).</b> With
    /// <see cref="AdjustPitch"/> off the mod publishes a TEMPO adjustment, so the reel changes speed
    /// with the pitch held and there is no vinyl scratch. With it on it publishes FREQUENCY, which
    /// resamples, so the pitch rides the speed. The toggle is the retired Conductor's, reused
    /// verbatim (same label, same tooltip, same default) because it is the same question, and the
    /// split into a (tempo, frequency) PAIR is its
    /// <see cref="TypeBeatModConductor.TrackAdjustmentsFor"/> as well, called rather than copied:
    /// <c>TrackBass</c> throws below an aggregate tempo of
    /// <see cref="TypeBeatModConductor.TEMPO_FLOOR_RATE"/> (0.05) and this mod's floor is
    /// <see cref="V_EPSILON"/> (1/512), far below it, so the sub-floor region needs both properties
    /// at once. This mod therefore depends on the RETIRED mod's pure half, which is a deliberate
    /// dependency and not a leftover: that function is the solved form of "publish a rate the audio
    /// stack will actually take", and a second copy of it is a second thing to get wrong.</para>
    ///
    /// <para><b>Why the frequency mode is kept rather than deleted.</b> Some people want the
    /// scratch. It is also the only mode that is CLEAN AT THE 0X PARK (a resampled crawl is silence,
    /// while a time-stretcher's worst band is exactly the very slow one) and INSTANT ON A TRANSIENT
    /// (there is no analysis window to answer through). Tempo mode's park descends through that ugly
    /// band on its way to the floor, and that is accepted rather than fought: the descent is a
    /// transient of a few hundred milliseconds, the destination is near-silence (the sub-floor split
    /// hands the remainder to frequency, which floors the real output at 100 Hz), and the
    /// alternative, switching modes on the way down, trades a smooth ugly second for a click.
    /// Building that hybrid was considered and refused.</para>
    ///
    /// <para><b>Mode-specific tuning, because a stretcher is not a resampler.</b> It works in
    /// windows, so it answers a rate change a window late and smears under rapid modulation, and it
    /// is only clean in roughly 0.6x to 1.6x. So tempo mode eases the velocity over
    /// <see cref="SMOOTHING_TAU_TEMPO_MS"/> rather than <see cref="SMOOTHING_TAU_MS"/> and caps it at
    /// <see cref="TEMPO_MAX_VELOCITY"/> rather than at <see cref="V_MAX"/>. Those are the only two
    /// numbers that move (see <see cref="PuppeteerTuning.For"/>): the park, the chase law, the pace
    /// cap and the coast are one behaviour in both modes, and only the VELOCITY TRAJECTORY is
    /// gentler. A typist faster than <see cref="TEMPO_MAX_VELOCITY"/> is not chased past it; the
    /// excess is absorbed by POSITION error instead, which is to say the tape simply trails them a
    /// little longer, which is the trade the owner asked for.</para>
    ///
    /// <para><b>Why timing is FORGIVEN rather than unjudged.</b> Under strict following the song
    /// meets the caret BY CONSTRUCTION, so a press's distance from its target time carries no
    /// information about the player at all: it is a readout of the model's own steady-state lag
    /// (<see cref="T_CHASE_MS"/> worth of it, see <see cref="PuppeteerTuning.ChaseMs"/>), which
    /// would otherwise leak into every grade as a permanent "early". So
    /// <see cref="Gameplay.TypingEngine.WindowScale"/> is multiplied by
    /// <see cref="WINDOW_SCALE"/> and every press that lands on the right character judges Great.
    /// Everything else stays REAL and is what the mod is actually scored on: a wrong character is
    /// still wrong, a skipped word is still skipped, a missed line is still missed, completion is
    /// still completion, and the results screen works exactly as it always does. The score
    /// processor and its byte-compatible JS mirror in the web repo are untouched.</para>
    ///
    /// <para><b>Known physics, documented rather than fought.</b> The gameplay clock integrates the
    /// COMMANDED rate while the audio hardware is up to a playback buffer behind it (BASS's
    /// <c>PlaybackBufferLength</c>, about 100 ms), and at very low rates the interpolating clock
    /// drops out so the time read back is the raw 5 ms-quantised BASS position. Both show up as
    /// small errors between the model's position and the clock's, and both are swallowed by the
    /// correction term in <see cref="CommandedFrequency"/> rather than by any attempt to model the
    /// audio stack. That correction is also the one thing that WOBBLES audibly, which is what
    /// <see cref="RATE_DEADBAND"/> answers.</para>
    ///
    /// <para><b>The lyric offset.</b> The arm's target is a cell TARGET TIME, which lives on the
    /// lyric clock, while the tape's position is gameplay time; a non-zero <c>LyricOffsetMs</c> is
    /// therefore a CONSTANT BIAS on the gap, exactly as it is a constant bias on the Conductor's
    /// phase term. It is absorbed rather than plumbed: a constant bias on the gap is a constant
    /// offset on the steady-state lag, which is the one quantity this mod has already declared it
    /// does not judge on.</para>
    ///
    /// <para><b>Deliberately not <c>IApplicableToRate</c>.</b> Song select and the star-rating
    /// calculator want one number describing the whole play; for a follower there is none, so they
    /// show 1.00x, which is the honest answer. Same reasoning as the Conductor's.</para>
    ///
    /// <para><b>Replays (backlog 256).</b> The Conductor stores lyric times and simply does not
    /// reproduce its rate curve. This mod cannot do that, because under strict following a
    /// keystroke's lyric time is an OUTPUT of the model rather than an input to it. A Puppeteer run
    /// therefore records the input axis instead, WALL time, behind CONFIG frame bit 9
    /// (<see cref="Replays.TypeBeatReplayFrame.WallClockFrames"/>), and playback and rescoring
    /// re-derive every track time by re-running <see cref="PuppeteerClock"/> over those stamps (see
    /// <c>PuppeteerReplayTransform</c>). <see cref="WallStampMs"/> is the axis, and the reason every
    /// constant in this file is now a contract.</para>
    /// </summary>
    public class TypeBeatModPuppeteer : Mod, IUpdatableByPlayfield, IApplicableToTrack, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        /// <summary>
        /// The label only. The class is still <c>TypeBeatModPuppeteer</c> and the acronym is still
        /// "PT": see the class remarks for why those three are three different things.
        /// </summary>
        public override string Name => "Conductor";

        /// <summary>
        /// Free across the whole ruleset (pinned by
        /// <c>TypeBeatModPuppeteerTest.AcronymDoesNotCollideWithAnyOtherRulesetMod</c>), and it must
        /// be added to the server's always-unranked acronym list, which is the only thing keeping
        /// these plays off the ranked leaderboards.
        /// </summary>
        public override string Acronym => "PT";

        public override LocalisableString Description => "The song follows you.";

        public override IconUsage? Icon => OsuIcon.ModAutopilot;

        public override ModType Type => ModType.Fun;

        // A tape one player is pulling cannot be shared with a room, as a required mod or a free one.
        public override bool ValidForMultiplayer => false;
        public override bool ValidForMultiplayerAsFreeMod => false;

        /// <summary>
        /// Everything else that owns the playback-rate knob, plus the sibling follower. One side of
        /// a pair is enough for <c>ModUtils.CheckCompatibleSet</c>, which reads the relation in both
        /// directions, but the Conductor names this type as well: the convention here is to declare
        /// both sides where both files can be edited, and to declare only this side for the
        /// framework types that predate the ruleset and cannot name anything in it.
        /// </summary>
        public override Type[] IncompatibleMods => new[]
        {
            typeof(ModRateAdjust),
            typeof(ModTimeRamp),
            typeof(ModAdaptiveSpeed),
            typeof(TypeBeatModConductor),
        };

        // ---------------------------------------------------------------------------------------
        // Tuning, and it is a CONTRACT rather than a set of playtest knobs, which is the price
        // backlog 256's replay era charges. A bit-9 replay stores WALL stamps and re-derives its
        // track times by re-running this model (PuppeteerReplayTransform), so every constant below
        // that the model reads is part of what a stored run means: move one and every Puppeteer
        // replay already on disk re-derives on a tape its player never heard. If one ever has to
        // move, it needs an era of its own, exactly as the judgement rules did.
        //
        // Since backlog 258 that contract includes WHICH PRESET, because the model tuning is a
        // function of AdjustPitch (PuppeteerTuning.For). The toggle is therefore stored with the
        // score when it is non-default, and PuppeteerReplayTransform reads it back off the mod
        // instance rather than assuming a mode.
        //
        // Two constants below are deliberately OUTSIDE the contract, because the model never reads
        // them: T_CORRECT_MS and RATE_DEADBAND belong to CommandedFrequency, which is the driver's
        // half (how the real clock is glued to the model), and the transform re-derives from the
        // model alone. They can move on taste without re-basing a stored run.
        //
        // WINDOW_SCALE is a contract for the older, ordinary reason: the replay scorer reads it too,
        // so a stored run is re-judged on the ladder it was played on.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The position-to-velocity horizon, in WALL milliseconds: the tape asks for the velocity
        /// that would close the whole gap to the caret in this long. Short enough that the reel
        /// answers a keypress rather than lumbering after it, long enough that one fast character
        /// does not slam the rate into the ceiling. It is also the steady-state lag coefficient: an
        /// on-pace player runs a constant 150 ms of song behind their own caret, which is the lag
        /// <see cref="WINDOW_SCALE"/> exists to stop grading.
        /// </summary>
        public const double T_CHASE_MS = 150;

        /// <summary>
        /// Time constant of the velocity ease in FREQUENCY mode, in WALL milliseconds. The reel's
        /// inertia: the whole difference between a tape spinning up and a rate stepping. 120 ms is
        /// inside the band a player reads as "the song responded to me" and above the one they read
        /// as a click. Resampling answers a rate change on the sample it arrives at, so nothing in
        /// the audio path asks for more than that.
        /// </summary>
        public const double SMOOTHING_TAU_MS = 120;

        /// <summary>
        /// Time constant of the velocity ease in TEMPO mode, and the first of the two numbers the
        /// preset moves. A time-stretcher analyses in WINDOWS: it answers a rate change a window
        /// late, and a rate that keeps moving inside one window smears rather than tracking, so the
        /// same 120 ms that reads as responsive through a resampler reads as artefacts through a
        /// stretcher. 300 ms is the middle of the owner's 250 to 400 band: slow enough that the
        /// stretcher sees something close to a held rate over its own window, fast enough that a
        /// keypress still audibly moves the song rather than the song drifting on its own schedule.
        ///
        /// <para>What it costs is a slower spin-up, and it is paid where it is cheapest: the tape
        /// answers a burst of typing over about a third of a second instead of an eighth, and the
        /// steady state is not touched at all, because that is set by
        /// <see cref="T_CHASE_MS"/> and not by this.</para>
        ///
        /// <para><b>The second-order cost, which is worth knowing before anyone moves this number.</b>
        /// The chase loop is position over velocity, so it is second order, and its damping ratio
        /// falls as the ease lengthens: roughly 0.35 here against 0.56 at
        /// <see cref="SMOOTHING_TAU_MS"/>. Two things follow, both measured and both accepted. An
        /// on-pace player's velocity RINGS around 1.00x for about twice as long before settling (it
        /// rings around the right value, and <see cref="RATE_DEADBAND"/> flattens the tail of it
        /// outright). And a park OVERSHOOTS the caret cell further, about 240 ms of song against
        /// 61 ms, because a smoothed velocity cannot be zero at the instant the gap closes and there
        /// is more momentum to spend. Neither is graded: this mod does not judge the distance between
        /// a press and its target at all. Pushing this constant to the top of the owner's band (400)
        /// buys the stretcher a slower rate and costs more of both.</para>
        /// </summary>
        public const double SMOOTHING_TAU_TEMPO_MS = 300;

        /// <summary>
        /// The velocity floor, never zero, and a POWER OF TWO for the same reason
        /// <see cref="TypeBeatModConductor.MIN_FREQUENCY_SCALE"/> is: a published frequency of
        /// exactly zero makes the framework STOP the track, and a stopped track is a flush and an
        /// audible restart rather than a pause. At 1/512 the reel is heard as stopped while the
        /// mixer is still running.
        /// </summary>
        public const double V_EPSILON = TypeBeatModConductor.MIN_FREQUENCY_SCALE;

        /// <summary>
        /// The velocity ceiling on a typing arm in FREQUENCY mode. Hardware's number: that path
        /// resamples, and BASS refuses an absolute frequency above 100 kHz, so a 44.1 kHz song
        /// stops tracking at about 2.27x while the gameplay clock (which reads the bindable, not the
        /// hardware) goes on accelerating. Shared with the Conductor's pitch mode, which walls at
        /// exactly the same place for exactly the same reason.
        ///
        /// <para>It is also the band <see cref="CommandedFrequency"/> clamps into IN BOTH MODES, and
        /// that is not an oversight: the command is the model's velocity plus a bounded correction,
        /// so in tempo mode it may briefly exceed <see cref="TEMPO_MAX_VELOCITY"/> while the clock is
        /// being pulled back onto the model, and the correction needs somewhere to go. The tempo path
        /// honours 2.0x perfectly well (BASS_FX's tempo range runs to 51x), so the excursion costs
        /// only a moment of the stretcher's less clean band, whereas clamping the correction at 1.6
        /// would leave a persistent position error the loop could not close.</para>
        /// </summary>
        public const double V_MAX = TypeBeatModConductor.PITCH_ABSOLUTE_MAX_RATE;

        /// <summary>
        /// The velocity ceiling on a typing arm in TEMPO mode, and the second of the two numbers the
        /// preset moves. It is the TIME-STRETCHER's clean ceiling rather than a hardware wall: the
        /// algorithm holds together in roughly 0.6x to 1.6x and audibly falls apart above that, so
        /// there is nothing to gain by commanding a rate that will only sound broken.
        ///
        /// <para><b>What happens to a typist faster than this.</b> Nothing breaks and nothing is
        /// refused: the chase law is a position law, so an unreachable velocity simply leaves
        /// POSITION error on the table, and the tape trails them a little further behind their caret
        /// than <see cref="T_CHASE_MS"/> worth. That is the owner's stated trade, and it is free
        /// here because this mod does not judge on that distance at all (see
        /// <see cref="WINDOW_SCALE"/>): the only thing a bigger trailing gap costs is that the vocal
        /// is a little further behind the typing, which is exactly what a player outrunning the song
        /// has asked for.</para>
        /// </summary>
        public const double TEMPO_MAX_VELOCITY = 1.6;

        /// <summary>
        /// The velocity while COASTING: a finished line, or no line at all. Exactly 1.00x, flat, and
        /// since backlog 257 it really is flat rather than a ceiling on a chase: the coast arm's
        /// target is unreachable (<see cref="PuppeteerArm.Coast"/>), so there is no position term
        /// left to shape it. OFF A LINE THE SONG SIMPLY PLAYS, which is the owner's rule for every
        /// instrumental stretch: an intro, the tail of a line the player has finished, a gap between
        /// verses and the outro all sound exactly as they do unmodded, and the skip overlays behave
        /// exactly as they do unmodded.
        ///
        /// <para>The park in front of an untyped vocal did not go away with the coast's position
        /// term, it MOVED to where it belongs: a line activates a cue lead before its first vocal, so
        /// the ACTIVE arm has the caret in hand well before there is anything to sing, and it parks
        /// the tape ON the caret cell rather than at the start of a gap. See
        /// <see cref="PACE_HEADROOM"/> for the cap that keeps that approach at the song's own speed
        /// instead of sprinting it.</para>
        /// </summary>
        public const double COAST_MAX_VELOCITY = 1.0;

        /// <summary>
        /// The most caret travel one model tick may credit to the typing-pace estimate, in track
        /// milliseconds. It is a RATE LIMIT and not a clamp: what it holds back is released on the
        /// ticks that follow (see <see cref="PuppeteerClock.StepPace"/>), so it costs the estimate no
        /// accuracy at all, it only stops one keystroke arriving as an instantaneous spike. 20 ms per
        /// wall millisecond is ten times the hardware ceiling this mod can ever command, so no pace a
        /// human can produce is throttled by it.
        /// </summary>
        public const double PACE_RELEASE_MS_PER_TICK = 20;

        /// <summary>
        /// A forward caret step larger than this is a DISCONTINUITY and is credited to the pace
        /// estimate as nothing. One second of song is far more than any single keystroke advances a
        /// caret on a line anyone could outrun, and far less than the jumps that are not typing at
        /// all: a line hand-over, a re-seed after a coast, a seek. Without it a single such jump
        /// would be released into the estimate a tick at a time and would read as several hundred
        /// milliseconds of furious typing that never happened.
        /// </summary>
        public const double PACE_STEP_MAX_MS = 1000;

        /// <summary>
        /// How much faster than the measured typing pace the tape may run, which is the whole of its
        /// authority to CLOSE a gap rather than merely hold one. See
        /// <see cref="PuppeteerClock.TypingSustainedCap"/>: at exactly 1.0 the tape would keep the
        /// cue lead's 1500 ms gap for the rest of the song, and above it the cap stops binding in the
        /// steady state, so the settled lag stays exactly <see cref="T_CHASE_MS"/> worth of the
        /// player's pace and the chase horizon still means what it says.
        /// </summary>
        public const double PACE_HEADROOM = 1.5;

        /// <summary>
        /// The horizon the position CORRECTION is spread over, in wall milliseconds. The commanded
        /// frequency is the model's velocity (feed-forward) plus the model's position error over
        /// this, which is what keeps the real clock glued to the model without the model having to
        /// know anything about audio buffering or clock interpolation. Long enough that the
        /// correction is a bounded, smooth trim rather than a swing:
        /// <c>MasterGameplayClockContainer.checkPlaybackValidity</c> compares accumulated gameplay
        /// time against rate times elapsed with a 300 ms tolerance, and a bounded smooth command
        /// never approaches it.
        /// </summary>
        public const double T_CORRECT_MS = 250;

        /// <summary>
        /// THE WOBBLE DEADBAND (backlog 258). A commanded rate within this of exactly 1.00 is
        /// published as EXACTLY 1.00, so a song that is meant to be playing at its own speed really
        /// is, rather than being modulated a percent either way by the correction term chasing the
        /// clock's own noise. Driver-side and two-sided.
        ///
        /// <para><b>What it is actually for.</b> An on-pace player settles the model at a velocity of
        /// about 1, and the command is that velocity plus <c>(P - clock) / T_CORRECT_MS</c>. The
        /// clock is never exactly on the model (a playback buffer, the interpolating clock's 5 ms
        /// quantisation at low rates, the rate-scaled platform offset), so that second term jitters
        /// around zero forever, and a jittering rate is audible on held vocal notes in a way a
        /// steady one is not. Nothing about that jitter is information. Below the band it is thrown
        /// away; above it, real drift still moves the song exactly as before.</para>
        ///
        /// <para><b>The limit cycle it creates, which is the honest cost.</b> Inside the band the
        /// clock is held at exactly 1.00x while the MODEL goes on integrating its own velocity, so
        /// the two creep apart and the correction term grows. It stops growing when the command
        /// leaves the band, which by definition is when <c>|command - 1| >= RATE_DEADBAND</c>, so the
        /// drift the deadband can hide is bounded by <c>RATE_DEADBAND * T_CORRECT_MS</c>: 0.03 * 250
        /// = 7.5 ms of song. At that point the real command is published, the clock is pulled back,
        /// and the cycle repeats. A steady 7.5 ms bound is well under the 40 ms the retired
        /// Conductor's phase deadband accepted, well under one video frame, and far under anything a
        /// mod that forgives timing outright could care about.</para>
        ///
        /// <para><b>Why it cannot hold a park at 1.00x.</b> The band is on the COMMAND, and the
        /// command is not what drives the model: as the player stops, the model's velocity falls
        /// toward <see cref="V_EPSILON"/> whatever is being published, so the command falls with it,
        /// crosses the band's lower edge at 0.97 and keeps going. The descent is momentarily snapped
        /// to 1.00x on the way through and then continues, which is why the band has to stay NARROW
        /// and two-sided: a wide one would put an audible shelf in the middle of every wind-down.
        /// </para>
        /// </summary>
        public const double RATE_DEADBAND = 0.03;

        /// <summary>
        /// What the mod multiplies every judgement window by, so that every press landing on the
        /// right character judges Great. Read by <see cref="Scoring.TypeBeatReplayScorer"/> too, so
        /// a stored replay is re-judged on the same ladder the live run was.
        ///
        /// <para>A big round number rather than an infinity, because
        /// <see cref="Gameplay.TypingEngine.WindowScale"/> requires a FINITE positive scale (an
        /// infinite window would make every comparison against it meaningless and every sync quality
        /// a NaN). 1e6 puts the widest tier past 2000 seconds, which is longer than any map, so it
        /// is total in practice while staying an ordinary double.</para>
        ///
        /// <para>Why a window scale and not a "judge everything Great" flag: the scale is the
        /// existing, general, composable knob (Easy, Hard Rock and the rate mods all multiply their
        /// own factor into it), it needs no engine edit at all, and it keeps the sync readouts
        /// coherent with the grades instead of reporting a quality nobody was graded on.</para>
        /// </summary>
        public const double WINDOW_SCALE = 1e6;

        /// <summary>
        /// A frame longer than this in wall time is a hitch, a stall or a load, not a frame, and
        /// integrating it would spin the tape through a stretch of song nobody was typing over.
        /// Same number and same reasoning as <see cref="ConductorPacing.MAX_REAL_FRAME_MS"/>.
        /// </summary>
        public const double MAX_REAL_FRAME_MS = ConductorPacing.MAX_REAL_FRAME_MS;

        /// <summary>
        /// How far the gameplay clock has to step BACKWARDS before the driver reads it as a rewind
        /// rather than as noise. It is not zero, and that is not slop: the platform offset is
        /// applied to the clock RATE-SCALED (<c>FramedOffsetClock</c> over the interpolating source),
        /// so a fast rate change moves the offset's contribution and the reported time can tick back
        /// a millisecond or two while the song is playing perfectly normally forwards. A tape that
        /// re-anchored on that would stutter once per rate swing. 50 ms is far above the wobble and
        /// far below any real seek.
        /// </summary>
        public const double REWIND_THRESHOLD_MS = -50;

        /// <summary>
        /// How far the gameplay clock has to step FORWARDS in one frame before the driver reads it as
        /// a seek rather than as playback. The mirror of <see cref="REWIND_THRESHOLD_MS"/>, and the
        /// fix for backlog 257's freeze.
        ///
        /// <para><b>The bug it closes.</b> Live play DOES seek forwards: the intro
        /// <c>SkipOverlay</c> jumps the clock to <c>GameplayStartTime</c>, and every instrumental gap
        /// long enough to earn one has an overlay that calls <c>Player.PerformSkipTo</c>. Either can
        /// move the clock tens of seconds in a single frame. The tape does not move with it, so
        /// <see cref="CommandedFrequency"/>'s correction term (<c>(P - clock) / T_CORRECT_MS</c>)
        /// goes hugely negative and pins the command at <see cref="V_EPSILON"/>: the song stops dead
        /// while the tape crawls up to the clock at the coast speed, which takes ONE REAL SECOND PER
        /// SECOND SKIPPED. On a map with a long intro the skip overlay is the first thing a player
        /// sees, so the symptom is "the song never starts".</para>
        ///
        /// <para><b>Where the number comes from.</b> The stall guard answers first, so every frame
        /// that reaches this test is at most <see cref="MAX_REAL_FRAME_MS"/> (250 ms) of wall time,
        /// and the fastest the mod can ever command is <see cref="V_MAX"/>: the most track time
        /// ordinary playback can put in one testable frame is therefore 500 ms, and anything above
        /// that was not played, it was seeked. 1000 ms is twice that bound and two orders below the
        /// smallest gap that earns a skip overlay at all
        /// (<c>InstrumentalGaps.MIN_GAP_MS</c> is ten seconds), so the guard cannot fire on a hitch
        /// and cannot miss a skip.</para>
        /// </summary>
        public const double FORWARD_SEEK_THRESHOLD_MS = 1000;

        /// <summary>
        /// Cap on the ticks integrated for one frame. The stall guard already throws out anything
        /// over <see cref="MAX_REAL_FRAME_MS"/>, so this only bounds the arithmetic; it is not a
        /// behaviour.
        /// </summary>
        private const int max_ticks_per_frame = (int)MAX_REAL_FRAME_MS;

        /// <summary>
        /// Off by default, i.e. TEMPO adjustment, and the label, the tooltip and the default are the
        /// retired Conductor's verbatim (<see cref="TypeBeatModConductor.AdjustPitch"/>) because it
        /// is the same question about the same knob. OFF: the reel changes speed with the pitch held.
        /// ON: the reel resamples, so the pitch rides the speed, which is the vinyl scratch backlog
        /// 256 shipped as the only behaviour and 258 demoted to an option.
        ///
        /// <para>It also selects the MODEL TUNING (<see cref="PuppeteerTuning.For"/>), which makes it
        /// the one setting on this mod that is a replay-era input: see the tuning comment above, and
        /// <c>PuppeteerReplayTransform</c>, which reads this off the stored mod instance so a watched
        /// run re-derives under the preset it was played on. It rides to the server and into a stored
        /// score by the ordinary route, the mod settings payload, which carries a setting only when
        /// it is NON-DEFAULT; a payload with no <c>adjust_pitch</c> therefore means tempo, and that
        /// is exactly why this default can never quietly move once a build carrying it has
        /// shipped.</para>
        /// </summary>
        [SettingSource("Adjust pitch", "Should pitch be adjusted with speed")]
        public BindableBool AdjustPitch { get; } = new BindableBool();

        /// <summary>
        /// The instantaneous TOTAL rate, written once per frame and read by the HUD through
        /// <see cref="CurrentRate"/>. Since backlog 258 it is not itself the bindable handed to the
        /// track: it is split into a tempo and a frequency adjustment by
        /// <see cref="TypeBeatModConductor.TrackAdjustmentsFor"/>, whose PRODUCT is this value, since
        /// <c>GameplayClockExtensions.GetTrueGameplayRate</c> is sign * AggregateFrequency *
        /// AggregateTempo of exactly that adjustment set. The aggregate the pair is attached to is
        /// bound onto the real track and the gameplay clock's source IS that track, so writing here
        /// moves the music and gameplay time together with no new clock.
        ///
        /// <para>Its bounds are the model's own velocity band, so a value outside what the audio path
        /// can track cannot be published even by hand. They do NOT move with the mode, unlike the
        /// Conductor's: there the two modes had different CEILINGS (51x against 2x), while here the
        /// mode difference is a model cap (<see cref="TEMPO_MAX_VELOCITY"/>) and the published band
        /// has to stay wide enough for the correction term in both modes. No <c>Precision</c>:
        /// quantising a tape reel to 0.01 puts audible steps into a rate that is meant to
        /// glide.</para>
        /// </summary>
        public BindableNumber<double> SpeedChange { get; } = new BindableDouble(1)
        {
            MinValue = V_EPSILON,
            MaxValue = V_MAX,
        };

        /// <summary>The frequency last commanded. What the HUD rate readout shows.</summary>
        public double CurrentRate => SpeedChange.Value;

        /// <summary>The model's state, or null before the first frame anchors it.</summary>
        public PuppeteerState? Tape => tape;

        /// <summary>
        /// THE RECORDING AXIS (backlog 256), or null before the first frame anchors the tape.
        /// <c>TypeBeatReplayRecorder</c> stamps every frame of a Puppeteer run with this
        /// instead of the lyric time, and <c>PuppeteerReplayTransform</c> re-derives the lyric times
        /// by re-running the model over it. It is <c>anchor + the number of model TICKS taken since
        /// the anchor</c>, and every word of that is load-bearing:
        ///
        /// <list type="bullet">
        /// <item>THE TICK COUNT, not the raw stopwatch. The two differ: a stalled frame is thrown
        /// out of the integration entirely (see <see cref="MAX_REAL_FRAME_MS"/>), and the sub
        /// millisecond remainder of every frame is carried rather than integrated. Stamping the raw
        /// wall clock would hand the transform wall milliseconds the live model never spent, and it
        /// would run that many extra ticks. Stamping the tick count makes the axis literally "how
        /// far this model has been stepped", so the transform steps it exactly as far.</item>
        /// <item>THE ANCHOR, which is <see cref="AnchorMs"/>: the origin of the axis AND the track
        /// position the tape starts at, one number doing both jobs. That is what lets a stored run
        /// carry it in one field (the CONFIG frame's own time) and what makes the recorder and the
        /// transform provably agree about where the tape started.</item>
        /// <item>INTEGRAL by construction, since the anchor is rounded once and the tick count is an
        /// integer, so the legacy .osr encoding (integral frame deltas) is lossless and the deltas
        /// between successive frames are never negative.</item>
        /// </list>
        ///
        /// <para>A SEEK re-anchors the tape (see <see cref="SeekReanchor"/>) but deliberately leaves
        /// this axis alone, because a stamp that went backwards would scramble the stored order.
        /// KNOWN LIMITATION, and backlog 257 corrects what backlog 256 wrote here: live play is NOT
        /// seek-free. The intro skip and the instrumental-gap skips seek forwards, and a run that
        /// used one re-derives on a tape that never took the skip, so every keystroke after it
        /// derives at a track time earlier than the one it was really played at. Closing that means
        /// recording the seek (a second anchor in the stream), which is an era of its own; until then
        /// a Puppeteer replay is exact only for a run that skipped nothing.</para>
        /// </summary>
        public double? WallStampMs => tape is null ? null : anchorPositionMs + integratedTicks;

        /// <summary>
        /// The track position the tape was anchored at, rounded to a millisecond, or null before the
        /// first frame. The origin of <see cref="WallStampMs"/>, and what a bit-9 CONFIG frame's own
        /// time carries.
        /// </summary>
        public double? AnchorMs => tape is null ? null : anchorPositionMs;

        /// <summary>The tempo half of the pair published to the track. See <see cref="TypeBeatModConductor.TrackAdjustmentsFor"/>.</summary>
        private readonly BindableDouble tempoAdjustment = new BindableDouble(1);

        /// <summary>The frequency half of the pair published to the track.</summary>
        private readonly BindableDouble frequencyAdjustment = new BindableDouble(1);

        private IAdjustableAudioComponent? track;
        private DrawableTypeBeatRuleset? drawableRuleset;

        /// <summary>
        /// Wall clock, and the model's whole time base. Nothing derived from it reaches a target
        /// time, a judgement or a replay frame.
        /// </summary>
        private readonly Stopwatch wallClock = new Stopwatch();

        private double lastWallMs;

        /// <summary>Wall milliseconds seen but not yet integrated, always under one tick.</summary>
        private double pendingWallMs;

        private PuppeteerState? tape;
        private double? lastTrackTime;

        /// <summary>The rounded track position the tape was anchored at. See <see cref="AnchorMs"/>.</summary>
        private double anchorPositionMs;

        /// <summary>Model ticks taken since the anchor. See <see cref="WallStampMs"/>.</summary>
        private long integratedTicks;

        public TypeBeatModPuppeteer()
        {
            // Deliberately NOT RateAdjustModHelper, which binds SpeedChange straight onto one
            // property: the sub-floor region needs both properties at once (see
            // TypeBeatModConductor.TrackAdjustmentsFor), and this mod's floor is 1/512, two orders
            // under the tempo floor TrackBass throws below. The swap on toggling the setting is the
            // helper's own pattern, remove the old set and add the new one.
            SpeedChange.BindValueChanged(_ => updateTrackAdjustments());

            AdjustPitch.BindValueChanged(adjustPitch =>
            {
                if (track != null)
                {
                    removeAdjustments(track, adjustPitch.OldValue);
                    addAdjustments(track, adjustPitch.NewValue);
                }

                updateTrackAdjustments();
            });
        }

        public void ApplyToTrack(IAdjustableAudioComponent track)
        {
            reset();

            this.track = track;

            // Old and new are the same here, so this removes and re-adds the same pair. Removing an
            // adjustment a fresh track never carried is a no-op, which is what RateAdjustModHelper
            // relies on for exactly this call too.
            AdjustPitch.TriggerChange();
        }

        /// <summary>
        /// Two jobs. FORGIVE THE TIMING by multiplying the window scale (see
        /// <see cref="WINDOW_SCALE"/>), which is done here because this is where the engine exists
        /// and before the first keypress, exactly as Easy, Hard Rock and the rate mods do it. And
        /// capture the drawable ruleset so the live rate can be published for the HUD readout, the
        /// <see cref="DrawableTypeBeatRuleset.ConductorRate"/> half of the
        /// <c>FlashlightVisibleRadius</c> pattern: the mod writes, the always-on HUD reads, and no
        /// mod type is named inside the HUD.
        /// </summary>
        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset)
        {
            var typeBeatRuleset = (DrawableTypeBeatRuleset)drawableRuleset;

            typeBeatRuleset.Engine.WindowScale *= WINDOW_SCALE;

            this.drawableRuleset = typeBeatRuleset;

            reset();
            publish();
        }

        public void Update(Playfield playfield)
        {
            if (playfield is not TypeBeatPlayfield typeBeatPlayfield)
                return;

            double time = playfield.Clock.CurrentTime;

            // Read every frame, including the ones that return early, so the wall delta is one
            // frame's worth and not a whole stretch of frames.
            double wallElapsed = wallElapsedMs();

            if (tape is not PuppeteerState state || lastTrackTime is not double previous)
            {
                anchor(time);
                publish();
                return;
            }

            double trackDelta = time - previous;

            lastTrackTime = time;

            if (wallElapsed > MAX_REAL_FRAME_MS)
            {
                // A hitch, a stall or a load. The tape keeps its position and its velocity (the
                // audio cannot teleport either way), it just does not get credited a stretch of wall
                // time nobody was typing over.
                pendingWallMs = 0;
                publish();
                return;
            }

            if (SeekReanchor(time, trackDelta) is PuppeteerState reanchored)
            {
                // The tape's position describes a part of the song that is no longer the one being
                // played, so re-anchor onto the clock rather than letting the correction term drag
                // the rate to a bound while it catches up. See SeekReanchor for the two directions.
                //
                // The RECORDING axis is deliberately not re-anchored with it (see WallStampMs): a
                // stamp that went backwards would scramble the stored frame order.
                tape = reanchored;
                pendingWallMs = 0;
                publish();
                return;
            }

            pendingWallMs += Math.Max(wallElapsed, 0);

            int ticks = Math.Min((int)pendingWallMs, max_ticks_per_frame);

            // Only the ticks actually integrated leave the accumulator, and only they are counted on
            // the recording axis. The two must never come apart: WallStampMs is a promise about how
            // far the model has been stepped, and the transform steps it exactly that far.
            pendingWallMs -= ticks;
            integratedTicks += ticks;

            // One arm for this frame's ticks. The model is pure and the driver feeds it: the arm is
            // sampled once because that is when the engine state is readable, and the ticks are
            // canonical one millisecond steps because that is what makes the trajectory a function
            // of the schedule rather than of the frame rate. See PuppeteerClock.
            //
            // The PRESET is read fresh each frame off the toggle, so a mid-play change (which the
            // mod-select overlay does not allow, but a test may) takes effect where the model can
            // absorb it rather than being frozen at ApplyToTrack time.
            tape = PuppeteerClock.Run(state, ArmFor(typeBeatPlayfield.Engine, time), PuppeteerTuning.For(AdjustPitch.Value), ticks);

            publish();
        }

        /// <summary>
        /// The tape a SEEK demands, or null when the clock moved by an amount ordinary playback can
        /// explain. Pure and public so the two thresholds can be driven without a clock, a playfield
        /// or an audio stack, which is how the freeze this closes is pinned.
        ///
        /// <para>BACKWARDS (a rewind, past <see cref="REWIND_THRESHOLD_MS"/>): re-anchor with the
        /// velocity at ZERO rather than at 1. The reel is being restarted, and it spins up under the
        /// smoothing constant from wherever the arm asks it to.</para>
        ///
        /// <para>FORWARDS (a skip, past <see cref="FORWARD_SEEK_THRESHOLD_MS"/>): re-anchor at the
        /// song's own speed, which is <see cref="PuppeteerState.AnchoredAt"/>. The two differ on
        /// purpose. A rewind is a deliberate re-entry into a stretch of song, and starting it from
        /// still is what makes it audibly a re-entry; a forward skip lands where the music is
        /// supposed to be playing, and starting it from still would be a smaller copy of the very
        /// freeze this branch exists to remove. At velocity 1 with the tape on the clock,
        /// <see cref="CommandedFrequency"/> is exactly 1.00x on the first frame after the skip.</para>
        /// </summary>
        public static PuppeteerState? SeekReanchor(double clockTime, double trackDelta)
        {
            if (trackDelta < REWIND_THRESHOLD_MS)
                return PuppeteerState.AnchoredAt(clockTime) with { Velocity = 0 };

            if (trackDelta > FORWARD_SEEK_THRESHOLD_MS)
                return PuppeteerState.AnchoredAt(clockTime);

            return null;
        }

        /// <summary>
        /// What the engine is asking the tape for, right now. Public and static because it is the
        /// whole of the play's side of the model, and a stored replay re-derives it from engine
        /// state alone.
        ///
        /// <para>TWO ARMS since backlog 257. (1) ON A LINE, with a live caret on a judgeable cell:
        /// chase that cell's target time, at up to <see cref="V_MAX"/> and up to what the player's
        /// own typing pace sustains (<see cref="PuppeteerClock.TypingSustainedCap"/>). The target
        /// FREEZES when the player stops, which is the whole "hesitate and it drags to a stop"
        /// behaviour, and it steps backwards under a backspace, which the model answers by parking
        /// rather than rewinding (see <see cref="PuppeteerClock"/>). (2) OFF A LINE, which is the
        /// intro, an instrumental gap, the tail of a line the caret has finished, and the outro:
        /// <see cref="PuppeteerArm.Coast"/>, a flat 1.00x with no position term at all.</para>
        ///
        /// <para>The coast used to aim at the next line's first vocal, and getting the index right
        /// was subtle (a finished line has not SEALED, so
        /// <see cref="TypingEngine.NextUnsealedLineIndex"/> is still that same line). It aims at
        /// nothing now, so the whole question is gone: the arm no longer reads the line lifecycle,
        /// only the caret.</para>
        ///
        /// <para><see cref="TypingEngine.CurrentLeadLag"/> is what decides whether arm (1) applies,
        /// rather than a hand-rolled caret test: it is null in exactly the cases that must coast
        /// (finished, no active line, caret past the last cell, caret on a non-typeable cell), and
        /// it is <c>time - TargetTime</c>, so the target comes straight back out of it. It is also
        /// what keeps a caret PARKED ON A SPOILED WORD GAP under <c>StrictSpaces</c> on the typing
        /// arm: that caret is mid-line and on a typeable cell, so the song correctly waits at the
        /// gap for the fix rather than coasting away from the player.</para>
        /// </summary>
        public static PuppeteerArm ArmFor(TypingEngine engine, double time)
        {
            ArgumentNullException.ThrowIfNull(engine);

            if (engine.CurrentLeadLag(time) is double leadLag)
                return new PuppeteerArm(time - leadLag, V_MAX);

            return PuppeteerArm.Coast;
        }

        /// <summary>
        /// The rate actually commanded, from the model's state and the clock's own reading.
        /// Feed-forward plus a bounded correction: the velocity is what the model says the song
        /// should be running at, and the position error over <see cref="T_CORRECT_MS"/> is what
        /// pulls the real clock back onto the model when the two drift (they always do, see the
        /// class remarks on buffering and clock interpolation). Clamped into the
        /// <c>[V_EPSILON, V_MAX]</c> band <see cref="SpeedChange"/> publishes in, so the command is
        /// bounded by construction and never asks the audio path for something it cannot track.
        ///
        /// <para>Then DEADBANDED: a command within <see cref="RATE_DEADBAND"/> of 1.00 is published
        /// as exactly 1.00, so ordinary jitter never modulates the audio at all. The order matters
        /// and is this way round on purpose: the band is a statement about the number the audio path
        /// is handed, so it is applied last, to the clamped command, rather than to a raw request the
        /// clamp might have moved out of the band afterwards.</para>
        ///
        /// <para>The name is historical: with the toggle off this value reaches the track as a
        /// (tempo, frequency) pair whose product it is, not as a frequency. What it means, and has
        /// always meant, is the total rate.</para>
        ///
        /// <para>Driver-side, and NOT a replay-era contract: the transform re-derives from
        /// <see cref="PuppeteerClock"/> alone and never calls this, because the model does not read
        /// what was published. So both constants in here are free to move on taste.</para>
        /// </summary>
        public static double CommandedFrequency(PuppeteerState state, double clockTime)
        {
            double command = Math.Clamp(state.Velocity + ((state.PositionMs - clockTime) / T_CORRECT_MS), V_EPSILON, V_MAX);

            return Math.Abs(command - 1) < RATE_DEADBAND ? 1 : command;
        }

        /// <summary>
        /// The model preset a stored run was played under, read off the mod instance in
        /// <paramref name="mods"/>. This is the whole of the replay era's mode plumbing, and it lives
        /// here rather than in <c>PuppeteerReplayTransform</c> because the toggle belongs to the mod:
        /// the transform asks the mod which tape it was, and does not learn what a mode is.
        ///
        /// <para>No mod in the list means no Puppeteer run, so the answer is the default preset. A
        /// stored run whose mod list was lost or resolved to <c>UnknownMod</c> lands there too, which
        /// is the honest reading: the payload carries the toggle only when it is non-default, so an
        /// absent toggle and an absent mod say the same thing.</para>
        /// </summary>
        public static PuppeteerTuning TuningFor(IReadOnlyList<Mod>? mods)
        {
            bool adjustPitch = false;

            if (mods != null)
            {
                foreach (var mod in mods)
                {
                    if (mod is TypeBeatModPuppeteer puppeteer)
                    {
                        adjustPitch = puppeteer.AdjustPitch.Value;
                        break;
                    }
                }
            }

            return PuppeteerTuning.For(adjustPitch);
        }

        /// <summary>
        /// Start the tape. The position is ROUNDED to a millisecond, and the model starts on the
        /// rounded value rather than merely reporting it: the anchor is the one number a stored
        /// replay carries (see <see cref="AnchorMs"/>), so the live tape and a re-derived one have
        /// to start on the same double or every position after it differs.
        /// </summary>
        private void anchor(double time)
        {
            anchorPositionMs = Math.Round(time);
            integratedTicks = 0;

            tape = PuppeteerState.AnchoredAt(anchorPositionMs);
            lastTrackTime = time;
            pendingWallMs = 0;
        }

        private void reset()
        {
            tape = null;
            lastTrackTime = null;
            pendingWallMs = 0;
            lastWallMs = 0;
            anchorPositionMs = 0;
            integratedTicks = 0;
            wallClock.Restart();

            SpeedChange.Value = 1;

            if (drawableRuleset != null)
                drawableRuleset.ConductorRate = 1;
        }

        private void addAdjustments(IAdjustableAudioComponent target, bool adjustPitch)
        {
            if (!adjustPitch)
                target.AddAdjustment(AdjustableProperty.Tempo, tempoAdjustment);

            target.AddAdjustment(AdjustableProperty.Frequency, frequencyAdjustment);
        }

        private void removeAdjustments(IAdjustableAudioComponent target, bool adjustPitch)
        {
            if (!adjustPitch)
                target.RemoveAdjustment(AdjustableProperty.Tempo, tempoAdjustment);

            target.RemoveAdjustment(AdjustableProperty.Frequency, frequencyAdjustment);
        }

        private void updateTrackAdjustments()
        {
            (double tempo, double frequency) = TypeBeatModConductor.TrackAdjustmentsFor(SpeedChange.Value, AdjustPitch.Value);

            tempoAdjustment.Value = tempo;
            frequencyAdjustment.Value = frequency;
        }

        private double wallElapsedMs()
        {
            if (!wallClock.IsRunning)
                wallClock.Start();

            double now = wallClock.Elapsed.TotalMilliseconds;
            double elapsed = now - lastWallMs;

            lastWallMs = now;

            return elapsed;
        }

        private void publish()
        {
            double rate = tape is PuppeteerState state && lastTrackTime is double time
                ? CommandedFrequency(state, time)
                : 1;

            SpeedChange.Value = rate;

            if (drawableRuleset != null)
                drawableRuleset.ConductorRate = rate;
        }
    }
}
