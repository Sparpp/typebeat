// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Judgements;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Rank is graded on COMPLETION (% of cells typed), not accuracy: sloppy timing (ok/meh) must
    /// not cost the SS, and only missed cells move the grade. The cutoffs must stay in sync with
    /// the server's ScoringContract (typebeat-web); these tests pin the client half.
    /// </summary>
    [TestFixture]
    public class TypeBeatScoreProcessorTest
    {
        private static ScoreRank rank(Dictionary<HitResult, int> results)
            => new TypeBeatScoreProcessor(new TypeBeatRuleset()).RankFromScore(0, results);

        [Test]
        public void AllGreats_IsSS()
            => Assert.That(rank(new Dictionary<HitResult, int> { [HitResult.Great] = 100 }), Is.EqualTo(ScoreRank.X));

        [Test]
        public void SloppyTiming_ButEverythingTyped_IsStillSS()
        {
            // The headline rule: every character typed, even entirely with the worst timing
            // window, earns the SS. Accuracy will be low; the rank must not care.
            var results = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 10,
                [HitResult.Ok] = 30,
                [HitResult.Meh] = 60,
            };

            Assert.That(rank(results), Is.EqualTo(ScoreRank.X));
        }

        [Test]
        public void LineJudgements_AreInvisibleToCompletion()
        {
            // Line containers seal as IgnoreHit/IgnoreMiss, scoring-inert, so they must not
            // dilute (or inflate) the cell-based completion.
            var results = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 50,
                [HitResult.IgnoreHit] = 7,
                [HitResult.IgnoreMiss] = 3,
            };

            Assert.That(rank(results), Is.EqualTo(ScoreRank.X));
        }

        [TestCase(96, 4, ScoreRank.S)] // 96% typed
        [TestCase(94, 6, ScoreRank.A)]
        [TestCase(85, 15, ScoreRank.B)]
        [TestCase(75, 25, ScoreRank.C)]
        [TestCase(50, 50, ScoreRank.D)]
        public void MissedCells_GradeByCompletionBands(int typed, int missed, ScoreRank expected)
        {
            var results = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = typed,
                [HitResult.Miss] = missed,
            };

            Assert.That(rank(results), Is.EqualTo(expected));
        }

        [Test]
        public void OneMissedCell_DeniesTheSS()
        {
            var results = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 99,
                [HitResult.Miss] = 1,
            };

            Assert.That(rank(results), Is.EqualTo(ScoreRank.S));
        }

        [Test]
        public void ScoreInfoCompletion_UsesWholeMapDenominator()
        {
            // A fail 40 cells into a 100-cell map: 38 typed, 2 missed, 60 never judged.
            // Whole-map completion must read 38%, not 95%-of-what-it-saw.
            var score = new ScoreInfo
            {
                Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 38, [HitResult.Miss] = 2 },
                MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 },
            };

            Assert.That(TypeBeatScoreProcessor.ComputeCompletion(score), Is.EqualTo(0.38).Within(1e-9));
        }

        /// <summary>
        /// Backlog 133 raised the cell judgement's MaxResult from Great to Perfect to free a sixth
        /// result key, and the whole change rests on that costing the ACCURACY DENOMINATOR nothing.
        /// The denominator is the sum of <c>GetBaseScoreForResult(MaxResult)</c> over judged cells
        /// (ScoreProcessor.ApplyResultInternal), so it is unmoved exactly as long as Perfect is
        /// worth what Great used to be: 300. That is asserted here rather than assumed, because if
        /// it ever stopped holding, every score, rank and pp figure would move at once and the
        /// server's recomputation (ScoringContract) would disagree with the client about all of them.
        /// </summary>
        [Test]
        public void ThePerCellMaximumIsStill300SoTheAccuracyDenominatorIsUnmoved()
        {
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            var judgement = new TypeBeatCharJudgement();

            Assert.That(judgement.MaxResult, Is.EqualTo(HitResult.Perfect));
            Assert.That(judgement.MinResult, Is.EqualTo(HitResult.Miss));
            Assert.That(processor.GetBaseScoreForResult(judgement.MaxResult), Is.EqualTo(300));

            // The four quality tiers, the typo and the miss: a 300 / 200 / 100 / 50 ladder with the
            // typo re-weighted onto the bottom rung and a miss worth nothing.
            Assert.Multiple(() =>
            {
                Assert.That(processor.GetBaseScoreForResult(HitResult.Perfect), Is.EqualTo(300));
                Assert.That(processor.GetBaseScoreForResult(HitResult.Great), Is.EqualTo(200));
                Assert.That(processor.GetBaseScoreForResult(HitResult.Ok), Is.EqualTo(100));
                Assert.That(processor.GetBaseScoreForResult(HitResult.Meh), Is.EqualTo(50));
                Assert.That(processor.GetBaseScoreForResult(TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(50));
                Assert.That(processor.GetBaseScoreForResult(HitResult.Miss), Is.Zero);
            });

            // NOTHING a cell may legally take is worth more than the maximum, which is what stops a
            // recomputed accuracy exceeding 1 and what stops the server's accuracyJudged/accuracyMax
            // invariant tripping: every judged cell contributes exactly one to both counts and at
            // most 300 to the numerator against exactly 300 in the denominator.
            foreach (var result in new[]
                     {
                         HitResult.Miss, HitResult.Meh, HitResult.Ok, TypeBeatResultMapping.UNFIXED_TYPO,
                         HitResult.Great, HitResult.Perfect,
                     })
            {
                Assert.That(result.IsValidHitResult(judgement.MinResult, judgement.MaxResult), Is.True,
                    $"{result} must be a result a cell can actually take");
                Assert.That(processor.GetBaseScoreForResult(result),
                    Is.LessThanOrEqualTo(processor.GetBaseScoreForResult(judgement.MaxResult)), $"{result}");
                Assert.That(result.AffectsAccuracy(), Is.True, $"{result}");
            }

            // ...and the tier above the ceiling is not reachable, so the candidate set really is
            // closed at six (see TypeBeatResultMapping.UNFIXED_TYPO for why that matters).
            Assert.That(HitResult.SliderTailHit.IsValidHitResult(judgement.MinResult, judgement.MaxResult), Is.False);
        }

        /// <summary>
        /// The other half of the same claim, end to end rather than by table: a run in which every
        /// cell resolves at the top tier reads accuracy exactly 1.0 out of a real processor.
        /// </summary>
        [Test]
        public void AFullTopTierPlayReadsAccuracyOne()
        {
            var statistics = new Dictionary<HitResult, int> { [HitResult.Perfect] = 12 };
            var maximum = new Dictionary<HitResult, int> { [HitResult.Perfect] = 12 };
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());

            long numerator = 0, denominator = 0;

            foreach ((var result, int count) in statistics)
                numerator += processor.GetBaseScoreForResult(result) * count;

            foreach ((var result, int count) in maximum)
                denominator += processor.GetBaseScoreForResult(result) * count;

            Assert.That(denominator, Is.EqualTo(12 * 300));
            Assert.That((double)numerator / denominator, Is.EqualTo(1.0));
            Assert.That(TypeBeatScoreProcessor.ComputeCompletion(statistics), Is.EqualTo(1.0));
            Assert.That(TypeBeatScoreProcessor.RankFromCompletion(1.0), Is.EqualTo(ScoreRank.X));
        }
    }
}
