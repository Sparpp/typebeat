// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// Turns a WALL-CLOCK stamped replay (the Puppeteer era, CONFIG frame bit 9, backlog 256) back
    /// into ordinary TRACK-TIME frames, by re-running the tape model the run was played on.
    /// Everything downstream of this, the headless scorer and the watch path alike, then sees a
    /// perfectly ordinary replay and needs no knowledge of the mod at all.
    ///
    /// <para><b>Why the axis had to change.</b> Every other replay in this ruleset stores lyric
    /// times, which works because the song's position is an input the player reacts to. Under
    /// Puppeteer it is the other way round: the song's position is a FUNCTION of the typing, so a
    /// keystroke's lyric time is an OUTPUT of the model. Storing only outputs makes a run
    /// unreproducible, because the tape it was played on cannot be recovered from them. So a
    /// Puppeteer run stores the input axis, WALL time, and the tape is recovered by re-running the
    /// model rather than by being recorded.</para>
    ///
    /// <para><b>THE ANCHOR, and why it is one number.</b> Re-derivation needs the position the tape
    /// STARTED at, and it has to come out of the .osr, the beatmap and the mods alone. It is carried
    /// in the one field a CONFIG frame already has and never uses for anything else: its own
    /// <see cref="ReplayFrame.Time"/>. Under bit 9 that time is the ANCHOR, the rounded track
    /// position the tape was started at, and every other frame in the run is stamped
    /// <c>anchor + model ticks</c> (see <see cref="TypeBeatModPuppeteer.WallStampMs"/>). One number
    /// is therefore doing both jobs, the origin of the wall axis and the starting position of the
    /// tape, which is exactly what makes the recorder and this transform provably agree: the
    /// recorder writes it, this reads it, and there is no second quantity to drift.</para>
    ///
    /// <para>Three properties fall out of that choice and are worth naming. The stored frames stay
    /// SORTED, because the anchor is the first frame's time and the tick count only ever grows, so
    /// the legacy encoder's integral deltas are non-negative after the first and no consumer's
    /// re-sort can move anything. The stored times stay INTEGRAL, because the anchor is rounded once
    /// and ticks are whole. And the transform is IDEMPOTENT, because the derived CONFIG frame
    /// carries bit 9 CLEAR: a derived stream describes itself as what it now is, ordinary track
    /// time.</para>
    ///
    /// <para><b>THE CANONICAL CO-SIMULATION.</b> This is a contract, not an implementation detail:
    /// it and the constants in the preset the run was played under (see
    /// <see cref="PuppeteerTuning.For"/>, selected here by
    /// <see cref="TypeBeatModPuppeteer.TuningFor"/> off the stored mod list) are what a stored run
    /// MEANS. One wall millisecond at a time, from the anchor, and in this order:</para>
    /// <list type="number">
    /// <item><c>engine.Update(P)</c>, the scratch engine advanced to the tape's current position.</item>
    /// <item><c>arm = TypeBeatModPuppeteer.ArmFor(engine, P)</c>, read at every single tick. The
    /// cadence is the model's own unit deliberately: any coarser sampling would be a second constant
    /// to remember and would make the answer depend on it.</item>
    /// <item><c>PuppeteerClock.Step</c>, exactly one canonical tick, advancing P.</item>
    /// <item>The wall cursor advances by one, and every frame stamped there is emitted at
    /// <c>Math.Round(P)</c> and applied to the scratch engine at that same time, through
    /// <see cref="ReplayEngineFeed.Apply"/>, which is the call sequence live play makes.</item>
    /// </list>
    ///
    /// <para>The derived times are MONOTONIC because the model's position is (the tape never
    /// rewinds, see <see cref="PuppeteerClock"/>), so the derived stream is a legal replay by
    /// construction rather than by a sort.</para>
    ///
    /// <para><b>The recorded limitation.</b> The live run judged its keystrokes at the GAMEPLAY
    /// CLOCK's time, while this judges them at the model's position P. The two are not the same
    /// number: the live clock lags the model by the bounded correction transient
    /// (<see cref="TypeBeatModPuppeteer.T_CORRECT_MS"/>), the audio hardware lags the clock by up to
    /// a playback buffer, and the live driver samples its arm once per display frame where this
    /// samples every millisecond. For GRADES that difference is invisible, because Puppeteer
    /// forgives timing outright (<see cref="TypeBeatModPuppeteer.WINDOW_SCALE"/>). It is NOT
    /// invisible at a line's SEAL, which is derived from the clock and not from the windows: a
    /// keystroke the live run landed a few milliseconds inside a seal deadline can re-derive a few
    /// milliseconds outside it, and then its cell takes a Miss the live run did not. So a Puppeteer
    /// replay reproduces its run's statistics exactly except for presses sitting on a seal boundary,
    /// where it may differ by a cell. That is a recorded limitation of the era and not a bug to be
    /// hunted: closing it would mean storing the clock as well as the wall axis, which is storing
    /// the output this design exists to avoid trusting.</para>
    /// </summary>
    public static class PuppeteerReplayTransform
    {
        /// <summary>
        /// Whether <paramref name="replay"/> is stored on the wall axis and needs deriving. Keyed on
        /// the BIT and never on the mod list: the frames are self-describing, which is the whole
        /// point of a CONFIG frame, and it means a stored run cannot be mis-read because its mod
        /// list was lost, trimmed or resolved to <c>UnknownMod</c>.
        /// </summary>
        public static bool IsWallClockStamped(Replay replay)
        {
            ArgumentNullException.ThrowIfNull(replay);

            foreach (var frame in replay.Frames)
            {
                if (frame is TypeBeatReplayFrame typeBeatFrame && typeBeatFrame.IsConfig)
                    return typeBeatFrame.WallClockFrames;
            }

            return false;
        }

        /// <summary>
        /// The replay every consumer should actually run: <paramref name="replay"/> itself when it
        /// is already on the track axis (which is every replay but a Puppeteer one, so the ordinary
        /// path allocates nothing and is bit-identical to not calling this at all), and a NEW replay
        /// of derived track-time frames when it is not.
        ///
        /// <para>A new object rather than a rewrite in place: the caller's replay may be the one
        /// attached to a stored score, and re-encoding that after a rewrite would write derived
        /// times under a bit that says they are wall stamps.</para>
        /// </summary>
        public static Replay Derived(IBeatmap playable, IReadOnlyList<Mod> mods, Replay replay)
        {
            ArgumentNullException.ThrowIfNull(replay);

            if (!IsWallClockStamped(replay))
                return replay;

            return new Replay
            {
                HasReceivedAllFrames = replay.HasReceivedAllFrames,
                Frames = Derive(playable, mods, replay).Cast<ReplayFrame>().ToList(),
            };
        }

        /// <summary>
        /// The co-simulation itself. Returns one derived frame per input frame, in order, so a
        /// caller's frame accounting (the scorer's <c>UnconsumedFrames</c>, for one) means the same
        /// thing on either side of it.
        /// </summary>
        public static IReadOnlyList<TypeBeatReplayFrame> Derive(IBeatmap playable, IReadOnlyList<Mod> mods, Replay replay)
        {
            ArgumentNullException.ThrowIfNull(playable);
            ArgumentNullException.ThrowIfNull(replay);

            mods ??= Array.Empty<Mod>();

            var frames = replay.Frames.OfType<TypeBeatReplayFrame>().ToList();

            var config = frames.FirstOrDefault(f => f.IsConfig);

            if (config == null || !config.WallClockFrames)
                return frames;

            var lineObjects = playable.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.LineIndex).ToList();

            // Normalize exactly as the scorer and the drawable ruleset do, so engine position ==
            // LineIndex. The scratch engine is built by the SCORER's own builder rather than by a
            // copy of it: the engine this reads arms off and the engine that judges the derived
            // frames have to be the same engine, or the tape would be re-derived against a line
            // lifecycle the run was never judged on.
            for (int i = 0; i < lineObjects.Count; i++)
                lineObjects[i].LineIndex = i;

            var engine = TypeBeatReplayScorer.CreateEngine(playable, lineObjects, mods, RateWindowRule.ScaledByRate);

            // THE MODE (backlog 258). The model's tuning is a function of the mod's "Adjust pitch"
            // toggle, so the preset has to come out of the stored mod list rather than being assumed:
            // re-deriving a frequency-mode run under the tempo preset (or the other way round) puts
            // the watcher on a tape the player never heard, which is the same class of failure as
            // moving a model constant. The toggle rides into a stored score with the mod settings
            // payload, so it is there to be read.
            var tuning = TypeBeatModPuppeteer.TuningFor(mods);

            double anchor = config.AnchorMs;
            var tape = PuppeteerState.AnchoredAt(anchor);

            var derived = new List<TypeBeatReplayFrame>(frames.Count);

            int configIndex = frames.IndexOf(config);

            // A recorder emits the CONFIG frame first and nothing before it. Were a stream ever to
            // carry something ahead of its header, it is emitted at the anchor rather than dropped,
            // so the derived stream still holds exactly one frame per stored frame and a caller's
            // frame accounting means the same thing on both sides.
            for (int i = 0; i < configIndex; i++)
                emit(engine, derived, frames[i], anchor);

            // The CONFIG frame keeps the anchor as its time, which is what it already held, and
            // drops bit 9: the stream is track time now. Its era flags reach the scratch engine
            // before a single arm is read, exactly as playback applies them before a single
            // keystroke is judged.
            emit(engine, derived, config, anchor);

            int next = configIndex + 1;
            double wall = anchor;

            // Anything stamped at the anchor itself lands before the first tick.
            next = emitDue(engine, derived, frames, next, wall, tape.PositionMs);

            while (next < frames.Count)
            {
                engine.Update(tape.PositionMs);
                tape = PuppeteerClock.Step(tape, TypeBeatModPuppeteer.ArmFor(engine, tape.PositionMs), tuning);

                wall += PuppeteerClock.TICK_MS;

                next = emitDue(engine, derived, frames, next, wall, tape.PositionMs);
            }

            return derived;
        }

        /// <summary>
        /// Emit every frame stamped at or before <paramref name="wall"/> at the tape's current
        /// position, feeding each to the scratch engine so the arms that follow see the caret the
        /// keystroke moved.
        /// </summary>
        private static int emitDue(TypingEngine engine, List<TypeBeatReplayFrame> derived, IReadOnlyList<TypeBeatReplayFrame> frames, int next, double wall, double position)
        {
            while (next < frames.Count && frames[next].Time <= wall)
            {
                emit(engine, derived, frames[next], Math.Round(position));
                next++;
            }

            return next;
        }

        /// <summary>
        /// One derived frame: the stored frame at a track time, with bit 9 cleared (the derived
        /// stream is ordinary track time and must describe itself as such, which is also what makes
        /// this transform idempotent), fed to the scratch engine through the same
        /// <see cref="ReplayEngineFeed.Apply"/> live play and the scorer use.
        /// </summary>
        private static void emit(TypingEngine engine, List<TypeBeatReplayFrame> derived, TypeBeatReplayFrame source, double time)
        {
            var frame = clone(source, time);

            frame.WallClockFrames = false;

            derived.Add(frame);
            ReplayEngineFeed.Apply(engine, frame);
        }

        private static TypeBeatReplayFrame clone(TypeBeatReplayFrame source, double time) => new TypeBeatReplayFrame(time, source.Character)
        {
            AllowWrongInput = source.AllowWrongInput,
            SpaceSkipsWord = source.SpaceSkipsWord,
            SyllableTiming = source.SyllableTiming,
            WrongInputOnWordGaps = source.WrongInputOnWordGaps,
            StrictSpaces = source.StrictSpaces,
            FlexibleLines = source.FlexibleLines,
            CharTimedStretch = source.CharTimedStretch,
            BoundedRush = source.BoundedRush,
            FirstCharTiming = source.FirstCharTiming,
            WallClockFrames = source.WallClockFrames,
            BackDatedSealBreak = source.BackDatedSealBreak,
            LosslessSkipReclaim = source.LosslessSkipReclaim,
        };
    }
}
