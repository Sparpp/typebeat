// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 114: score recalculation. TypeBeatReplayScorer re-derives the SUBMITTED account of a run
// from its replay, headlessly, by driving the real TypingEngine into the real
// TypeBeatScoreProcessor through the shared TypeBeatResultMapping. These pins cover the two things
// that make it trustworthy:
//
//   1. under TypoRule.ImmediateMiss it reproduces the PRE-109 account, which is what every stored
//      score was priced under, so the tool can prove itself against stored numbers before it
//      writes new ones;
//   2. under TypoRule.Deferred it produces exactly the account backlog 109 describes.
//
// TestSceneTypeBeatReplayRescore is the other half: it holds this harness against a real Player's
// own score processor, so "the same numbers" is proven end to end rather than asserted here.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypeBeatReplayScorerTest
    {
        #region Fixture

        /// <summary>
        /// One twelve-cell word on [0, 240000] plus a short second line, the same shape
        /// <c>TestSceneTypeBeatTypoDeferral</c> uses: every cell can be struck dead on its target,
        /// and 11/12 vs 12/12 lands either side of the X cutoff.
        /// </summary>
        private const string word = "abcdefghijkl";

        private const double line_zero_end = 300000;

        private static TypeBeatBeatmap beatmap()
        {
            var first = new LyricLine
            {
                RawText = word,
                StartTime = 0,
                EndTime = line_zero_end,
                SingEndTime = 240000,
                Units = new[] { new TimedUnit { Text = word, StartTime = 0, EndTime = 240000 } },
            };

            var second = new LyricLine
            {
                RawText = "z",
                StartTime = line_zero_end,
                EndTime = 600000,
                SingEndTime = 400000,
                Units = new[] { new TimedUnit { Text = "z", StartTime = line_zero_end, EndTime = 400000 } },
            };

            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = first, Granularity = TimingGranularity.Line });
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = line_zero_end, LineIndex = 1, Line = second, Granularity = TimingGranularity.Line });

            // Nested per-cell objects are built by ApplyDefaults, which is what gives the score
            // processor its maximum_statistics.
            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>The cell target times of line 0, read off the engine's own flattening.</summary>
        private static IReadOnlyList<double> lineZeroTargets(IBeatmap map)
        {
            var line = ((TypeBeatHitObject)map.HitObjects[0]).Line;
            return TypingLine.FromLyricLine(line, TimingGranularity.Line, false).Cells.Select(c => c.TargetTime).ToList();
        }

        private static Replay replay(IEnumerable<TypeBeatReplayFrame> frames)
        {
            var r = new Replay();
            r.Frames.AddRange(frames);
            return r;
        }

        private static TypeBeatReplayAccount score(IBeatmap map, Replay r, TypoRule rule, params Mod[] mods)
            => TypeBeatReplayScorer.Score(map, mods, r, rule);

        private static int count(TypeBeatReplayAccount account, HitResult result)
            => account.Statistics.GetValueOrDefault(result);

        #endregion

        /// <summary>
        /// A clean run: every cell struck on its target. Both rules must agree, because the rules
        /// only differ on a wrong char, and both must produce the SS the map's twelve-plus-one
        /// cells earn.
        /// </summary>
        [Test]
        public void ACleanRunIsRuleIndependentAndPerfect()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            for (int i = 0; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var deferred = score(map, replay(frames), TypoRule.Deferred);
            var immediate = score(map, replay(frames), TypoRule.ImmediateMiss);

            Assert.Multiple(() =>
            {
                Assert.That(count(deferred, HitResult.Great), Is.EqualTo(13), "twelve cells on line 0 plus one on line 1");
                Assert.That(count(deferred, HitResult.Miss), Is.Zero);
                Assert.That(deferred.MaxCombo, Is.EqualTo(13));
                Assert.That(deferred.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(deferred.Completion, Is.EqualTo(1));
                Assert.That(deferred.UnconsumedFrames, Is.Zero);

                Assert.That(immediate.Statistics, Is.EqualTo(deferred.Statistics));
                Assert.That(immediate.MaxCombo, Is.EqualTo(deferred.MaxCombo));
                Assert.That(immediate.TotalScore, Is.EqualTo(deferred.TotalScore));
            });
        }

        /// <summary>
        /// The whole point of the harness. The same replay, a typo on cell 2 that is then FIXED,
        /// re-derives differently under the two rules, and each derivation is the account its own
        /// era submitted:
        ///
        /// <list type="bullet">
        /// <item>pre-109 the typo spent the cell's result on a Miss, and the fix could never take it
        /// back, so the play submits 12 greats, 1 miss and an A;</item>
        /// <item>since 109 the result was deferred, the fix earns the cell, and the play submits 13
        /// greats and an X.</item>
        /// </list>
        ///
        /// The mistype and its combo break survive either way: the recovery is of the CELL, not of
        /// the mistake.
        /// </summary>
        [Test]
        public void AFixedTypoRecoversItsCellOnlyUnderTheDeferredRule()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            frames.Add(new TypeBeatReplayFrame(targets[0], word[0]));
            frames.Add(new TypeBeatReplayFrame(targets[1], word[1]));
            frames.Add(new TypeBeatReplayFrame(targets[2], 'q')); // wrong
            frames.Add(new TypeBeatReplayFrame(targets[2], TypeBeatReplayFrame.BACKSPACE));
            frames.Add(new TypeBeatReplayFrame(targets[2], word[2])); // fixed

            for (int i = 3; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var deferred = score(map, replay(frames), TypoRule.Deferred);
            var immediate = score(map, replay(frames), TypoRule.ImmediateMiss);

            Assert.Multiple(() =>
            {
                Assert.That(count(deferred, HitResult.Great), Is.EqualTo(13));
                Assert.That(count(deferred, HitResult.Miss), Is.Zero);
                Assert.That(deferred.Completion, Is.EqualTo(1));
                Assert.That(deferred.Rank, Is.EqualTo(ScoreRank.X));

                Assert.That(count(immediate, HitResult.Great), Is.EqualTo(12));
                Assert.That(count(immediate, HitResult.Miss), Is.EqualTo(1));
                Assert.That(immediate.Completion, Is.EqualTo(12 / 13.0).Within(1e-9));
                Assert.That(immediate.Rank, Is.EqualTo(ScoreRank.A));

                // One wrong keypress, counted identically by both, and it broke combo at the
                // keypress under both (via Mistyped now, via the cell's Miss then).
                Assert.That(deferred.Mistypes, Is.EqualTo(1));
                Assert.That(immediate.Mistypes, Is.EqualTo(1));

                // The fix is worth a combo too, and only the deferred rule lets it count: pre-109
                // the retype was dropped on an already-judged cell, so the run after the break is
                // cell 2 plus 3..11 plus line 1 (11) now, against 3..11 plus line 1 (10) then.
                Assert.That(deferred.MaxCombo, Is.EqualTo(11));
                Assert.That(immediate.MaxCombo, Is.EqualTo(10));

                // Recovering the cell is worth real score.
                Assert.That(deferred.TotalScore, Is.GreaterThan(immediate.TotalScore));
            });
        }

        /// <summary>
        /// The typo left uncorrected. It costs a mistype and exactly one cell under BOTH rules:
        /// backlog 109 moved WHEN the cell resolves, not whether an abandoned typo costs one. The
        /// STATISTICS are therefore identical, which is the single biggest reason most stored
        /// scores cannot move at all.
        ///
        /// <para>What does move is <c>max_combo</c>, and this is the case that shows why. Pre-109
        /// the typo's Miss landed with the keypress, so one break was all it cost. Since 109 the
        /// break lands with the keypress and the cell's Miss lands again at the SEAL, i.e. after
        /// every later cell of that line has already been typed, so an uncorrected typo now cuts
        /// the combo run twice and can only ever lower the submitted max_combo.</para>
        /// </summary>
        [Test]
        public void AnUncorrectedTypoCostsTheSameCellButBreaksComboTwiceUnderTheDeferredRule()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            for (int i = 0; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], i == 2 ? 'q' : word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var deferred = score(map, replay(frames), TypoRule.Deferred);
            var immediate = score(map, replay(frames), TypoRule.ImmediateMiss);

            Assert.Multiple(() =>
            {
                // Everything accuracy, completion and rank are computed from is untouched.
                Assert.That(deferred.Statistics, Is.EqualTo(immediate.Statistics));
                Assert.That(deferred.Accuracy, Is.EqualTo(immediate.Accuracy));
                Assert.That(deferred.Completion, Is.EqualTo(immediate.Completion));
                Assert.That(deferred.Rank, Is.EqualTo(immediate.Rank));

                Assert.That(count(deferred, HitResult.Great), Is.EqualTo(12));
                Assert.That(count(deferred, HitResult.Miss), Is.EqualTo(1));
                Assert.That(deferred.Mistypes, Is.EqualTo(1));

                // The second break: the seal's Miss lands after cells 3..11 have run the combo back
                // up to 9, so line 1's cell starts a fresh run instead of extending it to 10.
                Assert.That(deferred.MaxCombo, Is.EqualTo(9));
                Assert.That(immediate.MaxCombo, Is.EqualTo(10));

                // Which is worth a little total score, through the combo-weighted portion.
                Assert.That(deferred.TotalScore, Is.LessThan(immediate.TotalScore));
            });
        }

        /// <summary>
        /// A rejected key under Gatekeeper. Neither rule ever gave the cell a result for it, so the
        /// two must agree exactly: the mistype count and the combo break, with the break arriving
        /// through a different seam in each era (Mistyped now, WrongKeyRejected then).
        /// </summary>
        [Test]
        public void AGatekeeperRejectionAccountsIdenticallyUnderBothRules()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, false) };

            frames.Add(new TypeBeatReplayFrame(targets[0], word[0]));
            frames.Add(new TypeBeatReplayFrame(targets[1], 'q')); // rejected, cell stays open
            frames.Add(new TypeBeatReplayFrame(targets[1], word[1]));

            for (int i = 2; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var deferred = score(map, replay(frames), TypoRule.Deferred, new TypeBeatModGatekeeper());
            var immediate = score(map, replay(frames), TypoRule.ImmediateMiss, new TypeBeatModGatekeeper());

            Assert.Multiple(() =>
            {
                Assert.That(deferred.Statistics, Is.EqualTo(immediate.Statistics));
                Assert.That(deferred.MaxCombo, Is.EqualTo(immediate.MaxCombo));
                Assert.That(deferred.TotalScore, Is.EqualTo(immediate.TotalScore));

                Assert.That(count(deferred, HitResult.Great), Is.EqualTo(13), "the rejection cost no cell");
                Assert.That(count(deferred, HitResult.Miss), Is.Zero);
                Assert.That(deferred.Mistypes, Is.EqualTo(1));
                Assert.That(deferred.MaxCombo, Is.EqualTo(12), "combo broke on the rejected key");
                Assert.That(deferred.Rank, Is.EqualTo(ScoreRank.X));
            });
        }

        /// <summary>
        /// The CONFIG frame, not the local defaults, decides the input model: a replay of a strict
        /// run recorded before Gatekeeper existed carries no mod at all, and bit 0 = 0 is the only
        /// thing that still judges it the way it was played.
        /// </summary>
        [Test]
        public void TheConfigFrameDecidesTheInputModelWithNoModPresent()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(0, false),
                new TypeBeatReplayFrame(targets[0], 'q'), // rejected, not typed through
                new TypeBeatReplayFrame(targets[0], word[0]),
            };

            for (int i = 1; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var account = score(map, replay(frames), TypoRule.ImmediateMiss);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Great), Is.EqualTo(13));
                Assert.That(count(account, HitResult.Miss), Is.Zero);
                Assert.That(account.Mistypes, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// A run that types nothing at all: every cell seals as a miss, under either rule, and the
        /// map's own cell count is what maximum_statistics reports.
        /// </summary>
        [Test]
        public void AnEmptyReplayMissesEveryCell()
        {
            var map = beatmap();

            var account = score(map, replay(new List<TypeBeatReplayFrame>()), TypoRule.Deferred);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Miss), Is.EqualTo(13));
                Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(13));
                Assert.That(account.MaxCombo, Is.Zero);
                Assert.That(account.Completion, Is.Zero);
                Assert.That(account.Rank, Is.EqualTo(ScoreRank.D));
            });
        }

        /// <summary>
        /// maximum_statistics is the map's, not the play's: one great per cell plus one inert
        /// result per LINE. The server's ScoringContract reads exactly this, and its completion
        /// denominator would move if a line container ever became accuracy-affecting.
        /// </summary>
        [Test]
        public void MaximumStatisticsIsOneGreatPerCellPlusAnInertResultPerLine()
        {
            var map = beatmap();
            var account = score(map, replay(new List<TypeBeatReplayFrame>()), TypoRule.Deferred);

            Assert.Multiple(() =>
            {
                Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(13));
                Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.IgnoreHit), Is.EqualTo(2));
                Assert.That(HitResult.IgnoreHit.AffectsAccuracy(), Is.False);
            });
        }
    }
}
