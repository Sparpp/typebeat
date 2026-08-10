// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// The rule deciding what a typed-through wrong character does to its cell's osu result. It
    /// exists so a stored score can be re-derived under the rule it was PLAYED under rather than
    /// only under the current one; live play is always <see cref="Deferred"/>.
    /// </summary>
    public enum TypoRule
    {
        /// <summary>
        /// The rule since backlog 109, and the only one live play uses: a wrong char resolves
        /// nothing. Correct the cell and the retype earns its real Great/Ok/Meh, leave it and the
        /// seal misses it. The combo break the keypress costs rides on
        /// <see cref="TypingEngine.Mistyped"/>, because no result exists to carry it.
        ///
        /// <para>Since backlog 122 it also means "and ONLY at the keypress": the deferred Miss is
        /// still a Miss, so it would otherwise break combo a second time at the seal. See
        /// <see cref="TypeBeatResultMapping.PrepaysCellComboBreak"/>.</para>
        /// </summary>
        Deferred,

        /// <summary>
        /// The rule every score stored BEFORE backlog 109 was judged under: a wrong char spent its
        /// cell's one result on a <see cref="HitResult.Miss"/> the instant it landed, which is also
        /// what broke osu's combo, so <see cref="TypingEngine.Mistyped"/> only counted the mistype
        /// and the combo break for a REJECTED key rode on
        /// <see cref="TypingEngine.WrongKeyRejected"/> instead.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        ImmediateMiss,
    }

    /// <summary>
    /// The single source of truth for how the engine's judgement stream becomes the osu result
    /// stream a submitted score carries. <see cref="Objects.Drawables.DrawableTypeBeatHitObject"/>
    /// applies it live; <see cref="TypeBeatReplayScorer"/> applies the same mapping headlessly when
    /// re-deriving a stored score from its replay, so the two can never drift.
    ///
    /// <para>It is deliberately only the MAPPING. Whose result it is (a cell drawable, or a
    /// headless slot), and the "first result on a cell wins" rule, belong to whoever owns the
    /// cells.</para>
    /// </summary>
    public static class TypeBeatResultMapping
    {
        /// <summary>
        /// The result every cell a line seals on without having resolved takes: a cell nobody
        /// typed, and (under <see cref="TypoRule.Deferred"/>) a cell left sitting wrong.
        /// </summary>
        public const HitResult SEAL_MISS = HitResult.Miss;

        /// <summary>
        /// The line container's own result. Scoring-inert, so osu accuracy tracks only the cells.
        /// </summary>
        public const HitResult LINE_RESULT = HitResult.IgnoreHit;

        /// <summary>
        /// The osu result an engine char judgement resolves its cell with, or null when the cell's
        /// result is DEFERRED and nothing at all is applied.
        ///
        /// <para>Mapping: Perfect-&gt;Great, Good-&gt;Ok, Ok-&gt;Meh, Premature/Lagging/Miss-&gt;Miss.
        /// Premature and Lagging accept the char with 0 engine points plus a combo break, and an osu
        /// Miss breaks combo too, so the mapping is behaviour-coherent for combo (the score weights
        /// differ). Miss reaches here only from a word abandoned by the space-skip setting, which
        /// announces the cells it gives up immediately instead of leaving them to the seal.</para>
        ///
        /// <para>WrongChar is the one that moved (backlog 109). A miss is a character the line ran
        /// out of time on; a typo is a typo, and in the default input model the player can still
        /// backspace and type the cell correctly, so the cell's one result waits to see which of the
        /// two it turns out to be. Under <see cref="TypoRule.ImmediateMiss"/> it is spent on a Miss
        /// straight away, which is what made the two indistinguishable AND unrecoverable, and is
        /// exactly what every stored score was priced under.</para>
        /// </summary>
        public static HitResult? CellResult(JudgementType type, TypoRule rule)
        {
            switch (type)
            {
                case JudgementType.Perfect:
                    return HitResult.Great;

                case JudgementType.Good:
                    return HitResult.Ok;

                case JudgementType.Ok:
                    return HitResult.Meh;

                case JudgementType.WrongChar:
                    return rule == TypoRule.Deferred ? null : HitResult.Miss;

                default:
                    // Premature, Lagging and Miss.
                    return HitResult.Miss;
            }
        }

        /// <summary>
        /// Whether a wrong KEYPRESS carries osu's combo break by hand at
        /// <see cref="TypingEngine.Mistyped"/> (backlog 109, both input models), as opposed to
        /// riding on the Miss result the cell used to take, with only a REJECTED key resetting
        /// combo by hand.
        /// </summary>
        public static bool MistypeCarriesTheComboBreak(TypoRule rule) => rule == TypoRule.Deferred;

        /// <summary>
        /// Whether this char judgement PREPAYS its cell's combo break, i.e. the break is taken now,
        /// at the keypress, and the result the cell eventually resolves with must not take it again
        /// (backlog 122).
        ///
        /// <para>Only a typed-through wrong char under <see cref="TypoRule.Deferred"/> can be in
        /// this position, and it is the exact hole backlog 109 left. Deferring the cell's result
        /// meant the break had to be mirrored by hand at the keypress, but the deferred result is
        /// still a <see cref="HitResult.Miss"/> when nobody fixes the cell, and osu breaks combo on
        /// every Miss. So one uncorrected typo cost TWO breaks: one at the keypress and one at the
        /// seal, AFTER the player had rebuilt a run through the rest of the line. That is strictly
        /// harsher than the pre-109 single break the deferral was meant to be no worse than, and
        /// backlog 114's replay recalculation measured it as the dominant reason stored scores lose
        /// <c>total_score</c> and <c>max_combo</c>.</para>
        ///
        /// <para>Under <see cref="TypoRule.ImmediateMiss"/> nothing prepays: the cell's Miss lands
        /// at the keypress and IS the break, so there is never a second one to suppress. The two
        /// rules therefore now agree that an uncorrected typo breaks combo exactly once, at the
        /// keypress; where they still differ is the CORRECTED typo, whose cell only Deferred can
        /// recover.</para>
        ///
        /// <para>The prepayment is per CELL, not per seal: it is redeemed by whatever result the
        /// cell finally takes, which is the seal's Miss in the ordinary case and the word-skip's
        /// immediate Miss when the player abandons the word instead. Both are the same statement,
        /// "this cell was never fixed", and both follow a break that has already been paid.</para>
        /// </summary>
        public static bool PrepaysCellComboBreak(JudgementType type, TypoRule rule)
            => type == JudgementType.WrongChar && rule == TypoRule.Deferred;
    }
}
