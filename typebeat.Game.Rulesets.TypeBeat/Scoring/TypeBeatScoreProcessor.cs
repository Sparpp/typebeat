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
    /// therefore rank, falls exactly as far as it would for a miss. What it still does NOT cost is
    /// the MISS COUNT, which is the distinction pp is built on: a miss says the player was too slow
    /// to finish the character at all, a typo says they finished it and got it wrong, so the miss
    /// term prices one and the mistype term the other. It is applied COMBO-NEUTRAL (see
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
        /// Declares that the result ABOUT TO BE APPLIED to one cell must leave combo exactly as it
        /// finds it: neither break it nor extend it, and take its combo-weighted score portion from
        /// the combo it found. The cell's combo consequence was already taken, by hand, at the
        /// keypress that spoiled it (<c>TypeBeatPlayfield.onMistyped</c>).
        ///
        /// <para>Only <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/> is ever marked, and it is
        /// marked at the seam that applies it (the seal), not at the keypress. That is what keeps a
        /// CORRECTED typo working: its cell is resolved by the retype's own Great/Ok/Meh, which is
        /// an ordinary combo-increasing hit and never passes through here, even though the same cell
        /// carried a wrong char earlier in the play.</para>
        ///
        /// <para>Backlog 122 built this ledger to suppress a second combo BREAK, because the
        /// deferred result was a Miss. Backlog 124 made that result a hit, so there is no second
        /// break left to suppress; what is left is the mirror-image problem, a hit that would hand
        /// back a combo increment the player did not earn, and it is the same ledger redeemed in the
        /// same place.</para>
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
        /// </summary>
        protected override double GetComboScoreChange(JudgementResult result)
        {
            if (result.HitObject is TypeBeatCharObject cell && comboNeutralCells.Contains((cell.LineIndex, cell.CellIndex)))
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
        /// <see cref="JudgementResult.HighestComboAtJudgement"/>, captured before anything moved.</para>
        ///
        /// <para><see cref="JudgementResult.ComboAfterJudgement"/> and
        /// <see cref="JudgementResult.HighestComboAfterJudgement"/> are deliberately left reading the
        /// moved values. They are what a REVERT subtracts, and rewriting them would make it subtract
        /// a contribution that was never added. Rewind is not supported by this ruleset at all
        /// (results come only from the monotonic engine), and the hand-written breaks this pairs
        /// with have always been invisible to it.</para>
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
        /// The base score a result is worth, i.e. its ACCURACY weight and (for the maximum result)
        /// its combo-portion weight. Exactly the base game's table but for two results:
        ///
        /// <list type="bullet">
        /// <item><see cref="HitResult.Great"/>, re-weighted from 300 down to 200. Backlog 133 made
        /// the cell's quality ladder FOUR tiers deep, and the four are worth 300 / 200 / 100 / 50, a
        /// halving per tier exactly like the windows themselves. The base game's table gives Perfect
        /// and Great the same 300 (Perfect is a tighter window that "does not give any bonus
        /// accuracy or score"), which would have made the top two tiers accuracy-identical and the
        /// tightest window worth nothing at all. Moving GREAT rather than Perfect is what keeps the
        /// per-cell MAXIMUM at 300, so the accuracy denominator stays <c>300 * cells</c> and the
        /// whole of the score, pp and rank pipeline is untouched by the extra tier.</item>
        /// <item><see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, re-weighted from 200 down to
        /// <see cref="HitResult.Meh"/>'s 50.</item>
        /// </list>
        ///
        /// <para>The tier is a relabelling, not a grade: <see cref="HitResult.Good"/> was the one
        /// result a type!beat cell could legally take that nothing else was using (see
        /// <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/> for why the candidate set is forced), so
        /// it carries a weight it inherited from a meaning it does not have here. Left at 200 an
        /// unfixed typo would cost LESS accuracy than a correct character typed late, which is
        /// plainly the wrong way round. At 50 it pays the most accuracy a judged cell can pay, which
        /// is what backlog 124 chose when the typo WAS a Meh, so this change moves completion, rank
        /// and health and leaves accuracy and total score bit-identical.</para>
        ///
        /// <para>Mirrored by the server (<c>ScoringContract.BaseScore</c>), which recomputes
        /// accuracy from the same dictionaries, and by <c>typebeat-core.js</c>. The judgement's
        /// MAXIMUM result is <see cref="HitResult.Perfect"/> and is worth 300, exactly what Great
        /// was worth when it was the maximum, so the accuracy DENOMINATOR is untouched.</para>
        /// </summary>
        public override int GetBaseScoreForResult(HitResult result)
        {
            if (result == TypeBeatResultMapping.UNFIXED_TYPO)
                return base.GetBaseScoreForResult(HitResult.Meh);

            if (result == HitResult.Great)
                return GREAT_BASE_SCORE;

            return base.GetBaseScoreForResult(result);
        }

        /// <summary>
        /// What the second quality tier is worth, against the top tier's 300 (see
        /// <see cref="GetBaseScoreForResult"/>). Halfway down a 300 / 200 / 100 / 50 ladder, and
        /// deliberately the base game's own <see cref="HitResult.Good"/> weight, so the four tiers
        /// step through values the rest of the scoring pipeline was already built around.
        /// </summary>
        public const int GREAT_BASE_SCORE = 200;

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
        /// result. It is also the only place the two are treated differently at all, which is what
        /// keeps pp free to price a typo as a typo (see <see cref="PerformancePoints"/>).</para>
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
