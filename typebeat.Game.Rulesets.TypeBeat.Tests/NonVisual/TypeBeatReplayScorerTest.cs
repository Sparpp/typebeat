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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Replays.Legacy;
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

        /// <summary>The clean run's submitted total, hardcoded because the point of
        /// <see cref="AStoredRunReDerivesToItsStoredTotals"/> is that no era arm may move it.</summary>
        private const long clean_run_total_score = 1000000;

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

        /// <summary>
        /// Score under the ERA the typo rule belongs to: pre-109 play (<c>ImmediateMiss</c>) also
        /// predates the combo restore, and <c>Deferred</c> stands for live play, which restores.
        /// The two rules are independent axes, so the overload below is what a test uses when it
        /// means to vary one of them on its own (backlog 140's parity pin does).
        /// </summary>
        private static TypeBeatReplayAccount score(IBeatmap map, Replay r, TypoRule rule, params Mod[] mods)
            => score(map, r, rule, rule == TypoRule.Deferred ? ComboRestoreRule.OnFix : ComboRestoreRule.Never, mods);

        private static TypeBeatReplayAccount score(IBeatmap map, Replay r, TypoRule rule, ComboRestoreRule comboRule, params Mod[] mods)
            => TypeBeatReplayScorer.Score(map, mods, r, rule, comboRule);

        private static int count(TypeBeatReplayAccount account, HitResult result)
            => account.Statistics.GetValueOrDefault(result);

        /// <summary>
        /// The user's own example (backlog 179): "cake", ONE word and ONE syllable (the final e is
        /// silent, so the syllabifier does not split it), sung over [1000, 3000]. The flat char ramp
        /// puts the point targets at 1000 / 1500 / 2000 / 2500, and the group's span runs from its
        /// first cell's target to the unit's end, so it is [1000, 3000]: every one of those targets
        /// sits inside it, which is what makes the same four keystrokes classify differently under
        /// the two rules.
        /// </summary>
        private static TypeBeatBeatmap cake()
        {
            var line = new LyricLine
            {
                RawText = "cake",
                StartTime = 0,
                EndTime = 60000,
                SingEndTime = 3000,
                Units = new[] { new TimedUnit { Text = "cake", StartTime = 1000, EndTime = 3000 } },
            };

            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        /// <summary>
        /// The four presses that spell "cake" in one flurry near the top of the syllable: on the
        /// beat, then 300, 550 and 900 milliseconds AHEAD of each following cell's point target,
        /// and all four inside the sung span. <paramref name="flags"/> is the CONFIG frame's flags
        /// word, taken through the LEGACY DECODE so the era arm is the one a stored .osr really
        /// produces rather than one the test constructs.
        /// </summary>
        private static Replay cakeRun(int flags)
        {
            var config = new TypeBeatReplayFrame();
            config.FromLegacy(new LegacyReplayFrame(0, (float)TypeBeatReplayFrame.CONFIG, flags, ReplayButtonState.None), new Beatmap());
            config.Time = 0;

            return replay(new List<TypeBeatReplayFrame>
            {
                config,
                new TypeBeatReplayFrame(1000, 'c'), // target 1000: delta 0 under either rule
                new TypeBeatReplayFrame(1200, 'a'), // target 1500: 300 early
                new TypeBeatReplayFrame(1450, 'k'), // target 2000: 550 early
                new TypeBeatReplayFrame(1600, 'e'), // target 2500: 900 early
            });
        }

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
        /// The typo COUNT survives either way, because it counts the keypress and no correction can
        /// unpress it. Its combo break does not: since backlog 140 the fix resumes the streak the
        /// wrong key broke, so the deferred arm's run is unbroken end to end. That axis is gated by
        /// its own rule and isolated in <see cref="AFixedTypoResumesTheStreakOnlyUnderTheLiveRule"/>;
        /// here the two arms are each judged under the whole of their own era.
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

                // The fix is worth a combo too, and the live era gives back the whole run: the
                // wrong key broke a streak of 2 (cells 0 and 1) and correcting cell 2 resumes it,
                // so the thirteen cells run unbroken. Pre-109 the retype was dropped on an
                // already-judged cell and the break stood, leaving 3..11 plus line 1.
                Assert.That(deferred.MaxCombo, Is.EqualTo(13));
                Assert.That(immediate.MaxCombo, Is.EqualTo(10));

                // Recovering the cell is worth real score.
                Assert.That(deferred.TotalScore, Is.GreaterThan(immediate.TotalScore));
            });
        }

        /// <summary>
        /// Backlog 140's era gate, on the axis it owns. The SAME keystrokes, judged under the same
        /// typo rule, re-derive a different max_combo depending on the combo-restore rule alone,
        /// and each is the number its own era submitted:
        ///
        /// <list type="bullet">
        /// <item><see cref="ComboRestoreRule.Never"/>, i.e. every score stored before 140: the
        /// wrong key on cell 2 broke a streak of 2 for good, so the run is cell 2's retype plus
        /// 3..11 plus line 1, eleven;</item>
        /// <item><see cref="ComboRestoreRule.OnFix"/>, i.e. live play: correcting cell 2 resumes
        /// that streak of 2 BEFORE the retype is judged, so the retype lands on 3 and the map runs
        /// unbroken to thirteen.</item>
        /// </list>
        ///
        /// <para>The gate is why no stored row moves: re-deriving one under the live rule would
        /// hand it two combo its fingers never earned, and price the rest of the run at a streak it
        /// never held. The restored streak is worth SCORE as well as max_combo, which is the whole
        /// point of restoring it before the judgement rather than after.</para>
        ///
        /// <para>Everything a typo costs on the record is untouched by the rule: one wrong keypress
        /// counted, thirteen cells resolved, completion whole.</para>
        /// </summary>
        [Test]
        public void AFixedTypoResumesTheStreakOnlyUnderTheLiveRule()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            frames.Add(new TypeBeatReplayFrame(targets[0], word[0]));
            frames.Add(new TypeBeatReplayFrame(targets[1], word[1]));
            frames.Add(new TypeBeatReplayFrame(targets[2], 'q')); // wrong, on a streak of 2
            frames.Add(new TypeBeatReplayFrame(targets[2], TypeBeatReplayFrame.BACKSPACE));
            frames.Add(new TypeBeatReplayFrame(targets[2], word[2])); // fixed

            for (int i = 3; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var restored = score(map, replay(frames), TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stands = score(map, replay(frames), TypoRule.Deferred, ComboRestoreRule.Never);

            Assert.Multiple(() =>
            {
                Assert.That(restored.MaxCombo, Is.EqualTo(13), "the streak of 2 resumes, so nothing is ever broken");
                Assert.That(stands.MaxCombo, Is.EqualTo(11), "the break stands, so the run restarts at the fix");

                // Restoring BEFORE the retype's judgement is what makes the fix worth score: every
                // cell from the fix onwards is weighted by a streak two higher.
                Assert.That(restored.TotalScore, Is.GreaterThan(stands.TotalScore));

                // The rule moves combo and nothing else. The keypress is still a typo, the cell is
                // still recovered, and both eras agree on every count.
                Assert.That(restored.Statistics, Is.EqualTo(stands.Statistics));
                Assert.That(restored.Mistypes, Is.EqualTo(1));
                Assert.That(stands.Mistypes, Is.EqualTo(1));
                Assert.That(restored.Completion, Is.EqualTo(1));
                Assert.That(restored.Accuracy, Is.EqualTo(stands.Accuracy));
                Assert.That(restored.Rank, Is.EqualTo(stands.Rank));
            });
        }

        /// <summary>
        /// The typo left uncorrected, which is where backlog 124 makes the two eras come apart in
        /// the one place backlog 122 had just made them agree. Pre-109 (<c>ImmediateMiss</c>) the
        /// cell is a MISS, which is a character the player never finished. Now it is an unfixed
        /// TYPO, a character they finished and got wrong: still one judged note, still costing
        /// accuracy, still costing the mistype and the combo break it took at the keypress, and
        /// since backlog 126 still costing COMPLETION and RANK, which is the whole of what the two
        /// eras now agree on. What it does not cost is the MISS COUNT, and that is the entire
        /// remaining difference: pp prices a miss and a typo by different terms, so the two must
        /// stay distinguishable in <c>statistics</c> even though completion treats them alike.
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

                Assert.That(count(deferred, TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1), "the cell was finished, wrongly");
                Assert.That(count(deferred, HitResult.Miss), Is.Zero, "and a finished cell is not a miss");
                Assert.That(count(deferred, HitResult.Meh), Is.Zero, "the typo has a key of its own, not the Ok tier's");

                Assert.That(count(immediate, TypeBeatResultMapping.UNFIXED_TYPO), Is.Zero);
                Assert.That(count(immediate, HitResult.Miss), Is.EqualTo(1), "the pre-109 arm must not move");

                // The mistype is what the wrong keypress leaves behind, identically in both eras.
                Assert.That(deferred.Mistypes, Is.EqualTo(1));
                Assert.That(immediate.Mistypes, Is.EqualTo(1));

                // COMPLETION AND RANK, which is backlog 126: the typo'd cell was not typed, so it
                // costs completion and rank exactly as the pre-109 miss did, 12/13 and an A. Between
                // backlog 124 and 126 this same play read completion 1 and took an X, which is what
                // the user objected to. What is NOT the same as the pre-109 arm is the miss count
                // and therefore pp, which is the whole reason the two keys stay apart.
                Assert.That(deferred.Completion, Is.EqualTo(12 / 13.0).Within(1e-12));
                Assert.That(deferred.Rank, Is.EqualTo(ScoreRank.A));
                Assert.That(immediate.Completion, Is.EqualTo(12 / 13.0).Within(1e-12));
                Assert.That(immediate.Rank, Is.EqualTo(ScoreRank.A));

                // ACCURACY is unmoved by backlog 126: the typo tier is re-weighted to the Meh value
                // (TypeBeatScoreProcessor.GetBaseScoreForResult), so it is still 12 Greats plus 50
                // against a 13-Great maximum, i.e. (12*300 + 50) / (13*300). Its stock weight of 200
                // would read 3800/3900 instead, i.e. a typo cheaper than a correct-but-late char.
                Assert.That(deferred.Accuracy, Is.EqualTo(3650 / 3900.0).Within(1e-12));
                Assert.That(immediate.Accuracy, Is.EqualTo(12 / 13.0).Within(1e-12));

                // ONE break, at the typo, under both rules: cells 3..11 run the combo back up to 9
                // and the seal neither cuts it nor extends it, so line 1's cell takes the run to 10.
                Assert.That(deferred.MaxCombo, Is.EqualTo(10));
                Assert.That(immediate.MaxCombo, Is.EqualTo(10));

                // A cell that scores 50 instead of 0, and contributes to the combo portion at the
                // combo it FOUND (9, not 10, see TypeBeatScoreProcessor.GetComboScoreChange), is
                // worth more than a miss. Pinned as a golden because the weight is the only thing
                // that decides it: weighting the same typo at 10 instead lands 758,457, i.e. it pays
                // the play for a run the seal did not extend. UNCHANGED by backlog 126, which is the
                // point of re-weighting the tier: only completion, rank and health move.
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
                // notes = great + ok + meh + typo + miss, one per cell, with the mistype apart.
                Assert.That(count(account, HitResult.Great), Is.EqualTo(12));
                Assert.That(count(account, HitResult.Ok), Is.Zero);
                Assert.That(count(account, HitResult.Meh), Is.Zero);
                Assert.That(count(account, TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1));
                Assert.That(count(account, HitResult.Miss), Is.Zero);
                Assert.That(account.MaximumStatistics.GetValueOrDefault(HitResult.Great), Is.EqualTo(13));

                // pp counts thirteen notes, none of them a miss, and prices the typo through the
                // mistype term instead. Twelve would inflate the length term and the combo ratio.
                Assert.That(notes.Notes, Is.EqualTo(13));
                Assert.That(notes.Misses, Is.Zero);
                Assert.That(notes.Typos, Is.EqualTo(1));

                // The denominator is the point: thirteen cells JUDGED, twelve of them typed, so
                // completion is 12/13 and not 1-over-nothing. This is what stops a line typed
                // entirely as typos judging nothing and taking an X for free.
                Assert.That(account.Completion, Is.EqualTo(12 / 13.0).Within(1e-12));
            });
        }

        /// <summary>
        /// Backlog 126 stated as the case that forced it: a run typed ENTIRELY wrong. Every cell is
        /// finished and none of them is right, and between backlog 124 and 126 that read completion
        /// 1 and took an X, because every cell resolved as a HIT and completion counted hits. Now
        /// the typo tier is excluded from the numerator and the same play reads completion 0 and a
        /// D, which is what a play that typed none of the map should read.
        ///
        /// <para>The MISS COUNT stays zero throughout, which is the property that must survive: pp
        /// still prices this play through the mistype term, not the cleanliness term, because the
        /// player did reach and finish every character.</para>
        /// </summary>
        [Test]
        public void ARunTypedEntirelyWrongIsNotAnX()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            // 'q' is in neither line, so every one of these is wrong wherever the caret sits.
            for (int i = 0; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], 'q'));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'q'));

            var account = score(map, replay(frames), TypoRule.Deferred);
            var notes = PerformancePoints.CountNotes(account.Statistics);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(13), "every cell finished, wrongly");
                Assert.That(count(account, HitResult.Great), Is.Zero);
                Assert.That(count(account, HitResult.Miss), Is.Zero, "nothing was left unfinished");

                // THE assertion backlog 126 exists for.
                Assert.That(account.Completion, Is.Zero);
                Assert.That(account.Rank, Is.EqualTo(ScoreRank.D));

                // Still thirteen notes and no miss, so pp keeps pricing this by the mistype term.
                Assert.That(notes.Notes, Is.EqualTo(13));
                Assert.That(notes.Misses, Is.Zero);
                Assert.That(notes.Typos, Is.EqualTo(13));

                // No cell ever extended a run, and every keypress broke one.
                Assert.That(account.MaxCombo, Is.Zero);
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
                Assert.That(count(account, TypeBeatResultMapping.UNFIXED_TYPO), Is.Zero, "and NOT the typo key");
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
                Assert.That(count(account, TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1), "the typo on cell 0");
                Assert.That(count(account, HitResult.Miss), Is.EqualTo(1), "line 1, never typed");

                // THE assertion: eleven, the run the player actually built. Twelve means the seal's
                // hit was allowed to extend it.
                Assert.That(account.MaxCombo, Is.EqualTo(11));
            });
        }

        /// <summary>
        /// Backlog 199 through the real score processor: one OFF-TIME press (the right character,
        /// 5000 ms late, well outside the Meh window) in the middle of an otherwise clean run.
        ///
        /// <para>Under the live <see cref="OffTimeRule.MehHit"/> the press is a hit, so the run
        /// carries straight through it to the full thirteen and the account reads as a complete play
        /// that lost accuracy. Under <see cref="OffTimeRule.BreaksCombo"/>, the rule every stored row
        /// was played on, the same keystream ends its run at the six characters before the press and
        /// the cell is a Miss.</para>
        ///
        /// <para>The statistics blobs differ by exactly one entry, a Meh where the stored arm has a
        /// Miss, which is the collision the rule accepts stated as a number: the submitted blob
        /// cannot tell an off-time press from a press that landed just inside the Meh window, and the
        /// twelve Greats and the thirteen judged cells are identical either way.</para>
        /// </summary>
        [Test]
        public void AnOffTimePressKeepsTheRunUnderTheLiveRuleAndLosesItUnderTheStoredOne()
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var frames = new List<TypeBeatReplayFrame> { TypeBeatReplayFrame.CreateConfigFrame(0, true) };

            // Every cell dead on target except cell 6, struck 5000 ms late: Line-granularity MehLate
            // is 2000, so it falls off the ladder as a Lagging press.
            for (int i = 0; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(i == 6 ? targets[i] + 5000 : targets[i], word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var r = replay(frames);

            var live = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var stored = TypeBeatReplayScorer.Score(map, Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, WordSkipRule.Reclaimable, ComboClaimRule.StreakedBreakWins,
                OffTimeRule.BreaksCombo);

            Assert.Multiple(() =>
            {
                Assert.That(live.MaxCombo, Is.EqualTo(13), "the run walks through the off-time press");
                Assert.That(stored.MaxCombo, Is.EqualTo(6), "the six cells before the press, and never rebuilt higher");

                // One entry apart, and that entry is the whole rule.
                Assert.That(count(live, HitResult.Great), Is.EqualTo(12));
                Assert.That(count(stored, HitResult.Great), Is.EqualTo(12));
                Assert.That(count(live, HitResult.Meh), Is.EqualTo(1));
                Assert.That(count(live, HitResult.Miss), Is.Zero);
                Assert.That(count(stored, HitResult.Meh), Is.Zero);
                Assert.That(count(stored, HitResult.Miss), Is.EqualTo(1));

                // Nothing else about the press moved: it is not a mistype under either rule, and the
                // cell is judged either way.
                Assert.That(live.Mistypes, Is.Zero);
                Assert.That(stored.Mistypes, Is.Zero);

                // Completion, rank and score follow the result, which is the point of choosing it.
                Assert.That(live.Completion, Is.EqualTo(1));
                Assert.That(stored.Completion, Is.LessThan(1));
                Assert.That(live.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(stored.Rank, Is.Not.EqualTo(ScoreRank.X));
                Assert.That(stored.TotalScore, Is.LessThan(live.TotalScore));
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

        #region Backlog 179: the syllable span is the live rule, and the replay carries its era

        /// <summary>
        /// The rule in one word. Every character of "cake" is typed while "cake" is being sung, so
        /// every one of them is PERFECT: four Greats, delta 0, and the osu-side accuracy that
        /// follows from a statistics dictionary with nothing but Greats in it. Three of the four
        /// presses are hundreds of milliseconds ahead of the cell's own point target, which is the
        /// whole difference: the classic rule prices that distance, the live rule does not, because
        /// the syllable is what the player is hearing.
        ///
        /// <para>The era arrives the way it does in production, on the replay's own CONFIG frame
        /// (flags bit 2), not as an argument to the scorer.</para>
        /// </summary>
        [Test]
        public void AWordTypedWhileItsSyllableIsSungIsPerfect()
        {
            // 1 = allow wrong input, 4 = syllable timing: exactly what the live client records.
            var account = score(cake(), cakeRun(5), TypoRule.Deferred);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Great), Is.EqualTo(4));
                Assert.That(count(account, HitResult.Ok), Is.Zero);
                Assert.That(count(account, HitResult.Meh), Is.Zero);
                Assert.That(count(account, HitResult.Miss), Is.Zero);
                Assert.That(account.Accuracy, Is.EqualTo(1));
                Assert.That(account.TotalScore, Is.EqualTo(1000000));
                Assert.That(account.MaxCombo, Is.EqualTo(4));
                Assert.That(account.Completion, Is.EqualTo(1));
                Assert.That(account.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(account.UnconsumedFrames, Is.Zero);
            });
        }

        /// <summary>
        /// The other side of the same seam, and the reason stored rows are safe: the SAME four
        /// keystrokes, re-derived from a replay whose flags word is one of the four that existed
        /// before backlog 179, are classified by point deltas exactly as they always were. 0 is a
        /// Great, 300 and 550 early are Oks (the Line tier's Great window is 250 early), 900 early
        /// is a Meh (its Ok window is 600 early), so the run is worth less than an SS and its
        /// accuracy is below 1. Bit 2 is absent from every one of those words, and absent means
        /// classic.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void ALegacyFlagsWordReDerivesOnPointDeltasExactlyAsBefore(int storedFlags)
        {
            var account = score(cake(), cakeRun(storedFlags), TypoRule.Deferred);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Great), Is.EqualTo(1));
                Assert.That(count(account, HitResult.Ok), Is.EqualTo(2));
                Assert.That(count(account, HitResult.Meh), Is.EqualTo(1));
                Assert.That(count(account, HitResult.Miss), Is.Zero);
                // 300 + 100 + 100 + 50 out of 4 * 300, the osu weights the four results carry.
                Assert.That(account.Accuracy, Is.EqualTo(550 / 1200.0).Within(1e-9));
                // The whole submitted total, hardcoded: this is the number a stored row holds,
                // where the same four keystrokes under the live rule are worth the full 1000000.
                Assert.That(account.TotalScore, Is.EqualTo(239280));
                Assert.That(account.MaxCombo, Is.EqualTo(4));
                Assert.That(account.UnconsumedFrames, Is.Zero);
            });
        }

        /// <summary>
        /// The identity pin, on a fixture that predates the rule: the twelve-cell clean run's
        /// account is BIT-FOR-BIT what it was before syllable-span judgement existed, for every
        /// flags word a stored replay can carry. This is the guarantee the recalculation tool rests
        /// on, so it is asserted on the whole submitted account (statistics, max combo, total score,
        /// completion, rank), not just on the parts this change could plausibly have moved.
        /// </summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void AStoredRunReDerivesToItsStoredTotals(int storedFlags)
        {
            var map = beatmap();
            var targets = lineZeroTargets(map);

            var config = new TypeBeatReplayFrame();
            config.FromLegacy(new LegacyReplayFrame(0, (float)TypeBeatReplayFrame.CONFIG, storedFlags, ReplayButtonState.None), new Beatmap());
            config.Time = 0;

            var frames = new List<TypeBeatReplayFrame> { config };

            for (int i = 0; i < word.Length; i++)
                frames.Add(new TypeBeatReplayFrame(targets[i], word[i]));

            frames.Add(new TypeBeatReplayFrame(line_zero_end, 'z'));

            var account = score(map, replay(frames), TypoRule.Deferred);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Great), Is.EqualTo(13));
                Assert.That(count(account, HitResult.Miss), Is.Zero);
                Assert.That(account.MaxCombo, Is.EqualTo(13));
                Assert.That(account.TotalScore, Is.EqualTo(clean_run_total_score));
                Assert.That(account.Completion, Is.EqualTo(1));
                Assert.That(account.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(account.UnconsumedFrames, Is.Zero);
            });
        }

        #endregion
    }
}
