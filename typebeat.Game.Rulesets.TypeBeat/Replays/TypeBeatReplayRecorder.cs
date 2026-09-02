// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.UI;
using typebeat.Game.Scoring;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>
    /// Records typing input into the target score's replay. Unlike positional rulesets there is no
    /// periodic sampling: the playfield's key handler pushes one event per EFFECTIVE engine call
    /// (accepted char, rejected-wrong char, or backspace that erased something) via
    /// <see cref="RecordInput"/>, and each becomes exactly one <see cref="TypeBeatReplayFrame"/>.
    /// The base class's periodic <c>RecordFrame(false)</c> ticks and the judgement-driven
    /// <c>RecordFrame(true)</c> calls find no pending event and record nothing.
    ///
    /// The first recorded event is preceded by a CONFIG frame capturing the engine's
    /// judgement-relevant settings (allow-wrong-input, space-skips-word, syllable-span timing,
    /// wrong-input-on-word-gaps, strict spaces, char-timed stretch, flexible lines, the rush
    /// bound and first-char timing), so playback
    /// can reproduce judgement regardless of
    /// the watching machine's local config, and regardless of which JUDGEMENT ERA the client watching
    /// it ships.
    ///
    /// <para><b>THE FRAME AXIS (backlog 256).</b> Ordinarily a frame's time is the lyric time the
    /// engine was fed at, and that is the whole of it. Under the PUPPETEER mod the song's position
    /// is a function of the typing, so a lyric time is an output of the tape model rather than an
    /// input to it, and a run stored that way could not reproduce the tape it was played on. Such a
    /// run is stamped on the mod's own WALL axis instead
    /// (<see cref="Mods.TypeBeatModPuppeteer.WallStampMs"/>) and says so in CONFIG frame bit 9;
    /// <c>PuppeteerReplayTransform</c> turns it back into track time by re-running the model.</para>
    ///
    /// <para>The axis must be the mod's OWN, not a second stopwatch that merely started at a similar
    /// time: the transform runs one canonical model tick per unit of it, so anything but the live
    /// model's own tick count re-derives a shifted tape. The mod instance is therefore handed in
    /// (<see cref="UI.DrawableTypeBeatRuleset"/> creates both this and the mods and is the one place
    /// that holds the list), rather than being looked up or approximated here.</para>
    ///
    /// <para>The decision is made ONCE, at the CONFIG frame, and the whole run follows it. A mixed
    /// stream would be unreadable, and the alternative (deciding per frame) could produce one if the
    /// tape were somehow not yet anchored. Without the mod, or before the tape is anchored, this
    /// records exactly what it always did: lyric times, bit 9 clear.</para>
    /// </summary>
    public partial class TypeBeatReplayRecorder : ReplayRecorder<TypeBeatAction>
    {
        private readonly TypingEngine engine;

        /// <summary>The Puppeteer mod in this run's stack, or null (which is every ordinary play).</summary>
        private readonly TypeBeatModPuppeteer? puppeteer;

        private TypeBeatReplayFrame? pendingFrame;
        private bool configEmitted;

        /// <summary>
        /// Whether this run is being stamped on the wall axis. Decided once, at the CONFIG frame,
        /// and then fixed for the whole run.
        /// </summary>
        private bool wallStamped;

        public TypeBeatReplayRecorder(Score score, TypingEngine engine, TypeBeatModPuppeteer? puppeteer = null)
            : base(score)
        {
            this.engine = engine;
            this.puppeteer = puppeteer;
        }

        /// <summary>
        /// Record one typing event. <paramref name="character"/> is the exact char fed to the engine
        /// (or <see cref="TypeBeatReplayFrame.BACKSPACE"/>); <paramref name="time"/> is the exact
        /// (already integral) engine time it was fed at.
        /// </summary>
        public void RecordInput(char character, double time)
        {
            if (!configEmitted)
            {
                configEmitted = true;

                // The axis decision, made once. The CONFIG frame's own time is the ANCHOR under
                // bit 9: the track position the tape started at, which is also the origin every
                // other frame's stamp is measured from. One number, two jobs, and it is the whole of
                // what the transform needs to re-derive the run.
                wallStamped = WallStamps(puppeteer);

                double configTime = wallStamped ? AnchorTimeFor(puppeteer!.AnchorMs!.Value) : time;

                emit(TypeBeatReplayFrame.CreateConfigFrame(configTime, engine.AllowWrongInput, engine.SpaceSkipsWord, engine.SyllableTiming, engine.WrongInputOnWordGaps, engine.StrictSpaces, engine.CharTimedStretch, flexibleLines: engine.FlexibleLineSnap, boundedRush: engine.BoundedRush, firstCharTiming: engine.FirstCharTiming, wallClockFrames: wallStamped, backDatedSealBreak: engine.BackDatedSealBreak));
            }

            emit(new TypeBeatReplayFrame(StampFor(puppeteer, wallStamped, time), character));
        }

        /// <summary>
        /// Whether a run is stamped on the wall axis: only when the Puppeteer mod is in the stack AND
        /// its tape has been anchored, which the first gameplay frame does. Before that there is no
        /// axis to stamp on, so the run records the way every other run does and the era bit stays
        /// clear. That is a safe degradation rather than a compromise: it needs a keystroke to arrive
        /// before the first frame of the play, and the answer it gives is an ordinary, correct,
        /// if less faithful, replay.
        ///
        /// <para>Public and static because it is the whole of the axis POLICY, and a recorder is a
        /// drawable that cannot be stood up headlessly; the instance path is a one-liner over
        /// this.</para>
        /// </summary>
        public static bool WallStamps(TypeBeatModPuppeteer? puppeteer) => puppeteer?.AnchorMs != null;

        /// <summary>
        /// The time written to a frame: the mod's wall stamp for a Puppeteer run, the lyric time the
        /// engine was fed at for every other. The stamp is already integral (a rounded anchor plus a
        /// whole number of model ticks), so the legacy .osr encoding stays lossless either way.
        ///
        /// <para><paramref name="wallStamped"/> is passed in rather than re-derived, because the
        /// decision belongs to the RUN and is made once at its CONFIG frame: a stream that changed
        /// axis half way through would be unreadable.</para>
        /// </summary>
        public static double StampFor(TypeBeatModPuppeteer? puppeteer, bool wallStamped, double time)
            => wallStamped && puppeteer?.WallStampMs is double stamp ? stamp : time;

        /// <summary>
        /// The anchor, nudged off the one value the legacy decoder cannot carry. A CONFIG frame is
        /// the first frame of a run, so its time IS its encoded delta, and
        /// <c>LegacyScoreDecoder.readLegacyReplay</c> silently drops a frame whose delta reads
        /// "-12345" (stable's seed-frame sentinel), which would take the run's whole header with it,
        /// nine era bits and the anchor included. The anchor is a free choice of origin and the
        /// transform reads back whatever was written, so moving it by a millisecond costs nothing and
        /// cannot desynchronise anything.
        /// </summary>
        public static double AnchorTimeFor(double anchor)
            => anchor.Equals(legacy_seed_sentinel_time) ? anchor + 1 : anchor;

        /// <summary>The frame delta the legacy decoder reads as stable's seed frame and discards.</summary>
        private const double legacy_seed_sentinel_time = -12345;

        private void emit(TypeBeatReplayFrame frame)
        {
            pendingFrame = frame;
            RecordFrame(true);
            pendingFrame = null;
        }

        protected override ReplayFrame? HandleFrame(Vector2 mousePosition, List<TypeBeatAction> actions, ReplayFrame? previousFrame)
        {
            var frame = pendingFrame;
            pendingFrame = null;
            return frame;
        }
    }
}
