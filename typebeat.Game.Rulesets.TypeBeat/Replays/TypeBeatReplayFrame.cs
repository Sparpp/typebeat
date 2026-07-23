// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Beatmaps;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.Replays.Types;

namespace typebeat.Game.Rulesets.TypeBeat.Replays
{
    /// <summary>
    /// One discrete typing input event. Typing has no positional state, so unlike the circle games a
    /// type!beat replay is not a sampled stream: it is exactly the sequence of engine mutations, one
    /// frame per accepted call into <see cref="Gameplay.TypingEngine"/>.
    ///
    /// <para><b>Frame format (the recalculation contract).</b> A frame is (Time, Character):</para>
    /// <list type="bullet">
    /// <item><see cref="ReplayFrame.Time"/> is the ENGINE (lyric-clock) time in milliseconds, already
    /// rounded to an integer at capture so the legacy .osr encoding (integral frame deltas) is
    /// lossless. It is the exact time value passed to <c>TypingEngine.Update</c>/<c>ProcessKey</c>,
    /// so judgement deltas recompute bit-identically.</item>
    /// <item><see cref="Character"/> is the exact character fed to the engine, AFTER keyboard-layout
    /// remapping and Shift application (so it carries the case the Literate mod judges on, and is
    /// independent of the player's physical layout). Two sentinels reuse ASCII control codes:
    /// <see cref="BACKSPACE"/> (0x08) is a backspace erase, and <see cref="CONFIG"/> (0x00) is a
    /// settings header frame carrying <see cref="AllowWrongInput"/> (the one judgement-relevant
    /// value that lives in local config rather than in the score's mods). Mods (Literate/Mashing/
    /// rate) travel in the score itself and need no frames.</item>
    /// </list>
    ///
    /// <para><b>Legacy (.osr) mapping</b>, chosen to round-trip through
    /// <see cref="typebeat.Game.Scoring.Legacy.LegacyScoreEncoder"/>/<c>Decoder</c> untouched:
    /// MouseX = character code, MouseY = config flags (bit 0 = allow-wrong-input; only meaningful on
    /// CONFIG frames), ButtonState = None, time = the integral frame time. All typeable characters
    /// (a-z, A-Z, 0-9, space) and both sentinels are far below the decoder's coordinate parse limits
    /// and its (256, -500) stable-header positions, so no stable fixup can mangle them.</para>
    ///
    /// <para>Only EFFECTIVE inputs are recorded (calls where the engine mutated state), which is what
    /// makes playback deterministic: replaying performs, per frame, <c>Update(Time)</c> then the
    /// keystroke, the same call sequence live play makes.</para>
    /// </summary>
    public class TypeBeatReplayFrame : ReplayFrame, IConvertibleReplayFrame
    {
        /// <summary>Sentinel character for a backspace erase (ASCII BS).</summary>
        public const char BACKSPACE = '\b';

        /// <summary>Sentinel character for the settings header frame (ASCII NUL).</summary>
        public const char CONFIG = '\0';

        /// <summary>
        /// The character fed to the engine (layout-remapped, Shift-cased), or a sentinel
        /// (<see cref="BACKSPACE"/>/<see cref="CONFIG"/>). Never a sentinel value for real typing:
        /// the typeable surface is a-z/A-Z/0-9/space only.
        /// </summary>
        public char Character;

        /// <summary>
        /// The engine's allow-wrong-input setting at record time. Only meaningful on
        /// <see cref="CONFIG"/> frames; playback applies it to the engine so a replay judges
        /// identically regardless of the watching machine's local setting.
        /// </summary>
        public bool AllowWrongInput;

        public bool IsBackspace => Character == BACKSPACE;

        public bool IsConfig => Character == CONFIG;

        public TypeBeatReplayFrame()
        {
        }

        public TypeBeatReplayFrame(double time, char character)
            : base(time)
        {
            Character = character;
        }

        public static TypeBeatReplayFrame CreateConfigFrame(double time, bool allowWrongInput) => new TypeBeatReplayFrame(time, CONFIG)
        {
            AllowWrongInput = allowWrongInput,
        };

        public void FromLegacy(LegacyReplayFrame currentFrame, IBeatmap beatmap, ReplayFrame? lastFrame = null)
        {
            Character = (char)(int)(currentFrame.MouseX ?? 0);
            AllowWrongInput = (((int)(currentFrame.MouseY ?? 0)) & 1) != 0;
        }

        public LegacyReplayFrame ToLegacy(IBeatmap beatmap) =>
            new LegacyReplayFrame(Time, Character, IsConfig && AllowWrongInput ? 1 : 0, ReplayButtonState.None);

        /// <summary>
        /// Never equivalent: every frame is a discrete keystroke. Two identical characters at the
        /// same (rounded) time are two real keypresses and must both survive recording, so the
        /// recorder's frame-collapse optimisation is disabled outright.
        /// </summary>
        public override bool IsEquivalentTo(ReplayFrame other) => false;
    }
}
