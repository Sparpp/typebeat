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
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// UNRANKED. The song follows the PLAYER instead of the player chasing the song: a per-frame
    /// controller bends the playback rate down when they fall behind and up when they run hot, like
    /// an orchestra following its conductor.
    ///
    /// <para><b>Where the control law lives.</b> Entirely in
    /// <see cref="ConductorController.Step"/>, a pure function; this class is only the plumbing that
    /// feeds it (engine observations in, <see cref="SpeedChange"/> out). Its three terms, the
    /// supply/demand feed-forward, the proportional term on phase error and the slew limit, are
    /// documented there.</para>
    ///
    /// <para><b>Why this is a MOD and not an engine change.</b> Every target time, judgement window,
    /// syllable span, seal deadline and replay frame in this ruleset is keyed to TRACK time, and
    /// track time stays monotonic under a varying rate. The mod bends how fast track time elapses
    /// and touches nothing that decides what a keypress is worth, so
    /// <see cref="Gameplay.TypingEngine"/>, <see cref="Scoring.TypeBeatScoreProcessor"/> and their
    /// byte-compatible JS mirror in the web repo are all untouched by construction. In particular it
    /// does NOT scale <see cref="Gameplay.TypingEngine.WindowScale"/> the way
    /// <see cref="TypeBeatModDoubleTime"/> does: that scale is one number set before the first
    /// keypress, which a rate that is a function of the play cannot be expressed as (the same reason
    /// <c>ModWindUp</c> / <c>ModWindDown</c> are outside it). The visible consequence is that
    /// real-time judgement tolerance BREATHES with the rate: slowing down for a struggling player
    /// hands them wider real-time windows on top of the extra time. That is generous on purpose, and
    /// it is most of why the mod is unranked.</para>
    ///
    /// <para><b>Deliberately not <c>IApplicableToRate</c>.</b> Song select and the star-rating
    /// calculator ask <c>ApplyToRate(0, rate)</c> for one number that describes the whole play, and
    /// for a follower there is no such number: the rate is a function of how the player types. So
    /// those surfaces show 1.00x, which is the honest answer, rather than a fiction picked to fill
    /// the field. Star rating is likewise unmoved.</para>
    ///
    /// <para><b>One frame of lag, by construction.</b> <see cref="Playfield.Update"/> dispatches
    /// <see cref="IUpdatableByPlayfield"/> before its children tick, and the engine is ticked by a
    /// child of the playfield, so the controller always reads the PREVIOUS frame's engine state and
    /// the rate it writes applies from the next. At 60 Hz that is under a slew step and it is
    /// invisible next to the deadband. For the same reason the phase error is sampled on the
    /// playfield's clock rather than the engine's lyric-offset clock: a non-zero
    /// <c>LyricOffsetMs</c> shows up as a constant bias on the phase term, and the 40 ms deadband
    /// absorbs the calibration values players actually use.</para>
    ///
    /// <para><b>Replays.</b> A frame is (track time, character) and nothing rate-derived is stored,
    /// so re-scoring a Conductor replay is bit-identical to re-scoring the same keystrokes unmodded
    /// (<c>TypeBeatModConductorTest</c> pins this). The rate CURVE is not stored and is not
    /// reproduced either: since backlog 254 the controller is integrated on REAL time, so a watched
    /// replay re-derives the curve from the watcher's own frame timing rather than the player's, and
    /// the two agree only approximately. That is the right trade, because the curve is heard and
    /// never scored: the law measures how fast a human is typing and how far the song has walked
    /// from their caret, and both of those are real-world quantities that stop meaning anything when
    /// they are integrated on a clock the law itself controls the speed of.</para>
    /// </summary>
    public class TypeBeatModConductor : Mod, IUpdatableByPlayfield, IApplicableToTrack, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override string Name => "Conductor";

        /// <summary>
        /// Free across the whole ruleset (pinned by
        /// <c>TypeBeatModConductorTest.AcronymDoesNotCollideWithAnyOtherRulesetMod</c>), and it must
        /// stay in step with the server's always-unranked acronym list, which is the only thing
        /// keeping these plays off the ranked leaderboards.
        /// </summary>
        public override string Acronym => "CT";

        public override LocalisableString Description => "The song follows you. Audio quality degrades at extreme rates.";

        public override IconUsage? Icon => OsuIcon.ModAdaptiveSpeed;

        public override ModType Type => ModType.Fun;

        // A rate that is a function of one player's typing cannot be shared with anyone else in the
        // room, so this never reaches multiplayer, as a required mod or a free one.
        public override bool ValidForMultiplayer => false;
        public override bool ValidForMultiplayerAsFreeMod => false;

        /// <summary>
        /// Everything else that owns the playback-rate knob. One side of a pair is enough for
        /// <c>ModUtils.CheckCompatibleSet</c>, which reads the relation in both directions, but it is
        /// declared here anyway because the three types above predate this one and cannot name it.
        /// <c>ModAdaptiveSpeed</c> is included even though this fork does not currently offer it: it
        /// is a per-frame rate controller with a different control law, and two of those fighting
        /// over one track is exactly the collision this list exists to stop.
        /// </summary>
        public override Type[] IncompatibleMods => new[] { typeof(ModRateAdjust), typeof(ModTimeRamp), typeof(ModAdaptiveSpeed) };

        // ---------------------------------------------------------------------------------------
        // Tuning. Every number here is a playtest starting point rather than a contract: nothing
        // stored, submitted or judged reads any of them, so they can move without re-basing a
        // single score.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Rate added per millisecond of phase error outside the deadband. The error is MAP
        /// milliseconds (how far the song is from the caret's own target time), which is the one
        /// quantity in the law that is properly measured in song time and stays that way; only the
        /// CADENCE the law is integrated at moved to real time in backlog 254.
        ///
        /// <para>It is also what sets how hard a gap is closed: an on-pace player who is <c>E</c> ms
        /// behind is asking the song for <c>1 - 0.002 * (E - 40)</c>, so half a second of lag is a
        /// target of 0.08x. That is meant to look drastic. The song has to fall behind the player to
        /// give the ground back, and it only stays there until the gap is inside the deadband.</para>
        /// </summary>
        public const double PROPORTIONAL_GAIN_PER_MS = 0.002;

        /// <summary>Phase error inside which the song holds its pace instead of chasing jitter. Also
        /// the bound on the loop's steady-state lag, since it is the only place the position term
        /// stops pulling.</summary>
        public const double DEADBAND_MS = 40;

        /// <summary>Maximum change in rate per second of REAL time (backlog 254). Small enough that
        /// <c>MasterGameplayClockContainer.checkPlaybackValidity</c>'s 300 ms tolerance never trips,
        /// and, being real, it is also a bound on how fast the change is HEARD: on track seconds the
        /// same number meant 0.8 * rate per real second, which decayed toward the band floor without
        /// ever landing on it.</summary>
        public const double SLEW_PER_SECOND = 0.8;

        /// <summary>Time constant of the typing-speed EMA, in REAL seconds (backlog 254). 90% of its
        /// weight is the last 1.6 seconds of the player's actual life, at any playback rate.</summary>
        public const double SUPPLY_TAU_SECONDS = 0.7;

        public const double DEFAULT_MIN_RATE = 0.5;
        public const double DEFAULT_MAX_RATE = 1.5;

        /// <summary>
        /// A TRUE zero (backlog 252): "wait for me" all the way to a standstill. The song does not
        /// actually stop, and neither does the controller: <see cref="TrackAdjustmentsFor"/> keeps a
        /// crawl on the track, and <see cref="ConductorPacing"/> paces the driver on REAL time, which
        /// since backlog 254 it does at every rate rather than only under
        /// <see cref="TEMPO_FLOOR_RATE"/>, so the keypress that lifts the rate again still lands on a
        /// step. Without both of those a floor of 0 is a one-way door.
        ///
        /// <para>A floor of exactly 0 is also the whole of the "pause when I stop typing" behaviour:
        /// a player who walks away mid-line is read as silent, the position term drags the target to
        /// the bottom of their band, and the song stops there. There is no pause machinery, and a
        /// player whose floor is above 0 gets the floor instead, which is the same rule.</para>
        /// </summary>
        public const double ABSOLUTE_MIN_RATE = 0;

        /// <summary>
        /// The ceiling on the default, pitch-preserved path, and it is hardware's number rather than
        /// a taste call: BASS_FX documents <c>BASS_ATTRIB_TEMPO</c> over -95% to +5000%, i.e. a tempo
        /// rate of 0.05x to 51x, and nothing in the framework clamps the top.
        /// </summary>
        public const double ABSOLUTE_MAX_RATE = 51.0;

        /// <summary>
        /// The ceiling while <see cref="AdjustPitch"/> is on, which is a different and much lower
        /// wall. That path resamples rather than time-stretching, so the rate multiplies the track's
        /// FREQUENCY, and BASS refuses a frequency above 100 kHz: a 44.1 kHz song stops tracking at
        /// about 2.27x, a 48 kHz one at about 2.08x. Past that wall the audio holds still while the
        /// gameplay CLOCK, which reads the bindable and not the hardware, goes on accelerating, so
        /// the music and every judgement time silently come apart. 2.0x sits under the lowest of the
        /// sample-rate-dependent walls, so the ceiling does not depend on which song is loaded.
        /// </summary>
        public const double PITCH_ABSOLUTE_MAX_RATE = 2.0;

        /// <summary>
        /// The lowest tempo the audio stack will take: <c>TrackBass</c>'s constructor installs a
        /// handler that THROWS <c>ArgumentException</c> if the aggregate tempo drops below it. Rates
        /// under this are published as this tempo times a frequency fraction instead, see
        /// <see cref="TrackAdjustmentsFor"/>. It used to be the line the driver's pacing switched to
        /// real time at as well; since backlog 254 the pacing is real time everywhere and this is
        /// purely an audio-stack bound.
        /// </summary>
        public const double TEMPO_FLOOR_RATE = 0.05;

        /// <summary>
        /// The smallest frequency fraction ever published. A POWER OF TWO, which is what makes the
        /// sub-floor split exact: scaling a double by a power of two rounds nothing, so tempo times
        /// frequency reconstructs the rate bit for bit. A rate of 0 reaches the audio stack as
        /// <see cref="TEMPO_FLOOR_RATE"/> * this, about 1e-4: the readout says 0%, the track crawls
        /// instead of stopping, and neither the framework's "frequency reached zero, stop the track"
        /// path nor TrackBass's tempo assertion is tripped.
        /// </summary>
        public const double MIN_FREQUENCY_SCALE = 1 / 512d;

        /// <summary>
        /// Cap on catch-up steps after a long frame, so a hitch cannot spin the controller. Since
        /// the accumulator advances on REAL time (backlog 254) the only frame that can reach it is a
        /// real one just under <see cref="ConductorPacing.MAX_REAL_FRAME_MS"/>, i.e. a hitch between
        /// 160 and 250 ms; anything longer is thrown out by the stall test instead. It used to be
        /// load-bearing at speed as well, where a 16 ms frame at 51x was 816 ms of track time.
        /// </summary>
        private const int max_steps_per_frame = 8;

        [SettingSource("Minimum rate", "The slowest the song will drop to while it waits for you", SettingControlType = typeof(MultiplierSettingsSlider))]
        public BindableNumber<double> MinRate { get; } = new BindableDouble(DEFAULT_MIN_RATE)
        {
            MinValue = ABSOLUTE_MIN_RATE,
            MaxValue = ABSOLUTE_MAX_RATE,
            Precision = 0.01,
        };

        [SettingSource("Maximum rate", "The fastest the song will climb to while it chases you", SettingControlType = typeof(MultiplierSettingsSlider))]
        public BindableNumber<double> MaxRate { get; } = new BindableDouble(DEFAULT_MAX_RATE)
        {
            MinValue = ABSOLUTE_MIN_RATE,
            MaxValue = ABSOLUTE_MAX_RATE,
            Precision = 0.01,
        };

        /// <summary>
        /// Off by default, i.e. TEMPO adjustment: a follower rate moves constantly and a constantly
        /// moving pitch is unlistenable. On, it becomes the frequency scale the rate mods use, and
        /// the whole band is capped at <see cref="PITCH_ABSOLUTE_MAX_RATE"/> because that path stops
        /// tracking well before the tempo path's ceiling.
        /// </summary>
        [SettingSource("Adjust pitch", "Should pitch be adjusted with speed")]
        public BindableBool AdjustPitch { get; } = new BindableBool();

        /// <summary>
        /// The instantaneous TOTAL rate, written once per frame and read by the HUD through
        /// <see cref="CurrentRate"/>. Unlike every other rate mod this is not itself the bindable
        /// handed to the track: it is split into a tempo and a frequency adjustment by
        /// <see cref="TrackAdjustmentsFor"/>, whose PRODUCT is this value. The aggregate those two
        /// are attached to (<c>GameplayClockContainer.AdjustmentsFromMods</c>) is bound onto the real
        /// track and the gameplay clock's source IS that track, so writing here moves the music and
        /// gameplay time together with no new clock.
        ///
        /// <para>No <c>Precision</c>, unlike the ramps: quantising a follower to 0.01 puts audible
        /// steps into a rate that is meant to glide.</para>
        /// </summary>
        public BindableNumber<double> SpeedChange { get; } = new BindableDouble(1)
        {
            MinValue = ABSOLUTE_MIN_RATE,
            MaxValue = ABSOLUTE_MAX_RATE,
        };

        /// <summary>The rate the controller last asked for. What the HUD readout shows.</summary>
        public double CurrentRate => SpeedChange.Value;

        public override IEnumerable<(LocalisableString setting, LocalisableString value)> SettingDescription
        {
            get
            {
                // Always described, even at the defaults: the band is the whole shape of the mod,
                // and an icon reading a bare "CT" says nothing about how far it is allowed to go.
                yield return ("Rate band", FormattableString.Invariant($@"{MinRate.Value:N2}x to {MaxRate.Value:N2}x"));

                if (!AdjustPitch.IsDefault)
                    yield return ("Adjust pitch", AdjustPitch.Value ? "On" : "Off");
            }
        }

        /// <summary>The tempo half of the pair published to the track. See <see cref="TrackAdjustmentsFor"/>.</summary>
        private readonly BindableDouble tempoAdjustment = new BindableDouble(1);

        /// <summary>The frequency half of the pair published to the track.</summary>
        private readonly BindableDouble frequencyAdjustment = new BindableDouble(1);

        private IAdjustableAudioComponent? track;

        /// <summary>
        /// Wall clock. It is the controller's TIME BASE (backlog 254): the pacing decision turns it
        /// into the fixed-step accumulator's advance, and it is still the only place the mod reads
        /// wall time. Nothing derived from it reaches a target time, a judgement or a replay frame,
        /// so what it can move is the rate curve and nothing else.
        /// </summary>
        private readonly Stopwatch realClock = new Stopwatch();

        private double lastRealMs;

        private ConductorState state = ConductorState.Initial;

        private TypingEngine? boundEngine;
        private DrawableTypeBeatRuleset? drawableRuleset;

        /// <summary>Countable-character density of each line, in cells per second. Built once per engine.</summary>
        private double[]? lineDemand;

        /// <summary>Accepted countable keypresses observed since the last controller step.</summary>
        private double pendingCells;

        private double? lastTrackTime;
        private double accumulator;

        public TypeBeatModConductor()
        {
            // Deliberately NOT RateAdjustModHelper, which binds SpeedChange straight onto one
            // property: the sub-floor region needs both properties at once (see
            // TrackAdjustmentsFor), and the two modes have different ceilings. The swap on toggling
            // the setting is the helper's own pattern, remove the old set and add the new one.
            SpeedChange.BindValueChanged(_ => updateTrackAdjustments());

            AdjustPitch.BindValueChanged(adjustPitch =>
            {
                applyModeCeiling(adjustPitch.NewValue);

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
        /// Split an internal rate into the (tempo, frequency) pair published to the track. Their
        /// PRODUCT is the rate the gameplay clock reads, since
        /// <c>GameplayClockExtensions.GetTrueGameplayRate</c> is sign * AggregateFrequency *
        /// AggregateTempo of exactly this adjustment set.
        ///
        /// <para>PITCH PRESERVED (the default): tempo carries the rate and frequency stays at 1,
        /// exactly as every other rate mod does, until the rate drops under
        /// <see cref="TEMPO_FLOOR_RATE"/>, which the audio stack throws on. Below that the tempo is
        /// held at the floor and the remainder is handed to the frequency as a power of two, so the
        /// product still reconstructs the rate bit for bit. The pitch drop that buys only exists in a
        /// band where the song is barely moving anyway, and it is what makes a band floor of 0
        /// possible at all.</para>
        ///
        /// <para>PITCH ADJUSTED: frequency carries the whole rate and the tempo stays at 1, again as
        /// the rate mods do. There is no floor to work around there, only the epsilon: a frequency of
        /// EXACTLY zero makes the framework stop the track outright, while any tiny non-zero value
        /// merely floors the real output at 100 Hz and leaves the clock tracking the bindable.</para>
        /// </summary>
        public static (double Tempo, double Frequency) TrackAdjustmentsFor(double rate, bool adjustPitch)
        {
            if (adjustPitch)
                return (1, Math.Max(rate, MIN_FREQUENCY_SCALE));

            if (rate >= TEMPO_FLOOR_RATE)
                return (rate, 1);

            double tempo = rate;
            double frequency = 1;

            while (tempo < TEMPO_FLOOR_RATE && frequency > MIN_FREQUENCY_SCALE)
            {
                tempo *= 2;
                frequency *= 0.5;
            }

            // The clamp only bites under TEMPO_FLOOR_RATE * MIN_FREQUENCY_SCALE (about 1e-4, which
            // reads as 0%), and that is the only place the product stops being the rate exactly: the
            // song crawls there instead of stopping.
            return (Math.Max(tempo, TEMPO_FLOOR_RATE), frequency);
        }

        /// <summary>
        /// Captures the drawable ruleset so the live rate can be published for the HUD readout, and
        /// publishes the starting value so the readout is present from the first frame. This is the
        /// <see cref="DrawableTypeBeatRuleset.ConductorRate"/> half of the
        /// <c>FlashlightVisibleRadius</c> pattern: the mod writes, the always-on HUD reads, and no
        /// mod type is named inside the HUD.
        /// </summary>
        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset)
        {
            this.drawableRuleset = (DrawableTypeBeatRuleset)drawableRuleset;

            reset();
            publish();
        }

        public void Update(Playfield playfield)
        {
            if (playfield is not TypeBeatPlayfield typeBeatPlayfield)
                return;

            var engine = typeBeatPlayfield.Engine;

            if (!ReferenceEquals(engine, boundEngine))
                bind(engine);

            double time = playfield.Clock.CurrentTime;

            // Read every frame, including the ones that return early, so the wall-clock delta is a
            // frame's worth and not a whole stretch of frames.
            double realElapsed = realElapsedMs();

            if (lastTrackTime is not double previous)
            {
                lastTrackTime = time;
                publish();
                return;
            }

            lastTrackTime = time;

            var pacing = ConductorPacing.Decide(time - previous, realElapsed);

            if (pacing.ClearFilter)
            {
                // Seeked backwards, rewound or stalled. The filter describes a stretch of the song
                // that is no longer the one being played, so empty it; the RATE is kept, because the
                // audio cannot teleport and the slew limit is what stops it trying.
                clearFilter();
                publish();
                return;
            }

            accumulator = Math.Min(accumulator + pacing.AdvanceMs, max_steps_per_frame * ConductorController.STEP_MS);

            (double lo, double hi) = rateBand();

            // IsLineComplete is a CARET predicate (caret past the last cell), and that is the read
            // this controller wants rather than "every cell of the line was typed". A player who
            // finished the line early, one who gave it up with the line skip and one who simply
            // walked away from it all park the caret in the same place, and all three of them are
            // now WAITING for the song rather than lagging it. A live retype selection is not gated
            // out here on purpose: the selection is held between two keystrokes and any caret move
            // invalidates it (TypeBeatPlayfield.Update), so it can only ever be open while the
            // player is idle, and the keystroke that consumes it pulls the caret back inside the
            // line before the next step reads this.
            var inputs = new ConductorInputs(0, demandFor(engine), phaseErrorFor(engine, time), engine.LineIsActive, engine.IsLineComplete);
            var tuning = ConductorTuning.Default.WithRateBand(lo, hi);

            while (accumulator >= ConductorController.STEP_MS)
            {
                accumulator -= ConductorController.STEP_MS;

                // Everything observed since the last step lands on the FIRST step of this frame and
                // the catch-up steps behind it see an idle player. A frame is shorter than a step
                // far more often than not, so that is the ordinary case rather than the exception,
                // and nothing is ever dropped: a frame that completes no step leaves the count
                // pending for the next one.
                state = ConductorController.Step(state, inputs with { AcceptedCells = pendingCells }, tuning, ConductorController.STEP_SECONDS);
                pendingCells = 0;
            }

            publish();
        }

        /// <summary>
        /// Signed phase error in milliseconds, POSITIVE when the player is ahead of the song.
        /// <see cref="TypingEngine.CurrentLeadLag"/> answers the opposite sign (it is
        /// <c>time - target</c>, i.e. how late the song is finding the caret), so it is negated
        /// here: a player who is ahead must make the song speed UP to reach them.
        /// </summary>
        private static double? phaseErrorFor(TypingEngine engine, double time)
            => engine.CurrentLeadLag(time) is double leadLag ? -leadLag : null;

        /// <summary>
        /// How fast the map is asking for characters right here, in countable cells per second.
        ///
        /// <para>Measured PER LINE rather than over a sliding time window on purpose. A window
        /// centred on the playhead collapses whenever it overhangs the end of a line, which would
        /// read every line ending as a density cliff and slam the feed-forward into the ceiling once
        /// a line. A line's own density has no such edge, is constant for as long as the term is
        /// engaged, and is a pure function of the beatmap, so a replay recomputes it exactly. The
        /// variation WITHIN a line is what the proportional term is for.</para>
        /// </summary>
        private double demandFor(TypingEngine engine)
        {
            int index = engine.ActiveLineIndex;

            if (index < 0)
                return 0;

            double[] demand = lineDemand ??= buildLineDemand(engine);

            return index < demand.Length ? demand[index] : 0;
        }

        private static double[] buildLineDemand(TypingEngine engine)
        {
            var lines = engine.Lines;
            double[] demand = new double[lines.Count];

            for (int k = 0; k < lines.Count; k++)
                demand[k] = DemandFor(lines[k]);

            return demand;
        }

        /// <summary>
        /// One line's countable-character density, in characters per second: how fast the map is
        /// asking the player to type while that line is the active one. 0 when there is nothing to
        /// measure (a one-character line, or one whose characters all share a target), which drops
        /// the feed-forward term rather than dividing by it.
        ///
        /// <para>Public because it is the whole of the map's side of the feed-forward and is worth
        /// pinning on its own. A pure function of the beatmap, so a replay recomputes it exactly.</para>
        /// </summary>
        public static double DemandFor(TypingLine line)
        {
            ArgumentNullException.ThrowIfNull(line);

            double first = 0;
            double last = 0;
            int count = 0;

            foreach (var cell in line.Cells)
            {
                if (!cell.IsCountable)
                    continue;

                if (count == 0)
                    first = cell.TargetTime;

                last = cell.TargetTime;
                count++;
            }

            // n characters span n-1 gaps, so the rate the line is sung at is (n-1) over the interval
            // between the first and the last character, not n over it.
            return count > 1 && last > first ? (count - 1) * 1000d / (last - first) : 0;
        }

        private void bind(TypingEngine engine)
        {
            if (boundEngine != null)
            {
                boundEngine.CharJudged -= onCharJudged;
                boundEngine.Rewound -= onRewound;
            }

            boundEngine = engine;
            engine.CharJudged += onCharJudged;
            engine.Rewound += onRewound;

            lineDemand = null;
            reset();
        }

        /// <summary>
        /// The supply signal: one accepted keypress on a COUNTABLE cell. Spaces are excluded because
        /// the demand side counts countable cells too, and the two have to be in the same currency
        /// (it is also the currency the rest of this ruleset measures character distance in).
        /// </summary>
        private void onCharJudged(CharJudgement judgement)
        {
            if (boundEngine == null || judgement.LineIndex < 0 || judgement.LineIndex >= boundEngine.Lines.Count)
                return;

            var cells = boundEngine.Lines[judgement.LineIndex].Cells;

            if (judgement.CellIndex >= 0 && judgement.CellIndex < cells.Count && cells[judgement.CellIndex].IsCountable)
                pendingCells++;
        }

        /// <summary>
        /// A backwards seek re-derives the whole run silently, so the controller's observations
        /// (which are events) describe a run that no longer happened. Same treatment as a long frame.
        /// </summary>
        private void onRewound() => clearFilter();

        private void clearFilter()
        {
            state = state.WithFilterCleared();
            pendingCells = 0;
            accumulator = 0;
            lastTrackTime = null;
        }

        private void reset()
        {
            (double lo, double hi) = rateBand();

            state = ConductorState.Initial with { Rate = Math.Clamp(1d, lo, hi) };
            pendingCells = 0;
            accumulator = 0;
            lastTrackTime = null;
            lastRealMs = 0;
            realClock.Restart();
            SpeedChange.Value = state.Rate;
        }

        /// <summary>
        /// The user's band as an ordered pair, pulled into the ceiling the active mode can actually
        /// honour. The sliders' own <c>MaxValue</c> already moves with the mode, so this only matters
        /// for the instant a toggle is being processed, but the law must never be handed a target the
        /// audio path cannot follow.
        /// </summary>
        private (double Lo, double Hi) rateBand()
        {
            double ceiling = AdjustPitch.Value ? PITCH_ABSOLUTE_MAX_RATE : ABSOLUTE_MAX_RATE;

            double lo = Math.Clamp(Math.Min(MinRate.Value, MaxRate.Value), ABSOLUTE_MIN_RATE, ceiling);
            double hi = Math.Clamp(Math.Max(MinRate.Value, MaxRate.Value), ABSOLUTE_MIN_RATE, ceiling);

            return (lo, hi);
        }

        /// <summary>
        /// Move both sliders and the published rate onto the active mode's ceiling.
        /// <c>BindableNumber</c> re-clamps its current value when <c>MaxValue</c> moves, so a band
        /// left at 51x collapses to 2x the moment "Adjust pitch" goes on, and stays where it landed
        /// if it goes off again.
        /// </summary>
        private void applyModeCeiling(bool adjustPitch)
        {
            double ceiling = adjustPitch ? PITCH_ABSOLUTE_MAX_RATE : ABSOLUTE_MAX_RATE;

            MinRate.MaxValue = ceiling;
            MaxRate.MaxValue = ceiling;
            SpeedChange.MaxValue = ceiling;
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
            (double tempo, double frequency) = TrackAdjustmentsFor(SpeedChange.Value, AdjustPitch.Value);

            tempoAdjustment.Value = tempo;
            frequencyAdjustment.Value = frequency;
        }

        private double realElapsedMs()
        {
            if (!realClock.IsRunning)
                realClock.Start();

            double now = realClock.Elapsed.TotalMilliseconds;
            double elapsed = now - lastRealMs;

            lastRealMs = now;

            return elapsed;
        }

        private void publish()
        {
            SpeedChange.Value = state.Rate;

            if (drawableRuleset != null)
                drawableRuleset.ConductorRate = state.Rate;
        }
    }
}
