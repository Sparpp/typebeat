// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Screens;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Scoring.Legacy;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Modeled on <see cref="typebeat.Game.Tests.Visual.ReplayStabilityTestScene"/>: a scripted
    /// replay (correct chars, a rejected wrong char, a backspace + inert retype, and a missed
    /// char) is played back through a real <see cref="ReplayPlayer"/>, then encoded to the legacy
    /// .osr format, decoded back, and played again. Both runs must produce identical judgements
    /// AND identical total score / accuracy / max combo, pinning that the .osr round-trip is
    /// lossless for typing frames and that playback is deterministic (the recalculation contract).
    /// </summary>
    [HeadlessTest]
    public partial class TestSceneTypeBeatReplayStability : RateAdjustedBeatmapTestScene
    {
        private ReplayPlayer currentPlayer = null!;
        private readonly List<HitResult> results = new List<HitResult>();

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        [Test]
        public void TestScriptedReplaySurvivesLegacyRoundTrip()
        {
            IBeatmap beatmap = createBeatmap();
            Replay replay = createReplay();

            var expectedResults = new[]
            {
                // line 0 "ab": both correct at target (the wrong 'x' is rejected without a result;
                // the backspace + retype of 'a' is scoring-inert), line resolves IgnoreHit.
                HitResult.Great, HitResult.Great, HitResult.IgnoreHit,
                // line 1 "cd": 'c' correct, 'd' never typed (Miss at seal), line IgnoreHit.
                HitResult.Great, HitResult.Miss, HitResult.IgnoreHit,
            };

            Score originalScore = null!;
            Score decodedScore = null!;

            long firstTotalScore = 0;
            double firstAccuracy = 0;
            int firstMaxCombo = 0;
            int firstMistypes = 0;

            AddStep("create replay score", () => originalScore = new Score
            {
                Replay = replay,
                ScoreInfo = new ScoreInfo(),
            });

            AddStep("set beatmap", () => Beatmap.Value = CreateWorkingBeatmap(beatmap));
            AddStep("set ruleset", () => Ruleset.Value = beatmap.BeatmapInfo.Ruleset);
            AddStep("push player", () => pushNewPlayer(originalScore));

            AddUntilStep("wait until player is loaded", () => currentPlayer.IsCurrentScreen());
            skipIntroIfPresent();
            AddUntilStep("wait for completion", () => currentPlayer.GameplayState.HasCompleted);
            AddAssert("judgement results before encode are correct", () => results, () => Is.EquivalentTo(expectedResults));
            AddStep("capture first-run score", () =>
            {
                firstTotalScore = currentPlayer.GameplayState.ScoreProcessor.TotalScore.Value;
                firstAccuracy = currentPlayer.GameplayState.ScoreProcessor.Accuracy.Value;
                firstMaxCombo = currentPlayer.GameplayState.ScoreProcessor.HighestCombo.Value;
                firstMistypes = currentPlayer.GameplayState.ScoreProcessor.Statistics.GetValueOrDefault(HitResult.ComboBreak);
            });

            // Backlog 72: the scripted replay's rejected 'x' is a MISTYPE, and re-simulating the
            // replay is the only way the count can ever be produced (it is not stored in the frame
            // stream, which holds the keystrokes themselves). Note the consequence: replaying an
            // OLD recording now grows a stat its original submission never carried, which is
            // correct, the presses were always there, they just used to leave no trace.
            AddAssert("the rejected key counted as one mistype", () => firstMistypes, () => Is.EqualTo(1));

            AddStep("exit player", () => currentPlayer.Exit());
            AddUntilStep("player exited", () => !currentPlayer.IsCurrentScreen());
            AddStep("dispose player", () => currentPlayer.Dispose());

            AddStep("encode and decode score", () =>
            {
                var encoder = new LegacyScoreEncoder(originalScore, beatmap);

                using (var stream = new MemoryStream())
                {
                    encoder.Encode(stream, leaveOpen: true);
                    stream.Position = 0;
                    decodedScore = new TestScoreDecoder(Beatmap.Value).Parse(stream);
                }
            });

            AddAssert("decoded frames match original", () =>
            {
                var original = replay.Frames.Cast<TypeBeatReplayFrame>().ToList();
                var decoded = decodedScore.Replay.Frames.Cast<TypeBeatReplayFrame>().ToList();

                return original.Count == decoded.Count
                       && original.Zip(decoded).All(pair => pair.First.Time == pair.Second.Time
                                                            && pair.First.Character == pair.Second.Character
                                                            && pair.First.AllowWrongInput == pair.Second.AllowWrongInput);
            });

            AddStep("push player again", () => pushNewPlayer(decodedScore));

            AddUntilStep("wait until player is loaded", () => currentPlayer.IsCurrentScreen());
            skipIntroIfPresent();
            AddUntilStep("wait for completion", () => currentPlayer.GameplayState.HasCompleted);
            AddAssert("judgement results after decode are correct", () => results, () => Is.EquivalentTo(expectedResults));
            AddAssert("total score identical", () => currentPlayer.GameplayState.ScoreProcessor.TotalScore.Value, () => Is.EqualTo(firstTotalScore));
            AddAssert("accuracy identical", () => currentPlayer.GameplayState.ScoreProcessor.Accuracy.Value, () => Is.EqualTo(firstAccuracy));
            AddAssert("max combo identical", () => currentPlayer.GameplayState.ScoreProcessor.HighestCombo.Value, () => Is.EqualTo(firstMaxCombo));
            AddAssert("mistype count identical", () => currentPlayer.GameplayState.ScoreProcessor.Statistics.GetValueOrDefault(HitResult.ComboBreak), () => Is.EqualTo(firstMistypes));
        }

        /// <summary>
        /// The backspace gate (backlog 24) is a LIVE-INPUT gate only: a replay is a recorded fact,
        /// so its 0x08 frames must still apply on playback even when the run's allow-wrong-input
        /// value is OFF, which is the shape of every replay recorded before the gate existed.
        /// Scripted: 'a' on time, erase it, retype it (inert), 'b', then line 1 in full. If the
        /// erase were dropped the retyped 'a' would fall on cell 'b' and be rejected, which the
        /// accuracy assert below catches.
        /// </summary>
        [Test]
        public void TestRecordedBackspaceAppliesEvenWithWrongInputDisallowed()
        {
            IBeatmap beatmap = createBeatmap();

            var replay = new Replay
            {
                Frames = new List<Rulesets.Replays.ReplayFrame>
                {
                    TypeBeatReplayFrame.CreateConfigFrame(500, false),
                    new TypeBeatReplayFrame(500, 'a'),
                    new TypeBeatReplayFrame(600, TypeBeatReplayFrame.BACKSPACE),
                    new TypeBeatReplayFrame(700, 'a'),
                    new TypeBeatReplayFrame(1000, 'b'),
                    new TypeBeatReplayFrame(2500, 'c'),
                    new TypeBeatReplayFrame(3000, 'd'),
                },
            };

            AddStep("set beatmap", () => Beatmap.Value = CreateWorkingBeatmap(beatmap));
            AddStep("set ruleset", () => Ruleset.Value = beatmap.BeatmapInfo.Ruleset);
            AddStep("push player", () => pushNewPlayer(new Score { Replay = replay, ScoreInfo = new ScoreInfo() }));

            AddUntilStep("wait until player is loaded", () => currentPlayer.IsCurrentScreen());
            skipIntroIfPresent();
            AddUntilStep("wait for completion", () => currentPlayer.GameplayState.HasCompleted);

            AddAssert("the run was judged in strict mode", () => !playbackEngine.AllowWrongInput);

            AddAssert("the recorded erase applied and the retype was inert", () =>
                playbackEngine.Lines[0].Cells[0].State == CellState.Correct
                && playbackEngine.Lines[0].Cells[0].TypedChar == 'a'
                && playbackEngine.Lines[0].Cells[0].JudgedDelta == 0);

            // 4 typeable cells, 4 keypresses: the erase + retype cost nothing, and no key was ever
            // rejected. A dropped backspace frame makes this 4/5.
            AddAssert("no keypress was rejected", () => playbackEngine.LiveAccuracy == 1 && playbackEngine.ConsecutiveWrongKeys == 0);

            AddStep("exit player", () => currentPlayer.Exit());
            AddUntilStep("player exited", () => !currentPlayer.IsCurrentScreen());
        }

        /// <summary>
        /// Backlog 27: rate mods are ranked at any speed, so the exact speed is part of the score
        /// and has to survive the .osr round trip. The legacy mod bitfield can only say "DT", never
        /// "DT at 1.73x", so the rate rides in the appended <c>LegacyReplaySoloScoreInfo</c> blob.
        /// This pins both halves: the decoded score still carries 1.73x, and pushing that score into
        /// a player actually runs the gameplay clock at 1.73x (what <see cref="ReplayPlayerLoader"/>
        /// does for real, by assigning the score's mods to the mod bindable before the player loads).
        /// </summary>
        [Test]
        public void TestRecordedSpeedChangeSurvivesRoundTripAndDrivesPlayback()
        {
            const double recorded_rate = 1.73;

            IBeatmap beatmap = createBeatmap();
            Score originalScore = null!;
            Score decodedScore = null!;

            AddStep("create rate-modded replay score", () =>
            {
                var scoreInfo = new ScoreInfo
                {
                    Ruleset = beatmap.BeatmapInfo.Ruleset,
                    BeatmapInfo = beatmap.BeatmapInfo,
                };

                scoreInfo.Mods = new Mod[] { new TypeBeatModDoubleTime { SpeedChange = { Value = recorded_rate } } };
                originalScore = new Score { Replay = createReplay(), ScoreInfo = scoreInfo };
            });

            AddStep("set beatmap", () => Beatmap.Value = CreateWorkingBeatmap(beatmap));
            AddStep("set ruleset", () => Ruleset.Value = beatmap.BeatmapInfo.Ruleset);

            AddStep("encode and decode score", () =>
            {
                var encoder = new LegacyScoreEncoder(originalScore, beatmap);

                using (var stream = new MemoryStream())
                {
                    encoder.Encode(stream, leaveOpen: true);
                    stream.Position = 0;
                    decodedScore = new TestScoreDecoder(Beatmap.Value).Parse(stream);
                }
            });

            AddAssert("decoded score kept the recorded rate", () =>
                decodedScore.ScoreInfo.Mods.OfType<TypeBeatModDoubleTime>().Single().SpeedChange.Value,
                () => Is.EqualTo(recorded_rate).Within(1e-9));

            AddAssert("decoded rate mod is still ranked", () =>
                decodedScore.ScoreInfo.Mods.OfType<TypeBeatModDoubleTime>().Single().Ranked);

            // ReplayPlayerLoader.OnEntering does exactly this before pushing the player.
            AddStep("apply the decoded score's mods", () => SelectedMods.Value = decodedScore.ScoreInfo.Mods);
            AddStep("push player", () => pushNewPlayer(decodedScore));
            AddUntilStep("wait until player is loaded", () => currentPlayer.IsCurrentScreen());

            AddAssert("gameplay clock runs at the recorded rate", () =>
                currentPlayer.ChildrenOfType<GameplayClockContainer>().Single().GetTrueGameplayRate(),
                () => Is.EqualTo(recorded_rate).Within(1e-6));

            AddStep("exit player", () => currentPlayer.Exit());
            AddUntilStep("player exited", () => !currentPlayer.IsCurrentScreen());
            AddStep("clear mods", () => SelectedMods.SetDefault());
        }

        /// <summary>
        /// Backlog 165: seeking BACKWARDS while watching a replay used to freeze gameplay. The engine
        /// has no reverse gear and the feeder's frame index only grew, so every keystroke between the
        /// seek target and the old position was already spent: the lyric stack and caret stopped dead
        /// while the song played on. The end-to-end pin for the ticker seam that fixes it (the unit
        /// pins for the rebuild itself are in <c>ReplayRewindTest</c>).
        ///
        /// <para>The scripted replay's rejected 'x' is what makes the second half of this a real
        /// test. Its MISTYPE is counted by hand off <c>TypingEngine.Mistyped</c> and never travels on
        /// a judgement result, so the framework's rewind cannot undo it; re-watching the same stretch
        /// would count it twice were it not re-derived from the rebuilt engine
        /// (<c>TypeBeatScoreProcessor.ResyncAfterRewind</c>).</para>
        /// </summary>
        [Test]
        public void TestBackwardsSeekRebuildsRatherThanFreezing()
        {
            IBeatmap beatmap = createBeatmap();

            AddStep("set beatmap", () => Beatmap.Value = CreateWorkingBeatmap(beatmap));
            AddStep("set ruleset", () => Ruleset.Value = beatmap.BeatmapInfo.Ruleset);
            AddStep("push player", () => pushNewPlayer(new Score { Replay = createReplay(), ScoreInfo = new ScoreInfo() }));

            AddUntilStep("wait until player is loaded", () => currentPlayer.IsCurrentScreen());
            skipIntroIfPresent();
            AddUntilStep("wait for completion", () => currentPlayer.GameplayState.HasCompleted);

            AddAssert("the whole run played", () => playbackEngine.IsFinished && playbackEngine.Mistypes == 1);
            AddAssert("one mistype was counted", () => mistypeStat(), () => Is.EqualTo(1));

            // 1500 sits after line 0 is fully typed ('a'@500 through 'b'@1000) and before line 1's
            // only keystroke ('c'@2500), so a correct rebuild has to keep line 0 and drop line 1.
            AddStep("seek back over line 1", () => currentPlayer.Seek(1500));

            AddUntilStep("line 1 rewound", () => playbackEngine.Lines[1].Cells.All(c => c.State == CellState.Untyped));

            AddAssert("the run is live again, not finished", () => !playbackEngine.IsFinished && playbackEngine.NextUnsealedLineIndex == 0);

            AddAssert("line 0 kept what it earned", () =>
                playbackEngine.Lines[0].Cells.All(c => c.State == CellState.Correct)
                && playbackEngine.Mistypes == 1);

            // The freeze itself: everything below this line only happens if the engine resumed.
            AddUntilStep("line 1 replays forward", () => playbackEngine.Lines[1].Cells[0].State == CellState.Correct);
            AddUntilStep("wait for completion again", () => currentPlayer.GameplayState.HasCompleted);

            AddAssert("the rewatched mistype was not counted twice", () => mistypeStat(), () => Is.EqualTo(1));

            AddAssert("the run finished again", () => playbackEngine.IsFinished);
            AddAssert("the untyped cell missed again", () => playbackEngine.Lines[1].Cells[1].State, () => Is.EqualTo(CellState.Missed));

            // Four scoring keypresses ('a', the rejected 'x', 'b', 'c'; the backspaced retype of 'a'
            // is inert and counts in neither term), three of them correct. A rebuild that replayed
            // the prefix twice, or dropped part of it, moves both terms.
            AddAssert("accuracy is the whole run's, counted once", () => playbackEngine.LiveAccuracy, () => Is.EqualTo(3 / 4d));

            AddStep("exit player", () => currentPlayer.Exit());
            AddUntilStep("player exited", () => !currentPlayer.IsCurrentScreen());
        }

        private int mistypeStat() => currentPlayer.GameplayState.ScoreProcessor.Statistics.GetValueOrDefault(HitResult.ComboBreak);

        private TypingEngine playbackEngine => currentPlayer.ChildrenOfType<TypeBeatPlayfield>().Single().Engine;

        private static IBeatmap createBeatmap()
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "ReplayStability";

            addLine(beatmap, 0, "ab", 0, 2000, 1500, 500, 1500);
            addLine(beatmap, 1, "cd", 2000, 4000, 3500, 2500, 3500);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, double unitStart, double unitEnd)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = unitStart, EndTime = unitEnd } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = line,
                Granularity = TimingGranularity.Line,
            });
        }

        /// <summary>
        /// Line 0 targets: 'a'@500, 'b'@1000. Line 1 targets: 'c'@2500, 'd'@3000.
        /// Scripted: 'a' on time, wrong 'x' (rejected), backspace, retype 'a' (inert), 'b' on
        /// time, then only 'c' on line 1, leaving 'd' to miss at seal.
        /// </summary>
        private static Replay createReplay() => new Replay
        {
            Frames = new List<Rulesets.Replays.ReplayFrame>
            {
                TypeBeatReplayFrame.CreateConfigFrame(500, false),
                new TypeBeatReplayFrame(500, 'a'),
                new TypeBeatReplayFrame(600, 'x'),
                new TypeBeatReplayFrame(700, TypeBeatReplayFrame.BACKSPACE),
                new TypeBeatReplayFrame(800, 'a'),
                new TypeBeatReplayFrame(1000, 'b'),
                new TypeBeatReplayFrame(2500, 'c'),
            },
        };

        private void skipIntroIfPresent() =>
            AddStep("skip intro if present", () =>
            {
                if (currentPlayer.ChildrenOfType<GameplayClockContainer>().Single().CurrentTime < 0)
                    currentPlayer.Seek(0);
            });

        private void pushNewPlayer(Score score)
        {
            // ShowResults false keeps the player current after completion so the score capture and
            // clean exit below stay race-free.
            var player = new ReplayPlayer(score, new PlayerConfiguration { ShowResults = false });
            player.OnLoadComplete += _ =>
            {
                player.GameplayState.ScoreProcessor.NewJudgement += result =>
                {
                    if (currentPlayer == player)
                        results.Add(result.Type);
                };
            };
            LoadScreen(currentPlayer = player);
            results.Clear();
        }

        private class TestScoreDecoder : LegacyScoreDecoder
        {
            private readonly WorkingBeatmap beatmap;

            public TestScoreDecoder(WorkingBeatmap beatmap)
            {
                this.beatmap = beatmap;
            }

            protected override Ruleset GetRuleset(int rulesetId) => beatmap.BeatmapInfo.Ruleset.CreateInstance();
            protected override WorkingBeatmap GetBeatmap(string md5Hash) => beatmap;
        }
    }
}
