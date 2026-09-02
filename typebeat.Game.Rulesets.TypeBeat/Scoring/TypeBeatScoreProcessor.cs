// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Scoring
{
    /// <summary>
    /// type!beat scoring. Total score, combo and ACCURACY are the standardised defaults, but the
    /// RANK is derived from <b>completion</b>: the fraction of typeable cells the player actually
    /// typed (any non-miss judgement), instead of accuracy. Typing every character earns an SS even
    /// with wrong-key stumbles and sloppy timing along the way, as long as the stumbles get fixed;
    /// timing quality still shows in accuracy, score and combo, it just no longer gates the grade.
    /// A cell only costs rank when the play did not TYPE IT RIGHT: either nobody typed it and the
    /// line ran out of time (a Miss), or it was typed wrong and left that way
    /// (<see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>). Backlog 124 gave the second case a result
    /// of its own so that pp could stop pricing it as a miss; backlog 126 is the other half of that,
    /// and it is the user's rule: DO NOT COUNT A TYPO IN COMPLETION. So an unfixed typo sits in
    /// completion's DENOMINATOR but not its numerator (see <see cref="CountsAsTyped"/>), and it, and
    /// therefore rank, falls exactly as far as it would for a miss. Backlog 213 finishes that arc:
    /// an uncorrected typo IS a miss to every consumer now, so it is worth 0 in accuracy
    /// (<see cref="GetBaseScoreForResult"/>), it counts in pp's MISS term instead of its typo term
    /// (<c>PerformancePoints.CountNotes</c>), and it is SHOWN in the miss column
    /// (<c>TypeBeatRuleset.GetDisplayResultFor</c>). What does NOT move is the WIRE: the seal still
    /// writes the typo's own key, so old rows stay comparable with new ones and the data keeps the
    /// distinction even though nothing prices it any more. It is applied COMBO-NEUTRAL (see
    /// <see cref="MarkComboNeutral"/>) because the break it owed was taken at the keypress.
    ///
    /// The server mirrors this exactly (typebeat-web ScoringContract.RankFromCompletion); keep
    /// the cutoffs in the two files in sync.
    ///
    /// <para>Wrong keypresses are counted SEPARATELY as MISTYPES (<see cref="MISTYPE_RESULT"/>,
    /// <see cref="RecordMistype"/>) and change none of the above: accuracy stays the timing quality
    /// of the cells that were typed, completion/rank stay cells-typed over total cells, so an old
    /// score and a new one mean the same thing and an SS is still reachable after a stumble. The
    /// count exists so mistyping is visible at all (it used to leave no trace but a broken combo)
    /// and so the server can price it in pp.</para>
    /// </summary>
    public partial class TypeBeatScoreProcessor : ScoreProcessor
    {
        // Completion → rank cutoffs. Same band shape as the base game's accuracy cutoffs so the
        // grades keep their familiar feel; X strictly requires every cell typed.
        public const double COMPLETION_CUTOFF_X = 1;
        public const double COMPLETION_CUTOFF_S = 0.95;
        public const double COMPLETION_CUTOFF_A = 0.9;
        public const double COMPLETION_CUTOFF_B = 0.8;
        public const double COMPLETION_CUTOFF_C = 0.7;

        /// <summary>
        /// The result key the MISTYPE stat (wrong keypresses) is persisted under, in the ordinary
        /// <c>statistics</c> dictionary and therefore on the wire as <c>"combo_break"</c>.
        ///
        /// <para><see cref="HitResult.ComboBreak"/> is the base game's purpose-built "breaks combo,
        /// affects nothing else" result: <see cref="HitResultExtensions.AffectsAccuracy"/>,
        /// <see cref="HitResultExtensions.IsBasic"/> and <see cref="HitResultExtensions.IsHit"/> are
        /// all false for it, and <see cref="ScoreProcessor.GetBaseScoreForResult"/> gives it 0. That
        /// is exactly a type!beat mistype: a combo break that must not move accuracy, completion or
        /// rank. Counting it needs no new enum member, so no wire, MessagePack or realm shape
        /// changes, and the server's ScoringContract already classifies <c>combo_break</c> as
        /// non-accuracy-affecting, so it can never inflate or deflate a recomputed score.</para>
        ///
        /// <para>It is recorded through <see cref="RecordMistype"/> rather than
        /// <see cref="JudgementProcessor.ApplyResult"/> on purpose: ApplyResult increments
        /// <see cref="JudgementProcessor.JudgedHits"/>, which is compared for EQUALITY against the
        /// map's hit-object count to decide the play is over, so one extra applied result would
        /// leave <c>HasCompleted</c> false forever and the results screen would never show.
        /// <see cref="ScoreProcessor.MaximumResultCounts"/> is likewise untouched: maximum_statistics
        /// stays one great per cell.</para>
        /// </summary>
        public const HitResult MISTYPE_RESULT = HitResult.ComboBreak;

        /// <summary>
        /// Cells whose combo consequence has ALREADY been taken, by hand, at the keypress that
        /// spoiled them, so the result they finally resolve with must leave combo exactly as it
        /// finds it. Keyed by (line, cell), which is what a <see cref="TypeBeatCharObject"/> carries
        /// and what every judgement stream is routed by, so live play and
        /// <see cref="TypeBeatReplayScorer"/> address exactly the same cells.
        /// </summary>
        private readonly HashSet<(int lineIndex, int cellIndex)> comboNeutralCells = new HashSet<(int, int)>();

        public TypeBeatScoreProcessor(TypeBeatRuleset ruleset)
            : base(ruleset)
        {
        }

        /// <summary>Wrong keypresses recorded so far (see <see cref="MISTYPE_RESULT"/>).</summary>
        public int Mistypes => ScoreResultCounts.GetValueOrDefault(MISTYPE_RESULT);

        /// <summary>
        /// Records one wrong KEYPRESS. Counting is ALL this does: no score, no accuracy, no
        /// completion, no rank, no health, no hit event, and deliberately not the combo break
        /// either, which <c>TypeBeatPlayfield.onMistyped</c> carries by hand for both input models
        /// (backlog 109) at the same seam that calls this. Keeping the two separate keeps this a
        /// pure counter: the break is a plain <see cref="ScoreProcessor.Combo"/> write, whereas
        /// anything routed through <see cref="JudgementProcessor.ApplyResult"/> would also move
        /// <see cref="JudgementProcessor.JudgedHits"/> and accuracy.
        /// </summary>
        public void RecordMistype()
            => ScoreResultCounts[MISTYPE_RESULT] = ScoreResultCounts.GetValueOrDefault(MISTYPE_RESULT) + 1;

        /// <summary>
        /// Reconciles the state this processor holds that a REWIND cannot reach, after the engine has
        /// been re-derived to an earlier time (<c>TypeBeatPlayfield.onRewound</c>, which documents the
        /// ordering that makes this safe). Reachable only while watching a replay or autoplay: live
        /// play cannot seek, and nothing here touches the stored score being watched.
        ///
        /// <para><see cref="ScoreProcessor.RevertResultInternal"/> undoes a result, and every count
        /// this processor keeps rides on one EXCEPT these two. The mistype count is written by
        /// <see cref="RecordMistype"/> off <see cref="Gameplay.TypingEngine.Mistyped"/>, deliberately
        /// not through <c>ApplyResult</c>, so a rewind leaves it at its high-water mark and playing
        /// the same stretch again would count every wrong keypress in it twice. It is therefore
        /// re-derived from the rebuilt engine rather than decremented: the engine counts exactly the
        /// keypresses it announces, one for one, so its own count IS the right value at the seek
        /// target.</para>
        ///
        /// <para>The combo-neutral ledger is dropped wholesale. It is only ever read at the moment a
        /// cell's result is applied, so clearing it cannot disturb a result already taken, and every
        /// cell that is still owed a mark gets it again when its line seals a second time. Keeping
        /// stale entries would be the harmful direction: a cell that was an unfixed typo before the
        /// seek can be corrected on the way back through, and its retype's ordinary combo-increasing
        /// hit would then be silently neutralised.</para>
        /// </summary>
        public void ResyncAfterRewind(int engineMistypes)
        {
            ScoreResultCounts[MISTYPE_RESULT] = engineMistypes;
            comboNeutralCells.Clear();
        }

        /// <summary>
        /// Puts back the streak a corrected typo's wrong keypress broke (backlog 140): osu's combo
        /// resumes at what it was before that keypress, plus everything earned since. The engine
        /// decides WHETHER and BY HOW MUCH (<see cref="Gameplay.TypingEngine.ComboRestored"/>); this is the
        /// hand-mirror into the score processor, the exact counterpart of the hand-mirrored break
        /// on <c>TypeBeatPlayfield.onMistyped</c>, and it exists here rather than at either caller
        /// so live play and <see cref="TypeBeatReplayScorer"/> cannot restore differently.
        ///
        /// <para>Applied as a DELTA, not as the engine's own combo value: the two counters are kept
        /// equal by mirroring every move, never by one overwriting the other, and adding back
        /// exactly what the break took is what that break's undo is.</para>
        ///
        /// <para><see cref="ScoreProcessor.HighestCombo"/> is pushed up here rather than left to the
        /// next result. The resumed run is a streak the player is holding right now, and the next
        /// result is not guaranteed to arrive: a corrected retype of a cell that was ALREADY judged
        /// (typo, fix, typo, fix on one cell) applies none at all, so waiting would drop the
        /// restored maximum whenever the fix is the last thing that happens on the map.</para>
        /// </summary>
        public void RestoreCombo(int streak)
        {
            if (streak <= 0)
                return;

            Combo.Value += streak;
            HighestCombo.Value = Math.Max(HighestCombo.Value, Combo.Value);
        }

        /// <summary>
        /// Declares that the result ABOUT TO BE APPLIED to one cell must leave combo exactly as it
        /// finds it: neither break it nor extend it, and take its combo-weighted score portion from
        /// the combo it found. The cell's combo consequence was already taken, by hand, at the
        /// keypress that spoiled it (<c>TypeBeatPlayfield.onMistyped</c>).
        ///
        /// <para>Two results are ever marked, and both are marked at the seam that applies them (the
        /// seal), never at the keypress: <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, and since
        /// backlog 167 the <see cref="TypeBeatResultMapping.SEAL_MISS"/> of a cell a word skip
        /// abandoned. Backlog 259 widens the second one from the abandoned cells to EVERY miss a
        /// line seals with (see <see cref="Gameplay.TypingEngine.BackDatedSealBreak"/>): under that
        /// rule the seal's one break is back-dated to the cells it misses and mirrored by hand at the
        /// same seam, exactly as the word skip's is, so no Miss result may carry it a second time and
        /// wipe a run the player built past those cells. That timing is what keeps a CORRECTED typo
        /// and a RECLAIMED skip working: such
        /// a cell is resolved by the retype's own Great/Ok/Meh, which is an ordinary combo-increasing
        /// hit and never passes through here, even though the same cell was spoiled earlier in the
        /// play.</para>
        ///
        /// <para>Backlog 122 built this ledger to suppress a second combo BREAK, because the
        /// deferred result was a Miss. Backlog 124 made that result a hit, so there was no second
        /// break left to suppress; what was left was the mirror-image problem, a hit that would hand
        /// back a combo increment the player did not earn, and it is the same ledger redeemed in the
        /// same place. Backlog 167 brings the original case back alongside it: an abandoned cell
        /// really does seal as a Miss, and its break really was taken at the skip, so BOTH
        /// directions are now live and <see cref="GetComboScoreChange"/> has to tell them apart.</para>
        ///
        /// <para>Idempotent. A mark on a cell that turns out to be already judged (its seal result
        /// is dropped) is simply never redeemed, and is cleared with the rest at
        /// <see cref="Reset"/>.</para>
        /// </summary>
        public void MarkComboNeutral(int lineIndex, int cellIndex) => comboNeutralCells.Add((lineIndex, cellIndex));

        /// <summary>
        /// The combo-weighted score portion a result contributes. A combo-neutral cell (see
        /// <see cref="MarkComboNeutral"/>) is weighted by the combo it FOUND rather than by the one
        /// the base implementation is about to leave behind, because it is not going to be allowed
        /// to leave that one behind: <see cref="ApplyScoreChange"/> puts it back.
        ///
        /// <para>This override is needed, rather than a repair afterwards, purely because of
        /// ORDER: <see cref="ScoreProcessor.ApplyResultInternal"/> accumulates the combo portion
        /// BEFORE it calls <see cref="ApplyScoreChange"/>, so by the time combo can be restored the
        /// contribution has already been banked. Weighting an uncorrected typo as though it had
        /// extended the run, while its combo does not, is the one incoherence available here, and
        /// this is where it is avoided.</para>
        ///
        /// <para>Gated on <see cref="HitResultExtensions.IncreasesCombo"/>, which is the whole of the
        /// difference between the ledger's two marked results (backlog 167). A marked HIT is one that
        /// is not going to be allowed to extend the run, so it is weighted by the combo it found. A
        /// marked MISS, the seal result of an abandoned cell, is a result the base implementation
        /// already values at nothing (it weights by the combo AFTER the judgement, which a miss
        /// leaves at zero); pricing it by the combo it found instead would pay a full combo-weighted
        /// portion for a character the player never typed.</para>
        /// </summary>
        protected override double GetComboScoreChange(JudgementResult result)
        {
            if (result.Type.IncreasesCombo() && result.HitObject is TypeBeatCharObject cell && comboNeutralCells.Contains((cell.LineIndex, cell.CellIndex)))
                return GetBaseScoreForResult(result.Judgement.MaxResult) * Math.Pow(result.ComboAtJudgement, COMBO_EXPONENT);

            return base.GetComboScoreChange(result);
        }

        /// <summary>
        /// Puts back the combo a combo-neutral cell's result moved (see
        /// <see cref="MarkComboNeutral"/>), which is the ONE place it is undone, shared by live play
        /// and recalculation because both drive this same processor.
        ///
        /// <para>Why undone rather than prevented. <see cref="ScoreProcessor.ApplyResultInternal"/>
        /// is sealed, and it moves <see cref="ScoreProcessor.Combo"/> for every result whose type
        /// <see cref="HitResultExtensions.AffectsCombo"/>, which every result a cell can take does.
        /// Swapping the result for one that does not would mean giving up the tier, and the tier is
        /// the whole decision (see <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>).
        /// <see cref="ScoreProcessor.ApplyScoreChange"/> is the ruleset hook that runs INSIDE that
        /// method, after the combo move and after the combo-weighted score portion has been
        /// accumulated, and before <c>updateScore</c> (which reads neither <c>Combo</c> nor
        /// <c>HighestCombo</c>). So restoring here leaves the result worth exactly what it is worth
        /// to every count, to accuracy and to the score, and changes only the combo the NEXT
        /// judgement is weighted by.</para>
        ///
        /// <para><see cref="ScoreProcessor.HighestCombo"/> IS repaired here, unlike in backlog 122,
        /// and the difference is the direction the result moves combo. A suppressed BREAK cannot
        /// have raised a running maximum, so 122 could leave it alone. A suppressed INCREMENT can:
        /// <c>ApplyResultInternal</c> pushes <c>HighestCombo</c> up from the incremented value two
        /// lines before this hook runs, so an uncorrected typo would otherwise inflate the submitted
        /// <c>max_combo</c> by one per typo. It is restored to
        /// <see cref="JudgementResult.HighestComboAtJudgement"/>, captured before anything moved.
        /// For a marked MISS (an abandoned cell's seal result, backlog 167) that write is a no-op by
        /// construction, exactly as it was in 122: a break cannot have raised the maximum. Only the
        /// <c>Combo</c> line does any work there, and putting back the run the player was holding is
        /// the whole point of the mark.</para>
        ///
        /// <para><see cref="JudgementResult.ComboAfterJudgement"/> and
        /// <see cref="JudgementResult.HighestComboAfterJudgement"/> are deliberately left reading the
        /// moved values. They are what a REVERT subtracts, and rewriting them would make it subtract
        /// a contribution that was never added. Rewind DOES now reach this processor, but only while
        /// watching a replay or autoplay, and only through results: a backwards seek rebuilds the
        /// engine (see <see cref="ResyncAfterRewind"/>) and the framework reverts the results whose
        /// time is now in the future, which is exactly the subtraction these two fields are for. The
        /// hand-written breaks this pairs with remain invisible to it, which is the residue that
        /// method documents.</para>
        /// </summary>
        protected override void ApplyScoreChange(JudgementResult result)
        {
            base.ApplyScoreChange(result);

            if (!result.Type.AffectsCombo() || result.HitObject is not TypeBeatCharObject cell)
                return;

            if (!comboNeutralCells.Contains((cell.LineIndex, cell.CellIndex)))
                return;

            Combo.Value = result.ComboAtJudgement;
            HighestCombo.Value = result.HighestComboAtJudgement;
        }

        /// <summary>
        /// The era this processor prices an UNCORRECTED TYPO at
        /// (<see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, read by
        /// <see cref="GetBaseScoreForResult"/> and nowhere else). Defaults to the LIVE rule, which
        /// is what live play takes; only <see cref="TypeBeatReplayScorer"/> ever assigns it, and
        /// only to re-derive a row stored before backlog 213 under the rule it was played under.
        /// </summary>
        public UnfixedTypoWorthRule UnfixedTypoWorth { get; set; } = UnfixedTypoWorthRule.Nothing;

        /// <summary>
        /// The base score a result is worth, i.e. its ACCURACY weight and (for the maximum result)
        /// its combo-portion weight. Exactly the base game's table but for
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, which is re-weighted down from its
        /// stock 200: to <see cref="HitResult.Miss"/>'s 0 under the live
        /// <see cref="UnfixedTypoWorthRule.Nothing"/>, and to <see cref="HitResult.Meh"/>'s 50 under
        /// <see cref="UnfixedTypoWorthRule.MehCredit"/>, the rule every row stored before backlog
        /// 213 was played under.
        ///
        /// <para>The tier is a relabelling, not a grade: <see cref="HitResult.Good"/> was the one
        /// result a type!beat cell could legally take that nothing else was using (see
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/> for why the candidate set is forced), so
        /// it carries a weight it inherited from a meaning it does not have here. Left at 200 an
        /// unfixed typo would cost LESS accuracy than a correct character typed late, which is
        /// plainly the wrong way round. Backlog 124 put it at 50, the most accuracy a JUDGED cell
        /// could pay; backlog 213 takes it to 0, because an uncorrected typo IS a miss: the player
        /// did not put that character in that cell, and pricing a wrong character above a dropped
        /// one was the last place the two still came apart.</para>
        ///
        /// <para>The judgement's MAXIMUM result is still Great, so the accuracy DENOMINATOR is
        /// untouched: the cell stays in the fraction and pays 0 of 300, which is what makes accuracy
        /// genuinely fall rather than the cell quietly leaving. GRADES do not move here at all
        /// (completion already counts an unfixed typo as untyped, backlog 126, so rank fell for it
        /// long before accuracy did); the accuracy-derived surfaces fall out of the weight.</para>
        ///
        /// <para>Mirrored by the server (<c>ScoringContract.BaseScore</c>), which recomputes
        /// accuracy from the same dictionaries, and by <c>typebeat-core.js</c>.</para>
        /// </summary>
        public override int GetBaseScoreForResult(HitResult result)
            => result == TypeBeatResultMapping.UNFIXED_TYPO
                ? base.GetBaseScoreForResult(TypeBeatResultMapping.UnfixedTypoIsWorthNothing(UnfixedTypoWorth) ? HitResult.Miss : HitResult.Meh)
                : base.GetBaseScoreForResult(result);

        protected override void Reset(bool storeResults)
        {
            base.Reset(storeResults);
            comboNeutralCells.Clear();
        }

        /// <summary>
        /// The mistype count a finished score CARRIES, or null when it carries none. Null is not
        /// zero: plays from before the stat existed have no key at all, and every display must show
        /// nothing for those rather than inventing a clean run.
        /// </summary>
        public static int? MistypesOf(ScoreInfo score)
            => score.Statistics.TryGetValue(MISTYPE_RESULT, out int mistypes) ? mistypes : null;

        public override ScoreRank RankFromScore(double accuracy, IReadOnlyDictionary<HitResult, int> results)
            => RankFromCompletion(ComputeCompletion(results));

        /// <summary>Grade is awarded on completion, so the results-screen gauge fills to completion.</summary>
        public override double GradeProgress(ScoreInfo score) => ComputeCompletion(score);

        /// <summary>
        /// Whether a judged cell counts as TYPED, i.e. belongs in completion's numerator. Every
        /// osu hit does EXCEPT <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, which is the whole
        /// of backlog 126: the player did put a character in that cell, but not the right one, so
        /// the cell is no more typed than one the line ran out of time on and it must cost
        /// completion and rank exactly as a miss does.
        ///
        /// <para>This is why the typo needs a key of its own rather than sharing
        /// <see cref="HitResult.Meh"/> with a slow-but-correct keypress, as it did between backlog
        /// 124 and 126: a rule keyed on the result cannot separate two things stored under one
        /// result. UNCHANGED by backlog 213, and deliberately: completion has counted an unfixed
        /// typo as untyped since 126, so the fold found this half already done and rank does not
        /// move at all under it. What the fold changed is everything that still priced the typo
        /// ABOVE a miss, namely accuracy, pp and the results columns.</para>
        ///
        /// <para>Mirrored by the server's <c>ScoringContract.CountsAsTyped</c> and by
        /// <c>typebeat-core.js</c>.</para>
        /// </summary>
        public static bool CountsAsTyped(HitResult result) => result.IsHit() && result != TypeBeatResultMapping.UNFIXED_TYPO;

        /// <summary>
        /// Completion over a set of judgement counts: typed cells / judged cells. Mid-play the
        /// denominator is what has been judged so far (completion sits at 1 until a cell seals as
        /// a miss or an unfixed typo); at the end of a completed play it is the whole map.
        /// </summary>
        public static double ComputeCompletion(IReadOnlyDictionary<HitResult, int> results)
        {
            int typed = 0, judged = 0;

            foreach ((var result, int count) in results)
            {
                // Line containers judge as IgnoreHit and carry no accuracy weight; the same
                // filter keeps them (and any bonus results) out of completion.
                if (!result.AffectsAccuracy())
                    continue;

                judged += count;

                if (CountsAsTyped(result))
                    typed += count;
            }

            return judged > 0 ? (double)typed / judged : 1;
        }

        /// <summary>
        /// Whole-map completion for a finished score: typed cells over the TOTAL cell count (from
        /// <see cref="ScoreInfo.MaximumStatistics"/>), so a failed run reads as "typed 43% of the
        /// map" rather than 100%-of-what-it-saw. Equal to the judged-denominator value for any
        /// completed play.
        /// </summary>
        public static double ComputeCompletion(ScoreInfo score)
        {
            int typed = 0, total = 0;

            foreach ((var result, int count) in score.Statistics)
            {
                if (result.AffectsAccuracy() && CountsAsTyped(result))
                    typed += count;
            }

            foreach ((var result, int count) in score.MaximumStatistics)
            {
                if (result.AffectsAccuracy())
                    total += count;
            }

            return total > 0 ? (double)typed / total : 1;
        }

        public static ScoreRank RankFromCompletion(double completion)
        {
            if (completion >= COMPLETION_CUTOFF_X)
                return ScoreRank.X;
            if (completion >= COMPLETION_CUTOFF_S)
                return ScoreRank.S;
            if (completion >= COMPLETION_CUTOFF_A)
                return ScoreRank.A;
            if (completion >= COMPLETION_CUTOFF_B)
                return ScoreRank.B;
            if (completion >= COMPLETION_CUTOFF_C)
                return ScoreRank.C;

            return ScoreRank.D;
        }
    }
}
