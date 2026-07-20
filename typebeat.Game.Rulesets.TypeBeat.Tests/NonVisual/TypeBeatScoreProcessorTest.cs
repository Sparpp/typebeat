// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Rank is graded on COMPLETION (% of cells typed), not accuracy: sloppy timing (ok/meh) must
    /// not cost the SS, and only missed cells move the grade. The cutoffs must stay in sync with
    /// the server's ScoringContract (typebeat-web) — these tests pin the client half.
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
            // The headline rule: every character typed — even entirely with the worst timing
            // window — earns the SS. Accuracy will be low; the rank must not care.
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
            // Line containers seal as IgnoreHit/IgnoreMiss — scoring-inert, so they must not
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
    }
}
