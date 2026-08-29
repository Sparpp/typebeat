// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Localisation;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Recite: you have to know the words. Every character the player has not typed yet is hidden,
    /// so the lyric is written out one keypress at a time and nothing can be read ahead. What the
    /// player HAS typed stays on screen exactly as it always renders (the sync-tinted correct ramp,
    /// the error red), and so does the map's playhead: the sung underline sweep and the sung caret
    /// keep marking where the vocal is, which is the only cue left for what to type and when.
    ///
    /// <para>PURELY VISUAL, in the same sense <see cref="TypeBeatModFlashlight"/> is: it writes one
    /// flag on the drawable ruleset (<see cref="UI.DrawableTypeBeatRuleset.HideUpcomingText"/>) and
    /// touches neither <see cref="Gameplay.TypingEngine"/> nor the score processor. That is why it
    /// needs no ERA bit in the replay CONFIG frame and no <c>ReplayEngineFeed</c> arm, unlike
    /// <see cref="TypeBeatModFletcher"/>: an era flag has to be recoverable from a stored replay,
    /// whereas a visual mod is re-derived from the score's mod list alone every time the replay is
    /// watched, so a Recite replay hides the same characters with nothing recorded about it. It is
    /// also why the seam is <see cref="ApplyToDrawableRuleset"/> rather than
    /// <c>DrawableTypeBeatRuleset.createEngine</c>: nothing here has to be known before the engine
    /// exists.</para>
    ///
    /// <para>ModType.DifficultyIncrease, following Flashlight (the mod whose behaviour this copies)
    /// rather than Fletcher (its structural template): Recite adds a handicap on top of the same
    /// input model instead of swapping one model for another, which is the line Conversion is drawn
    /// on here.</para>
    ///
    /// <para>Stacking with <see cref="TypeBeatModFlashlight"/> still composes cleanly as "hidden by
    /// either" (independent per-cell factors), but the owner judged the resulting stack unplayable
    /// in practice, so the pairing is withdrawn: see <see cref="IncompatibleMods"/>.</para>
    ///
    /// <para>ACCEPTED RESIDUAL: a wrong key typed onto a lyric cell marks it Wrong, and a Wrong
    /// lyric cell renders its own LYRIC character in error red (see
    /// <see cref="UI.LyricLineDisplay.CellGlyph"/>, which substitutes the typed char for word gaps
    /// only). So a player can reveal a whole line by mashing one wrong key through it. That is left
    /// alone rather than special-cased: it costs a wrong judgement per character revealed, which
    /// destroys the accuracy and the combo of the run doing it, so the exploit is self-defeating and
    /// buys nothing a leaderboard would show.</para>
    /// </summary>
    public class TypeBeatModRecite : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override string Name => "Recite";

        public override string Acronym => "RE";

        public override LocalisableString Description => "The words are hidden until you type them.";

        public override ModType Type => ModType.DifficultyIncrease;

        // Legacy self-report only; the authoritative multiplier lives in the non-obsolete
        // TypeBeatScoreMultiplierCalculator. Both say 1.07x and must move together.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.07;
#pragma warning restore CS0672

        /// <summary>
        /// Ranked. Every character still has to be typed and is still judged against the same
        /// windows, so the shared leaderboards are comparing the same run with one cue removed.
        /// </summary>
        public override bool Ranked => true;

        /// <summary>
        /// Withdrawn from <see cref="TypeBeatModFlashlight"/>: both mods hide the lyric text
        /// surface, and the owner judged the combined stack unplayable. Declared on both sides,
        /// the same reciprocal pattern <see cref="TypeBeatModEasy.IncompatibleMods"/> and
        /// <see cref="TypeBeatModHardRock.IncompatibleMods"/> use, so the exclusion fires no matter
        /// which mod is picked first.
        /// </summary>
        public override Type[] IncompatibleMods => new[] { typeof(TypeBeatModFlashlight) };

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).HideUpcomingText = true;
    }
}
