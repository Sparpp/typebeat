// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Overlays;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Select;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneSongSelectDifficultyMetrics : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Purple);

        [Cached(typeof(IBindable<SongSelect.BeatmapSetLookupResult>))]
        private readonly Bindable<SongSelect.BeatmapSetLookupResult> onlineLookupResult = new Bindable<SongSelect.BeatmapSetLookupResult>();

        private BeatmapTitleWedge.DifficultyDisplay display = null!;
        private BeatmapMetadataWedge metadata = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        [Test]
        public void TestHardRockRefreshesRhythmWithoutChangingRate()
        {
            string plain = string.Empty;
            string hardRock = string.Empty;
            AddStep("show map metrics", () =>
            {
                SelectedMods.Value = Array.Empty<Mod>();
                var map = new TypeBeatBeatmap
                {
                    BeatmapInfo = new BeatmapInfo { Ruleset = Ruleset.Value, DifficultyName = "Metric test" },
                };
                for (int i = 0; i < 12; i++)
                {
                    double start = i * 1000;
                    var line = new LyricLine
                    {
                        RawText = "The bad-cat sat.",
                        StartTime = start,
                        EndTime = start + 1000,
                        SingEndTime = start + 1000,
                        Units = new[] { new TimedUnit { Text = "The bad-cat sat.", StartTime = start, EndTime = start + 1000 } },
                    };
                    map.HitObjects.Add(new TypeBeatHitObject { Line = line, LineIndex = i, StartTime = start });
                }
                Beatmap.Value = CreateWorkingBeatmap(map);
                Child = new Container
                {
                    Width = 700,
                    AutoSizeAxes = Axes.Y,
                    Child = display = new BeatmapTitleWedge.DifficultyDisplay(),
                };
            });
            AddUntilStep("rhythm row loaded", () => metric("Rhythm")?.Value.Content != null);
            AddAssert("Complexity replaces Typability", () => metric("Complexity") != null && metric("Typability") == null);
            AddAssert("Rhythm sits to the right of Complexity", () => metric("Rhythm")!.ScreenSpaceDrawQuad.AABBFloat.Left
                > metric("Complexity")!.ScreenSpaceDrawQuad.AABBFloat.Left);
            AddStep("remember baseline", () => plain = metric("Rhythm")!.Value.Content!);
            AddStep("enable HR at 1x", () => SelectedMods.Value = new Mod[] { new TypeBeatModHardRock() });
            AddUntilStep("rhythm refreshes for HR", () => metric("Rhythm")!.Value.Content != plain);
            AddAssert("HR shows greater rhythm", () => number(metric("Rhythm")!.Value.Content!) > number(plain));
            AddStep("remember HR", () => hardRock = metric("Rhythm")!.Value.Content!);
            AddStep("remove HR at 1x", () => SelectedMods.Value = Array.Empty<Mod>());
            AddUntilStep("baseline restored", () => metric("Rhythm")!.Value.Content == plain);
            AddStep("rapid same-rate HR changes", () =>
            {
                SelectedMods.Value = new Mod[] { new TypeBeatModHardRock() };
                SelectedMods.Value = Array.Empty<Mod>();
                SelectedMods.Value = new Mod[] { new TypeBeatModHardRock() };
            });
            AddUntilStep("latest HR reading wins", () => metric("Rhythm")!.Value.Content == hardRock);
            AddStep("restore no mods", () => SelectedMods.Value = Array.Empty<Mod>());
            AddUntilStep("baseline returns after rapid changes", () => metric("Rhythm")!.Value.Content == plain);
        }

        [Test]
        public void TestPolyglotPaceRefreshesForModCombinationsAndLiveRateChanges()
        {
            AddStep("show Japanese map metrics", () =>
            {
                SelectedMods.Value = Array.Empty<Mod>();
                var map = new TypeBeatBeatmap();
                map.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
                map.Metadata.Language = BeatmapLanguage.Japanese;
                var line = new LyricLine
                {
                    RawText = string.Concat(Enumerable.Repeat("shi!", 20)), StartTime = 0, EndTime = 6000, SingEndTime = 6000,
                    Units = new[] { new TimedUnit
                    {
                        Text = string.Concat(Enumerable.Repeat("shi!", 20)), Original = string.Concat(Enumerable.Repeat("し！", 20)),
                        StartTime = 0, EndTime = 6000,
                    } },
                };
                map.HitObjects.Add(new TypeBeatHitObject { Line = line, StartTime = 0 });
                Beatmap.Value = CreateWorkingBeatmap(map);
                Child = new FillFlowContainer
                {
                    Width = 700, AutoSizeAxes = Axes.Y, Direction = FillDirection.Vertical,
                    Children = new Drawable[]
                    {
                        display = new BeatmapTitleWedge.DifficultyDisplay(),
                        metadata = new BeatmapMetadataWedge(),
                    },
                };
                metadata.Show();
            });
            AddUntilStep("nomod average loaded on both panels", () => metric("Average WPM")?.Value.Content == "120" && paceValues().LastOrDefault() == "120 WPM");
            foreach (var selection in new (Mod[] Mods, string Average)[]
            {
                (new Mod[] { new TypeBeatModLiterate() }, "160"),
                (new Mod[] { new TypeBeatModLiterate(), new TypeBeatModPolyglot() }, "120"),
                (new Mod[] { new TypeBeatModPolyglot() }, "80"),
                (new Mod[] { new TypeBeatModLiterate() }, "160"),
                (Array.Empty<Mod>(), "120"),
            })
            {
                AddStep("select " + string.Join("+", selection.Mods.Select(mod => mod.Acronym)), () => SelectedMods.Value = selection.Mods);
                AddUntilStep("both averages refresh to " + selection.Average, () => metric("Average WPM")?.Value.Content == selection.Average
                    && paceValues().LastOrDefault() == selection.Average + " WPM");
            }
            var rateMod = new TypeBeatModDoubleTime();
            AddStep("select Polyglot and custom rate", () => SelectedMods.Value = new Mod[] { new TypeBeatModPolyglot(), rateMod });
            AddUntilStep("1.5x loaded", () => metric("Average WPM")?.Value.Content == "120" && paceValues().LastOrDefault() == "120 WPM");
            string[] previousPace = Array.Empty<string>();
            foreach (double rate in new[] { 1.25, 1.75 })
            {
                AddStep("adjust rate to " + rate, () =>
                {
                    previousPace = paceValues();
                    rateMod.SpeedChange.Value = rate;
                });
                AddUntilStep("all pace figures refresh live", () => paceValues().Length == 3
                    && paceValues().Zip(previousPace).All(pair => pair.First != pair.Second)
                    && paceValues().Last() == (80 * rate).ToString("0") + " WPM"
                    && metric("Average WPM")?.Value.Content == (80 * rate).ToString("0")
                    && metric("Target WPM")?.Value.Content + " WPM" == paceValues()[1]);
            }
            AddStep("restore no mods", () => SelectedMods.Value = Array.Empty<Mod>());
        }

        private string[] paceValues() => metadata.ChildrenOfType<BeatmapMetadataWedge.TypingPaceDisplay>()
            .SelectMany(p => p.ChildrenOfType<OsuSpriteText>()).Select(t => t.Text.ToString()).Where(t => t.EndsWith(" WPM", StringComparison.Ordinal)).ToArray();

        private BeatmapTitleWedge.StatisticDifficulty? metric(string label)
            => display?.ChildrenOfType<BeatmapTitleWedge.StatisticDifficulty>().SingleOrDefault(s => s.Value.Label.ToString() == label);

        private static double number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
    }
}
