// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps.Drawables;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.BeatmapSet;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The set overlay's typing stats against a sample two-difficulty set: BPM and the selected difficulty's word and
    /// character counts in the stats box, the pace block under the success rate, and the description and tags sections.
    /// </summary>
    [TestFixture]
    public partial class TestSceneBeatmapSetTypingStats : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Pink);

        private static readonly APIBeatmap measured = new APIBeatmap
        {
            OnlineID = 1, DifficultyName = "Measured", Length = 185_000,
            WordCount = 412, CharCount = 1903, AverageWpm = 61.6, TargetWpm = 70.4, PeakWpm = 95.2,
            WpmCurve = Enumerable.Range(0, 100).Select(i => (float)(40 + i % 20)).ToArray(),
        };

        private static readonly APIBeatmap unmeasured = new APIBeatmap { OnlineID = 2, DifficultyName = "Unmeasured", Length = 90_000 };

        private static APIBeatmapSet set(string description = "a description", string tags = "pop english") => new APIBeatmapSet
        {
            OnlineID = 266, BPM = 120, Description = description, Tags = tags,
            Beatmaps = new[] { measured, unmeasured },
        };

        private BasicStats stats = null!;

        private bool shows(Drawable root, string text) => root.ChildrenOfType<OsuSpriteText>().Any(t => t.Text.ToString() == text);

        [Test]
        public void TestStatsBox()
        {
            AddStep("load stats box", () => Child = stats = new BasicStats
            {
                Width = BeatmapSetOverlay.RIGHT_WIDTH,
                AutoSizeAxes = Axes.Y,
                BeatmapSet = set(),
                BeatmapInfo = measured,
            });

            AddAssert("shows BPM", () => shows(stats, "120 bpm"));
            AddAssert("shows words", () => shows(stats, "412 words"));
            AddAssert("shows chars", () => shows(stats, "1,903 chars"));

            AddStep("select the unmeasured difficulty", () => stats.BeatmapInfo = unmeasured);
            AddAssert("words unknown", () => shows(stats, "- words"));
            AddAssert("chars unknown", () => shows(stats, "- chars"));
            AddAssert("BPM unchanged", () => shows(stats, "120 bpm"));
        }

        private SuccessRate successRate = null!;

        private Drawable paceBlock => successRate.ChildrenOfType<Container>().Single(c => c.Name == @"typing pace");

        [Test]
        public void TestPaceBlock()
        {
            AddStep("load success rate", () => Child = successRate = new SuccessRate
            {
                Width = BeatmapSetOverlay.RIGHT_WIDTH,
                Height = 200,
                Beatmap = measured,
            });

            AddAssert("pace line", () => shows(successRate, "Peak 95 · Target 70 · Avg 62"));
            AddAssert("graph has the curve", () => successRate.ChildrenOfType<PaceGraph>().Single().Data.Max(), () => Is.EqualTo(1f));
            AddUntilStep("block visible", () => paceBlock.Alpha, () => Is.EqualTo(1));

            AddStep("select the unmeasured difficulty", () => successRate.Beatmap = unmeasured);
            AddUntilStep("block hidden", () => paceBlock.Alpha, () => Is.EqualTo(0));
        }

        private Info info = null!;

        [Test]
        public void TestDescriptionAndTags()
        {
            AddStep("load info with a described, tagged set", () =>
            {
                Child = info = new Info();
                info.BeatmapSet.Value = set();
                info.Beatmap.Value = measured;
            });

            AddUntilStep("description shown", () => info.ChildrenOfType<MetadataSectionDescription>().Single().Alpha, () => Is.GreaterThan(0));
            AddUntilStep("tags shown", () => info.ChildrenOfType<MetadataSectionMapperTags>().Single().Alpha, () => Is.GreaterThan(0));

            AddStep("set with neither", () => info.BeatmapSet.Value = set(description: string.Empty, tags: string.Empty));
            AddUntilStep("description hidden", () => info.ChildrenOfType<MetadataSectionDescription>().Single().Alpha, () => Is.EqualTo(0));
            AddUntilStep("tags hidden", () => info.ChildrenOfType<MetadataSectionMapperTags>().Single().Alpha, () => Is.EqualTo(0));
        }
    }
}
