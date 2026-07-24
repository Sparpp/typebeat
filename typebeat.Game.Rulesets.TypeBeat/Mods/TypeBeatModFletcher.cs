// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Fletcher: unpins the player's caret from the song's playhead. Normally typing is locked to the
    /// line the song is on, opening at that line's cue and ending when the line seals; with this mod
    /// the caret is yours. Finish a line early and you are typing the next one immediately (no waiting
    /// for its cue); fall behind and the song moving on no longer snaps the caret off the line you are
    /// still finishing. What replaces the timing lock is a DISTANCE lock: rush more than
    /// <see cref="Gameplay.TypingEngine.FLETCHER_MAX_CHARS_AHEAD"/> countable characters ahead of the
    /// playhead and your presses stop earning combo; drag more than
    /// <see cref="Gameplay.TypingEngine.FLETCHER_DRAG_GRACE_MS"/> past a line's deadline and the line
    /// is force-sealed under you, exactly as it would have been without the mod.
    ///
    /// Judgement itself is untouched: every char is still judged against its own target time, so
    /// rushing reads as early deltas and dragging as late ones, and accuracy, sync% and the judgement
    /// counts report the drift honestly. Implemented by flipping a single engine flag
    /// (<see cref="Gameplay.TypingEngine.FletcherEnabled"/>), the same pattern Literate and Mashing
    /// use, so the unmodded judgement path is byte-identical and replays reproduce a Fletcher run
    /// bit-exactly (the mod travels on the score, and playback applies it before the first frame).
    /// </summary>
    public class TypeBeatModFletcher : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override string Name => "Fletcher";

        public override string Acronym => "FT";

        public override LocalisableString Description => "Were you Rushing or were you Dragging?!";

        /// <summary>
        /// A conversion, not a difficulty knob: it neither simply helps nor simply hurts, it changes
        /// what the game asks of you (timing pressure becomes distance pressure).
        /// </summary>
        public override ModType Type => ModType.Conversion;

        // The real multiplier is defined in TypeBeatScoreMultiplierCalculator (the authoritative,
        // non-obsolete path osu now uses). This obsolete override is kept only so the mod also
        // self-reports 0.98x for any legacy reader.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 0.98;
#pragma warning restore CS0672

        /// <summary>
        /// Ranked. Nothing about the mod removes work from the player: every character still has to be
        /// typed, and every character is still judged against its own target time. The freedoms it
        /// grants (no cue lock, no mid-line snatch) are worth a touch less than a locked run, hence the
        /// 0.98x. Scores therefore reach the shared leaderboards, which is why the engine changes are
        /// confined behind one flag and pinned by the replay round-trip and default-path tests.
        /// </summary>
        public override bool Ranked => true;

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.FletcherEnabled = true;
    }
}
