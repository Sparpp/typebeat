// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Mods;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The per-beatmap availability hook (backlog 331) in the mod select overlay song select uses: on a
    /// map with no original-script lyrics the Polyglot panel is still shown, marked unavailable, and a
    /// click does not select it; on a map with originals it selects like any other mod.
    /// </summary>
    public partial class TestScenePolyglotModSelect : OsuTestScene
    {
        [Cached]
        private readonly OverlayColourProvider colourProvider = new OverlayColourProvider(OverlayColourScheme.Green);

        private UserModSelectOverlay overlay = null!;

        private static IBeatmap beatmap(RulesetInfo ruleset, bool withOriginals)
        {
            var map = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            map.BeatmapInfo.Ruleset = ruleset;

            var line = new LyricLine
            {
                RawText = "privet",
                StartTime = 0,
                EndTime = 2000,
                SingEndTime = 2000,
                Units = new[] { new TimedUnit { Text = "privet", StartTime = 0, EndTime = 2000, Original = withOriginals ? "привет" : null } },
            };

            map.HitObjects.Add(new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word });
            return map;
        }

        private void create(bool withOriginals)
        {
            AddStep("create overlay", () =>
            {
                SelectedMods.Value = Array.Empty<Mod>();
                Ruleset.Value = new TypeBeatRuleset().RulesetInfo;
                Beatmap.Value = CreateWorkingBeatmap(beatmap(Ruleset.Value, withOriginals));

                Child = overlay = new UserModSelectOverlay
                {
                    RelativeSizeAxes = Axes.Both,
                    State = { Value = Visibility.Visible },
                    Beatmap = { Value = Beatmap.Value },
                    SelectedMods = { BindTarget = SelectedMods },
                };
            });

            AddUntilStep("polyglot panel loaded", () => polyglotPanel != null);
        }

        private ModPanel? polyglotPanel => overlay.ChildrenOfType<ModPanel>().SingleOrDefault(p => p.Mod is TypeBeatModPolyglot);

        private ModState polyglotState => overlay.AllAvailableMods.Single(s => s.Mod is TypeBeatModPolyglot);

        [Test]
        public void TestRankingFollowsTheLanguageWhenChangingMaps()
        {
            ModSelectFooterContent footer = null!;
            create(withOriginals: true);
            AddStep("create ranking footer", () =>
            {
                footer = (ModSelectFooterContent)overlay.CreateFooterContent();
                Add(footer);
                footer.Show();
            });
            AddStep("select Polyglot", () => polyglotPanel!.TriggerClick());
            AddUntilStep("selected", () => SelectedMods.Value.Any(m => m is TypeBeatModPolyglot));
            AddUntilStep("ranking footer loaded", () => footer.ChildrenOfType<RankingInformationDisplay>().Any());
            foreach (var language in new[] { BeatmapLanguage.Japanese, BeatmapLanguage.Chinese, BeatmapLanguage.Russian, BeatmapLanguage.Korean, BeatmapLanguage.French })
            {
                AddStep("switch to " + language, () =>
                {
                    var map = beatmap(Ruleset.Value, withOriginals: true);
                    map.BeatmapInfo.Metadata.Language = language;
                    Beatmap.Value = CreateWorkingBeatmap(map);
                    overlay.Beatmap.Value = Beatmap.Value;
                });
                AddUntilStep("ranking reflects " + language, () => footer.ChildrenOfType<RankingInformationDisplay>().Single().Ranked.Value
                    == (language is not (BeatmapLanguage.Chinese or BeatmapLanguage.Korean)));
                AddAssert("Polyglot stays selected", () => SelectedMods.Value.Any(m => m is TypeBeatModPolyglot));
            }
        }

        [Test]
        public void TestUnavailableOnAMapWithoutOriginals()
        {
            create(withOriginals: false);

            AddAssert("marked unavailable", () => !polyglotState.AvailableForBeatmap.Value);
            AddAssert("still shown", () => polyglotState.Visible);
            AddStep("click it", () => polyglotPanel!.TriggerClick());
            AddAssert("not selected", () => !SelectedMods.Value.Any(m => m is TypeBeatModPolyglot));
        }

        [Test]
        public void TestAvailableOnAMapWithOriginals()
        {
            create(withOriginals: true);

            AddAssert("marked available", () => polyglotState.AvailableForBeatmap.Value);
            AddStep("click it", () => polyglotPanel!.TriggerClick());
            AddUntilStep("selected", () => SelectedMods.Value.Any(m => m is TypeBeatModPolyglot));
        }
    }
}
