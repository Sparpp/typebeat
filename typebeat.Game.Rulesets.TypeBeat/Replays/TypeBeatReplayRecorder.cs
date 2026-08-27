// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
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
    /// wrong-input-on-word-gaps, strict spaces, char-timed stretch and flexible lines), so playback
    /// can reproduce judgement regardless of
    /// the watching machine's local config, and regardless of which JUDGEMENT ERA the client watching
    /// it ships.
    /// </summary>
    public partial class TypeBeatReplayRecorder : ReplayRecorder<TypeBeatAction>
    {
        private readonly TypingEngine engine;

        private TypeBeatReplayFrame? pendingFrame;
        private bool configEmitted;

        public TypeBeatReplayRecorder(Score score, TypingEngine engine)
            : base(score)
        {
            this.engine = engine;
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
                emit(TypeBeatReplayFrame.CreateConfigFrame(time, engine.AllowWrongInput, engine.SpaceSkipsWord, engine.SyllableTiming, engine.WrongInputOnWordGaps, engine.StrictSpaces, engine.CharTimedStretch, flexibleLines: engine.FlexibleLineSnap));
            }

            emit(new TypeBeatReplayFrame(time, character));
        }

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
