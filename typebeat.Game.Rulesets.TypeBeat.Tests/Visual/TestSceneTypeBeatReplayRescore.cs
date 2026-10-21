// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.Scoring;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 114: the fidelity proof for <see cref="TypeBeatReplayScorer"/>.
    ///
    /// <para>Score recalculation only means anything if the headless harness produces the SAME
    /// account a real client submits. That is not provable from the engine alone: the submitted
    /// <c>statistics</c>, <c>max_combo</c> and <c>total_score</c> come out of osu's
    /// <see cref="ScoreProcessor"/>, driven by the DRAWABLE layer, and backlog 109's change lives
    /// entirely in that layer. So each test here plays a run into a live
    /// <see cref="typebeat.Game.Screens.Play.Player"/> (its real playfield, its real
    /// <see cref="TypeBeatScoreProcessor"/>), then re-derives the account from the (char, time)
    /// sequence alone and holds the two against each other field by field.</para>
    ///
    /// <para>The keystrokes are fed straight to the engine at exact cell target times, which is
    /// what lets a run of twelve cells be scripted deterministically; the same pairs become the
    /// replay the harness reads. That the RECORDER writes exactly these pairs for real key presses,
    /// and that feeding them back reproduces engine state, is
    /// <c>TestSceneTypeBeatReplayRecording</c>'s job; what these scenes add is the layer above it,
    /// where the same pairs become a submitted account.</para>
    ///
    /// <para>Nothing here hardcodes a judgement tier. The assertion is EQUALITY between two
    /// independent computations of one run, which is exactly the property recalculation needs.</para>
    /// </summary>
    public partial class TestSceneTypeBeatReplayRescore : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        /// <summary>The submitted numbers are the subject, so the play must carry no mods at all
        /// (the base would otherwise auto-append NoFail).</summary>
        protected override bool AllowFail => true;

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;

        /// <summary>
        /// Twelve cells on line 0 over [0, 240000] so every one can be struck dead on its target,
        /// plus a short line 1 so sealing line 0 does not end the play. The same shape
        /// <c>TestSceneTypeBeatTypoDeferral</c> uses, for the same reason: it is the smallest map
        /// on which the two combo readings differ by an unmistakable margin.
        /// </summary>
        private const string word = "abcdefghijkl";

        private const double line_zero_end = 300000;

        /// <summary>Line 0's text for the next player load. The word-skip scenes need word gaps.</summary>
        private string lineZeroText = word;

        /// <summary>Line 1's text for the next player load. The back-dated seal scene needs cells
        /// typed on both sides of line 0's seal.</summary>
        private string lineOneText = "z";

        /// <summary>The (char, time) pairs fed to the engine, in order: the replay of this run.</summary>
        private readonly List<TypeBeatReplayFrame> frames = new List<TypeBeatReplayFrame>();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var first = new LyricLine
            {
                RawText = lineZeroText,
                StartTime = 0,
                EndTime = line_zero_end,
                SingEndTime = 240000,
                Units = new[] { new TimedUnit { Text = lineZeroText, StartTime = 0, EndTime = 240000 } },
            };

            var second = new LyricLine
            {
                RawText = lineOneText,
                StartTime = line_zero_end,
                EndTime = 600000,
                SingEndTime = 400000,
                Units = new[] { new TimedUnit { Text = lineOneText, StartTime = line_zero_end, EndTime = 400000 } },
            };

            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>
                {
                    new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = first, Granularity = TimingGranularity.Line },
                    new TypeBeatHitObject { StartTime = line_zero_end, LineIndex = 1, Line = second, Granularity = TimingGranularity.Line },
                },
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "ReplayRescore";
            return beatmap;
        }

        private void loadPlayer()
        {
            AddStep("reset the scripted replay", () =>
            {
                lineZeroText = word;
                lineOneText = "z";
                frames.Clear();
                frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));
            });

            AddStep("load player", () => LoadPlayer());
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line active", () => engine.ActiveLineIndex == 0);
            AddAssert("the default wrong-key model", () => engine.AllowWrongInput);
        }

        /// <summary>
        /// Loads a player on the given map and opens the replay with the two CONFIG frames
        /// <c>TypeBeatReplayRecorder</c> writes for the engine the live stack actually built, so the
        /// rescore runs under every era the live run did (the back-dated seal break and the lossless
        /// skip reclaim above all, which the bare frame the scenes above use leaves clear).
        /// </summary>
        private void loadRecordedPlayer(string lineZero, string lineOne, bool spaceSkipsWord = false, params Mod[] mods)
        {
            AddStep("reset the scripted replay", () =>
            {
                lineZeroText = lineZero;
                lineOneText = lineOne;
                frames.Clear();
            });

            AddStep("load player", () => LoadPlayer(mods));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line active", () => engine.ActiveLineIndex == 0);

            AddStep("write the recorder's CONFIG frames", () =>
            {
                engine.SpaceSkipsWord = spaceSkipsWord;

                frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, engine.AllowWrongInput, engine.SpaceSkipsWord, engine.SyllableTiming, engine.WrongInputOnWordGaps, engine.StrictSpaces, engine.CharTimedStretch, flexibleLines: engine.FlexibleLineSnap, boundedRush: engine.BoundedRush, firstCharTiming: engine.FirstCharTiming, backDatedSealBreak: engine.BackDatedSealBreak, losslessSkipReclaim: engine.LosslessSkipReclaim, foldsDisplacedClaim: engine.FoldsDisplacedClaim, unhalvedHardRockWindows: engine.UnhalvedHardRockWindows, manualNewlines: engine.ManualNewlines, newlineOnTypedLetter: engine.NewlineOnTypedLetter, firstLineLeadIn: engine.FirstLineLeadIn));
                frames.Add(TypeBeatReplayFrame.CreateExtendedConfigFrame(0, rushCapCostsAccuracy: engine.RushCapCostsAccuracy, inputEra2: engine.InputEra2, authoredSyllablesOnly: engine.AuthoredSyllablesOnly, alignSubdivisionTargets: engine.AlignSubdivisionTargets, earlyFinish: engine.EarlyFinish, japaneseRomajiInput: engine.JapaneseRomajiInput, japaneseWordTiming: engine.JapaneseWordTiming, japaneseInputEra2: engine.JapaneseInputEra2, japaneseInputEra3: engine.JapaneseInputEra3, keyTimeJudgement: engine.KeyTimeJudgement));
            });

            AddAssert("the live stack back-dates the seal break", () => engine.BackDatedSealBreak);
        }

        private static Mod[] pinnedCaret(bool pinned) => pinned ? new Mod[] { new TypeBeatModFletcher() } : Array.Empty<Mod>();

        /// <summary>Type line 0 from wherever its caret stands to its end, each cell dead on its own target.</summary>
        private void typeOutLineZero() => AddStep("type out the rest of line 0", () =>
        {
            while (engine.ActiveLineIndex == 0 && engine.CaretIndex < engine.Lines[0].Cells.Count)
            {
                var cell = engine.Lines[0].Cells[engine.CaretIndex];
                press(cell.Expected, cell.TargetTime);
            }
        });

        /// <summary>Type cells [from, to) of line 1, each dead on its own target.</summary>
        private void typeLineOne(int from, int to) => AddStep($"type line 1's cells {from}..{to - 1}", () =>
        {
            for (int i = from; i < to; i++)
            {
                var cell = engine.Lines[1].Cells[i];
                press(cell.Expected, cell.TargetTime);
            }
        });

        /// <summary>Type cells [from, to) of line 0 correctly, each dead on its own target.</summary>
        private void typeCorrectly(int from, int to) => AddStep($"type cells {from}..{to - 1}", () =>
        {
            for (int i = from; i < to; i++)
            {
                var cell = engine.Lines[0].Cells[i];
                press(cell.Expected, cell.TargetTime);
            }
        });

        /// <summary>'q' is in neither line, so it is reliably wrong wherever the caret is.</summary>
        private void typeTypoOnCell(int index) =>
            AddStep($"type a wrong char onto cell {index}", () => press('q', engine.Lines[0].Cells[index].TargetTime));

        private void backspaceOnCell(int index) => AddStep($"backspace over cell {index}", () =>
        {
            engine.Update(engine.Lines[0].Cells[index].TargetTime);

            if (engine.ProcessBackspace())
                frames.Add(new TypeBeatReplayFrame(engine.Lines[0].Cells[index].TargetTime, TypeBeatReplayFrame.BACKSPACE));
        });

        /// <summary>
        /// Run line 0 past its DRAG CUTOFF so it seals inside this very step, costing no keystroke
        /// and no frame. Its <c>EndTime</c> alone is not out of time under the default stack: the
        /// unpinned caret holds a finished line the player is standing on open for another
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> past <c>EndTime + SealGraceMs</c>, so
        /// an update to <c>EndTime + 1</c> sealed NOTHING and the scene silently fell back on the
        /// real gameplay clock fast-forwarding all 300 s of map to the cutoff, a wait whose wall
        /// length scales with machine load and is what took this scene to ~9 s in isolation and
        /// over the 10 s until-step budget under a loaded full suite. Driven past the cutoff, the
        /// seal and its hand-over of the caret are synchronous and nothing waits on the clock.
        /// </summary>
        private void sealLineZero()
        {
            AddStep("run line 0 past its drag cutoff", () => engine.Update(line_zero_end + TypingEngine.FLETCHER_DRAG_GRACE_MS + 1));
            AddAssert("the seal really happened in that step", () =>
                engine.NextUnsealedLineIndex == 1 && engine.ActiveLineIndex == 1);
        }

        /// <summary>
        /// Type line 1's single cell, dead on its own start. The seal's hand-over already put the
        /// caret on line 1 (asserted above), and the press time is the cell's own target rather
        /// than the engine's, so the recorded frame and its judgement are identical however far
        /// past the cutoff the seal step ran the engine.
        /// </summary>
        private void typeLineOneCell()
        {
            AddStep("type line 1's cell", () => press(engine.Lines[1].Cells[0].Expected, line_zero_end + 1));
        }

        /// <summary>
        /// Exactly what <c>TypeBeatKeyHandler</c> does for one keystroke: advance the engine to the
        /// press time, judge, and record the pair if the engine actually consumed it.
        /// </summary>
        private void press(char c, double time)
        {
            engine.Update(time);

            if (engine.ProcessKey(c, time))
                frames.Add(new TypeBeatReplayFrame(time, c));
        }

        /// <summary>
        /// Runs the WHOLE map out of time and captures the submitted account in the same step.
        ///
        /// <para>Both halves matter. The map has to finish, because the harness always plays a
        /// replay to the end of the beatmap and an unsealed line 1 would leave the live processor a
        /// cell and a line short. And the capture has to happen here, because every consequence of
        /// that <c>Update</c> (seal, deferred misses, the line's own inert result) is raised
        /// SYNCHRONOUSLY through the playfield into the score processor, so this is the last moment
        /// the values are readable without racing the results screen the completed play pushes.</para>
        /// </summary>
        private void runTheMapOut() => AddStep("run the map out of time", () =>
        {
            engine.Update(600000 + 10000);

            liveStatistics = nonZero(Player.ScoreProcessor.Statistics);
            liveMaximumStatistics = nonZero(Player.ScoreProcessor.MaximumStatistics);
            liveMaxCombo = Player.ScoreProcessor.HighestCombo.Value;
            liveAccuracy = Player.ScoreProcessor.Accuracy.Value;
            liveRank = Player.ScoreProcessor.Rank.Value;
            liveTotalScore = Player.ScoreProcessor.TotalScore.Value;
            liveTotalScoreWithoutMods = Player.ScoreProcessor.TotalScoreWithoutMods.Value;
            liveCombo = Player.ScoreProcessor.Combo.Value;
            engineCombo = engine.Combo;
        });

        private List<KeyValuePair<HitResult, int>> liveStatistics = null!;
        private List<KeyValuePair<HitResult, int>> liveMaximumStatistics = null!;
        private int liveMaxCombo;
        private double liveAccuracy;
        private ScoreRank liveRank;
        private long liveTotalScore;
        private long liveTotalScoreWithoutMods;
        private int liveCombo;
        private int engineCombo;

        /// <summary>
        /// A typo typed through and then FIXED: the run backlog 109 changed the pricing of, and the
        /// one a harness gets wrong first if it mirrors the drawable layer incorrectly (the cell
        /// recovers, the mistype and its combo break do not).
        ///
        /// <para>Since backlog 210 it is also the round-trip that pins the CORRECTION CAP across the
        /// two engines: the fixed cell is an Ok on both sides, and it has to be the SAME Ok, because
        /// the cap is applied inside the engine and both the live playfield and the headless scorer
        /// run one.</para>
        /// </summary>
        [Test]
        public void TestAFixedTypoRescoresToTheLivePlayersAccount()
        {
            loadPlayer();

            typeCorrectly(0, 2);
            typeTypoOnCell(2);
            backspaceOnCell(2);
            typeCorrectly(2, 12);
            runTheMapOut();

            AddAssert("the run recovered the cell, capped at Ok", () =>
                liveStatistics.Single(kvp => kvp.Key == HitResult.Great).Value == 11
                && liveStatistics.Single(kvp => kvp.Key == HitResult.Ok).Value == 1
                && liveStatistics.Single(kvp => kvp.Key == HitResult.ComboBreak).Value == 1);

            compare();
        }

        /// <summary>
        /// The same typo left uncorrected: the cell resolves at the SEAL, so the account depends on
        /// the harness getting the seal seam right as well as the keypress one.
        /// </summary>
        [Test]
        public void TestAnUncorrectedTypoRescoresToTheLivePlayersAccount()
        {
            loadPlayer();

            typeCorrectly(0, 2);
            typeTypoOnCell(2);
            typeCorrectly(3, 12);
            runTheMapOut();

            // The typo resolves as an unfixed typo (backlog 124), line 1's untyped cell as a real
            // miss: the seal seam has to hand out BOTH results, from the same loop, for this to hold.
            AddAssert("the typo took its own result, and line 1's cell a miss", () =>
                liveStatistics.Single(kvp => kvp.Key == TypeBeatResultMapping.UNFIXED_TYPO).Value == 1
                && liveStatistics.Single(kvp => kvp.Key == HitResult.Miss).Value == 1);

            compare();
        }

        /// <summary>
        /// The uncorrected typo with line 1 typed as well, which is the run backlog 122 changed and
        /// the only shape on which the combo treatment of the seal's result is observable at all. In
        /// the scene above line 1 is never typed, so its own miss breaks the combo whatever the seal
        /// did; here the run has to survive the seal and extend to 10.
        ///
        /// <para>That makes this the scene that would catch the two wirings of the combo-neutral
        /// mark coming apart, the live <c>TypeBeatPlayfield.applySealResults</c> and the harness's
        /// own <c>CellRegistry.Seal</c> (both through <see cref="TypeBeatComboFeed.SealedCellResult"/>):
        /// they address the same cell by (line, cell), and if either stopped marking it, its
        /// max_combo would move off 10 and the equality below would fail.</para>
        /// </summary>
        [Test]
        public void TestAComboCarriedAcrossASealRescoresToTheLivePlayersAccount()
        {
            loadPlayer();

            typeCorrectly(0, 2);
            typeTypoOnCell(2);
            typeCorrectly(3, 12);
            sealLineZero();
            typeLineOneCell();
            runTheMapOut();

            AddAssert("the run survived the seal and took line 1's cell", () => liveMaxCombo == 10);

            compare();
        }

        /// <summary>
        /// A typo deep inside the line, so a long combo run precedes the break and a second run
        /// follows it. Also pins that what the scripted presses recorded is exactly the frame
        /// sequence <c>TypeBeatKeyHandler</c> would have handed the recorder for this run: one
        /// frame per EFFECTIVE input, config frame first, wrong char included.
        /// </summary>
        [Test]
        public void TestATypoMidLineRescoresToTheLivePlayersAccount()
        {
            loadPlayer();

            typeCorrectly(0, 6);
            typeTypoOnCell(6);
            typeCorrectly(7, 12);
            runTheMapOut();

            AddAssert("the recorded frame sequence is one per effective input", () =>
                string.Concat(frames.Select(f => f.Character)) == "\0abcdefqhijkl");

            compare();
        }

        /// <summary>
        /// A word skip the player never came back for (backlog 167): the skip's one combo break is
        /// written by hand at <see cref="TypingEngine.WordAbandoned"/>, and the Miss the abandoned
        /// cell finally takes at the seal is marked combo-neutral at
        /// <see cref="TypingEngine.AbandonSealed"/> so it cannot take that break again. Both writes
        /// are hand-mirrored on each side, so this is the scene that holds the two copies together.
        ///
        /// <para>Run under both carets. Unpinned (the default stack) the abandoned cell's own
        /// judgement arrives with a combo of 0, so the rush-cap arm writes the same break a moment
        /// later; only the pinned caret leaves the skip's write as the one thing breaking osu's
        /// combo.</para>
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void TestAnAbandonedWordSkipRescoresToTheLivePlayersAccount(bool pinned)
        {
            loadRecordedPlayer("abc def ghi", "z", spaceSkipsWord: true, mods: pinnedCaret(pinned));

            typeCorrectly(0, 2);
            AddStep("skip the rest of the first word", () => press(' ', engine.Lines[0].Cells[2].TargetTime));
            AddAssert("the skip abandoned cell 2", () => engine.Lines[0].Cells[2].State == CellState.Abandoned);
            typeOutLineZero();
            sealLineZero();
            typeLineOneCell();
            runTheMapOut();

            AddAssert("the abandoned cell took the run's only miss", () =>
                liveStatistics.Single(kvp => kvp.Key == HitResult.Miss).Value == 1);

            compare(expectedMistypes: 0);
        }

        /// <summary>
        /// The same skip walked back into and retyped: the cell leaves the phantom state through
        /// <see cref="TypingEngine.AbandonReclaimed"/> and the retype resumes the streak the skip
        /// broke (<see cref="TypingEngine.ComboRestored"/>), so the hand-written break at the skip
        /// has to be undone by the hand-written restore on both sides alike.
        /// </summary>
        [TestCase(false)]
        [TestCase(true)]
        public void TestAReclaimedWordSkipRescoresToTheLivePlayersAccount(bool pinned)
        {
            loadRecordedPlayer("abc def ghi", "z", spaceSkipsWord: true, mods: pinnedCaret(pinned));

            typeCorrectly(0, 2);
            AddStep("skip the rest of the first word", () => press(' ', engine.Lines[0].Cells[2].TargetTime));
            AddAssert("the skip abandoned cell 2", () => engine.Lines[0].Cells[2].State == CellState.Abandoned);
            AddStep("backspace back into the skipped word", () =>
            {
                double time = engine.Lines[0].Cells[2].TargetTime;

                while (engine.CaretIndex > 2 && engine.ProcessBackspace())
                    frames.Add(new TypeBeatReplayFrame(time, TypeBeatReplayFrame.BACKSPACE));
            });
            AddAssert("the cell is typeable again", () => engine.CaretIndex == 2 && engine.Lines[0].Cells[2].State == CellState.Untyped);
            typeOutLineZero();
            sealLineZero();
            typeLineOneCell();
            runTheMapOut();

            AddAssert("nothing was missed and the run survived the skip", () =>
                liveStatistics.All(kvp => kvp.Key != HitResult.Miss) && liveMaxCombo == 12);

            compare(expectedMistypes: 0);
        }

        /// <summary>
        /// A line given up with Enter and sealed after the player has built a run on the next one
        /// (backlog 259): the seal's one break is written by hand as the run the engine is left
        /// holding, and every seal miss is marked combo-neutral, so the cells typed on line 1 before
        /// the seal must survive it on both sides. The only scene in which a seal's break leaves a
        /// combo standing, which is what tells the back-dated write from a plain wipe.
        /// </summary>
        [Test]
        public void TestABackDatedSealBreakRescoresToTheLivePlayersAccount()
        {
            loadRecordedPlayer(word, "wxyz");

            typeCorrectly(0, 6);
            AddStep("give up the rest of line 0", () =>
            {
                double time = engine.Lines[0].Cells[6].TargetTime;
                engine.Update(time);

                if (engine.ProcessEnter(time))
                    frames.Add(new TypeBeatReplayFrame(time, TypeBeatReplayFrame.ENTER));
            });
            AddStep("reach line 1", () => engine.Update(engine.Lines[1].Cells[0].TargetTime));
            AddAssert("line 1 is active and line 0 still open", () => engine.ActiveLineIndex == 1 && engine.NextUnsealedLineIndex == 0);
            typeLineOne(0, 1);
            sealLineZero();
            AddAssert("the seal left line 1's run standing on both accounts", () =>
                engine.Combo == 1 && Player.ScoreProcessor.Combo.Value == 1);
            typeLineOne(1, 4);
            runTheMapOut();

            AddAssert("line 0's six untyped cells were missed", () =>
                liveStatistics.Single(kvp => kvp.Key == HitResult.Miss).Value == 6);

            compare(expectedMistypes: 0);
        }

        /// <summary>
        /// A wrong key REJECTED under Gatekeeper. Its break rides on <see cref="TypingEngine.Mistyped"/>
        /// live, while the scorer also listens on <see cref="TypingEngine.WrongKeyRejected"/> for the
        /// pre-109 rule; under the live rule only the first may write, so the two sides must agree.
        /// </summary>
        [Test]
        public void TestARejectedWrongKeyRescoresToTheLivePlayersAccount()
        {
            loadRecordedPlayer(word, "z", mods: new TypeBeatModGatekeeper());

            AddAssert("the strict wrong-key model", () => !engine.AllowWrongInput);
            typeCorrectly(0, 3);
            AddStep("press a wrong key on cell 3", () => press('q', engine.Lines[0].Cells[3].TargetTime));
            typeCorrectly(3, 12);
            sealLineZero();
            typeLineOneCell();
            runTheMapOut();

            AddAssert("the rejection broke the run", () => liveMaxCombo == 10);

            compare();
        }

        /// <summary>
        /// Re-derives the account from the replay alone and holds every submitted field against the
        /// live processor's.
        /// </summary>
        private void compare(int expectedMistypes = 1)
        {
            TypeBeatReplayAccount rescored = null!;

            AddStep("rescore from the replay alone", () =>
            {
                var replay = new Replay();
                replay.Frames.AddRange(frames);

                rescored = TypeBeatReplayScorer.Score(
                    Player.GameplayState.Beatmap,
                    Player.GameplayState.Mods,
                    replay,
                    TypoRule.Deferred,
                    ComboRestoreRule.OnFix);
            });

            AddAssert("the replay reproduces the submitted statistics", () =>
                nonZero(rescored.Statistics).SequenceEqual(liveStatistics));

            AddAssert("...and maximum_statistics", () =>
                nonZero(rescored.MaximumStatistics).SequenceEqual(liveMaximumStatistics));

            AddAssert("...and max_combo", () => rescored.MaxCombo == liveMaxCombo);

            AddAssert("...and accuracy, rank and total score", () =>
                rescored.Accuracy == liveAccuracy
                && rescored.Rank == liveRank
                && rescored.TotalScore == liveTotalScore
                && rescored.TotalScoreWithoutMods == liveTotalScoreWithoutMods);

            AddAssert("every recorded frame was consumed", () => rescored.UnconsumedFrames == 0);

            // The combo the live processor ended on is the engine's: every hand-written write kept
            // the two together.
            AddAssert("the live combo is the engine's", () => liveCombo == engineCombo);

            // The run really did contain the thing these scenes are about, so the equality above is
            // not equality between two empty accounts.
            AddAssert("the run carried its mistypes and a real combo", () =>
                rescored.Mistypes == expectedMistypes && rescored.MaxCombo >= 5);
        }

        private static List<KeyValuePair<HitResult, int>> nonZero(IReadOnlyDictionary<HitResult, int> counts)
            => counts.Where(kvp => kvp.Value != 0).OrderBy(kvp => kvp.Key).ToList();
    }
}
