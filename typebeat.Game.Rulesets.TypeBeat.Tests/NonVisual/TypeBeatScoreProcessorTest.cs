// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
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

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, params TimedUnit[] units)
            => new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = end,
                Units = units,
            };

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

        /// <summary>
        /// The LIVE rank must follow completion even when accuracy stands perfectly still
        /// (backlog 200). The base processor recomputes rank on ACCURACY change, but this ruleset
        /// ranks on COMPLETION, which moves with the result counts: a run whose judged cells all
        /// carry the same weight (a Meh and an unfixed typo both weigh Meh's 50, see
        /// <see cref="TypeBeatScoreProcessor.GetBaseScoreForResult"/>) freezes accuracy after its
        /// first judgement while its completion, and so its true rank, keeps moving. Reachable in
        /// real play since backlog 199 made an off-time press a Meh; before the fix the HUD and
        /// results screen kept whatever rank the first judgement set, while the stored row was
        /// ranked correctly by the server.
        /// </summary>
        [Test]
        public void LiveRank_FollowsCompletion_WhileAccuracyStandsStill()
        {
            var hitObject = new TypeBeatHitObject
            {
                Line = line("hello there world", 1000, 4000,
                    unit("hello", 1000, 2000), unit("there", 2000, 3000), unit("world", 3000, 4000)),
                StartTime = 1000,
                LineIndex = 0,
                Granularity = TimingGranularity.Line,
            };
            hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());

            var beatmap = new Beatmap<TypeBeatHitObject> { BeatmapInfo = new BeatmapInfo() };
            beatmap.HitObjects.Add(hitObject);

            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());
            processor.ApplyBeatmap(beatmap);

            var cells = hitObject.NestedHitObjects.OfType<TypeBeatCharObject>().ToList();
            Assert.That(cells, Has.Count.EqualTo(17), "the fixture line is 17 cells; the walk below assumes it");

            double? frozenAccuracy = null;
            int typed = 0, judged = 0;

            for (int i = 0; i < cells.Count; i++)
            {
                // The second cell is an unfixed typo, everything else a slow-but-correct press:
                // completion walks 1/1, 1/2, 2/3, ... 16/17, crossing the D, C, B and A bands.
                var type = i == 1 ? TypeBeatResultMapping.UNFIXED_TYPO : HitResult.Meh;

                processor.ApplyResult(new JudgementResult(cells[i], cells[i].CreateJudgement()) { Type = type });

                judged++;
                if (TypeBeatScoreProcessor.CountsAsTyped(type))
                    typed++;

                // The staleness precondition, asserted rather than assumed: accuracy is pinned flat
                // from the first judgement on, so nothing but the counts can be moving the rank.
                frozenAccuracy ??= processor.Accuracy.Value;
                Assert.That(processor.Accuracy.Value, Is.EqualTo(frozenAccuracy.Value).Within(1e-12), $"cell {i}: accuracy must not move");

                Assert.That(processor.Rank.Value,
                    Is.EqualTo(TypeBeatScoreProcessor.RankFromCompletion((double)typed / judged)),
                    $"cell {i}: rank must track completion {typed}/{judged}");
            }
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
