// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// The rule deciding what CORRECTING a typo does to the combo its wrong keypress broke. It
    /// exists for the same reason <see cref="Scoring.TypoRule"/> does: a stored score has to be re-derived
    /// under the rule it was PLAYED under rather than only under the current one. Live play is
    /// always <see cref="OnFix"/>.
    /// </summary>
    public enum ComboRestoreRule
    {
        /// <summary>
        /// The rule since backlog 140, and the only one live play uses: backspacing and correctly
        /// retyping a wrong cell RESUMES the streak that cell's wrong keypress broke (the streak it
        /// broke, plus whatever has been earned since), provided no other combo break WITH A STREAK
        /// OF ITS OWN landed in between (see <see cref="ComboClaimRule"/> for that qualifier, which
        /// is backlog 176). See <see cref="TypingEngine.ComboRestored"/> for the mechanism and for
        /// why an intervening break ends the claim.
        ///
        /// <para>It is what makes fixing a typo worth anything once typos are counted as EVENTS:
        /// the count is spent the moment the wrong key lands and no correction can take it back, so
        /// without this the only thing a fix would buy is the accuracy and completion the cell is
        /// worth, and leaving the typo sitting there would cost the player nothing extra in
        /// combo.</para>
        /// </summary>
        OnFix,

        /// <summary>
        /// The rule every score stored BEFORE backlog 140 was played under: the break the wrong
        /// keypress took is permanent, and the corrected retype starts a fresh run from zero.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        Never,
    }

    /// <summary>
    /// The rule deciding WHICH break owns the streak when a redeemable one lands while an older
    /// claim is still outstanding (see <see cref="TypingEngine.ComboRestored"/>). Kept as its own
    /// axis rather than folded into <see cref="ComboRestoreRule"/> for the reason
    /// <see cref="Scoring.RateWindowRule"/> is kept out of <see cref="SpaceTimingRule"/>: they are two
    /// independent facts (whether a fix restores at all, and who the claim belongs to), and the next
    /// change to either has no reason to move the other. Live play is always
    /// <see cref="StreakedBreakWins"/>, and the axis is inert under
    /// <see cref="ComboRestoreRule.Never"/>, where no snapshot is ever taken.
    /// </summary>
    public enum ComboClaimRule
    {
        /// <summary>
        /// The rule since backlog 176, and the only one live play uses: a break takes ownership of
        /// the streak only if it HAS a streak to own. A wrong keypress or a word skip that lands
        /// while the run is already at zero costs nothing, so it leaves the outstanding claim where
        /// it is instead of replacing it with an empty one, and correcting the older cell still
        /// resumes the run.
        ///
        /// <para>Nothing else about the claim moves: a break that really does cost a streak takes
        /// ownership exactly as before, and a zero-streak break with NOTHING outstanding still
        /// snapshots its own empty claim, so redeeming a 0 restores nothing where nothing better was
        /// pending.</para>
        /// </summary>
        StreakedBreakWins,

        /// <summary>
        /// The rule every score stored BEFORE backlog 176 was played under: the LATEST redeemable
        /// break owns the claim unconditionally, so a second wrong key (on the next cell, or on the
        /// same one) or a skip over an existing typo overwrote the snapshot with the streak at that
        /// moment, which the first break had already zeroed. Correcting both cells then restored
        /// nothing at all.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        LatestBreakWins,
    }

    /// <summary>
    /// The rule deciding whether combo credited by the very press that TOOK a claim counts as a
    /// streak that can take that claim away again (see <see cref="TypingEngine.ComboRestored"/>).
    /// ONE press can do both: a space struck inside a word abandons the rest of it (the skip, which
    /// snapshots the claim) and is then judged on the word gap it lands on, where it rebuilds the run
    /// to exactly 1. That 1 is not progress the player made after the break, it is the break's own
    /// press, so under the live rule it does not arm the next break with a streak.
    ///
    /// <para>Kept as its own axis rather than folded into <see cref="ComboClaimRule"/> for the reason
    /// that one is kept out of <see cref="ComboRestoreRule"/>: they are two independent facts
    /// (whether an EMPTY break is passive, and whether a break standing on nothing but the claim's
    /// own credit is), and a stored row has to be re-derived under the pair it was played under. Live
    /// play is always <see cref="NotAStreakOfItsOwn"/>, and the axis is inert under
    /// <see cref="ComboClaimRule.LatestBreakWins"/> and under <see cref="ComboRestoreRule.Never"/>,
    /// where no claim is passive and no claim exists at all.</para>
    ///
    /// <para>It is a NARROW axis, narrower even than <see cref="WordSkipRule"/>: only a word skip
    /// ever credits a claim's own press, so it reaches only a row that skipped a word AND broke again
    /// on the gap that skip parked it against, before earning a single character back.</para>
    /// </summary>
    public enum SkipSpaceCreditRule
    {
        /// <summary>
        /// The rule since backlog 243, and the only one live play uses: combo standing only because
        /// the claim's own press credited it is not a streak, so a break that takes nothing more than
        /// that leaves the outstanding claim exactly where it is, precisely as a break landing at
        /// zero does (<see cref="ComboClaimRule.StreakedBreakWins"/>). The credit is SPENT by that
        /// break, so the next one is measured against zero again and any character the player really
        /// does type in between arms it normally.
        ///
        /// <para>Nothing else moves: the skipping space still earns its gap cell's judgement and
        /// still rebuilds the run to 1 (that combo is real, it is only its OWNERSHIP of the claim
        /// that this denies), and a break with a streak of its own takes the claim exactly as
        /// before.</para>
        /// </summary>
        NotAStreakOfItsOwn,

        /// <summary>
        /// The rule every score stored BEFORE backlog 243 was played under: the skip's own space
        /// counted like any other correct character, so a typo on the very gap it parked the caret on
        /// broke a streak of 1, cleared backlog 176's zero test, and overwrote the deep claim the
        /// same press had just taken with a worthless one. Walking back and typing the whole word out
        /// then restored nothing.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        AStreakLikeAnyOther,
    }

    /// <summary>
    /// The rule deciding what a word skip does to the cells it gives up (see
    /// <see cref="TypingEngine.SpaceSkipsWord"/>). Same reason as the rules above: a stored score has
    /// to be re-derived under the rule it was PLAYED under. Live play is always
    /// <see cref="Reclaimable"/>.
    ///
    /// <para>It reaches only rows played with the space-skip SETTING on (the shipped default since
    /// backlog 198; it was off before that), and among those only the rows that actually abandoned a
    /// word: a row that never did is graded identically under both arms, because the branch it gates
    /// is never entered.</para>
    /// </summary>
    public enum WordSkipRule
    {
        /// <summary>
        /// The rule since backlog 167, and the only one live play uses: an abandoned cell enters
        /// <see cref="CellState.Abandoned"/> and resolves NOTHING at the skip. Backspacing into the
        /// word puts the cells back to <see cref="CellState.Untyped"/>, and re-typing them earns
        /// their ordinary judgements plus the streak the skip broke (the same snapshot machinery a
        /// corrected typo redeems, see <see cref="ComboRestoreRule.OnFix"/>). Left alone, the seal
        /// resolves them as the misses they turned out to be.
        ///
        /// <para>The skip still takes exactly ONE combo break, at the skip, and the miss count and
        /// the osu results are what moved to the seal. So a skip nobody goes back for costs precisely
        /// what it cost before: the same cells, the same one break, the same rank.</para>
        /// </summary>
        Reclaimable,

        /// <summary>
        /// The rule every score stored BEFORE backlog 167 was played under: an abandoned cell was
        /// marked <see cref="CellState.Missed"/> and took its <see cref="Rulesets.Scoring.HitResult.Miss"/> at the
        /// skip itself, spending the cell's one result there, so no amount of backspacing could ever
        /// earn it back.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        ImmediateMiss,
    }

    /// <summary>
    /// The rule deciding whether a space typed on a space cell is part of the TIMING challenge. Same
    /// reason as <see cref="Scoring.TypoRule"/> and <see cref="ComboRestoreRule"/>: a stored score has to be
    /// re-derived under the rule it was PLAYED under. Live play is always <see cref="Untimed"/>.
    ///
    /// <para>This is the axis with the widest reach of the four, because every map has spaces: a row
    /// judged under the wrong arm here differs on tier counts (a loosely hit space is Great under one
    /// and Meh, Premature or Lagging under the other), on max_combo (only the timed arm can break a
    /// streak on a space) and therefore on accuracy, rank and pp.</para>
    /// </summary>
    public enum SpaceTimingRule
    {
        /// <summary>
        /// The rule since backlog 148, and the only one live play uses: the spacebar is outside the
        /// timing challenge, so a space typed on a space cell is judged on a ZEROED delta. It takes
        /// the top tier however loosely it was hit, never breaks combo, and stays out of the sync
        /// timeline and the sync mean (the word gap is where a typist's hands reset, not a note to
        /// hit). See <see cref="TypingEngine.ProcessKey"/>, which is where it is implemented.
        /// </summary>
        Untimed,

        /// <summary>
        /// The rule every score stored BEFORE backlog 148 was played under: a space was graded on its
        /// real delta like any other character, so it could land in any tier, could break combo as a
        /// Premature or a Lagging press, and counted in both sync readouts.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        Timed,
    }

    /// <summary>
    /// The rule deciding what an OFF-TIME press costs: the RIGHT character struck outside the
    /// outermost Meh window, which <see cref="SyncWindows.Classify"/> calls
    /// <see cref="JudgementType.Premature"/> or <see cref="JudgementType.Lagging"/>. Same reason as
    /// the rules above: a stored score has to be re-derived under the rule it was PLAYED under
    /// rather than only under the current one. Live play is always <see cref="MehHit"/>.
    ///
    /// <para>Its reach is every row that ever fumbled a beat, which is nearly all of them, so it
    /// sits alongside <see cref="SpaceTimingRule"/> as one of the wide axes rather than with the
    /// narrow ones: the two arms disagree on max_combo, on the miss count, on accuracy, on
    /// completion and therefore on rank and pp.</para>
    /// </summary>
    public enum OffTimeRule
    {
        /// <summary>
        /// The rule since backlog 199, and the only one live play uses: an off-time press is a HIT.
        /// It still earns ZERO engine points (the score ladder is unchanged, and a mistimed press
        /// still pays for itself there), but it EXTENDS the combo like any other accepted character,
        /// raises no <see cref="TypingEngine.ComboBroken"/>, and leaves an outstanding restorable
        /// claim alone, because only a BREAK discards one.
        ///
        /// <para>Its osu result is <see cref="Rulesets.Scoring.HitResult.Meh"/> rather than
        /// <see cref="Rulesets.Scoring.HitResult.Miss"/>, and that single mapping change is what makes ACCURACY the
        /// punishment: Meh is the lowest weight a judged cell can take (50 of 300), so an off-time
        /// press costs the most accuracy available and nothing else. It deliberately follows from
        /// that, rather than being arranged separately, that the press stops counting as a MISS
        /// statistic, stops costing completion and rank, and takes Meh's health increase instead of
        /// the Miss drain: those are all things the result decides, and the decision is that an
        /// off-time press is a poor hit and not a dropped character.</para>
        ///
        /// <para><b>An accepted collision.</b> Meh is already the engine's widest SCORING tier, so
        /// in the submitted <c>statistics</c> blob an off-time press is indistinguishable from a
        /// press that landed just inside the Meh window. The candidate set is forced (see
        /// <see cref="Scoring.TypeBeatResultMapping.UNFIXED_TYPO"/> for why a cell may only ever resolve as
        /// one of {Miss, Meh, Ok, Good, Great}, with Good already spent on the unfixed typo), so
        /// there is no free key to keep them apart, and separating them would mean pricing the
        /// distinction somewhere. It is not priced: both are correct characters typed loosely, and
        /// the classification ladder keeps the two apart everywhere it matters live (the
        /// Premature/Lagging tiers, the sync tint, the sync readouts, the results counts).</para>
        /// </summary>
        MehHit,

        /// <summary>
        /// The rule every score stored BEFORE backlog 199 was played under: an off-time press was a
        /// BREAK. The engine zeroed the combo, discarded any outstanding restorable claim and raised
        /// <see cref="TypingEngine.ComboBroken"/>, and the cell resolved as a
        /// <see cref="Rulesets.Scoring.HitResult.Miss"/>, which carried that break into osu's combo, counted against
        /// the miss statistic and cost completion, rank and the Miss health drain, all for a
        /// character the player did type correctly.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        BreaksCombo,
    }

    /// <summary>
    /// The rule deciding what a CORRECTED typo's cell is worth: a cell that held a wrong character
    /// before it was ever judged, then earned its judgement from a correct retype. Same reason as
    /// the rules above: a stored score has to be re-derived under the rule it was PLAYED under
    /// rather than only under the current one. Live play is always <see cref="Capped"/>.
    ///
    /// <para>Its reach is every row that ever fixed a typo, so it sits with the wide axes rather
    /// than the narrow ones, but it moves LESS per row than <see cref="OffTimeRule"/> does: the two
    /// arms disagree on tier counts and therefore on accuracy and total_score, and on nothing else
    /// at all. max_combo, the miss count, completion and rank are identical under both, because a
    /// capped cell is still a hit that extends the run and still counts as typed.</para>
    /// </summary>
    public enum CorrectionCreditRule
    {
        /// <summary>
        /// The rule since backlog 210, and the only one live play uses: a cell that held a wrong
        /// character before its first judgement resolves at min(the retype's own tier, Ok). A
        /// perfectly timed fix takes <see cref="JudgementType.Ok"/> instead of
        /// <see cref="JudgementType.Great"/>; an Ok-timed one is already there; a Meh-timed one,
        /// and an off-time one, stay exactly where they were, because they are already below the
        /// cap.
        ///
        /// <para><b>What it is for.</b> Before it, a word typed wrong and fixed could score
        /// bit-identically to a word typed right: the mistype travels as
        /// <see cref="Scoring.TypeBeatScoreProcessor.MISTYPE_RESULT"/>, which is accuracy-inert by design
        /// (see that field), and the corrected cell's deferred result was graded purely on the
        /// RETYPE's timing, so a fast corrector paid nothing in accuracy. The cap is the one number
        /// that makes perfect play strictly beat corrected play PER CELL, and it leaves the ordering
        /// clean: clean 300 &gt; corrected at most 100 &gt; unfixed typo
        /// (<see cref="Scoring.TypeBeatResultMapping.UNFIXED_TYPO"/>, re-weighted, see
        /// <see cref="Scoring.TypeBeatScoreProcessor.GetBaseScoreForResult"/>) = miss 0 since backlog 213,
        /// and 50 &gt; 0 before it.</para>
        ///
        /// <para><b>A state, not a counter.</b> The cell carries ONE flag
        /// (<see cref="TypingCell.HeldWrongBeforeJudged"/>), so wrong-fix-wrong-fix cycles cap once
        /// and can never demote below the min() rule however many times the player goes round.</para>
        ///
        /// <para>What it deliberately does NOT touch: the combo restore a fix earns
        /// (<see cref="ComboRestoreRule"/>, which runs before the press is judged at all), the
        /// unfixed typo's seal path, completion (Ok counts as typed exactly as Great does) and the
        /// pp formula. HEALTH follows the result, as it does for every other cell: a capped fix
        /// recovers <see cref="Scoring.TypeBeatHealthProcessor.OK_HEALTH_INCREASE"/> rather than
        /// <see cref="Scoring.TypeBeatHealthProcessor.GREAT_HEALTH_INCREASE"/>, which is the same coherence
        /// <see cref="OffTimeRule.MehHit"/> relies on (the result is the decision, and HP reads the
        /// result) and not a rule of its own.</para>
        /// </summary>
        Capped,

        /// <summary>
        /// The rule every score stored BEFORE backlog 210 was played under: a corrected cell was
        /// graded on the retype's own timing and nothing else, so a fix struck inside the Great
        /// window was worth a full 300 and the typo cost the play no accuracy at all.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        Full,
    }

    /// <summary>
    /// The predicates that decide each era rule the engine reads, one per rule. They live beside
    /// the engine rather than beside the score mapping so the gameplay core depends on no other
    /// part of the ruleset; <see cref="Scoring.TypeBeatResultMapping"/> and
    /// <see cref="Scoring.TypeBeatReplayScorer"/> read them from here.
    /// </summary>
    public static class EraRules
    {
        /// <summary>
        /// Whether correcting a wrong cell puts back the streak its keypress broke (backlog 140,
        /// both input models, though only the default one can ever reach it: a REJECTED key writes
        /// no cell, so there is nothing to go back and fix).
        ///
        /// <para>Read in exactly one place, <see cref="TypingEngine.ProcessKey"/>, so the rule is
        /// IMPLEMENTED once and only SELECTED twice (live play takes the engine's default,
        /// <see cref="Scoring.TypeBeatReplayScorer"/> sets the era's). Both the engine's own combo and osu's
        /// then follow from the one decision, which is what stops the two drifting.</para>
        /// </summary>
        public static bool FixRestoresTheComboBreak(ComboRestoreRule rule) => rule == ComboRestoreRule.OnFix;

        /// <summary>
        /// Whether a redeemable break that cost NO streak leaves an outstanding claim alone rather
        /// than taking it over (backlog 176).
        ///
        /// <para>Read in exactly one place, <c>TypingEngine.snapshotRedeemableBreak</c>, which both
        /// write sites funnel through, so the rule is IMPLEMENTED once and only SELECTED twice: live
        /// play takes the engine's default and <see cref="Scoring.TypeBeatReplayScorer"/> sets the era's.
        /// The same shape as <see cref="FixRestoresTheComboBreak"/>, and for the same reason.</para>
        /// </summary>
        public static bool OnlyABreakWithAStreakTakesTheClaim(ComboClaimRule rule) => rule == ComboClaimRule.StreakedBreakWins;

        /// <summary>
        /// Whether combo credited by the claim's OWN press (the space that skipped the word, judged
        /// on the gap it landed on) is excluded from the streak the next break is measured by
        /// (backlog 243).
        ///
        /// <para>Read in exactly one place, <c>TypingEngine.snapshotRedeemableBreak</c>, the same one
        /// site <see cref="OnlyABreakWithAStreakTakesTheClaim"/> is read in and which both write
        /// sites funnel through, so the rule is IMPLEMENTED once and only SELECTED twice: live play
        /// takes the engine's default and <see cref="Scoring.TypeBeatReplayScorer"/> sets the era's. The same
        /// shape as <see cref="FixRestoresTheComboBreak"/>, and for the same reason.</para>
        ///
        /// <para>It needs no CONFIG frame bit, exactly as <see cref="ComboClaimRule"/> and
        /// <see cref="OffTimeRule"/> do not: who owns a claim never moves the caret, so a stored
        /// replay's keystream is coherent under either arm and the arm can be selected from
        /// outside.</para>
        /// </summary>
        public static bool TheClaimsOwnCreditIsNotAStreak(SkipSpaceCreditRule rule) => rule == SkipSpaceCreditRule.NotAStreakOfItsOwn;

        /// <summary>
        /// Whether a space typed on a space cell is judged on a zeroed delta rather than on the clock
        /// (backlog 148).
        ///
        /// <para>Read in exactly two places, both inside <see cref="TypingEngine"/> (the keypress
        /// itself, and the sync readouts' cell filter), so the rule is IMPLEMENTED once and only
        /// SELECTED twice: live play takes the engine's default and
        /// <see cref="Scoring.TypeBeatReplayScorer"/> sets the era's. That is the same shape
        /// <see cref="FixRestoresTheComboBreak"/> has, and for the same reason.</para>
        /// </summary>
        public static bool SpacesAreUntimed(SpaceTimingRule rule) => rule == SpaceTimingRule.Untimed;

        /// <summary>
        /// Whether a word skip leaves its cells RE-TYPEABLE rather than missing them on the spot
        /// (backlog 167).
        ///
        /// <para>Read in exactly one place, <c>TypingEngine.skipCurrentWord</c>, so the rule
        /// is IMPLEMENTED once and only SELECTED twice: live play takes the engine's default and
        /// <see cref="Scoring.TypeBeatReplayScorer"/> sets the era's. The same shape as
        /// <see cref="FixRestoresTheComboBreak"/> and <see cref="SpacesAreUntimed"/>, and for the
        /// same reason. Everything downstream (the backspace's transparent step-back, the seal's
        /// resolution, the phantom state itself) is unreachable under the pre-167 arm without a
        /// switch of its own, because no cell ever enters <see cref="CellState.Abandoned"/>.</para>
        /// </summary>
        public static bool SkippedWordIsReclaimable(WordSkipRule rule) => rule == WordSkipRule.Reclaimable;

        /// <summary>
        /// Whether an off-time press (the right character, outside the outermost Meh window) is a
        /// HIT that extends the run rather than a break that ends it (backlog 199).
        ///
        /// <para>Read in exactly two places, the engine's one keypress arm and
        /// <see cref="Scoring.TypeBeatResultMapping.CellResult"/>, which is the whole of the rule: the engine decides the combo
        /// and the mapping decides the osu result, and both have to move together or the two
        /// counters drift. Only SELECTED twice, live play taking the engine's default and
        /// <see cref="Scoring.TypeBeatReplayScorer"/> setting the era's, which is the same shape
        /// <see cref="FixRestoresTheComboBreak"/> has and for the same reason.</para>
        ///
        /// <para>It needs no CONFIG frame bit, unlike the judgement-rule and input-model eras: combo
        /// policy never moves the caret, so a stored replay's keystream is coherent under either arm
        /// and the arm can be selected from outside. That is exactly how
        /// <see cref="ComboRestoreRule"/>, <see cref="ComboClaimRule"/> and
        /// <see cref="WordSkipRule"/> work.</para>
        /// </summary>
        public static bool OffTimePressIsAHit(OffTimeRule rule) => rule == OffTimeRule.MehHit;

        /// <summary>
        /// The tier a correct keypress is AWARDED, given the tier the clock classified it as
        /// (<paramref name="type"/>) and whether its cell held a wrong character before it was ever
        /// judged (<paramref name="heldWrongBeforeJudged"/>,
        /// <see cref="TypingCell.HeldWrongBeforeJudged"/>). Under
        /// <see cref="CorrectionCreditRule.Capped"/>, the live rule, a corrected cell takes
        /// min(<paramref name="type"/>, <see cref="JudgementType.Ok"/>): backlog 210, so that
        /// perfect play strictly beats corrected play per cell.
        ///
        /// <para><b>Only Great moves</b>, and that is the whole of the min(): of the tiers a correct
        /// press can be classified as, Great is the only one ABOVE Ok. Meh is already below the cap,
        /// and <see cref="JudgementType.Premature"/> / <see cref="JudgementType.Lagging"/> are
        /// off the ladder entirely (they resolve as a Meh or a Miss per
        /// <see cref="OffTimeRule"/>), so a capped cell struck off-time is untouched here and that
        /// axis keeps its own answer. Written as a min over the ladder rather than as
        /// "Great becomes Ok" because the ladder is what the rule is about, and a fourth tier would
        /// have to be placed against the cap deliberately.</para>
        ///
        /// <para><b>Why the tier and not the osu result.</b> The cap could equally have been applied
        /// in <see cref="Scoring.TypeBeatResultMapping.CellResult"/>, and is not, for two reasons. It keeps live play, the replay
        /// scorer and the JS mirror on ONE decision: the engine judges, and everything downstream
        /// (the result the drawable applies, the tier counts, the HP the result carries) follows from the single tier it announces, rather than the result
        /// being capped in one place while the announcement said something else. And it keeps the
        /// ANNOUNCED judgement honest: a capped cell raises <see cref="TypingEngine.CharJudged"/>
        /// as an Ok, so the player SEES the Ok their stored score is graded on. Capping the result
        /// alone would have shown a Great and stored an Ok.</para>
        ///
        /// <para>Read in exactly two places, both of them the retype-judging arms of
        /// <see cref="TypingEngine.ProcessKey"/> (the awarded judgement, and the inert re-judgement
        /// a later correct retype of the same cell re-derives), so the rule is IMPLEMENTED once and
        /// only SELECTED twice: live play takes the engine's default and
        /// <see cref="Scoring.TypeBeatReplayScorer"/> sets the era's. Same shape as
        /// <see cref="FixRestoresTheComboBreak"/>, and for the same reason.</para>
        ///
        /// <para>It needs no CONFIG frame bit, exactly as <see cref="OffTimeRule"/> does not: the
        /// cap never moves the caret, so a stored replay's keystream is coherent under either arm
        /// and the arm can be selected from outside.</para>
        /// </summary>
        public static JudgementType AwardedTier(JudgementType type, bool heldWrongBeforeJudged, CorrectionCreditRule rule)
        {
            if (!heldWrongBeforeJudged || rule != CorrectionCreditRule.Capped)
                return type;

            return type == JudgementType.Great ? JudgementType.Ok : type;
        }

        /// <summary>
        /// The tier a press is AWARDED when it lands with the caret out past the rush cap under
        /// <see cref="TypingEngine.RushCapCostsAccuracy"/> (backlog 347): the lowest hit tier,
        /// <see cref="JudgementType.Meh"/>, whatever the clock said. That is the whole of what such a
        /// press costs now; it credits combo like any other hit.
        ///
        /// <para>A min over the ladder, on the same terms as <see cref="AwardedTier"/>: Great and Ok
        /// move down to Meh, Meh stays, and <see cref="JudgementType.Premature"/> /
        /// <see cref="JudgementType.Lagging"/> are left alone. Those are already off the ladder (0
        /// points, and an osu Meh under <see cref="OffTimeRule.MehHit"/>), so lifting one to Meh
        /// would PAY a press for being both mistimed and out past the cap. The penalty never
        /// upgrades.</para>
        ///
        /// <para>Applied after <see cref="AwardedTier"/>, so a corrected cell that is also over the
        /// cap takes the lower of the two caps, which is this one. The delta is never touched: the
        /// sync readouts and the timeline still see the press the player really made.</para>
        /// </summary>
        public static JudgementType RushCapTier(JudgementType type)
            => type is JudgementType.Great or JudgementType.Ok ? JudgementType.Meh : type;

        /// <summary>
        /// Whether a corrected cell's judgement is capped at <see cref="JudgementType.Ok"/>
        /// (backlog 210). The predicate form of <see cref="AwardedTier"/>, for readers that want the
        /// rule rather than the mapping.
        /// </summary>
        public static bool CorrectionIsCapped(CorrectionCreditRule rule) => rule == CorrectionCreditRule.Capped;
    }
}
