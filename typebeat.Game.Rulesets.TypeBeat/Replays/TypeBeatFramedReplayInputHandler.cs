// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>
    /// Paces the frame-stable gameplay clock through the replay's keystroke times (the standard
    /// lazer plumbing: <see cref="FramedReplayInputHandler{TFrame}.SetFrameFromTime"/> steps the
    /// clock so it lands exactly on every frame boundary). It intentionally injects NO inputs into
    /// the input stack: typing input is not action-shaped, so the playfield feeds the recorded
    /// frames straight into the <see cref="Gameplay.TypingEngine"/> at their recorded times (see
    /// <c>TypeBeatPlayfield.EngineTicker</c>). Its presence still flips the usual switches:
    /// <c>HasReplayLoaded</c>, and <c>UseParentInput = false</c> on the ruleset input manager so
    /// live keys cannot leak into a watched replay.
    /// </summary>
    public class TypeBeatFramedReplayInputHandler : FramedReplayInputHandler<TypeBeatReplayFrame>
    {
        public TypeBeatFramedReplayInputHandler(Replay replay)
            : base(replay)
        {
        }

        /// <summary>Every frame is a discrete keystroke; none may be skipped over.</summary>
        protected override bool IsImportant(TypeBeatReplayFrame frame) => true;
    }
}
