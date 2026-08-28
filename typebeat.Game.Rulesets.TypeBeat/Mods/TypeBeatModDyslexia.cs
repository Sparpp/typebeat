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
    /// Dyslexia (backlog 231): the letters of a word may arrive in ANY ORDER. Each press is offered
    /// to every character of the word the caret is inside that has not been typed yet, and the first
    /// one it fits is the one it lands on, judged against that character's own target or syllable
    /// span. A key that fits nothing left in the word is wrong exactly as it is without the mod.
    ///
    /// <para>Implemented by one engine flag,
    /// <see cref="Gameplay.TypingEngine.AnyOrderWithinWord"/>, which forks the expected-character
    /// lookup in <c>ProcessKey</c> and nothing else: every consequence of a press is already
    /// addressed by cell index, so grading, the combo-restore claim, the rendering repaint and the
    /// score all follow the matched cell without a second decision anywhere. The caret stays the
    /// leftmost untyped character of the line, which is what keeps the rush cap, the Flashlight
    /// window and line completion measuring what they measure today.</para>
    ///
    /// <para>UNRANKED, by leaving <see cref="Mod.Ranked"/> at its false default: word order is most
    /// of what typing to a lyric asks of the player, so a run that does not have to keep it is not
    /// the same play as one that does and its scores have no business on the shared leaderboards.
    /// No multiplier of any kind either, in the calculator or in pp, both of which treat an unlisted
    /// acronym as 1.0x: nothing about it is worth pricing, because nothing it produces is submitted
    /// as a ranked score in the first place.</para>
    ///
    /// <para>A MOD, not an ERA: it needs no CONFIG frame bit, because a bit exists to tell runs
    /// recorded BEFORE a rule existed apart from ones recorded after it, and no stored run can carry
    /// a mod that did not exist when it was played. Re-derivation rides the score's mod list, which
    /// is why both engine factories read it (<c>DrawableTypeBeatRuleset.createEngine</c>, the
    /// authoritative one, and <c>TypeBeatReplayScorer.createEngine</c>, the one that re-scores a
    /// stored replay), and <see cref="ApplyToDrawableRuleset"/> re-asserts the same value derived
    /// from the same question so it cannot disagree. That last one exists mainly to make the mod
    /// <c>HasImplementation</c>, hence selectable, exactly as
    /// <see cref="TypeBeatModFletcher"/> documents for itself. <see cref="TypeBeatModMashing"/> is
    /// the precedent for the whole shape: a mod-only engine flag with no bit at all.</para>
    /// </summary>
    public class TypeBeatModDyslexia : Mod, IApplicableToDrawableRuleset<TypeBeatHitObject>
    {
        public override string Name => "Dyslexia";

        /// <summary>
        /// "DX". Not "DY": a two-letter code is read at a glance on a leaderboard row, and DX is the
        /// one that reads as the word. Free against every other acronym the ruleset ships, its base
        /// classes included, which <c>TypeBeatModDyslexiaTest</c> pins against
        /// <c>Ruleset.AllMods</c> rather than against a list written out by hand.
        /// </summary>
        public override string Acronym => "DX";

        public override LocalisableString Description => "The letters of a word can be typed in any order.";

        /// <summary>
        /// CONVERSION, the category an INPUT MODEL swap belongs to, and the same reading
        /// <see cref="TypeBeatModGatekeeper"/> was moved to for the same reason (backlog 144):
        /// nothing is tightened or loosened, the question the game asks changes. Unranked while both
        /// of the other Conversion mods are ranked, which is not a contradiction: the category says
        /// what KIND of change it is, and <see cref="Mod.Ranked"/> says what the scores are worth.
        /// </summary>
        public override ModType Type => ModType.Conversion;

        /// <summary>
        /// Mashing already makes every key the right key, rewriting the press into the caret cell's
        /// expected character before the word is ever searched, so on an ordinary character Dyslexia
        /// has nothing left to find and does nothing at all. Declared incompatible rather than left
        /// to that: a mod that changes nothing while another is on is a mod select lies about, and a
        /// FREESTYLE slot is exempt from mashing's rewrite, so the pair would not even be reliably
        /// inert.
        /// </summary>
        public override Type[] IncompatibleMods => new[] { typeof(TypeBeatModMashing) };

        // Legacy self-report only; the authoritative multiplier lives in the non-obsolete
        // TypeBeatScoreMultiplierCalculator, which leaves this mod unlisted (i.e. 1.0x) on purpose.
        // Both say 1.0x and must move together, which for this mod means neither ever moves.
#pragma warning disable CS0672 // Member overrides obsolete member
        public override double ScoreMultiplier => 1.0;
#pragma warning restore CS0672

        public void ApplyToDrawableRuleset(DrawableRuleset<TypeBeatHitObject> drawableRuleset) =>
            ((DrawableTypeBeatRuleset)drawableRuleset).Engine.AnyOrderWithinWord = true;
    }
}
