// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Graphics.Carousel;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets;
using typebeat.Game.Screens.Select;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// "Use on game intro" is a per-SET toggle: it is offered when right-clicking the set panel (and the
    /// standalone panel, which stands for its set when difficulties are not grouped), and no longer on a
    /// difficulty panel, whose menu is <see cref="SoloSongSelect.GetForwardActions"/>. Ticking it writes
    /// <see cref="BeatmapSetInfo.IntroPoolInclusion"/>.
    /// </summary>
    [TestFixture]
    public partial class TestSceneIntroPoolSetToggle : ScreenTestScene
    {
        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        // OsuGame provides this to song select's carousel; the test browser's game base does not.
        [Cached(typeof(BeatmapStore))]
        private readonly RealmDetachedBeatmapStore beatmapStore = new RealmDetachedBeatmapStore();

        private SoloSongSelect songSelect = null!;
        private BeatmapSetInfo beatmapSet = null!;

        private PanelBeatmapSet setPanel = null!;
        private PanelBeatmap difficultyPanel = null!;
        private PanelBeatmapStandalone standalonePanel = null!;

        [BackgroundDependencyLoader]
        private void load() => Add(beatmapStore);

        public override void SetUpSteps()
        {
            base.SetUpSteps();

            AddStep("add a two difficulty set", () => beatmapSet = realm.Write(r =>
            {
                var ruleset = r.Find<RulesetInfo>("typebeat")!;
                var set = new BeatmapSetInfo();

                foreach (string name in new[] { "Normal", "Hard" })
                {
                    set.Beatmaps.Add(new BeatmapInfo(ruleset, new BeatmapDifficulty(), new BeatmapMetadata { Title = "intro toggle", Artist = "test" })
                    {
                        DifficultyName = name,
                        BeatmapSet = set,
                    });
                }

                return r.Add(set).Detach();
            }));

            AddStep("load song select", () => LoadScreen(songSelect = new SoloSongSelect()));
            AddUntilStep("song select loaded", () => songSelect.IsLoaded);

            AddStep("add panels", () =>
            {
                var beatmap = beatmapSet.Beatmaps.First();

                // Added beside the screen stack rather than replacing it, which would dispose song select.
                Add(new DependencyProvidingContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    CachedDependencies = new (Type, object)[]
                    {
                        (typeof(ISongSelect), songSelect),
                        (typeof(OverlayColourProvider), new OverlayColourProvider(OverlayColourScheme.Blue)),
                    },
                    Child = new FillFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Direction = FillDirection.Vertical,
                        Children = new Drawable[]
                        {
                            setPanel = new PanelBeatmapSet { Item = new CarouselItem(new GroupedBeatmapSet(null, beatmapSet)) },
                            difficultyPanel = new PanelBeatmap { Item = new CarouselItem(new GroupedBeatmap(null, beatmap)) },
                            standalonePanel = new PanelBeatmapStandalone { Item = new CarouselItem(new GroupedBeatmap(null, beatmap)) },
                        }
                    }
                });
            });

            AddUntilStep("panels loaded", () => setPanel.IsLoaded && difficultyPanel.IsLoaded && standalonePanel.IsLoaded);
        }

        [Test]
        public void ToggleIsOnTheSetPanelOnly()
        {
            AddAssert("set panel offers the toggle", () => introToggle(setPanel.ContextMenuItems) != null);
            AddAssert("standalone panel offers the toggle", () => introToggle(standalonePanel.ContextMenuItems) != null);
            AddAssert("difficulty panel does not", () => introToggle(difficultyPanel.ContextMenuItems) == null);
            AddAssert("difficulty panel still has its forward actions", () => difficultyPanel.ContextMenuItems!.Length > 0);
        }

        [Test]
        public void SetToggleSlotsBetweenDetailsAndCollections()
        {
            AddAssert("toggle sits directly above collections", () =>
            {
                var items = setPanel.ContextMenuItems!;
                int toggle = Array.FindIndex(items, i => i is ToggleMenuItem t && t.Text.Value.Equals(SongSelectStrings.UseOnGameIntro));
                int collections = Array.FindIndex(items, i => i.Text.Value.Equals(CommonStrings.Collections));

                // toggle, spacer, collections.
                return toggle >= 0 && collections == toggle + 2;
            });
        }

        [Test]
        public void TickingTheSetToggleWritesTheSet()
        {
            AddAssert("no override yet", () => realm.Run(r => r.Find<BeatmapSetInfo>(beatmapSet.ID)!.IntroPoolInclusion) == null);

            // No difficulty declares a beatdrop (these have no files at all), so the toggle shows unticked
            // and ticking it opts the whole set in.
            AddAssert("shown unticked", () => introToggle(setPanel.ContextMenuItems)!.State.Value == false);
            AddStep("tick it", () => introToggle(setPanel.ContextMenuItems)!.Action.Value?.Invoke());

            AddAssert("set opted in", () => realm.Run(r => r.Find<BeatmapSetInfo>(beatmapSet.ID)!.IntroPoolInclusion) == true);
            AddAssert("difficulties untouched", () => realm.Run(r => r.Find<BeatmapSetInfo>(beatmapSet.ID)!.Beatmaps.All(b => b.UserSettings.IntroPoolInclusion == null)));
            AddAssert("reopened menu shows it ticked", () => introToggle(setPanel.ContextMenuItems)!.State.Value);
            AddAssert("standalone panel agrees", () => introToggle(standalonePanel.ContextMenuItems)!.State.Value);
        }

        private static ToggleMenuItem? introToggle(MenuItem[]? items)
            => items?.OfType<ToggleMenuItem>().SingleOrDefault(i => i.Text.Value.Equals(SongSelectStrings.UseOnGameIntro));
    }
}
