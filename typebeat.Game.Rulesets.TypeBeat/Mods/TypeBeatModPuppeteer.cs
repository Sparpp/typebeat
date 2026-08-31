// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using osu.Framework.Audio;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
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
    /// spins up, hesitate and it drags to a stop, walk away and it parks. It is frequency-only, so
    /// the pitch bends with the speed, and that is the aesthetic rather than a compromise.
    ///
    /// <para><b>Where the model lives.</b> Entirely in <see cref="PuppeteerClock"/>, a pure function
    /// integrated in fixed one millisecond WALL ticks; this class is only the driver that feeds it
    /// (an arm read off the engine each frame, a commanded frequency out). The law, the arms and the
    /// reason the tape can never rewind are all documented there.</para>
    ///
    /// <para><b>Why frequency only.</b> Tempo time-stretching is a fixed-window algorithm: it is
    /// built to hold pitch across a rate that changes rarely, and a rate that changes every frame
    /// smears it into artefacts. Resampling has no such window, so the reel sounds like a reel.
    /// The price is the ceiling: BASS refuses an absolute frequency above 100 kHz, so a 44.1 kHz
    /// song stops tracking at about 2.27x, and <see cref="V_MAX"/> sits under the lowest of those
    /// sample-rate-dependent walls. Only one adjustment is published (frequency), and the tempo
    /// aggregate stays at exactly 1.</para>
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
    /// audio stack.</para>
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
        public override string Name => "Puppeteer";

        /// <summary>
        /// Free across the whole ruleset (pinned by
        /// <c>TypeBeatModPuppeteerTest.AcronymDoesNotCollideWithAnyOtherRulesetMod</c>), and it must
        /// be added to the server's always-unranked acronym list, which is the only thing keeping
        /// these plays off the ranked leaderboards.
        /// </summary>
        public override string Acronym => "PT";

        public override LocalisableString Description => "The song is on a string. It follows your typing exactly, and the pitch bends with it.";

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
        /// Time constant of the velocity ease, in WALL milliseconds. The reel's inertia: the whole
        /// difference between a tape spinning up and a rate stepping. 120 ms is inside the band a
        /// player reads as "the song responded to me" and above the one they read as a click.
        /// </summary>
        public const double SMOOTHING_TAU_MS = 120;

        /// <summary>
        /// The velocity floor, never zero, and a POWER OF TWO for the same reason
        /// <see cref="TypeBeatModConductor.MIN_FREQUENCY_SCALE"/> is: a published frequency of
        /// exactly zero makes the framework STOP the track, and a stopped track is a flush and an
        /// audible restart rather than a pause. At 1/512 the reel is heard as stopped while the
        /// mixer is still running.
        /// </summary>
        public const double V_EPSILON = TypeBeatModConductor.MIN_FREQUENCY_SCALE;

        /// <summary>
        /// The velocity ceiling on a typing arm. Hardware's number: this mod publishes FREQUENCY,
        /// which resamples, and BASS refuses an absolute frequency above 100 kHz, so a 44.1 kHz song
        /// stops tracking at about 2.27x while the gameplay clock (which reads the bindable, not the
        /// hardware) goes on accelerating. Shared with the Conductor's pitch mode, which walls at
        /// exactly the same place for exactly the same reason.
        /// </summary>
        public const double V_MAX = TypeBeatModConductor.PITCH_ABSOLUTE_MAX_RATE;

        /// <summary>
        /// The velocity ceiling while COASTING: a finished line, or no line at all. Exactly 1.00x,
        /// which is the whole of backlog 253's lesson applied to a tape reel. The target on a coast
        /// arm is the NEXT line's first vocal, which can be many seconds away, so an uncapped chase
        /// would sprint the tail of every line and every instrumental gap. Capped at the song's own
        /// speed the finished line simply plays out, and then eases into a park AT the next vocal if
        /// the player has not started typing yet: breaks sound normal and the skip overlays behave
        /// exactly as they do unmodded.
        /// </summary>
        public const double COAST_MAX_VELOCITY = 1.0;

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
        /// Cap on the ticks integrated for one frame. The stall guard already throws out anything
        /// over <see cref="MAX_REAL_FRAME_MS"/>, so this only bounds the arithmetic; it is not a
        /// behaviour.
        /// </summary>
        private const int max_ticks_per_frame = (int)MAX_REAL_FRAME_MS;

        /// <summary>
        /// The frequency adjustment published to the track, and the ONLY one: the tempo aggregate
        /// stays at exactly 1, so <c>GameplayClockExtensions.GetTrueGameplayRate</c> (sign *
        /// AggregateFrequency * AggregateTempo) is this value alone. The aggregate it is attached to
        /// is bound onto the real track and the gameplay clock's source IS that track, so writing
        /// here moves the music and gameplay time together with no new clock.
        ///
        /// <para>Unlike the Conductor's <c>SpeedChange</c> this is not split into a pair: there is no
        /// tempo half to split into, which is the whole of "frequency only". Its bounds are the
        /// model's own velocity band, so a value outside what the audio path can track cannot be
        /// published even by hand. No <c>Precision</c>: quantising a tape reel to 0.01 puts audible
        /// steps into a rate that is meant to glide.</para>
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
        /// <para>The axis assumes the run never SEEKS, which live play guarantees (only a replay can
        /// seek, and a replay is not recorded). A rewind re-anchors the tape but deliberately leaves
        /// this axis alone, because a stamp that went backwards would scramble the stored order,
        /// which is worse than the times being wrong in a situation that cannot arise.</para>
        /// </summary>
        public double? WallStampMs => tape is null ? null : anchorPositionMs + integratedTicks;

        /// <summary>
        /// The track position the tape was anchored at, rounded to a millisecond, or null before the
        /// first frame. The origin of <see cref="WallStampMs"/>, and what a bit-9 CONFIG frame's own
        /// time carries.
        /// </summary>
        public double? AnchorMs => tape is null ? null : anchorPositionMs;

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

        public void ApplyToTrack(IAdjustableAudioComponent track)
        {
            reset();

            this.track = track;

            // Removing an adjustment a fresh track never carried is a no-op, which is what
            // RateAdjustModHelper relies on for exactly this call too.
            track.RemoveAdjustment(AdjustableProperty.Frequency, SpeedChange);
            track.AddAdjustment(AdjustableProperty.Frequency, SpeedChange);
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

            if (trackDelta < REWIND_THRESHOLD_MS)
            {
                // A real seek backwards. The tape's position describes a part of the song that is no
                // longer being played, so re-anchor onto the clock rather than letting the
                // correction term drag the rate to a bound while it catches up. Velocity goes to
                // zero rather than to 1: the reel is being restarted, and it spins up under the
                // smoothing constant from wherever the arm asks it to.
                //
                // The RECORDING axis is deliberately not re-anchored with it (see WallStampMs): a
                // stamp that went backwards would scramble the stored frame order, and a seek cannot
                // happen while recording anyway, since only a replay can seek and a replay is not
                // recorded.
                tape = new PuppeteerState(time, 0);
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
            tape = PuppeteerClock.Run(state, ArmFor(typeBeatPlayfield.Engine, time), PuppeteerTuning.Default, ticks);

            publish();
        }

        /// <summary>
        /// What the engine is asking the tape for, right now. Public and static because it is the
        /// whole of the play's side of the model, and a stored replay re-derives it from engine
        /// state alone.
        ///
        /// <para>THREE ARMS. (1) A live caret on a judgeable cell: chase that cell's target time, at
        /// up to <see cref="V_MAX"/>. The target FREEZES when the player stops, which is the whole
        /// "hesitate and it drags to a stop" behaviour, and it steps backwards under a backspace,
        /// which the model answers by parking rather than rewinding (see
        /// <see cref="PuppeteerClock"/>). (2) An active line whose caret has run off the end: coast
        /// at up to 1.00x toward the NEXT line's first vocal, so a player who finished early hears
        /// the line play out and the tape parks on the next cue waiting for them. (3) No active line
        /// at all (the intro, an instrumental gap, the outro): the same coast, toward
        /// <see cref="TypingEngine.NextUnsealedLineIndex"/>'s first vocal.</para>
        ///
        /// <para>The line-complete arm looks at <c>ActiveLineIndex + 1</c> and NOT at
        /// <see cref="TypingEngine.NextUnsealedLineIndex"/>, which is the one place the engine's real
        /// line lifecycle bites: a finished line has not SEALED yet, so the next-unsealed index is
        /// still that same line and its first vocal is already behind the tape. Aiming there would
        /// be a backwards target and the tape would park in the middle of a line it should be
        /// playing out.</para>
        ///
        /// <para><see cref="TypingEngine.CurrentLeadLag"/> is what decides whether arm (1) applies,
        /// rather than a hand-rolled caret test: it is null in exactly the cases that must coast
        /// (finished, no active line, caret past the last cell, caret on a non-typeable cell), and
        /// it is <c>time - TargetTime</c>, so the target comes straight back out of it.</para>
        /// </summary>
        public static PuppeteerArm ArmFor(TypingEngine engine, double time)
        {
            ArgumentNullException.ThrowIfNull(engine);

            if (engine.CurrentLeadLag(time) is double leadLag)
                return new PuppeteerArm(time - leadLag, V_MAX);

            int next = engine.LineIsActive ? engine.ActiveLineIndex + 1 : engine.NextUnsealedLineIndex;

            // No next line: the outro. Nothing to aim at, so the cap is the whole of the arm and the
            // song plays itself out at its own speed.
            double target = next >= 0 && next < engine.Lines.Count
                ? engine.Lines[next].FirstVocalTime
                : double.PositiveInfinity;

            return new PuppeteerArm(target, COAST_MAX_VELOCITY);
        }

        /// <summary>
        /// The frequency actually commanded, from the model's state and the clock's own reading.
        /// Feed-forward plus a bounded correction: the velocity is what the model says the song
        /// should be running at, and the position error over <see cref="T_CORRECT_MS"/> is what
        /// pulls the real clock back onto the model when the two drift (they always do, see the
        /// class remarks on buffering and clock interpolation). Clamped into the same
        /// <c>[V_EPSILON, V_MAX]</c> band the model itself runs in, so the command is bounded by
        /// construction and never asks the audio path for something it cannot track.
        /// </summary>
        public static double CommandedFrequency(PuppeteerState state, double clockTime)
            => Math.Clamp(state.Velocity + ((state.PositionMs - clockTime) / T_CORRECT_MS), V_EPSILON, V_MAX);

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
