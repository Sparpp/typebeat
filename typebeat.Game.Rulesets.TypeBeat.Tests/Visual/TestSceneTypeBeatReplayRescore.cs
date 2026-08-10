// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
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

        /// <summary>The (char, time) pairs fed to the engine, in order: the replay of this run.</summary>
        private readonly List<TypeBeatReplayFrame> frames = new List<TypeBeatReplayFrame>();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
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
                frames.Clear();
                frames.Add(TypeBeatReplayFrame.CreateConfigFrame(0, true));
            });

            AddStep("load player", () => LoadPlayer());
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line active", () => engine.ActiveLineIndex == 0);
            AddAssert("the default wrong-key model", () => engine.AllowWrongInput);
        }

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

        /// <summary>Run line 0 out of time so it seals, which costs no keystroke and no frame.</summary>
        private void sealLineZero() => AddStep("run line 0 out of time", () => engine.Update(line_zero_end + 1));

        /// <summary>Type line 1's single cell, at the moment the seal ran the clock to.</summary>
        private void typeLineOneCell()
        {
            AddUntilStep("line 1 active", () => engine.ActiveLineIndex == 1);
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
        });

        private List<KeyValuePair<HitResult, int>> liveStatistics = null!;
        private List<KeyValuePair<HitResult, int>> liveMaximumStatistics = null!;
        private int liveMaxCombo;
        private double liveAccuracy;
        private ScoreRank liveRank;
        private long liveTotalScore;
        private long liveTotalScoreWithoutMods;

        /// <summary>
        /// A typo typed through and then FIXED: the run backlog 109 changed the pricing of, and the
        /// one a harness gets wrong first if it mirrors the drawable layer incorrectly (the cell
        /// recovers, the mistype and its combo break do not).
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

            AddAssert("the run recovered the cell", () =>
                liveStatistics.Single(kvp => kvp.Key == HitResult.Great).Value == 12
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
        /// mark coming apart, the live <c>TypeBeatPlayfield.onLineSealed</c> and the harness's own
        /// <c>CellRegistry.Seal</c>: they address the same cell by (line, cell), and if either
        /// stopped marking it, its max_combo would move off 10 and the equality below would
        /// fail.</para>
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
        /// Re-derives the account from the replay alone and holds every submitted field against the
        /// live processor's.
        /// </summary>
        private void compare()
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
                    TypoRule.Deferred);
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

            // The run really did contain the thing these scenes are about, so the equality above is
            // not equality between two empty accounts.
            AddAssert("the run carried a mistype and a real combo", () =>
                rescored.Mistypes == 1 && rescored.MaxCombo >= 5);
        }

        private static List<KeyValuePair<HitResult, int>> nonZero(IReadOnlyDictionary<HitResult, int> counts)
            => counts.Where(kvp => kvp.Value != 0).OrderBy(kvp => kvp.Key).ToList();
    }
}
