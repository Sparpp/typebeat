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
        /// nothing. Correct the cell and the retype earns its real Great/Ok/Meh; leave it and the
        /// seal resolves it as <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>. The combo break the
        /// keypress costs rides on <see cref="TypingEngine.Mistyped"/>, because no result exists to
        /// carry it.
        ///
        /// <para>Backlog 124 decided what the seal resolves it AS, and that is the second half of
        /// this rule: a typo is not a MISS. A miss is a cell the player never finished, a typo is a
        /// cell they finished wrongly, and the two say different things about the play, which is
        /// why pp prices them separately. So the uncorrected typo takes the worst HIT tier
        /// (<see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>) rather than a Miss.</para>
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
        /// The result a cell the play never finished takes: nobody typed it, and the line ran out
        /// of time on it. See <see cref="UnresolvedCellResult"/> for the cell that WAS finished,
        /// wrongly.
        /// </summary>
        public const HitResult SEAL_MISS = HitResult.Miss;

        /// <summary>
        /// The result a typed-through wrong character nobody corrected takes at the seal
        /// (backlog 124). A MISS says the player was too slow to finish the character at all; a
        /// typo says they finished it and got it wrong. Those are different facts about a play, so
        /// they must not arrive as the same result.
        ///
        /// <para><b>Why Meh, and why the choice is forced.</b> Three things have to stay true of
        /// the cell, and together they leave exactly one candidate:</para>
        /// <list type="bullet">
        /// <item>It must still be JUDGED: <see cref="HitResultExtensions.AffectsAccuracy"/> has to
        /// be true, or the cell drops out of <c>TypeBeatScoreProcessor.ComputeCompletion</c>'s
        /// DENOMINATOR and a line typed entirely as typos would judge nothing, compute completion 1
        /// and hand out an X for free.</item>
        /// <item>It must count as TYPED: <see cref="HitResultExtensions.IsHit"/> has to be true, or
        /// the cell costs completion exactly as a miss does and nothing has changed.</item>
        /// <item>It must still be a NOTE: it has to be in
        /// <see cref="PerformancePoints.NOTE_RESULTS"/> (great/ok/meh/miss), or pp's length term
        /// and combo ratio inflate over a shorter map than the player played.</item>
        /// </list>
        /// <para>The intersection of "is a hit" and "is a note" is {Great, Ok, Meh}, and Meh is its
        /// floor: 50 base score against the cell's 300 maximum, so a typo pays the most accuracy a
        /// counted, typed cell can pay. Nothing outside that set works:
        /// <see cref="HitResult.IgnoreHit"/> is not accuracy-affecting, the tick results are not
        /// notes, and <see cref="HitResult.ComboBreak"/> is neither.</para>
        ///
        /// <para>This is what STRICT mode has always done, arrived at from the other side: a
        /// Gatekeeper-rejected key never cost the cell anything either, because the player still
        /// had to type the right character afterwards. The cost of getting a character wrong has
        /// always been the mistype count plus the combo break, and it stays exactly that.</para>
        ///
        /// <para>COMBO is the one thing this tier gets wrong on its own, and the caller fixes it:
        /// a Meh <see cref="HitResultExtensions.IncreasesCombo"/>, and the cell's combo consequence
        /// was already taken at the keypress (<c>TypeBeatPlayfield.onMistyped</c>). So the result
        /// is applied COMBO-NEUTRAL, see
        /// <see cref="TypeBeatScoreProcessor.MarkComboNeutral"/>.</para>
        /// </summary>
        public const HitResult UNFIXED_TYPO = HitResult.Meh;

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
        /// two it turns out to be. It is decided by <see cref="UnresolvedCellResult"/> at the seal.
        /// Under <see cref="TypoRule.ImmediateMiss"/> it is spent on a Miss straight away, which is
        /// what made the two indistinguishable AND unrecoverable, and is exactly what every stored
        /// score was priced under.</para>
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
        /// The result a cell takes when the LINE decides its fate instead of a keypress: the seal
        /// reaches a cell that has resolved nothing. Two cases, and backlog 124 is that they are
        /// two:
        ///
        /// <list type="bullet">
        /// <item><paramref name="leftWrong"/>: the player typed the character and got it wrong, and
        /// never went back for it. <see cref="UNFIXED_TYPO"/>, a hit.</item>
        /// <item>otherwise: nobody ever put anything in the cell, so the line ran out of time on it.
        /// <see cref="SEAL_MISS"/>, and it costs completion and rank exactly as it always has.</item>
        /// </list>
        ///
        /// <para>Deliberately keyed on the cell's CURRENT state rather than on "did a wrong key ever
        /// land here": a typo that was backspaced away and then left empty is a cell the player did
        /// NOT finish, and it must read as the miss it is.</para>
        ///
        /// <para><see cref="TypoRule.ImmediateMiss"/> answers Miss to both, which is what it must
        /// do: under that rule the wrong char already spent the cell's one result at the keypress,
        /// so a still-wrong cell is already judged and the seal's result is dropped anyway. Saying
        /// Miss keeps the pre-109 arm reproducible whatever the caller hands it.</para>
        /// </summary>
        public static HitResult UnresolvedCellResult(bool leftWrong, TypoRule rule)
            => leftWrong && rule == TypoRule.Deferred ? UNFIXED_TYPO : SEAL_MISS;
    }
}
