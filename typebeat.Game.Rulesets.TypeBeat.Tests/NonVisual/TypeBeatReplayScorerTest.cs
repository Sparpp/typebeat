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
//   2. under TypoRule.Deferred it produces exactly the account backlog 109 and 124 describe.
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
        /// The typo left uncorrected, which is where backlog 124 makes the two eras come apart in
        /// the one place backlog 122 had just made them agree. Pre-109 (<c>ImmediateMiss</c>) the
        /// cell is a MISS, which is a character the player never finished. Now it is an unfixed
        /// TYPO, a character they finished and got wrong: still one judged note, still costing
        /// accuracy, still costing the mistype and the combo break it took at the keypress, but no
        /// longer costing the miss count, completion or rank.
        ///
        /// <para>COMBO is the quantity that must NOT move, and it does not: one break, at the
        /// keypress, under both rules. Backlog 122 got there by suppressing the deferred Miss's
        /// second break; 124 gets there by making the result a hit and applying it combo-neutral, so
        /// it can neither break the run a second time nor extend it by the cell that spoiled it.</para>
        /// </summary>
        [Test]
        public void AnUncorrectedTypoIsATypoNowAndWasAMissBeforeBacklog109()
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
                // Twelve cells struck clean under both. The thirteenth is the whole difference.
                Assert.That(count(deferred, HitResult.Great), Is.EqualTo(12));
                Assert.That(count(immediate, HitResult.Great), Is.EqualTo(12));

                Assert.That(count(deferred, HitResult.Meh), Is.EqualTo(1), "the cell was finished, wrongly");
                Assert.That(count(deferred, HitResult.Miss), Is.Zero, "and a finished cell is not a miss");

                Assert.That(count(immediate, HitResult.Meh), Is.Zero);
                Assert.That(count(immediate, HitResult.Miss), Is.EqualTo(1), "the pre-109 arm must not move");

                // The mistype is what the wrong keypress leaves behind, identically in both eras.
                Assert.That(deferred.Mistypes, Is.EqualTo(1));
                Assert.That(immediate.Mistypes, Is.EqualTo(1));

                // Completion and rank: the play typed every cell, so it keeps the X it would have had
                // without the typo. Pre-109 the same play read 12/13 and an A.
                Assert.That(deferred.Completion, Is.EqualTo(1).Within(1e-12));
                Assert.That(deferred.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(immediate.Completion, Is.EqualTo(12 / 13.0).Within(1e-12));
                Assert.That(immediate.Rank, Is.EqualTo(ScoreRank.A));

                // ACCURACY still pays, and it is the one scale that does: 12 Greats plus a Meh
                // against a 13-Great maximum, i.e. (12*300 + 50) / (13*300).
                Assert.That(deferred.Accuracy, Is.EqualTo(3650 / 3900.0).Within(1e-12));
                Assert.That(immediate.Accuracy, Is.EqualTo(12 / 13.0).Within(1e-12));

                // ONE break, at the typo, under both rules: cells 3..11 run the combo back up to 9
                // and the seal neither cuts it nor extends it, so line 1's cell takes the run to 10.
                Assert.That(deferred.MaxCombo, Is.EqualTo(10));
                Assert.That(immediate.MaxCombo, Is.EqualTo(10));

                // A cell that scores 50 instead of 0, and contributes to the combo portion at the
                // combo it FOUND (9, not 10, see TypeBeatScoreProcessor.GetComboScoreChange), is
                // worth more than a miss. Pinned as a golden because the weight is the only thing
                // that decides it: weighting the same Meh at 10 instead lands 758,457, i.e. it pays
                // the play for a run the seal did not extend.
                Assert.That(deferred.TotalScore, Is.EqualTo(756145));
                Assert.That(immediate.TotalScore, Is.EqualTo(684636));
            });
        }

        /// <summary>
        /// The combo run the player builds AFTER an uncorrected typo survives the seal and carries
        /// into the next line, which is the whole of backlog 122 stated as one number.
        ///
        /// <para>Line 0 is where the two readings come apart: the typo is on cell 2, so cells 3..11
        /// rebuild a run of 9 and the seal's result arrives after all of them. Line 1's single cell
        /// then reads 10 if the run survived and 1 if it did not, and <c>max_combo</c> is the
        /// running maximum of the two, so it reads 10 or 9. This asserts the SUBMITTED number, off
        /// the score processor's own <c>HighestCombo</c> via <c>PopulateScore</c>, not the engine's
        /// live HUD combo, which is a separate account and was never the thing that broke twice.</para>
        /// </summary>
        [Test]
        public void TheComboRunAfterAnUncorrectedTypoSurvivesTheSeal()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            for (int i = 0; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], i == 2 ? 'q' : word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var withTypo = score(map, replay(frames), TypoRule.Deferred);

            // The same map typed clean, as the ceiling the typo has to fall short of.
            var cleanFrames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            for (int i = 0; i < word.Length; i++)
                cleanFrames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

            cleanFrames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var clean = score(map, replay(cleanFrames), TypoRule.Deferred);

            Assert.Multiple(() =>
            {
                // 9 cells after the typo on line 0, plus line 1's cell: the run crosses the seal.
                Assert.That(withTypo.MaxCombo, Is.EqualTo(10));

                // It really did break, once, at the typo: 13 cells, so a clean run reads 13.
                Assert.That(clean.MaxCombo, Is.EqualTo(13));
                Assert.That(withTypo.MaxCombo, Is.LessThan(clean.MaxCombo));
                Assert.That(withTypo.Mistypes, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The DENOMINATOR, which is the constraint backlog 124 had to work inside. Taking the cell
        /// out of the miss count must not take it out of the count altogether: it stays one judged
        /// note, so <c>notes</c> is still one per cell and accuracy, the combo ratio and the pp
        /// length term keep measuring the map the player actually played. Had the cell simply stopped
        /// resolving, a line typed entirely as typos would judge nothing and read completion 1 over
        /// an empty denominator.
        /// </summary>
        [Test]
        public void AnUncorrectedTypoStaysInTheDenominator()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            for (int i = 0; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], i == 2 ? 'q' : word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var account = score(map, replay(frames), TypoRule.Deferred);

            var notes = PerformancePoints.CountNotes(account.Statistics);

            Assert.Multiple(() =>
            {
                // notes = great + ok + meh + miss, one per cell, with the mistype counted apart.
                Assert.That(count(account, HitResult.Great), Is.EqualTo(12));
                Assert.That(count(account, HitResult.Ok), Is.Zero);
                Assert.That(count(account, HitResult.Meh), Is.EqualTo(1));
                Assert.That(count(account, HitResult.Miss), Is.Zero);
                Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(13));

                // pp counts thirteen notes, none of them a miss, and prices the typo through the
                // mistype term instead. Twelve would inflate the length term and the combo ratio.
                Assert.That(notes.Notes, Is.EqualTo(13));
                Assert.That(notes.Misses, Is.Zero);
                Assert.That(notes.Mistypes, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// A cell the line genuinely ran out of time on, held against the typo above so the two are
        /// visibly different facts. Same map, same one spoiled cell, and the only difference is that
        /// nobody ever finished it: it is a MISS, it costs completion and rank, and there is no
        /// mistype behind it because no wrong key was ever pressed.
        /// </summary>
        [Test]
        public void ACellTheLineRanOutOfTimeOnIsStillAMiss()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            // The player stops after cell 10, which is the ONLY way a cell is ever left untyped: the
            // caret cannot move past a cell without something being put into it, so an untyped cell
            // is always one the play never reached. Line 0's cell 11 and line 1's single cell are
            // both left to their seals.
            for (int i = 0; i < word.Length - 1; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

            var account = score(map, replay(frames), TypoRule.Deferred);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Great), Is.EqualTo(11));
                Assert.That(count(account, HitResult.Miss), Is.EqualTo(2), "never finished, so misses");
                Assert.That(count(account, HitResult.Meh), Is.Zero);
                Assert.That(account.Mistypes, Is.Zero, "no wrong key was ever pressed");

                Assert.That(account.Completion, Is.EqualTo(11 / 13.0).Within(1e-12));
                Assert.That(account.Rank, Is.EqualTo(ScoreRank.B));
            });
        }

        /// <summary>
        /// The unfixed typo's result is a HIT, and a hit increases combo, so the seal would otherwise
        /// hand the player back the very cell that broke their run. It does not: the result is
        /// applied combo-neutral.
        ///
        /// <para>The fixture is built so that the SEAL is the last combo event of the play, which is
        /// the only shape that can tell a full repair from a half one. The typo is on cell 0 and
        /// cells 1..11 are typed clean, so the run and the running maximum are both 11 when the seal
        /// arrives, and line 1 is never typed at all, so nothing after the seal can push the maximum
        /// up again. Restoring <c>Combo</c> alone would leave <c>HighestCombo</c> at the 12 that
        /// <c>ApplyResultInternal</c> already banked, two lines before the hook that repairs it.</para>
        /// </summary>
        [Test]
        public void TheUnfixedTypoDoesNotExtendTheSubmittedMaxCombo()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            for (int i = 0; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], i == 0 ? 'q' : word[i]));

            var account = score(map, replay(frames), TypoRule.Deferred);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Great), Is.EqualTo(11), "cells 1..11");
                Assert.That(count(account, HitResult.Meh), Is.EqualTo(1), "the typo on cell 0");
                Assert.That(count(account, HitResult.Miss), Is.EqualTo(1), "line 1, never typed");

                // THE assertion: eleven, the run the player actually built. Twelve means the seal's
                // hit was allowed to extend it.
                Assert.That(account.MaxCombo, Is.EqualTo(11));
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
