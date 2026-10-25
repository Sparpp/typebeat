// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The compose screen's word block view spans the full width, and the lyric line list is a
    /// collapsible sidebar laid over its left side: open by default, collapsed and reopened by the
    /// toggle on its right edge, which stays on screen while collapsed.
    /// </summary>
    public partial class TestSceneEditorLineListSidebar : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = "hello world",
                    StartTime = 1000,
                    EndTime = 3000,
                    SingEndTime = 3000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 1900 },
                        new TimedUnit { Text = "world", StartTime = 2000, EndTime = 3000 },
                    },
                },
            });
            return beatmap;
        }

        private LyricComposeScreen compose() => Editor.ChildrenOfType<LyricComposeScreen>().Single();
        private LineListSidebar sidebar() => compose().LineListSidebar;

        [Test]
        public void SidebarStartsOpenOverAFullWidthWordView()
        {
            AddUntilStep("compose loaded", () => Editor.ChildrenOfType<ActiveLineDetailPanel>().Any(p => p.IsLoaded) && sidebar().IsLoaded);
            AddAssert("sidebar open", () => sidebar().Expanded.Value);
            AddAssert("word view spans the whole width", () =>
            {
                var area = sidebar().ScreenSpaceDrawQuad.AABBFloat;
                var detail = compose().ChildrenOfType<ActiveLineDetailPanel>().Single().ScreenSpaceDrawQuad.AABBFloat;
                return Math.Abs(detail.Right - area.Right) < 1 && detail.Width > area.Width * 0.9f;
            });
            AddAssert("list overlays the left of the word view", () =>
            {
                var area = sidebar().ScreenSpaceDrawQuad.AABBFloat;
                var list = sidebar().Panel.ScreenSpaceDrawQuad.AABBFloat;
                return Math.Abs(list.Left - area.Left) < 1 && Math.Abs(list.Width - area.Width * LineListSidebar.WIDTH) < 1;
            });
        }

        [Test]
        public void ToggleCollapsesAndReopensTheSidebar()
        {
            AddUntilStep("compose loaded", () => sidebar().IsLoaded && sidebar().ToggleButton.IsLoaded);

            AddStep("click the toggle", () =>
            {
                InputManager.MoveMouseTo(sidebar().ToggleButton);
                InputManager.Click(MouseButton.Left);
            });
            AddAssert("collapsed", () => !sidebar().Expanded.Value);
            AddUntilStep("list slid off to the left", () => sidebar().Panel.ScreenSpaceDrawQuad.AABBFloat.Right <= sidebar().ScreenSpaceDrawQuad.AABBFloat.Left + 1);
            AddAssert("toggle still on screen", () => sidebar().ToggleButton.ScreenSpaceDrawQuad.AABBFloat.Left >= sidebar().ScreenSpaceDrawQuad.AABBFloat.Left);

            AddStep("click the toggle again", () =>
            {
                InputManager.MoveMouseTo(sidebar().ToggleButton);
                InputManager.Click(MouseButton.Left);
            });
            AddAssert("open again", () => sidebar().Expanded.Value);
            AddUntilStep("list back at the left edge", () => Math.Abs(sidebar().Panel.ScreenSpaceDrawQuad.AABBFloat.Left - sidebar().ScreenSpaceDrawQuad.AABBFloat.Left) < 1);
        }
    }
}
