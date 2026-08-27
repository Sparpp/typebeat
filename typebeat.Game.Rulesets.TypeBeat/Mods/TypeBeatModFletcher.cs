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
    /// Fletcher: PINS the player's caret to the song's playhead. Backlog 208 reversed the mod. The
    /// freedoms it used to grant (open the next line the moment you finish one, keep a line the song
    /// has left, trade the timing lock for a character-distance lock) are now what the game does for
    /// everybody, and Fletcher is the drill sergeant who takes them back: typing opens at the line's
    /// cue and not a beat before, the boundary snatches whatever you have not finished, and there is
    /// no borrowed time at either end. Rush and you are typing into a line that is not open yet; drag
    /// and the line goes without you.
    ///
    /// <para>Implemented by holding <see cref="Gameplay.TypingEngine.FletcherEnabled"/> and
    /// <see cref="Gameplay.TypingEngine.FlexibleLineSnap"/> at FALSE, which is the classic engine
    /// path this fork ran on for its whole life before 208 and is therefore the best-pinned code in
    /// the ruleset. The authoritative site for both is <c>DrawableTypeBeatRuleset.createEngine</c>,
    /// which reads this mod off the mod list before the engine exists: they are ERA flags the replay
    /// recorder's CONFIG frame reads, and an era flag cannot be applied late. The
    /// <see cref="ApplyToDrawableRuleset"/> below re-asserts the same two values (it cannot
    /// disagree, both are derived from "this mod is in the list") and is what makes the mod
    /// <c>HasImplementation</c>, hence selectable.</para>
    ///
    /// <para>Judgement itself is untouched, exactly as it was when the mod meant the opposite: every
    /// char is still judged against its own target time, so accuracy, sync% and the judgement counts
    /// mean what they always did.</para>
    /// </summary>
    public class TypeBeatModFletcher : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override string Name => "Fletcher";

        /// <summary>
        /// NOT "FT". That acronym is retired to <see cref="TypeBeatModLegacyFletcher"/>, which is
        /// what every stored score carrying it was actually played under (the FLEXIBLE caret, now
        /// the default), so reusing it here would re-derive those runs under the opposite rule and
        /// price them off the wrong multiplier.
        /// </summary>
        public override string Acronym => "FC";

        public override LocalisableString Description => "Were you Rushing or were you Dragging?!";

        /// <summary>
        /// A conversion, not a difficulty knob: it neither simply helps nor simply hurts, it changes
        /// what the game asks of you (distance pressure becomes timing pressure).
        /// </summary>
        public override ModType Type => ModType.Conversion;

        // The real multiplier is defined in TypeBeatScoreMultiplierCalculator (the authoritative,
        // non-obsolete path osu now uses). This obsolete override is kept only so the mod also
        // self-reports 1.02x for any legacy reader.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.02;
#pragma warning restore CS0672

        /// <summary>
        /// Ranked, at the mirror image of the 0.98x the mod carried while it meant the opposite.
        /// Nothing about it adds work in characters: every character still has to be typed and is
        /// still judged against its own target time. What it takes away is slack, at both ends of
        /// every line, and slack the default hands out for free is worth a small premium back, hence
        /// the 1.02x. Scores reach the shared leaderboards, which is why the engine difference is
        /// confined to two flags and pinned by the replay era tests.
        /// </summary>
        public override bool Ranked => true;

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset)
        {
            var engine = ((DrawableTypeBeatRuleset)drawableRuleset).Engine;

            engine.FletcherEnabled = false;
            engine.FlexibleLineSnap = false;
        }
    }
}
