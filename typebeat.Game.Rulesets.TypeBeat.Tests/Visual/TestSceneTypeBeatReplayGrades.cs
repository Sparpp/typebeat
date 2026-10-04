// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Online.Leaderboards;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Select;
using typebeat.Game.Screens.Ranking.Contracted;
using typebeat.Game.Screens.Ranking.Expanded.Accuracy;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatReplayGrades : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        [TestCase(ScoreRank.X, 0.90, 0, ScoreRank.A)]
        [TestCase(ScoreRank.A, 0.99, 0, ScoreRank.X)]
        [TestCase(ScoreRank.S, 0.97, 3, ScoreRank.A)]
        [TestCase(ScoreRank.X, 0.60, 40, ScoreRank.C)]
        public void SelectorRowAndTooltipShowCurrentGrade(ScoreRank storedRank, double accuracy, int misses, ScoreRank expected)
        {
            ScoreInfo score = null!;
            BeatmapLeaderboardScore row = null!;
            BeatmapLeaderboardScore.LeaderboardScoreTooltip.TotalScoreRankPanel tooltip = null!;

            AddStep("load replay row and tooltip", () =>
            {
                score = new ScoreInfo(ruleset: new TypeBeatRuleset().RulesetInfo)
                {
                    Rank = storedRank,
                    Accuracy = accuracy,
                    Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 - misses, [HitResult.Miss] = misses },
                };
                Child = new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Children = new Drawable[]
                    {
                        row = new BeatmapLeaderboardScore(score, sheared: false),
                        tooltip = new BeatmapLeaderboardScore.LeaderboardScoreTooltip.TotalScoreRankPanel(),
                    },
                };
            });
            AddUntilStep("replay controls loaded", () => row.IsLoaded && tooltip.IsLoaded);
            AddStep("show tooltip grade", () => tooltip.Score = score);

            string letter = DrawableRank.GetRankLetter(expected);
            AddUntilStep("row displays current letter grade", () => row.ChildrenOfType<OsuSpriteText>().Any(text => text.Text.ToString() == letter));
            AddUntilStep("tooltip displays the same grade", () => tooltip.ChildrenOfType<OsuSpriteText>().Any(text => text.Text.ToString() == letter));
            AddAssert("historical rank is preserved", () => score.Rank == storedRank);
        }

        [TestCase(ScoreRank.X, 0.90, 0, ScoreRank.A)]
        [TestCase(ScoreRank.A, 0.99, 0, ScoreRank.X)]
        [TestCase(ScoreRank.S, 0.97, 3, ScoreRank.A)]
        [TestCase(ScoreRank.X, 0.60, 40, ScoreRank.C)]
        public void OpenedReplayResultsShowCurrentGrade(ScoreRank storedRank, double accuracy, int misses, ScoreRank expected)
        {
            ScoreInfo score = null!;
            AccuracyCircle circle = null!;
            ContractedPanelMiddleContent contracted = null!;

            AddStep("open replay result panels", () =>
            {
                score = new ScoreInfo(ruleset: new TypeBeatRuleset().RulesetInfo)
                {
                    Rank = storedRank,
                    Accuracy = accuracy,
                    Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 100 - misses, [HitResult.Miss] = misses },
                };
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Children = new Drawable[]
                    {
                        circle = new AccuracyCircle(score) { Width = 200, Height = 200 },
                        new Container
                        {
                            X = 220,
                            Width = 200,
                            Height = 400,
                            Child = contracted = new ContractedPanelMiddleContent(score),
                        },
                    },
                };
            });
            AddUntilStep("result panels loaded", () => circle.IsLoaded && contracted.IsLoaded);

            string letter = DrawableRank.GetRankLetter(expected);
            AddUntilStep("expanded results display current grade", () => circle.ChildrenOfType<RankText>().First()
                .ChildrenOfType<OsuSpriteText>().Any(text => text.Text.ToString() == letter));
            AddUntilStep("contracted results display current grade", () => contracted.ChildrenOfType<DrawableRank>()
                .Single().ChildrenOfType<OsuSpriteText>().Any(text => text.Text.ToString() == letter));
            AddAssert("opening results preserves the recorded grade", () => score.Rank == storedRank);
        }
    }
}
