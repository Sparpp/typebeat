// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Screens;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Replays;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 415, end to end: a stored Japanese Polyglot replay watched through a real
    /// <see cref="ReplayPlayer"/> is played on the cells it was recorded on. The space era changes the
    /// cell list, so the player has to hand the replay to the Polyglot mod BEFORE the playable beatmap
    /// (and its nested scoring objects) is built; a replay without extended bit 8 keeps the authored
    /// space between 月 and と as a cell, one with it does not.
    /// </summary>
    [HeadlessTest]
    public partial class TestSceneTypeBeatJapaneseSpaceEra : RateAdjustedBeatmapTestScene
    {
        private ReplayPlayer currentPlayer = null!;
        private readonly List<HitResult> results = new List<HitResult>();

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        [TestCase(false, "月 と")]
        [TestCase(true, "月と")]
        public void TestTheWatchedReplayIsPlayedOnItsOwnCells(bool era3, string expected)
        {
            IBeatmap beatmap = createBeatmap();
            int cells = expected.Length;

            AddStep("set beatmap", () => Beatmap.Value = CreateWorkingBeatmap(beatmap));
            AddStep("set ruleset", () => Ruleset.Value = beatmap.BeatmapInfo.Ruleset);
            AddStep("select Polyglot", () => SelectedMods.Value = new Mod[] { new TypeBeatModPolyglot() });
            AddStep("push player", () => pushNewPlayer(new Score { Replay = createReplay(era3), ScoreInfo = new ScoreInfo() }));

            AddUntilStep("wait until player is loaded", () => currentPlayer.IsCurrentScreen());

            AddAssert("the engine cut the recorded cells", () => engine.Lines[0].DisplayText, () => Is.EqualTo(expected));
            AddAssert("the nested scoring objects cut the same cells", () =>
                currentPlayer.GameplayState.Beatmap.HitObjects.OfType<TypeBeatHitObject>().Single()
                             .NestedHitObjects.OfType<TypeBeatCharObject>().Count(c => c.Phase == 0), () => Is.EqualTo(cells));

            AddStep("skip intro if present", () =>
            {
                if (currentPlayer.ChildrenOfType<GameplayClockContainer>().Single().CurrentTime < 0)
                    currentPlayer.Seek(0);
            });
            AddUntilStep("wait for completion", () => currentPlayer.GameplayState.HasCompleted);

            AddAssert("every recorded press landed on its cell", () => results.Where(r => r != HitResult.IgnoreHit),
                () => Is.EqualTo(Enumerable.Repeat(HitResult.Great, cells)));

            AddStep("exit player", () => currentPlayer.Exit());
            AddUntilStep("player exited", () => !currentPlayer.IsCurrentScreen());
            AddStep("clear mods", () => SelectedMods.SetDefault());
        }

        private Gameplay.TypingEngine engine => currentPlayer.ChildrenOfType<TypeBeatPlayfield>().Single().Engine;

        private static IBeatmap createBeatmap()
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "JapaneseSpaceEra";
            beatmap.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 0,
                LineIndex = 0,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = "tsuki to", Original = "月 と", StartTime = 0, EndTime = 4000, SingEndTime = 3000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "tsuki", Original = "月", StartTime = 1000, EndTime = 2000 },
                        new TimedUnit { Text = "to", Original = "と", StartTime = 2500, EndTime = 3000 },
                    },
                },
            });

            return beatmap;
        }

        /// <summary>
        /// Committed-text frames (extended bit 5 clear) pressing each recorded cell at its target:
        /// 月@1000, the authored space@2000 (legacy only), と@2500.
        /// </summary>
        private static Replay createReplay(bool era3)
        {
            var replay = new Replay
            {
                Frames = new List<Rulesets.Replays.ReplayFrame>
                {
                    TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true),
                    TypeBeatReplayFrame.CreateExtendedConfigFrame(500, japaneseInputEra3: era3),
                    new TypeBeatReplayFrame(1000, '月'),
                },
            };

            if (!era3)
                replay.Frames.Add(new TypeBeatReplayFrame(2000, ' '));

            replay.Frames.Add(new TypeBeatReplayFrame(2500, 'と'));
            return replay;
        }

        private void pushNewPlayer(Score score)
        {
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
    }
}
