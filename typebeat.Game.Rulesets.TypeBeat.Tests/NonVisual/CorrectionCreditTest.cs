// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Backlog 210: a CORRECTED typo keeps an accuracy cost.
//
// Before it, a word typed wrong and then fixed could score bit-identically to a word typed right.
// The mistype travels as TypeBeatScoreProcessor.MISTYPE_RESULT, which is accuracy-inert by design,
// and the spoiled cell's osu result is DEFERRED (backlog 109), so the retype was graded purely on
// its OWN timing: a player quick enough to fix inside the Great window paid nothing at all in
// accuracy. TheCapIsWhyThisFileExists below is that shape, and pins the old behaviour still being
// reachable through the era switch alongside the new one being the default.
//
// The rule is one number: a cell that held a wrong character before it was ever judged resolves at
// min(the retype's own tier, Ok). So per cell the ordering is clean 300 > corrected at most 100 >
// unfixed typo 50 (UNFIXED_TYPO, re-weighted) > miss 0, and perfect play strictly beats corrected
// play. Nothing else moves: the combo a fix restores (backlog 140), completion, rank, the miss count
// and the unfixed typo's seal path are all untouched, and HEALTH follows the result exactly as it
// does for every other cell (TypeBeatHealthTest.FixingATypoLeavesHealthOneTierBelowTypingItRight).
//
// The cap is a STATE and not a counter: one flag on the cell, set only while the cell is still
// unjudged. Which settles both cycle shapes at once, and both are pinned here: wrong-fix-wrong-fix
// caps exactly once, and a cell judged CLEAN before it was ever spoiled keeps that clean judgement.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class CorrectionCreditTest
    {
        #region Engine fixture

        /// <summary>
        /// One line, "abcdefgh", cell i targeting 1000 + 500i, the same shape
        /// <see cref="ComboRestoreTest"/> uses. Line-granularity windows: Great [-250, 400],
        /// Ok [-600, 1000], Meh [-1200, 2000].
        /// </summary>
        private const string word = "abcdefgh";

        private static double target(int cellIndex) => 1000 + 500 * cellIndex;

        private static LyricBeatmap engineMap() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = string.Empty,
                AudioFileName = "a.mp3",
            },
            Lines = new List<LyricLine>
            {
                new LyricLine
                {
                    RawText = word,
                    StartTime = 1000,
                    EndTime = 60000,
                    SingEndTime = 5000,
                    Units = new[] { new TimedUnit { Text = word, StartTime = 1000, EndTime = 5000 } },
                },
            },
            Granularity = TimingGranularity.Line,
        };

        private static TypingEngine started(CorrectionCreditRule rule = CorrectionCreditRule.Capped)
        {
            var engine = new TypingEngine(engineMap()) { CorrectionCredit = rule };
            engine.Update(1000);
            return engine;
        }

        private static void typeCorrectly(TypingEngine engine, int from, int to)
        {
            for (int i = from; i < to; i++)
                Assert.That(engine.ProcessKey(word[i], target(i)), Is.True);
        }

        /// <summary>Type a wrong char into the cell the caret is on.</summary>
        private static void typo(TypingEngine engine, int cellIndex)
        {
            Assert.That(engine.CaretIndex, Is.EqualTo(cellIndex));
            Assert.That(engine.ProcessKey('z', target(cellIndex)), Is.True);
        }

        /// <summary>Backspace onto <paramref name="cellIndex"/> and type it correctly, at an offset from its target.</summary>
        private static void fix(TypingEngine engine, int cellIndex, double offsetMs = 0)
        {
            while (engine.CaretIndex > cellIndex)
                Assert.That(engine.ProcessBackspace(), Is.True);

            Assert.That(engine.ProcessKey(word[cellIndex], target(cellIndex) + offsetMs), Is.True);
        }

        /// <summary>The tier the last press was ANNOUNCED as, which is what the stage shows.</summary>
        private static JudgementType lastAnnounced(List<CharJudgement> judgements) => judgements[^1].Type;

        #endregion

        #region Account fixture

        private static LyricLine correctionLine() => new LyricLine
        {
            RawText = "hear from me all",
            StartTime = 0,
            EndTime = 40000,
            SingEndTime = 20000,
            Units = new[]
            {
                new TimedUnit { Text = "hear", StartTime = 0, EndTime = 5000 },
                new TimedUnit { Text = "from", StartTime = 5000, EndTime = 10000 },
                new TimedUnit { Text = "me", StartTime = 10000, EndTime = 15000 },
                new TimedUnit { Text = "all", StartTime = 15000, EndTime = 20000 },
            },
        };

        private static LyricBeatmap lyricBeatmap(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = string.Empty,
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        private static TypeBeatBeatmap built(LyricLine line)
        {
            var map = new TypeBeatBeatmap();
            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

            foreach (var hitObject in map.HitObjects)
                hitObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty(), CancellationToken.None);

            return map;
        }

        private static double[] cellTargets(LyricLine line)
            => new TypingEngine(lyricBeatmap(line)).Lines[0].Cells.Select(c => c.TargetTime).ToArray();

        private static Replay replay(params (double time, char c)[] presses)
        {
            var r = new Replay();
            r.Frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true, false));

            foreach ((double time, char c) in presses)
                r.Frames.Add(new TypeBeatReplayFrame(time, c));

            return r;
        }

        private const string correction_text = "hear from me all";

        /// <summary>
        /// Sixteen cells, every one struck on target, except that the cell at
        /// <paramref name="spoiledCell"/> is optionally spoiled and fixed. Both arms press that cell's
        /// AWARDED keystroke at exactly <paramref name="fixOffsetMs"/> past its target, so a clean run
        /// and a corrected one are compared on identical timing and the only difference is the detour.
        /// </summary>
        private static Replay run(int? spoiledCell, double fixOffsetMs)
        {
            double[] targets = cellTargets(correctionLine());
            var presses = new List<(double, char)>();

            for (int i = 0; i < correction_text.Length; i++)
            {
                if (i == spoiledCell)
                {
                    presses.Add((targets[i] + fixOffsetMs / 2, 'x'));
                    presses.Add((targets[i] + fixOffsetMs / 2 + 1, TypeBeatReplayFrame.BACKSPACE));
                }

                presses.Add((targets[i] + (i == spoiledCell ? fixOffsetMs : 0), correction_text[i]));
            }

            return replay(presses.ToArray());
        }

        /// <summary>
        /// The correction-heavy fixture the era pins run on: the 'm' of "me" spoiled and fixed 200 ms
        /// late (inside the Great window, so the cap bites) and the 'e' spoiled and fixed 1200 ms late
        /// (inside Meh, outside Ok, so the cap has nothing to take). Everything else on target.
        /// </summary>
        private static Replay correctionRun()
        {
            double[] targets = cellTargets(correctionLine());
            var presses = new List<(double, char)>();

            for (int i = 0; i < 10; i++)
                presses.Add((targets[i], correction_text[i]));

            presses.Add((targets[10], 'x'));
            presses.Add((targets[10] + 100, TypeBeatReplayFrame.BACKSPACE));
            presses.Add((targets[10] + 200, 'm'));

            presses.Add((targets[11], 'z'));
            presses.Add((targets[11] + 100, TypeBeatReplayFrame.BACKSPACE));
            presses.Add((targets[11] + 1200, 'e'));

            for (int i = 12; i < correction_text.Length; i++)
                presses.Add((targets[i], correction_text[i]));

            return replay(presses.ToArray());
        }

        private static TypeBeatReplayAccount score(Replay r, CorrectionCreditRule rule)
            => TypeBeatReplayScorer.Score(built(correctionLine()), Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix,
                SpaceTimingRule.Untimed, RateWindowRule.ScaledByRate, WordSkipRule.Reclaimable, ComboClaimRule.StreakedBreakWins,
                OffTimeRule.MehHit, rule);

        private static int count(TypeBeatReplayAccount account, HitResult result)
            => account.Statistics.GetValueOrDefault(result);

        #endregion

        // -----------------------------------------------------------------------------------------
        // The rule.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The whole of backlog 210, on the shape it came out of. The SAME sixteen keystrokes at the
        /// SAME times, except that one run spoils a cell and fixes it 200 ms after its target while
        /// the other simply types that cell 200 ms after its target. Both presses that earn the cell
        /// are struck at exactly the same moment, so under the old rule the two runs are graded
        /// identically: the detour was free.
        ///
        /// <para>The era arm is what makes that claim testable rather than historical, and it is
        /// checked in both directions: under <see cref="CorrectionCreditRule.Full"/> the corrected
        /// run's accuracy is bit-identical to the clean run's (the bug), and under the live
        /// <see cref="CorrectionCreditRule.Capped"/> it is strictly worse (the fix).</para>
        /// </summary>
        [Test]
        public void TheCapIsWhyThisFileExists()
        {
            var clean = run(spoiledCell: null, fixOffsetMs: 200);
            var corrected = run(spoiledCell: 10, fixOffsetMs: 200);

            var cleanAccount = score(clean, CorrectionCreditRule.Capped);
            var cappedAccount = score(corrected, CorrectionCreditRule.Capped);
            var fullAccount = score(corrected, CorrectionCreditRule.Full);

            Assert.Multiple(() =>
            {
                // The clean run: sixteen cells struck inside the Great window, so a perfect account.
                Assert.That(count(cleanAccount, HitResult.Great), Is.EqualTo(16));
                Assert.That(cleanAccount.Accuracy, Is.EqualTo(1).Within(1e-9));

                // Pre-210: the corrected cell was graded on the retype's own timing alone, so the
                // typo cost the play NO accuracy. This is the thing the task exists to stop.
                Assert.That(count(fullAccount, HitResult.Great), Is.EqualTo(16));
                Assert.That(fullAccount.Accuracy, Is.EqualTo(cleanAccount.Accuracy).Within(1e-9));

                // Today: the corrected cell is capped at Ok, so accuracy pays. Fifteen Greats plus
                // one Ok, over a perfect 4800.
                Assert.That(count(cappedAccount, HitResult.Great), Is.EqualTo(15));
                Assert.That(count(cappedAccount, HitResult.Ok), Is.EqualTo(1));
                Assert.That(cappedAccount.Accuracy, Is.EqualTo((15 * 300 + 100) / 4800.0).Within(1e-9));
                Assert.That(cappedAccount.Accuracy, Is.LessThan(cleanAccount.Accuracy));
                Assert.That(cappedAccount.TotalScore, Is.LessThan(cleanAccount.TotalScore));

                // One wrong keypress, and it is still counted: the cap prices the CORRECTION, and
                // the mistype stat prices the keypress, so both are on the record.
                Assert.That(cappedAccount.Mistypes, Is.EqualTo(1));
            });
        }

        /// <summary>
        /// The ordering the cap is chosen to produce, read straight off the accuracy weights a cell
        /// can carry: a clean Great is 300, a corrected cell is at most 100, an uncorrected typo is 50
        /// (<see cref="TypeBeatResultMapping.UNFIXED_TYPO"/>, re-weighted by
        /// <see cref="TypeBeatScoreProcessor.GetBaseScoreForResult"/>) and a miss is 0. Strictly
        /// decreasing, which is the property "perfect play beats corrected play beats an unfixed typo
        /// beats a dropped character" reduces to per cell.
        /// </summary>
        [Test]
        public void ThePerCellOrderingIsStrict()
        {
            var processor = new TypeBeatScoreProcessor(new TypeBeatRuleset());

            int clean = processor.GetBaseScoreForResult(HitResult.Great);
            int corrected = processor.GetBaseScoreForResult(HitResult.Ok);
            int unfixed = processor.GetBaseScoreForResult(TypeBeatResultMapping.UNFIXED_TYPO);
            int missed = processor.GetBaseScoreForResult(HitResult.Miss);

            Assert.Multiple(() =>
            {
                Assert.That(clean, Is.EqualTo(300));
                Assert.That(corrected, Is.EqualTo(100));
                Assert.That(unfixed, Is.EqualTo(50));
                Assert.That(missed, Is.Zero);

                Assert.That(clean, Is.GreaterThan(corrected));
                Assert.That(corrected, Is.GreaterThan(unfixed));
                Assert.That(unfixed, Is.GreaterThan(missed));
            });
        }

        /// <summary>
        /// It is a CAP and not a demotion: min(the retype's tier, Ok). A fix inside the Great window
        /// comes down to Ok, one already inside Ok stays exactly where it is, and one that only made
        /// the Meh window is left alone, because it is already below the cap and demoting it further
        /// would price a slow fix as though it were something else. Pinned against the same presses
        /// under <see cref="CorrectionCreditRule.Full"/>, which is the uncapped ladder.
        /// </summary>
        [TestCase(0, JudgementType.Great, JudgementType.Ok)]
        [TestCase(200, JudgementType.Great, JudgementType.Ok)]
        [TestCase(700, JudgementType.Ok, JudgementType.Ok)]
        [TestCase(1500, JudgementType.Meh, JudgementType.Meh)]
        public void TheCapIsAMinimumOverTheLadder(double fixOffsetMs, JudgementType uncapped, JudgementType capped)
        {
            Assert.Multiple(() =>
            {
                Assert.That(fixedTier(CorrectionCreditRule.Full, fixOffsetMs), Is.EqualTo(uncapped));
                Assert.That(fixedTier(CorrectionCreditRule.Capped, fixOffsetMs), Is.EqualTo(capped));
            });
        }

        private static JudgementType fixedTier(CorrectionCreditRule rule, double fixOffsetMs)
        {
            var engine = started(rule);
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;

            typeCorrectly(engine, 0, 3);
            typo(engine, 3);
            fix(engine, 3, fixOffsetMs);

            return lastAnnounced(judgements);
        }

        /// <summary>
        /// The cap moves the TIER, so what the stage announces and what the score processor stores
        /// are the same thing by construction rather than by two files agreeing. Both are checked
        /// here: the engine's own tier counts (which the results screen shows) and the osu result
        /// <see cref="TypeBeatResultMapping.CellResult"/> maps that tier to.
        /// </summary>
        [Test]
        public void TheAnnouncedJudgementIsTheStoredOne()
        {
            var engine = started();
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;

            typeCorrectly(engine, 0, 3);
            typo(engine, 3);
            fix(engine, 3);

            var counts = engine.BuildResults().Counts;

            Assert.Multiple(() =>
            {
                Assert.That(lastAnnounced(judgements), Is.EqualTo(JudgementType.Ok));
                Assert.That(counts[JudgementType.Great], Is.EqualTo(3), "the three clean cells, and not the fixed one");
                Assert.That(counts[JudgementType.Ok], Is.EqualTo(1));
                Assert.That(TypeBeatResultMapping.CellResult(lastAnnounced(judgements), TypoRule.Deferred), Is.EqualTo(HitResult.Ok));
            });
        }

        /// <summary>
        /// The DELTA is untouched, which is why the cap costs accuracy and nothing else: the sync
        /// readouts, the sync timeline and the grade computed from them measure where the player's
        /// hands were, and a fix struck dead on target really was struck dead on target. Only what
        /// the cell is WORTH moved.
        /// </summary>
        [Test]
        public void TheCapDoesNotMoveTheSyncReadouts()
        {
            var capped = started();
            var full = started(CorrectionCreditRule.Full);

            foreach (var engine in new[] { capped, full })
            {
                typeCorrectly(engine, 0, 3);
                typo(engine, 3);
                fix(engine, 3);
                typeCorrectly(engine, 4, word.Length);
            }

            Assert.Multiple(() =>
            {
                Assert.That(capped.BuildResults().SyncPercent, Is.EqualTo(full.BuildResults().SyncPercent).Within(1e-9));
                Assert.That(capped.BuildResults().SyncPercent, Is.EqualTo(100).Within(1e-9), "every press was dead on target");
                Assert.That(capped.Lines[0].Cells[3].JudgedDelta, Is.EqualTo(0));
            });
        }

        // -----------------------------------------------------------------------------------------
        // The cap is a STATE, not a counter.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Wrong, fix, wrong, fix on ONE cell caps exactly once, and can never demote below the min()
        /// rule however many times the player goes round. The second fix is a scoring-inert retype
        /// (the cell was already judged), so nothing new is counted, and the tier it ANNOUNCES has to
        /// be the capped Ok the cell actually stored: announcing the raw Great would show the player
        /// a judgement their score does not carry.
        /// </summary>
        [Test]
        public void TheCapIsAStateSoRepeatedCyclesCapOnce()
        {
            var engine = started();
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;

            typeCorrectly(engine, 0, 3);
            typo(engine, 3);
            fix(engine, 3);

            Assert.That(lastAnnounced(judgements), Is.EqualTo(JudgementType.Ok));

            // Round two on the same cell: spoil it again, and fix it again.
            Assert.That(engine.ProcessBackspace(), Is.True);
            typo(engine, 3);
            fix(engine, 3);

            var counts = engine.BuildResults().Counts;

            Assert.Multiple(() =>
            {
                Assert.That(lastAnnounced(judgements), Is.EqualTo(JudgementType.Ok), "the inert retype re-derives the capped award");
                Assert.That(counts[JudgementType.Ok], Is.EqualTo(1), "one cap, one count, however many cycles");
                Assert.That(counts[JudgementType.Great], Is.EqualTo(3));
                Assert.That(engine.Mistypes, Is.EqualTo(2), "the keypresses are counted twice, as they always were");
            });
        }

        /// <summary>
        /// Wrong, backspace, wrong AGAIN, then fix: still one cap. There is nothing here for a
        /// counter to double, which is the point of holding the rule as a flag on the cell.
        /// </summary>
        [Test]
        public void TwoWrongCharactersOnOneCellAreStillOneCap()
        {
            var engine = started();
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;

            typeCorrectly(engine, 0, 3);
            typo(engine, 3);
            Assert.That(engine.ProcessBackspace(), Is.True);
            typo(engine, 3);
            fix(engine, 3);

            var counts = engine.BuildResults().Counts;

            Assert.Multiple(() =>
            {
                Assert.That(lastAnnounced(judgements), Is.EqualTo(JudgementType.Ok));
                Assert.That(counts[JudgementType.Ok], Is.EqualTo(1));
                Assert.That(counts[JudgementType.Great], Is.EqualTo(3));
            });
        }

        /// <summary>
        /// The residue the cap's framing leaves, pinned as the documented behaviour it is. A cell
        /// typed CORRECTLY, backspaced into, spoiled and then retyped keeps its ORIGINAL clean
        /// judgement: the retype is inert (a cell takes only its first result, and that result was
        /// earned before the cell ever held a wrong character), so there is nothing for the cap to
        /// govern. The flag is set only while the cell is still unjudged, which is exactly what makes
        /// this case fall out rather than needing an arm of its own.
        ///
        /// <para>It is the right answer and not merely the reachable one: the player did type that
        /// character right, first time, at that timing, and going back over it afterwards cannot
        /// un-earn a judgement that was already awarded and already counted.</para>
        /// </summary>
        [Test]
        public void ACellJudgedCleanBeforeItEverHeldWrongKeepsItsCleanJudgement()
        {
            var engine = started();
            var judgements = new List<CharJudgement>();
            engine.CharJudged += judgements.Add;

            typeCorrectly(engine, 0, 4); // cell 3 is judged Great, cleanly
            Assert.That(lastAnnounced(judgements), Is.EqualTo(JudgementType.Great));

            // Back into it, spoil it, back out, and type it right again.
            Assert.That(engine.ProcessBackspace(), Is.True);
            typo(engine, 3);
            fix(engine, 3);

            var counts = engine.BuildResults().Counts;

            Assert.Multiple(() =>
            {
                Assert.That(lastAnnounced(judgements), Is.EqualTo(JudgementType.Great), "the clean judgement stands");
                Assert.That(counts[JudgementType.Great], Is.EqualTo(4));
                Assert.That(counts[JudgementType.Ok], Is.Zero);
            });
        }

        // -----------------------------------------------------------------------------------------
        // What the cap deliberately does NOT move.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Backlog 140 is untouched: the streak a wrong keypress broke is still resumed by the fix,
        /// and the resume still lands BEFORE the retype is judged, so the capped press is priced at
        /// the restored run. The cap costs accuracy; it does not quietly cost combo as well.
        /// </summary>
        [Test]
        public void TheCapLeavesTheComboRestoreAlone()
        {
            var engine = started();
            var restored = new List<int>();
            var judgements = new List<CharJudgement>();
            engine.ComboRestored += restored.Add;
            engine.CharJudged += judgements.Add;

            typeCorrectly(engine, 0, 3);
            typo(engine, 3);
            typeCorrectly(engine, 4, 6);
            fix(engine, 3);

            Assert.Multiple(() =>
            {
                Assert.That(restored, Is.EqualTo(new[] { 3 }));
                Assert.That(engine.Combo, Is.EqualTo(6), "3 restored + 2 earned since + the fix itself");
                Assert.That(lastAnnounced(judgements), Is.EqualTo(JudgementType.Ok));
                Assert.That(judgements[^1].ComboAfter, Is.EqualTo(6));
            });
        }

        /// <summary>
        /// A capped cell struck OFF TIME is left to <see cref="OffTimeRule"/>, which already answers
        /// below the cap. The two axes compose without either needing to know about the other: the
        /// cap is a min over the ladder and an off-time press is not on the ladder at all.
        /// </summary>
        [Test]
        public void AnOffTimeFixIsLeftToTheOffTimeRule()
        {
            Assert.Multiple(() =>
            {
                // MehLate is 2000 at Line granularity, so 2500 past the target is off the ladder.
                Assert.That(fixedTier(CorrectionCreditRule.Capped, 2500), Is.EqualTo(JudgementType.Lagging));
                Assert.That(fixedTier(CorrectionCreditRule.Full, 2500), Is.EqualTo(JudgementType.Lagging));

                // ...and it resolves as the off-time rule says, under both credit arms, because the
                // cap never reached the tier.
                Assert.That(TypeBeatResultMapping.CellResult(JudgementType.Lagging, TypoRule.Deferred), Is.EqualTo(HitResult.Meh));
                Assert.That(TypeBeatResultMapping.CellResult(JudgementType.Lagging, TypoRule.Deferred, OffTimeRule.BreaksCombo), Is.EqualTo(HitResult.Miss));
            });
        }

        /// <summary>
        /// Everything the submitted account carries EXCEPT the tier counts, the accuracy they produce
        /// and the total score is identical under the two arms, on a correction-heavy run. That is
        /// the reach of the axis, stated as an equality rather than as a claim: <c>max_combo</c>, the
        /// miss count, the mistype count, completion and rank all come out the same, because a capped
        /// cell is still a hit that extends the run and still counts as typed.
        /// </summary>
        [Test]
        public void TheCapCostsAccuracyAndTotalScoreAndNothingElse()
        {
            var r = correctionRun();
            var capped = score(r, CorrectionCreditRule.Capped);
            var full = score(r, CorrectionCreditRule.Full);

            Assert.Multiple(() =>
            {
                Assert.That(capped.MaxCombo, Is.EqualTo(full.MaxCombo));
                Assert.That(capped.MaxCombo, Is.EqualTo(16), "the fixes restore the run, under both arms");
                Assert.That(count(capped, HitResult.Miss), Is.EqualTo(count(full, HitResult.Miss)));
                Assert.That(count(capped, HitResult.Miss), Is.Zero);
                Assert.That(capped.Mistypes, Is.EqualTo(full.Mistypes));
                Assert.That(capped.Completion, Is.EqualTo(full.Completion).Within(1e-9));
                Assert.That(capped.Completion, Is.EqualTo(1).Within(1e-9));
                Assert.That(capped.Rank, Is.EqualTo(full.Rank));
                Assert.That(capped.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(capped.MaximumStatistics, Is.EquivalentTo(full.MaximumStatistics));

                // ...and the two things that DO move.
                Assert.That(capped.Accuracy, Is.LessThan(full.Accuracy));
                Assert.That(capped.TotalScore, Is.LessThan(full.TotalScore));
            });
        }

        // -----------------------------------------------------------------------------------------
        // The era gate.
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The load-bearing default, the same one <see cref="JudgementEraTest"/> holds for the other
        /// axes: a caller that does not name the switch is judged under the LIVE rule, so every
        /// existing call site kept its meaning when the parameter was added, and live play takes the
        /// engine's own default.
        /// </summary>
        [Test]
        public void TheCorrectionEraDefaultsToTheLiveRule()
        {
            var r = correctionRun();

            var implicitEra = TypeBeatReplayScorer.Score(built(correctionLine()), Array.Empty<Mod>(), r, TypoRule.Deferred, ComboRestoreRule.OnFix);
            var explicitLive = score(r, CorrectionCreditRule.Capped);

            Assert.Multiple(() =>
            {
                Assert.That(implicitEra.Statistics, Is.EquivalentTo(explicitLive.Statistics));
                Assert.That(implicitEra.TotalScore, Is.EqualTo(explicitLive.TotalScore));
                Assert.That(implicitEra.Accuracy, Is.EqualTo(explicitLive.Accuracy));

                Assert.That(new TypingEngine(lyricBeatmap()).CorrectionCredit, Is.EqualTo(CorrectionCreditRule.Capped));
            });
        }

        /// <summary>
        /// <see cref="CorrectionCreditRule.Full"/> re-derives the account a pre-210 client submitted,
        /// BYTE for byte. The numbers below were captured off the shipped code before the cap existed,
        /// on this exact fixture, which is what makes this a reproduction pin and not a restatement of
        /// the implementation: the recalculation tool's reproduce gate compares precisely these
        /// quantities against the stored row, and every one of them has to come back unchanged for a
        /// row that is not corrupt.
        /// </summary>
        [Test]
        public void TheFullEraReproducesTheAccountStoredBeforeTheCap()
        {
            var account = score(correctionRun(), CorrectionCreditRule.Full);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Great), Is.EqualTo(15));
                Assert.That(count(account, HitResult.Ok), Is.Zero);
                Assert.That(count(account, HitResult.Meh), Is.EqualTo(1));
                Assert.That(count(account, HitResult.Miss), Is.Zero);
                Assert.That(account.Mistypes, Is.EqualTo(2));
                Assert.That(account.MaxCombo, Is.EqualTo(16));
                Assert.That(account.TotalScore, Is.EqualTo(856625));
                Assert.That(account.Accuracy, Is.EqualTo(4550 / 4800.0).Within(1e-9));
                Assert.That(account.Completion, Is.EqualTo(1).Within(1e-9));
                Assert.That(account.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(account.UnconsumedFrames, Is.Zero, "the replay round-trips whole under the stored arm");
            });
        }

        /// <summary>
        /// The same fixture under the live arm, spelled out so the DIFFERENCE the era makes is on the
        /// record rather than only its direction. One Great becomes an Ok (the fix inside the Great
        /// window) and the Meh-timed fix is untouched, so accuracy falls by 200 of the cell's 300 and
        /// nothing else about the account moves. The replay round-trips whole under this arm too.
        /// </summary>
        [Test]
        public void TheCappedEraGradesTheSameRunOneTierDown()
        {
            var account = score(correctionRun(), CorrectionCreditRule.Capped);

            Assert.Multiple(() =>
            {
                Assert.That(count(account, HitResult.Great), Is.EqualTo(14));
                Assert.That(count(account, HitResult.Ok), Is.EqualTo(1));
                Assert.That(count(account, HitResult.Meh), Is.EqualTo(1), "the slow fix was already below the cap");
                Assert.That(count(account, HitResult.Miss), Is.Zero);
                Assert.That(account.Mistypes, Is.EqualTo(2));
                Assert.That(account.MaxCombo, Is.EqualTo(16));
                Assert.That(account.Accuracy, Is.EqualTo(4350 / 4800.0).Within(1e-9));
                Assert.That(account.Completion, Is.EqualTo(1).Within(1e-9));
                Assert.That(account.Rank, Is.EqualTo(ScoreRank.X));
                Assert.That(account.UnconsumedFrames, Is.Zero);
            });
        }

        /// <summary>
        /// The credit axis is INERT for a run that never fixes a typo, which is what lets the
        /// recalculation tool set the stored-era arm unconditionally instead of first working out
        /// whether the row contains a correction. Same claim as
        /// <see cref="JudgementEraTest.TheRateEraChangesNothingWithoutARateMod"/>, and pinned for the
        /// same reason.
        /// </summary>
        [Test]
        public void TheCreditEraChangesNothingWithoutACorrection()
        {
            var r = run(spoiledCell: null, fixOffsetMs: 200);

            var capped = score(r, CorrectionCreditRule.Capped);
            var full = score(r, CorrectionCreditRule.Full);

            Assert.Multiple(() =>
            {
                Assert.That(capped.Statistics, Is.EquivalentTo(full.Statistics));
                Assert.That(capped.MaxCombo, Is.EqualTo(full.MaxCombo));
                Assert.That(capped.TotalScore, Is.EqualTo(full.TotalScore));
                Assert.That(capped.Accuracy, Is.EqualTo(full.Accuracy));
            });
        }

        /// <summary>
        /// An UNCORRECTED typo is not this axis's business: it never earns a judgement at all, so it
        /// still resolves at the seal as <see cref="TypeBeatResultMapping.UNFIXED_TYPO"/> under both
        /// arms. Worth its own pin because the cap and the seal path are two answers to the same
        /// question (what a spoiled cell is worth) and only one of them moved.
        /// </summary>
        [Test]
        public void AnUncorrectedTypoIsUntouchedByEitherArm()
        {
            double[] targets = cellTargets(correctionLine());
            var presses = new List<(double, char)>();

            for (int i = 0; i < correction_text.Length; i++)
                presses.Add((targets[i], i == 10 ? 'x' : correction_text[i]));

            var r = replay(presses.ToArray());

            var capped = score(r, CorrectionCreditRule.Capped);
            var full = score(r, CorrectionCreditRule.Full);

            Assert.Multiple(() =>
            {
                Assert.That(count(capped, TypeBeatResultMapping.UNFIXED_TYPO), Is.EqualTo(1));
                Assert.That(capped.Statistics, Is.EquivalentTo(full.Statistics));
                Assert.That(capped.TotalScore, Is.EqualTo(full.TotalScore));
                Assert.That(capped.Completion, Is.EqualTo(15 / 16.0).Within(1e-9));
            });
        }
    }
}
