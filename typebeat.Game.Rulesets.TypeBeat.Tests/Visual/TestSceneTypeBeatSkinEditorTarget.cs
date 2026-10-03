// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osuTK.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Overlays.SkinEditor;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Play.HUD;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 389, the skin editor half: on a gameplay screen the editor must open on a target
    /// that actually holds the combo counter, or the combo counter can be added from the toolbox
    /// but never selected and edited.
    ///
    /// <para>The layout is the cause. A Player keeps the plain <c>HUD</c> layer
    /// (<c>GlobalSkinnableContainers.MainHUDComponents</c> with no ruleset: score, accuracy,
    /// health, pp, song progress, key counter) and adds a SECOND copy of that lookup carrying the
    /// ruleset, on which the fork hangs the ruleset-specific pieces: the gameplay leaderboard, the
    /// combo counter and the spectator list. The editor used to open on whichever target was laid
    /// out first (the fork's own "Typing counters" layer), which does not contain the combo counter,
    /// so there was no blueprint to click. <see cref="SkinEditor"/> now prefers the ruleset-bearing
    /// HUD layer as its opening target; this fixture pins that, and pins that the combo counter on
    /// that layer really is selectable through the editor.</para>
    /// </summary>
    public partial class TestSceneTypeBeatSkinEditorTarget : PlayerTestScene
    {
        [Cached]
        public readonly EditorClipboard Clipboard = new EditorClipboard();

        private TestSkinEditor skinEditor = null!;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "Song";

            var line = new LyricLine
            {
                RawText = "ab",
                StartTime = 0,
                EndTime = 1200,
                SingEndTime = 1200,
                Units = new[] { new TimedUnit { Text = "ab", StartTime = 0, EndTime = 1200 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 0,
                LineIndex = 0,
                Line = line,
                Granularity = TimingGranularity.Line,
            });

            return beatmap;
        }

        [Test]
        public void TestComboCounterIsEditableOnTheDefaultTarget()
        {
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);

            AddStep("create skin editor on the player", () => Add(skinEditor = new TestSkinEditor(Player)));

            AddUntilStep("editor ready", () => skinEditor.IsLoaded && skinEditor.ChildrenOfType<SkinBlueprintContainer>().Any());
            AddUntilStep("a default blueprint exists", () => skinEditor.ChildrenOfType<SkinBlueprint>().Any());

            AddAssert("default target contains the combo counter",
                () => skinEditor.ChildrenOfType<SkinBlueprint>().Any(b => b.Item is ArgonComboCounter));

            AddAssert("combo counter is selectable",
                () => skinEditor.ChildrenOfType<SkinBlueprint>().Single(b => b.Item is ArgonComboCounter).IsSelectable);

            AddStep("move mouse to the combo counter", () =>
            {
                var combo = Player.ChildrenOfType<ArgonComboCounter>().Single();
                InputManager.MoveMouseTo(combo);
            });

            // Hovered proves the blueprint receives positional input at the counter's on-screen
            // position, which is exactly what was missing before (there was no blueprint at all).
            AddUntilStep("combo blueprint is hovered under the cursor",
                () => skinEditor.ChildrenOfType<SkinBlueprint>().Single(b => b.Item is ArgonComboCounter).IsHovered);
        }

        private partial class TestSkinEditor : SkinEditor
        {
            public TestSkinEditor(Drawable targetScreen)
                : base(targetScreen)
            {
            }
        }
    }
}
