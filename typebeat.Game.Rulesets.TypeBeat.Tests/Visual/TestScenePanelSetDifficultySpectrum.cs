// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.Drawables;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Carousel;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Overlays;
using typebeat.Game.Screens.Select;
using typebeat.Game.Tests.Visual;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 471: the set panel shows upstream's difficulty spectrum beside the status pill, one dot per difficulty
    /// in star order, coloured by <see cref="OsuColour.ForStarDifficulty"/>, with no ruleset icon in front (upstream
    /// heads the dots with the osu! circle; type!beat has one ruleset). There is no screenshot capture in this test
    /// project, so the dots' colours, sizes and on-screen order ARE the visual check.
    /// </summary>
    [TestFixture]
    public partial class TestScenePanelSetDifficultySpectrum : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Aquamarine);

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        private PanelBeatmapSet panel = null!;

        private PanelBeatmapSet.SpreadDisplay spread => panel.ChildrenOfType<PanelBeatmapSet.SpreadDisplay>().Single();

        private Circle[] dots => spread.ChildrenOfType<Circle>().OrderBy(c => c.ScreenSpaceDrawQuad.TopLeft.X).ToArray();

        // Deliberately out of star order: the display sorts.
        private static readonly double[] stars = { 4.2, 1.3, 6.8, 2.6, 3.5 };

        [Test]
        public void OneDotPerDifficultyNoRulesetIcon()
        {
            AddStep("set ruleset", () => Ruleset.Value = new TypeBeatRuleset().RulesetInfo);
            showSet(stars);

            AddUntilStep("one dot per difficulty", () => dots.Length, () => Is.EqualTo(stars.Length));
            AddAssert("dots ascend by stars and use the star colour mapping", () =>
                dots.Select(d => d.Colour.TopLeft.SRGB).SequenceEqual(stars.OrderBy(s => s).Select(s => colours.ForStarDifficulty(s))));
            AddAssert("all dots full size and opaque", () => dots.All(d => d.Size == new Vector2(7, 12) && d.Alpha == 1));

            // The ruleset icon upstream puts first would be a SpriteIcon (the fallback) or the ruleset's CreateIcon()
            // drawable; the row must hold the dots and nothing else.
            AddAssert("no ruleset icon", () => !spread.ChildrenOfType<SpriteIcon>().Any() && !spread.ChildrenOfType<ConstrainedIconContainer>().Any());
            AddAssert("spectrum holds only dots", () => spread.ChildrenOfType<FillFlowContainer>().Single().Children.All(c => c is Circle));
            AddAssert("no collapsed count", () => !spread.ChildrenOfType<OsuSpriteText>().Any());

            AddAssert("spectrum sits right of the status pill", () =>
                dots.First().ScreenSpaceDrawQuad.TopLeft.X > panel.ChildrenOfType<BeatmapSetOnlineStatusPill>().Single().ScreenSpaceDrawQuad.TopRight.X);
            AddAssert("dots are vertically centred on the pill", () =>
            {
                float pillCentre = panel.ChildrenOfType<BeatmapSetOnlineStatusPill>().Single().ScreenSpaceDrawQuad.Centre.Y;
                return dots.All(d => Math.Abs(d.ScreenSpaceDrawQuad.Centre.Y - pillCentre) < 1.5f);
            });
            AddAssert("panel is not a button for scoping", () => !spread.ChildrenOfType<OsuAnimatedButton>().Any());
        }

        [Test]
        public void FilteredOutDifficultiesDrawSmallerAndFaded()
        {
            AddStep("set ruleset", () => Ruleset.Value = new TypeBeatRuleset().RulesetInfo);
            showSet(stars);
            AddUntilStep("dots shown", () => dots.Length, () => Is.EqualTo(stars.Length));

            // Only the two hardest survive the "filter".
            AddStep("filter to the two hardest", () =>
                panel.VisibleBeatmaps.Value = currentSet.Beatmaps.OrderByDescending(b => b.StarRating).Take(2).ToHashSet());

            AddUntilStep("still one dot per difficulty", () => dots.Length, () => Is.EqualTo(stars.Length));
            AddAssert("three filtered dots are small and faded", () => dots.Take(3).All(d => d.Size == new Vector2(5, 10) && d.Alpha == 0.5f));
            AddAssert("two visible dots are full", () => dots.Skip(3).All(d => d.Size == new Vector2(7, 12) && d.Alpha == 1));
        }

        [Test]
        public void ManyDifficultiesCollapseToACount()
        {
            AddStep("set ruleset", () => Ruleset.Value = new TypeBeatRuleset().RulesetInfo);
            showSet(Enumerable.Range(0, PanelBeatmapSet.SpreadDisplay.MAX_DIFFICULTIES_BEFORE_COLLAPSING + 3).Select(i => 1 + i * 0.5).ToArray());

            // In song select the carousel always feeds the filtered set (BeatmapCarousel.updateVisibleBeatmaps); a null
            // feed means "unknown" and, as upstream, draws every dot uncollapsed.
            AddStep("feed all as visible, as the carousel does", () => panel.VisibleBeatmaps.Value = currentSet.Beatmaps.ToHashSet());

            AddUntilStep("count shown", () => spread.ChildrenOfType<OsuSpriteText>().SingleOrDefault()?.Text.ToString(),
                () => Is.EqualTo((PanelBeatmapSet.SpreadDisplay.MAX_DIFFICULTIES_BEFORE_COLLAPSING + 3).ToString()));
            AddAssert("no dots", () => dots.Length, () => Is.Zero);
        }

        private BeatmapSetInfo currentSet = null!;

        private void showSet(IReadOnlyList<double> starRatings)
        {
            AddStep("show set panel", () =>
            {
                var set = new BeatmapSetInfo();
                var metadata = new BeatmapMetadata { Title = "spectrum", Artist = "test" };

                for (int i = 0; i < starRatings.Count; i++)
                {
                    set.Beatmaps.Add(new BeatmapInfo(Ruleset.Value, new BeatmapDifficulty(), metadata)
                    {
                        DifficultyName = $"diff {i}",
                        StarRating = starRatings[i],
                        BeatmapSet = set,
                    });
                }

                currentSet = set;

                Child = new FillFlowContainer
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Width = 0.5f,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Child = panel = new PanelBeatmapSet { Item = new CarouselItem(new GroupedBeatmapSet(null, set)) },
                };
            });

            AddUntilStep("panel loaded", () => panel.IsLoaded);
        }
    }
}
