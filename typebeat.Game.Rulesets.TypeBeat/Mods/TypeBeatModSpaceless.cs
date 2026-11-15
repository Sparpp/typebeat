// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Framework.Localisation;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Mods
{
    /// <summary>
    /// Spaceless (backlog 470): the player never presses space between words. Every inter-word space
    /// completes itself, and the spacebar stops being input altogether, so it can never be a wrong
    /// key either.
    ///
    /// <list type="bullet">
    /// <item>THE CELL LIST changes, so it is stamped on the line objects like
    /// <see cref="TypeBeatModShorthand"/> (<see cref="TypeBeatHitObject.Spaceless"/>, applied after
    /// conversion and before ApplyDefaults) and read by both engine factories off the mod list. Every
    /// space cell is built completed (<see cref="Gameplay.TypingCell.AutoCompleted"/>, the very cell
    /// Shorthand completes its spaces with): not typeable, so it has no nested scoring object, no
    /// judgement, no combo and no place in the accuracy denominator. The caret hops it the way it
    /// hops a mark the default stream keeps. Letters keep their targets, their syllables and their
    /// words, so nothing else about the play moves.</item>
    /// <item>THE KEY is refused by the engine before any step reads it
    /// (<see cref="Gameplay.TypingEngine.Spaceless"/>): not a typo, not a Gatekeeper rejection, not a
    /// word skip, not a mash, not the manual newline (Enter or a typed letter still hands a finished
    /// line on). A refused press is never recorded, and a replay frame holding one is refused again
    /// on re-derivation, so live play, replays and the server's re-scoring agree.</item>
    /// </list>
    ///
    /// <para>A MOD, not an ERA: no CONFIG frame bit, for the reason <see cref="TypeBeatModDyslexia"/>
    /// gives (no stored run can carry a mod that did not exist when it was played). The score's mod
    /// list is the whole contract.</para>
    ///
    /// <para>UNRANKED, like Shorthand: the spacebar is a real share of what a lyric asks of the player
    /// (one keypress per word), so a run without it is not the same play. Priced at Shorthand's 0.6x
    /// (owner decision) in <see cref="Scoring.TypeBeatScoreMultiplierCalculator"/>; there is no pp
    /// term, since an unranked mod earns none.</para>
    /// </summary>
    public class TypeBeatModSpaceless : Mod, IApplicableAfterBeatmapConversion
    {
        public override string Name => "Spaceless";

        /// <summary>
        /// "SL", read as SpaceLess. "SP" was the other candidate and reads as "speed" at a glance on a
        /// board row. Free against every acronym the ruleset ships, which
        /// <c>TypeBeatModSpacelessTest</c> pins against <c>Ruleset.AllMods</c>.
        /// </summary>
        public override string Acronym => "SL";

        public override LocalisableString Description => "No spacebar between words: spaces complete themselves, and the key is ignored.";

        public override ModType Type => ModType.DifficultyReduction;

        // Legacy self-report; the real multiplier is in TypeBeatScoreMultiplierCalculator.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 0.6;
#pragma warning restore CS0672

        public override bool Ranked => false;

        /// <summary>
        /// <see cref="TypeBeatModShorthand"/> already completes every space and leaves the key inert,
        /// so worn together this mod would change nothing, which is a mod select lying about it.
        /// <see cref="TypeBeatModPolyglot"/>'s Japanese input reads the spacebar as the confirmation
        /// key (a trailing n, a word skip), so taking the key away breaks that input model; the
        /// cell list ignores this mod under Polyglot, as it ignores Shorthand. Both pairs are declared
        /// on both sides.
        /// </summary>
        public override Type[] IncompatibleMods => new[] { typeof(TypeBeatModShorthand), typeof(TypeBeatModPolyglot) };

        public void ApplyToBeatmap(IBeatmap beatmap)
        {
            foreach (var line in beatmap.HitObjects.OfType<TypeBeatHitObject>())
                line.Spaceless = true;
        }
    }
}
