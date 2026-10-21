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
        /// why pp prices them separately. So the uncorrected typo takes a key of its OWN
        /// (<see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>) rather than a Miss. Backlog 126 is
        /// which key, and what it costs: keeping the two distinguishable in <c>statistics</c> is
        /// what lets pp keep pricing a typo as a typo while COMPLETION treats it as the unfinished
        /// cell it is.</para>
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
    /// The rule deciding what an UNCORRECTED typo's cell is WORTH: a cell the player finished with
    /// the wrong character and never went back for, which the seal resolves as
    /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>. Same reason as the rules above: a stored
    /// score has to be re-derived under the rule it was PLAYED under rather than only under the
    /// current one. Live play is always <see cref="Nothing"/>.
    ///
    /// <para>It moves exactly what <see cref="CorrectionCreditRule"/> moves and no more: the
    /// accuracy weight of those cells, and therefore <c>total_score</c>. <c>statistics</c> itself is
    /// IDENTICAL under both arms (the seal still writes the same key, which is the whole design of
    /// backlog 213), and so are <c>max_combo</c>, the miss count as it is STORED, completion and
    /// rank.</para>
    /// </summary>
    public enum UnfixedTypoWorthRule
    {
        /// <summary>
        /// The rule since backlog 213, and the only one live play uses: an uncorrected typo is
        /// worth 0 of its cell's 300, the same as a <see cref="HitResult.Miss"/>. It is the ACCURACY
        /// half of folding the uncorrected typo into the miss, and the fold's whole shape is that
        /// the WIRE does not move and every CONSUMER reclassifies (the same pattern backlog 140 used
        /// for the mistype's <c>combo_break</c> key): the seal keeps writing
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, so old rows stay comparable and the
        /// typo-versus-timeout distinction survives in the data, while accuracy, pp and every
        /// display read it as the miss it is.
        ///
        /// <para>The cell's MAXIMUM result is still <see cref="HitResult.Great"/>, so the accuracy
        /// DENOMINATOR does not move. That is what makes accuracy genuinely drop rather than the
        /// cell quietly leaving the fraction.</para>
        ///
        /// <para>What this deliberately does NOT touch, because each was already right: COMPLETION
        /// counts an uncorrected typo as untyped (backlog 126), so rank is untouched here and falls
        /// only through the accuracy-derived surfaces; HEALTH already drains it as a miss
        /// (backlog 125); and the combo-restore mechanics are untouched, though the incentive
        /// sharpens, since fixing a typo now recovers a full miss's worth of accuracy rather than
        /// 250 of 300.</para>
        /// </summary>
        Nothing,

        /// <summary>
        /// The rule every score stored BEFORE backlog 213 was played under: an uncorrected typo was
        /// re-weighted to <see cref="HitResult.Meh"/>'s 50 of 300, the cheapest a JUDGED cell could
        /// be, on the reading that a cell the player reached and finished is not the same as one the
        /// line ran out of time on. That reading survives in the key; only the price moved.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        MehCredit,
    }

    /// <summary>
    /// The rule deciding whether a rate-adjusting mod scales the judgement windows. Same reason as
    /// the three above. Live play is always <see cref="ScaledByRate"/>, and the axis only reaches a
    /// stored row that carries one of the rate mods (DT / NC / HT / DC), unlike
    /// <see cref="SpaceTimingRule"/>, which reaches every row there is.
    ///
    /// <para>Kept as its own rule rather than folded into <see cref="SpaceTimingRule"/> even though
    /// backlog 148 and 150 ship together and no stored row can separate them: they are two
    /// independent facts about how a press was graded, and the next change to either has no reason to
    /// move the other. It is read by <see cref="TypeBeatReplayScorer"/> alone, which is the only
    /// caller that can build an engine for a run it did not judge.</para>
    /// </summary>
    public enum RateWindowRule
    {
        /// <summary>
        /// The rule since backlog 150, and the only one live play uses: the windows are multiplied by
        /// the clock rate, so the tolerance in REAL time is the same at every rate. Double Time
        /// therefore does not tighten the timing challenge and Half Time does not loosen it.
        /// </summary>
        ScaledByRate,

        /// <summary>
        /// The rule every score stored BEFORE backlog 150 was played under: the windows were fixed in
        /// BEATMAP milliseconds whatever the rate, so a Double Time run had 1/1.5 of the real-time
        /// tolerance an unmodded one had, and a Half Time run had 1/0.75 of it.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        Unscaled,
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
        /// (backlog 124, re-keyed by backlog 126). A MISS says the player was too slow to finish the
        /// character at all; a typo says they finished it and got it wrong. Those are different
        /// facts about a play, so they must not arrive as the same result.
        ///
        /// <para><b>Why the key has to be its own, and why it is Good.</b> Backlog 124 spent
        /// <see cref="HitResult.Meh"/> on this, which made an unfixed typo indistinguishable in
        /// <c>statistics</c> from a slow-but-CORRECT keypress (the engine's widest scoring tier
        /// resolves as Meh). The server
        /// sees nothing but that dictionary, so with the two sharing a key no consumer could ever
        /// price them differently, and the typo counted as a TYPED cell: a run typed entirely wrong
        /// read completion 1 and took an X. Making it cost completion therefore requires a key
        /// nothing else uses.</para>
        ///
        /// <para>The candidate set is not a preference, it is forced.
        /// <c>DrawableHitObject.ApplyResult</c> refuses any result outside
        /// <see cref="HitResultExtensions.IsValidHitResult"/> for the cell's judgement, and
        /// <see cref="HitResultExtensions.ValidateHitResultPair"/> forces MinResult to be
        /// <see cref="HitResult.Miss"/> for the Great-max
        /// <see cref="Judgements.TypeBeatCharJudgement"/>. So a cell may only ever resolve as one of
        /// {Miss, Meh, Ok, Good, Great}. Great/Ok/Meh are the engine's three quality tiers and Miss
        /// is <see cref="SEAL_MISS"/>, which leaves exactly one free slot,
        /// <see cref="HitResult.Good"/>. No tick, bonus or ignore result is reachable at all, so
        /// there is no non-hit alternative to weigh up.</para>
        ///
        /// <para>Backlog 133 raised the ceiling to <see cref="HitResult.Perfect"/> for a fourth
        /// quality tier and backlog 147 took both back out, so the accounting above is once again
        /// exactly as tight as it reads: five reachable results, four of them spoken for.</para>
        ///
        /// <para><b>What that key does, and what has to be adapted round it.</b> Good is a basic,
        /// accuracy-affecting, combo-affecting HIT, so out of the box it lands in
        /// <c>TypeBeatScoreProcessor.ComputeCompletion</c>'s denominator (wanted: pp's length term
        /// and the combo ratio still measure the map the player played) and in
        /// <see cref="PerformancePoints.NOTE_RESULTS"/> as a note that is not a miss (wanted: pp
        /// keeps pricing a typo by the mistype term, never by the miss term). Three things it gets
        /// wrong on its own, each fixed at the one place that owns it:</para>
        /// <list type="bullet">
        /// <item>It is a hit, so it would count as a TYPED cell. Completion excludes it by name
        /// (<c>TypeBeatScoreProcessor.ComputeCompletion</c>), which is the whole of backlog 126: an
        /// unfixed typo now costs completion, and therefore rank, exactly as a miss does.</item>
        /// <item>It <see cref="HitResultExtensions.IncreasesCombo"/>, and the cell's combo
        /// consequence was already taken at the keypress
        /// (<c>TypeBeatComboFeed.OnMistyped</c>). So the result is applied COMBO-NEUTRAL, see
        /// <see cref="TypeBeatScoreProcessor.MarkComboNeutral"/>.</item>
        /// <item>Its stock base score is 200 of the cell's 300, which would make a typo cost LESS
        /// accuracy than a correct-but-late character.
        /// <see cref="TypeBeatScoreProcessor.GetBaseScoreForResult"/> re-weights it, to 50 (the Meh
        /// value) until backlog 213 and to 0 (the Miss value) since, under
        /// <see cref="UnfixedTypoWorthRule"/>. The MAXIMUM result stays Great either way, so the
        /// accuracy denominator never moves and the re-weight is paid in full.</item>
        /// </list>
        ///
        /// <para>HEALTH is the fourth (backlog 125): a stock Good RECOVERS health, so a run typed
        /// entirely wrong could not die. <see cref="TypeBeatHealthProcessor"/> drains
        /// <see cref="TypeBeatHealthProcessor.MISS_HEALTH_DRAIN"/> for it instead.</para>
        /// </summary>
        public const HitResult UNFIXED_TYPO = HitResult.Good;

        /// <summary>
        /// The line container's own result. Scoring-inert, so osu accuracy tracks only the cells.
        /// </summary>
        public const HitResult LINE_RESULT = HitResult.IgnoreHit;

        /// <summary>
        /// The osu result an engine char judgement resolves its cell with, or null when the cell's
        /// result is DEFERRED and nothing at all is applied.
        ///
        /// <para>Mapping: the three QUALITY tiers are the IDENTITY (Great, Ok and Meh each resolve
        /// as the osu result they are named for), Miss-&gt;Miss, and Premature/Lagging follow
        /// <paramref name="offTimeRule"/>. Under the live <see cref="OffTimeRule.MehHit"/> an
        /// off-time press resolves as <see cref="HitResult.Meh"/>, which is behaviour-coherent for
        /// combo: the engine now EXTENDS the run on such a press and a Meh extends osu's, so
        /// nothing is mirrored by hand and nothing double-counts. Under
        /// <see cref="OffTimeRule.BreaksCombo"/>, the pre-199 rule, it resolves as
        /// <see cref="HitResult.Miss"/>, which is coherent the other way: the engine breaks and so
        /// does the Miss.</para>
        ///
        /// <para><b>The collision this accepts (backlog 199).</b> Under the live rule an off-time
        /// press and a press that landed just INSIDE the Meh window arrive at the score processor as
        /// the same result, so no consumer of the <c>statistics</c> blob can tell them apart. That is
        /// the price of making accuracy the punishment: the candidate set is forced (see
        /// <see cref="UNFIXED_TYPO"/>), Good is spent on the unfixed typo, and Meh is the only key
        /// left whose weight says "a correct character, typed as loosely as a judged cell can be".
        /// The distinction survives everywhere it is actually used, which is live and on the results
        /// screen: <see cref="JudgementType.Premature"/> and <see cref="JudgementType.Lagging"/> are
        /// still their own tiers, still counted separately, still tinted separately.</para>
        ///
        /// <para>Miss reaches here only from a word abandoned by the space-skip setting under
        /// <see cref="WordSkipRule.ImmediateMiss"/>, the pre-167 rule, which spent the cell's one
        /// result at the skip instead of leaving it to the seal.</para>
        ///
        /// <para>Abandoned joins WrongChar on the DEFERRED side (backlog 167), and for the same
        /// reason it is deferred: the cell is still re-typeable, and a cell takes only its first
        /// result, so applying one now is exactly what would make earning it back impossible. Under
        /// the live rule an abandoned cell reaches the score processor either as its retype's own
        /// Great/Ok/Meh or as a <see cref="SEAL_MISS"/> at the seal, never here.</para>
        ///
        /// <para>WrongChar is the one that moved (backlog 109). A miss is a character the line ran
        /// out of time on; a typo is a typo, and in the default input model the player can still
        /// backspace and type the cell correctly, so the cell's one result waits to see which of the
        /// two it turns out to be. It is decided by <see cref="UnresolvedCellResult"/> at the seal.
        /// Under <see cref="TypoRule.ImmediateMiss"/> it is spent on a Miss straight away, which is
        /// what made the two indistinguishable AND unrecoverable, and is exactly what every stored
        /// score was priced under.</para>
        /// </summary>
        public static HitResult? CellResult(JudgementType type, TypoRule rule, OffTimeRule offTimeRule = OffTimeRule.MehHit)
        {
            switch (type)
            {
                case JudgementType.Great:
                    return HitResult.Great;

                case JudgementType.Ok:
                    return HitResult.Ok;

                case JudgementType.Meh:
                    return HitResult.Meh;

                case JudgementType.WrongChar:
                    return rule == TypoRule.Deferred ? null : HitResult.Miss;

                case JudgementType.Abandoned:
                    return null;

                case JudgementType.Premature:
                case JudgementType.Lagging:
                    return EraRules.OffTimePressIsAHit(offTimeRule) ? HitResult.Meh : HitResult.Miss;

                default:
                    // Miss.
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
        /// Whether an uncorrected typo is worth NOTHING in accuracy, i.e. exactly what a
        /// <see cref="SEAL_MISS"/> is worth (backlog 213).
        ///
        /// <para>Read in exactly one place,
        /// <see cref="TypeBeatScoreProcessor.GetBaseScoreForResult"/>, so the rule is IMPLEMENTED
        /// once and only SELECTED twice: live play takes the score processor's own default and
        /// <see cref="TypeBeatReplayScorer"/> sets the era's. Same shape as
        /// <see cref="EraRules.FixRestoresTheComboBreak"/>, and for the same reason.</para>
        ///
        /// <para>It needs no CONFIG frame bit, exactly as <see cref="OffTimeRule"/> and
        /// <see cref="CorrectionCreditRule"/> do not: an accuracy weight never moves the caret, so a
        /// stored replay's keystream is coherent under either arm and the arm can be selected from
        /// outside.</para>
        /// </summary>
        public static bool UnfixedTypoIsWorthNothing(UnfixedTypoWorthRule rule) => rule == UnfixedTypoWorthRule.Nothing;

        /// <summary>
        /// Whether a rate-adjusting mod multiplies the judgement windows by its clock rate
        /// (backlog 150). Read by <see cref="TypeBeatReplayScorer"/> only: live play applies the
        /// scale in <c>DrawableTypeBeatRuleset.createEngine</c>, which has no era to express.
        /// </summary>
        public static bool RateScalesTheWindows(RateWindowRule rule) => rule == RateWindowRule.ScaledByRate;

        /// <summary>
        /// The result a cell takes when the LINE decides its fate instead of a keypress: the seal
        /// reaches a cell that has resolved nothing. Two cases, and backlog 124 is that they are
        /// two:
        ///
        /// <list type="bullet">
        /// <item><paramref name="leftWrong"/>: the player typed the character and got it wrong, and
        /// never went back for it. <see cref="UNFIXED_TYPO"/>, its own key.</item>
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
        ///
        /// <para><b>Backlog 213 does not move this line, and that is the design.</b> The fold of the
        /// uncorrected typo into the miss is a CONSUMER-side reclassification
        /// (<see cref="UnfixedTypoWorthRule"/>, <c>PerformancePoints.CountNotes</c>,
        /// <see cref="TypeBeatRuleset.GetDisplayResultFor"/>): the seal keeps writing
        /// <see cref="UNFIXED_TYPO"/>, so no wire, realm or MessagePack shape changes, stored rows
        /// stay comparable with new ones, and the typo-versus-timeout distinction survives in the
        /// data even though nothing prices it apart any more.</para>
        ///
        /// <para><b>The residue that fold accepts.</b> A typo the player DID correct leaves no trace
        /// in the result columns at all: its cell resolves as the retype's own (capped) tier, so the
        /// only record of it is the <c>combo_break</c> mistype count and the streak it broke
        /// mid-play. The alternative was a typos tile of its own beside the counts, and the fold was
        /// chosen over it: one flub is priced once, and the columns then sum to the judged cell
        /// count.</para>
        /// </summary>
        public static HitResult UnresolvedCellResult(bool leftWrong, TypoRule rule)
            => leftWrong && rule == TypoRule.Deferred ? UNFIXED_TYPO : SEAL_MISS;
    }
}
