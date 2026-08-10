// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
    /// A cell only costs rank when it ends the line unresolved: never typed, or typed wrong and left
    /// that way (backlog 109). Both reach here as one <see cref="HitResult.Miss"/> at the seal, but
    /// only the never-typed one breaks COMBO there: a typo's break was already taken at the keypress
    /// (backlog 122, see <see cref="PrepayComboBreak"/>).
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
        /// Cells whose combo break has ALREADY been taken, by hand, at the keypress that spoiled
        /// them. Keyed by (line, cell), which is what a <see cref="TypeBeatCharObject"/> carries and
        /// what every judgement stream is routed by, so live play and
        /// <see cref="TypeBeatReplayScorer"/> address exactly the same cells.
        /// </summary>
        private readonly HashSet<(int lineIndex, int cellIndex)> prepaidComboBreaks = new HashSet<(int, int)>();

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
        /// Declares that one cell's combo break is already paid: the caller has just reset
        /// <see cref="ScoreProcessor.Combo"/> by hand for the keypress that spoiled this cell, so the
        /// <see cref="HitResult.Miss"/> the cell eventually resolves with must NOT break combo again.
        /// See <see cref="TypeBeatResultMapping.PrepaysCellComboBreak"/> for the policy and backlog
        /// 122 for why it exists.
        ///
        /// <para>Idempotent, and harmless on a cell that goes on to be FIXED: the retype resolves the
        /// cell with a Great/Ok/Meh, which increases combo and never consults this set.</para>
        /// </summary>
        public void PrepayComboBreak(int lineIndex, int cellIndex) => prepaidComboBreaks.Add((lineIndex, cellIndex));

        /// <summary>
        /// Redeems a prepaid combo break (see <see cref="PrepayComboBreak"/>), which is the ONE
        /// place the second reset is undone, shared by live play and recalculation because both
        /// drive this same processor.
        ///
        /// <para>Why here. <see cref="ScoreProcessor.ApplyResultInternal"/> is sealed and resets
        /// <see cref="ScoreProcessor.Combo"/> for every result whose type
        /// <see cref="HitResultExtensions.BreaksCombo"/>, which <see cref="HitResult.Miss"/> does and
        /// must keep doing: swapping the seal's result for anything else would move
        /// <c>statistics</c>, <c>notes</c>, accuracy, completion and rank, none of which this
        /// changes. <see cref="ScoreProcessor.ApplyScoreChange"/> is the ruleset hook that runs
        /// INSIDE that method, after the combo reset and after the combo-weighted score portion has
        /// been accumulated, and before <c>updateScore</c> (which reads neither
        /// <c>Combo</c> nor <c>HighestCombo</c>). So restoring here leaves the miss worth exactly
        /// what it was worth, 0 to the combo portion and one Miss to every count, and changes only
        /// the combo the NEXT judgement is weighted by.</para>
        ///
        /// <para><see cref="ScoreProcessor.HighestCombo"/>, which is what <c>max_combo</c> is
        /// submitted from, needs no repair: it is a running maximum, so a reset that never
        /// happened cannot have lowered it, and the run that continues past this point pushes it up
        /// as it goes. That is the same property that makes <c>onMistyped</c>'s hand-written break
        /// safe.</para>
        ///
        /// <para><see cref="JudgementResult.ComboAfterJudgement"/> is deliberately left reading 0.
        /// It is the value the combo portion was already accumulated from, and rewriting it would
        /// make a REVERT subtract a contribution that was never added. Rewind is not supported by
        /// this ruleset at all (results come only from the monotonic engine), and the hand-written
        /// breaks this pairs with have always been invisible to it.</para>
        /// </summary>
        protected override void ApplyScoreChange(JudgementResult result)
        {
            base.ApplyScoreChange(result);

            if (!result.Type.BreaksCombo() || result.HitObject is not TypeBeatCharObject cell)
                return;

            if (prepaidComboBreaks.Contains((cell.LineIndex, cell.CellIndex)))
                Combo.Value = result.ComboAtJudgement;
        }

        protected override void Reset(bool storeResults)
        {
            base.Reset(storeResults);
            prepaidComboBreaks.Clear();
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
        /// Completion over a set of judgement counts: typed cells / judged cells. Mid-play the
        /// denominator is what has been judged so far (completion sits at 1 until a cell seals as
        /// a miss); at the end of a completed play it is the whole map.
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

                if (result.IsHit())
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
                if (result.AffectsAccuracy() && result.IsHit())
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
