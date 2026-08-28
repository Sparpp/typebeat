// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
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
    /// (<c>TypeBeatModConductorTest</c> pins this). The rate CURVE is not stored either: it is
    /// re-derived by running this same controller over the same frames, which is why the integration
    /// is fixed-step in TRACK time and reads no wall clock at all. Any residual divergence is
    /// audio-only.</para>
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

        public override LocalisableString Description => "The song follows you.";

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

        /// <summary>Rate added per millisecond of phase error outside the deadband.</summary>
        public const double PROPORTIONAL_GAIN_PER_MS = 0.002;

        /// <summary>Phase error inside which the song holds its pace instead of chasing jitter.</summary>
        public const double DEADBAND_MS = 40;

        /// <summary>Maximum change in rate per second of track time. Small enough that
        /// <c>MasterGameplayClockContainer.checkPlaybackValidity</c>'s 300 ms tolerance never trips.</summary>
        public const double SLEW_PER_SECOND = 0.8;

        /// <summary>Time constant of the typing-speed EMA. 90% of its weight is the last 1.6 seconds.</summary>
        public const double SUPPLY_TAU_SECONDS = 0.7;

        public const double DEFAULT_MIN_RATE = 0.5;
        public const double DEFAULT_MAX_RATE = 1.5;

        /// <summary>The widest band the two sliders may be dragged to. The floor is "stop and wait
        /// for me" territory and the ceiling matches Double Time's own maximum.</summary>
        public const double ABSOLUTE_MIN_RATE = 0.05;

        public const double ABSOLUTE_MAX_RATE = 2.0;

        /// <summary>A frame longer than this is a pause, a seek or a stall rather than a frame.</summary>
        private const double max_frame_elapsed_ms = 250;

        /// <summary>Cap on catch-up steps after a long frame, so a hitch cannot spin the controller.</summary>
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
        /// moving pitch is unlistenable. On, it becomes the frequency scale the rate mods use.
        /// </summary>
        [SettingSource("Adjust pitch", "Should pitch be adjusted with speed")]
        public BindableBool AdjustPitch { get; } = new BindableBool();

        /// <summary>
        /// The instantaneous rate, written once per frame and read by the audio stack. Bound to the
        /// track through <see cref="RateAdjustModHelper"/> exactly as every other rate mod is: the
        /// aggregate it is attached to (<c>GameplayClockContainer.AdjustmentsFromMods</c>) is bound
        /// onto the real track, and the gameplay clock's source IS that track, so writing here moves
        /// the music and gameplay time together with no new clock.
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

        private readonly RateAdjustModHelper rateAdjustHelper;

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
            rateAdjustHelper = new RateAdjustModHelper(SpeedChange);
            rateAdjustHelper.HandleAudioAdjustments(AdjustPitch);
        }

        public void ApplyToTrack(IAdjustableAudioComponent track)
        {
            reset();
            rateAdjustHelper.ApplyToTrack(track);
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

            if (lastTrackTime is not double previous)
            {
                lastTrackTime = time;
                publish();
                return;
            }

            lastTrackTime = time;

            double elapsed = time - previous;

            if (!(elapsed > 0) || elapsed > max_frame_elapsed_ms)
            {
                // Paused, seeked backwards, rewound or stalled. The filter describes a stretch of
                // the song that is no longer the one being played, so empty it; the RATE is kept,
                // because the audio cannot teleport and the slew limit is what stops it trying.
                clearFilter();
                publish();
                return;
            }

            accumulator = Math.Min(accumulator + elapsed, max_steps_per_frame * ConductorController.STEP_MS);

            var inputs = new ConductorInputs(0, demandFor(engine), phaseErrorFor(engine, time), engine.LineIsActive);
            var tuning = ConductorTuning.Default.WithRateBand(MinRate.Value, MaxRate.Value);

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
            double lo = Math.Min(MinRate.Value, MaxRate.Value);
            double hi = Math.Max(MinRate.Value, MaxRate.Value);

            state = ConductorState.Initial with { Rate = Math.Clamp(1d, lo, hi) };
            pendingCells = 0;
            accumulator = 0;
            lastTrackTime = null;
            SpeedChange.Value = state.Rate;
        }

        private void publish()
        {
            SpeedChange.Value = state.Rate;

            if (drawableRuleset != null)
                drawableRuleset.ConductorRate = state.Rate;
        }
    }
}
