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
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
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
            });

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
        }

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
