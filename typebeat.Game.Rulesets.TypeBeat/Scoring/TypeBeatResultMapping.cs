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
    /// The rule deciding what CORRECTING a typo does to the combo its wrong keypress broke. It
    /// exists for the same reason <see cref="TypoRule"/> does: a stored score has to be re-derived
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
    /// <see cref="RateWindowRule"/> is kept out of <see cref="SpaceTimingRule"/>: they are two
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
    /// <para>It reaches only rows played with the space-skip SETTING on, which is off by default, so
    /// it is the narrowest of the axes: a row that never abandoned a word is graded identically under
    /// both arms, because the branch it gates is never entered.</para>
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
        /// marked <see cref="CellState.Missed"/> and took its <see cref="HitResult.Miss"/> at the
        /// skip itself, spending the cell's one result there, so no amount of backspacing could ever
        /// earn it back.
        ///
        /// <para>Only score RECALCULATION selects this. Nothing in gameplay may.</para>
        /// </summary>
        ImmediateMiss,
    }

    /// <summary>
    /// The rule deciding whether a space typed on a space cell is part of the TIMING challenge. Same
    /// reason as <see cref="TypoRule"/> and <see cref="ComboRestoreRule"/>: a stored score has to be
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
        /// <para>Its osu result is <see cref="HitResult.Meh"/> rather than
        /// <see cref="HitResult.Miss"/>, and that single mapping change is what makes ACCURACY the
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
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/> for why a cell may only ever resolve as
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
        /// <see cref="HitResult.Miss"/>, which carried that break into osu's combo, counted against
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
        /// <see cref="TypeBeatScoreProcessor.MISTYPE_RESULT"/>, which is accuracy-inert by design
        /// (see that field), and the corrected cell's deferred result was graded purely on the
        /// RETYPE's timing, so a fast corrector paid nothing in accuracy. The cap is the one number
        /// that makes perfect play strictly beat corrected play PER CELL, and it leaves the ordering
        /// clean: clean 300 &gt; corrected at most 100 &gt; unfixed typo
        /// (<see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, re-weighted, see
        /// <see cref="TypeBeatScoreProcessor.GetBaseScoreForResult"/>) = miss 0 since backlog 213,
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
        /// recovers <see cref="TypeBeatHealthProcessor.OK_HEALTH_INCREASE"/> rather than
        /// <see cref="TypeBeatHealthProcessor.GREAT_HEALTH_INCREASE"/>, which is the same coherence
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
    /// stored row that carries one of the rate mods (DT / NC / HT), unlike
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
        /// (<c>TypeBeatPlayfield.onMistyped</c>). So the result is applied COMBO-NEUTRAL, see
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
                    return OffTimePressIsAHit(offTimeRule) ? HitResult.Meh : HitResult.Miss;

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
        /// Whether correcting a wrong cell puts back the streak its keypress broke (backlog 140,
        /// both input models, though only the default one can ever reach it: a REJECTED key writes
        /// no cell, so there is nothing to go back and fix).
        ///
        /// <para>Read in exactly one place, <see cref="TypingEngine.ProcessKey"/>, so the rule is
        /// IMPLEMENTED once and only SELECTED twice (live play takes the engine's default,
        /// <see cref="TypeBeatReplayScorer"/> sets the era's). Both the engine's own combo and osu's
        /// then follow from the one decision, which is what stops the two drifting.</para>
        /// </summary>
        public static bool FixRestoresTheComboBreak(ComboRestoreRule rule) => rule == ComboRestoreRule.OnFix;

        /// <summary>
        /// Whether a redeemable break that cost NO streak leaves an outstanding claim alone rather
        /// than taking it over (backlog 176).
        ///
        /// <para>Read in exactly one place, <c>TypingEngine.snapshotRedeemableBreak</c>, which both
        /// write sites funnel through, so the rule is IMPLEMENTED once and only SELECTED twice: live
        /// play takes the engine's default and <see cref="TypeBeatReplayScorer"/> sets the era's.
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
        /// takes the engine's default and <see cref="TypeBeatReplayScorer"/> sets the era's. The same
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
        /// <see cref="TypeBeatReplayScorer"/> sets the era's. That is the same shape
        /// <see cref="FixRestoresTheComboBreak"/> has, and for the same reason.</para>
        /// </summary>
        public static bool SpacesAreUntimed(SpaceTimingRule rule) => rule == SpaceTimingRule.Untimed;

        /// <summary>
        /// Whether a word skip leaves its cells RE-TYPEABLE rather than missing them on the spot
        /// (backlog 167).
        ///
        /// <para>Read in exactly one place, <c>TypingEngine.skipCurrentWord</c>, so the rule
        /// is IMPLEMENTED once and only SELECTED twice: live play takes the engine's default and
        /// <see cref="TypeBeatReplayScorer"/> sets the era's. The same shape as
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
        /// <see cref="CellResult"/>, which is the whole of the rule: the engine decides the combo
        /// and the mapping decides the osu result, and both have to move together or the two
        /// counters drift. Only SELECTED twice, live play taking the engine's default and
        /// <see cref="TypeBeatReplayScorer"/> setting the era's, which is the same shape
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
        /// in <see cref="CellResult"/>, and is not, for two reasons. It keeps live play, the replay
        /// scorer and the JS mirror on ONE decision: the engine judges, and everything downstream
        /// (the result the drawable applies, the tier counts, the engine's own point ladder, the HP
        /// the result carries) follows from the single tier it announces, rather than the result
        /// being capped in one place while the announcement said something else. And it keeps the
        /// ANNOUNCED judgement honest: a capped cell raises <see cref="TypingEngine.CharJudged"/>
        /// as an Ok, so the player SEES the Ok their stored score is graded on. Capping the result
        /// alone would have shown a Great and stored an Ok.</para>
        ///
        /// <para>Read in exactly two places, both of them the retype-judging arms of
        /// <see cref="TypingEngine.ProcessKey"/> (the awarded judgement, and the inert re-judgement
        /// a later correct retype of the same cell re-derives), so the rule is IMPLEMENTED once and
        /// only SELECTED twice: live play takes the engine's default and
        /// <see cref="TypeBeatReplayScorer"/> sets the era's. Same shape as
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
        /// Whether a corrected cell's judgement is capped at <see cref="JudgementType.Ok"/>
        /// (backlog 210). The predicate form of <see cref="AwardedTier"/>, for readers that want the
        /// rule rather than the mapping.
        /// </summary>
        public static bool CorrectionIsCapped(CorrectionCreditRule rule) => rule == CorrectionCreditRule.Capped;

        /// <summary>
        /// Whether an uncorrected typo is worth NOTHING in accuracy, i.e. exactly what a
        /// <see cref="SEAL_MISS"/> is worth (backlog 213).
        ///
        /// <para>Read in exactly one place,
        /// <see cref="TypeBeatScoreProcessor.GetBaseScoreForResult"/>, so the rule is IMPLEMENTED
        /// once and only SELECTED twice: live play takes the score processor's own default and
        /// <see cref="TypeBeatReplayScorer"/> sets the era's. Same shape as
        /// <see cref="FixRestoresTheComboBreak"/>, and for the same reason.</para>
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
