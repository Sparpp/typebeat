// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Online.Spectator;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Replays;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatScoreProcessorTest
    {
        [TestCase(1, 0, ScoreRank.X)]
        [TestCase(0.98, 0, ScoreRank.X)]
        [TestCase(0.979999, 0, ScoreRank.S)]
        [TestCase(1, 0.001, ScoreRank.S)]
        [TestCase(0.92, 0.029999, ScoreRank.S)]
        [TestCase(0.92, 0.03, ScoreRank.A)]
        [TestCase(0.99, 0.03, ScoreRank.A)]
        [TestCase(0.919999, 0, ScoreRank.A)]
        [TestCase(0.85, 0.15, ScoreRank.A)]
        [TestCase(0.849999, 0, ScoreRank.B)]
        [TestCase(0.75, 0.25, ScoreRank.B)]
        [TestCase(0.749999, 0, ScoreRank.C)]
        [TestCase(0.60, 0.4, ScoreRank.C)]
        [TestCase(0.599999, 0, ScoreRank.D)]
        [TestCase(0, 1, ScoreRank.D)]
        public void GradeBoundaries(double accuracy, double missedFraction, ScoreRank expected)
            => Assert.That(TypeBeatScoreProcessor.RankFromAccuracy(accuracy, missedFraction), Is.EqualTo(expected));

        [Test]
        public void SloppyTimingDoesNotEarnSSForFullCompletion()
        {
            var results = new Dictionary<HitResult, int> { [HitResult.Meh] = 100 };
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            Assert.That(processor.RankFromScore(1 / 6.0, results), Is.EqualTo(ScoreRank.D));
        }

        [Test]
        public void RawMistypesAndLineContainersDoNotDiluteMissFraction()
        {
            var results = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 97,
                [HitResult.Miss] = 2,
                [TypeBeatResultMapping.UNFIXED_TYPO] = 1,
                [TypeBeatScoreProcessor.MISTYPE_RESULT] = 1000,
                [HitResult.IgnoreHit] = 1000,
                [HitResult.IgnoreMiss] = 1000,
            };
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            Assert.That(processor.RankFromScore(0.97, results), Is.EqualTo(ScoreRank.A), "exactly 3% missed denies S");
            results[TypeBeatResultMapping.UNFIXED_TYPO] = 0;
            results[HitResult.Great] = 98;
            Assert.That(processor.RankFromScore(0.98, results), Is.EqualTo(ScoreRank.S), "2% missed permits S, denies SS");
        }

        [TestCase(ScoreRank.X, 0.98)]
        [TestCase(ScoreRank.XH, 0.98)]
        [TestCase(ScoreRank.S, 0.92)]
        [TestCase(ScoreRank.SH, 0.92)]
        [TestCase(ScoreRank.A, 0.85)]
        [TestCase(ScoreRank.B, 0.75)]
        [TestCase(ScoreRank.C, 0.60)]
        [TestCase(ScoreRank.D, 0)]
        public void GradeGaugeUsesNewAccuracyBands(ScoreRank rank, double cutoff)
        {
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            Assert.That(processor.AccuracyCutoffFromRank(rank), Is.EqualTo(cutoff));
            Assert.That(processor.GradeProgress(new ScoreInfo { Accuracy = 0.92 }), Is.EqualTo(0.92));
        }

        private static (TypeBeatScoreProcessor processor, List<TypeBeatCharObject> cells) setup(string text = "abcdefghij")
        {
            var hitObject = new TypeBeatHitObject
            {
                Line = new LyricLine
                {
                    RawText = text,
                    StartTime = 1000,
                    EndTime = 4000,
                    SingEndTime = 4000,
                    Units = new[] { new TimedUnit { Text = text, StartTime = 1000, EndTime = 4000 } },
                },
                StartTime = 1000,
                LineIndex = 0,
                Granularity = TimingGranularity.Line,
            };
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());
            var beatmap = new Beatmap<TypeBeatHitObject> { BeatmapInfo = new BeatmapInfo() };
            beatmap.HitObjects.Add(hitObject);
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(beatmap);
            return (processor, hitObject.NestedHitObjects.OfType<TypeBeatCharObject>().ToList());
        }

        private static JudgementResult apply(TypeBeatScoreProcessor processor, TypeBeatCharObject cell, HitResult type)
        {
            var result = new JudgementResult(cell, cell.CreateJudgement()) { Type = type };
            processor.ApplyResult(result);
            return result;
        }

        [Test]
        public void PerfectScoreScalesWithMapProgressAndFinishesAtOneMillion()
        {
            var (processor, cells) = setup();
            Assert.That(processor.MaximumTotalScore, Is.EqualTo(1000000));
            for (int i = 0; i < cells.Count; i++)
            {
                apply(processor, cells[i], HitResult.Great);
                Assert.That(processor.TotalScoreWithoutMods.Value, Is.EqualTo((i + 1) * 100000));
                Assert.That(processor.Rank.Value, Is.EqualTo(ScoreRank.X));
            }
        }

        [Test]
        public void ScoreRewardsAccuracyMoreThanLongestCombo()
        {
            var (accurate, accurateCells) = setup(new string('a', 200));
            var (lessAccurate, lessAccurateCells) = setup(new string('a', 200));
            for (int i = 0; i < accurateCells.Count; i++)
            {
                if (i % 10 == 0)
                    accurate.Combo.Value = 0;
                apply(accurate, accurateCells[i], HitResult.Great);
                apply(lessAccurate, lessAccurateCells[i], i % 10 == 0 ? HitResult.Ok : HitResult.Great);
            }
            Assert.That(accurate.HighestCombo.Value, Is.EqualTo(10));
            Assert.That(lessAccurate.HighestCombo.Value, Is.EqualTo(200));
            Assert.That(accurate.TotalScore.Value, Is.GreaterThan(980000), "lost streaks cost only the small pp bonus");
            Assert.That(accurate.TotalScore.Value, Is.GreaterThan(lessAccurate.TotalScore.Value));
        }

        [Test]
        public void MissPositionDoesNotAlterScoreForSameAccuracyMissesAndLongestCombo()
        {
            var (early, earlyCells) = setup();
            var (late, lateCells) = setup();
            for (int i = 0; i < earlyCells.Count; i++)
            {
                apply(early, earlyCells[i], i == 0 ? HitResult.Miss : HitResult.Great);
                apply(late, lateCells[i], i == 9 ? HitResult.Miss : HitResult.Great);
            }
            Assert.That(early.HighestCombo.Value, Is.EqualTo(late.HighestCombo.Value));
            Assert.That(early.TotalScore.Value, Is.EqualTo(late.TotalScore.Value));
            Assert.That(early.TotalScore.Value, Is.InRange(1, 999999));
        }

        [Test]
        public void RestoringLastTypoRefreshesScoreWithoutAnotherJudgement()
        {
            var (processor, cells) = setup("abc");
            apply(processor, cells[0], HitResult.Great);
            apply(processor, cells[1], HitResult.Great);
            processor.Combo.Value = 0;
            processor.RecordMistype();
            apply(processor, cells[2], HitResult.Great);
            long beforeFix = processor.TotalScore.Value;
            processor.RestoreCombo(2);
            Assert.That(beforeFix, Is.LessThan(1000000));
            Assert.That(processor.TotalScore.Value, Is.EqualTo(1000000));
            Assert.That(processor.Mistypes, Is.EqualTo(1), "recovered wrong presses remain scoring-neutral");
        }

        [Test]
        public void RevertingMissRestoresLiveGradeAndPartialScore()
        {
            var (processor, cells) = setup();
            for (int i = 0; i < 9; i++)
                apply(processor, cells[i], HitResult.Great);
            var miss = apply(processor, cells[9], HitResult.Miss);
            Assert.That(processor.Rank.Value, Is.EqualTo(ScoreRank.A));
            processor.RevertResult(miss);
            Assert.That(processor.Rank.Value, Is.EqualTo(ScoreRank.X));
            Assert.That(processor.TotalScore.Value, Is.EqualTo(900000));
        }

        [Test]
        public void ReplayHeaderRecomputesSameScoreAndGrade()
        {
            var (live, cells) = setup();
            live.Mods.Value = new[] { new TypeBeatModNoFail() };
            for (int i = 0; i < cells.Count; i++)
                apply(live, cells[i], i == 0 ? HitResult.Miss : HitResult.Great);
            live.RecordMistype();
            var score = new ScoreInfo();
            live.PopulateScore(score);
            var frame = new ReplayFrame { Header = new FrameHeader(score, live.GetScoreProcessorStatistics()) };
            var (replay, _) = setup();
            replay.Mods.Value = live.Mods.Value;
            replay.ResetFromReplayFrame(frame);
            Assert.That(replay.TotalScore.Value, Is.EqualTo(live.TotalScore.Value));
            Assert.That(replay.TotalScoreWithoutMods.Value, Is.EqualTo(live.TotalScoreWithoutMods.Value));
            Assert.That(replay.Accuracy.Value, Is.EqualTo(live.Accuracy.Value));
            Assert.That(replay.Rank.Value, Is.EqualTo(live.Rank.Value));
            Assert.That(replay.Mistypes, Is.EqualTo(1));
        }

        [Test]
        public void ScoreInfoCompletionStillUsesWholeMapDenominator()
        {
            var score = new ScoreInfo
            {
                Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 38, [HitResult.Miss] = 2 },
                MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 },
            };
            Assert.That(TypeBeatScoreProcessor.ComputeCompletion(score), Is.EqualTo(0.38).Within(1e-9));
        }
    }
}
