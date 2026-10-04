// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class StoredScoreGradeTest
    {
        [TestCase(0.99, 0, ScoreRank.X)]
        [TestCase(0.98, 1, ScoreRank.S)]
        [TestCase(0.92, 2, ScoreRank.S)]
        [TestCase(0.97, 3, ScoreRank.A)]
        [TestCase(0.85, 15, ScoreRank.A)]
        [TestCase(0.75, 25, ScoreRank.B)]
        [TestCase(0.60, 40, ScoreRank.C)]
        [TestCase(0.59, 41, ScoreRank.D)]
        public void StoredGradesUseCurrentRulesWithoutRewritingHistory(double accuracy, int misses, ScoreRank expected)
        {
            var score = new ScoreInfo(ruleset: new TypeBeatRuleset().RulesetInfo)
            {
                Rank = ScoreRank.X,
                Accuracy = accuracy,
                StatisticsJson = JsonConvert.SerializeObject(new Dictionary<HitResult, int>
                {
                    [HitResult.Great] = 100 - misses,
                    [HitResult.Miss] = misses,
                }),
            };

            Assert.That(score.GetDisplayRank(), Is.EqualTo(expected));
            Assert.That(score.Rank, Is.EqualTo(ScoreRank.X));
        }

        [Test]
        public void UnfixedTyposCountAsMissedCellsRatherThanKeystrokes()
        {
            var score = new ScoreInfo(ruleset: new TypeBeatRuleset().RulesetInfo)
            {
                Rank = ScoreRank.X,
                Accuracy = 0.97,
                Statistics = new Dictionary<HitResult, int>
                {
                    [HitResult.Great] = 97,
                    [HitResult.Miss] = 2,
                    [TypeBeatResultMapping.UNFIXED_TYPO] = 1,
                    [TypeBeatScoreProcessor.MISTYPE_RESULT] = 1000,
                    [HitResult.IgnoreHit] = 1000,
                },
            };

            Assert.That(score.GetDisplayRank(), Is.EqualTo(ScoreRank.A));
        }

        [TestCase(0.99, 0, ScoreRank.XH)]
        [TestCase(0.97, 1, ScoreRank.SH)]
        [TestCase(0.97, 3, ScoreRank.A)]
        public void FlashlightKeepsSilverGradesOnTheCurrentLadder(double accuracy, int misses, ScoreRank expected)
        {
            var score = new ScoreInfo(ruleset: new TypeBeatRuleset().RulesetInfo)
            {
                Rank = ScoreRank.XH,
                Accuracy = accuracy,
                Mods = new Mod[] { new TypeBeatModFlashlight() },
                Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 - misses, [HitResult.Miss] = misses },
            };

            Assert.That(score.GetDisplayRank(), Is.EqualTo(expected));
        }

        [TestCase(true, ScoreRank.F)]
        [TestCase(false, ScoreRank.X)]
        public void FailedScoresRemainFailed(bool passed, ScoreRank stored)
        {
            var score = new ScoreInfo(ruleset: new TypeBeatRuleset().RulesetInfo)
            {
                Passed = passed,
                Rank = stored,
                Accuracy = 1,
                Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 },
            };

            Assert.That(score.GetDisplayRank(), Is.EqualTo(ScoreRank.F));
        }

        [Test]
        public void MissingRulesetsKeepTheirStoredGrade()
        {
            var score = new ScoreInfo { Rank = ScoreRank.B, Accuracy = 1 };
            Assert.That(score.GetDisplayRank(), Is.EqualTo(ScoreRank.B));
        }
    }
}
