// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Screens;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Compose;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The BLANK map (what an audio-only import produces) is a first-class editor state: the editor
    /// boots on a beatmap with zero lyric lines, every compose surface renders empty instead of
    /// crashing, the first line can be authored from nothing, and testing gameplay is refused
    /// (a play with no typeable cells has nothing to score).
    /// </summary>
    public partial class TestSceneTypeBeatBlankEditor : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            // Deliberately no hit objects: exactly what LyricMapImporter's blank .osz decodes to.
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>(),
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Some Artist";
            beatmap.BeatmapInfo.Metadata.Title = "Some Song";

            return beatmap;
        }

        [Test]
        public void TestEditorBootsOnAZeroLineMap()
        {
            AddAssert("editor ready", () => Editor.ReadyForUse);
            AddAssert("beatmap really is empty", () => EditorBeatmap.HitObjects.Count == 0);

            AddUntilStep("lyric compose screen shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            // Every surface that lists lyric content must simply be empty, not absent and not broken.
            AddAssert("line list present with no rows", () =>
                !Editor.ChildrenOfType<LineListPanel>().Single().ChildrenOfType<OsuTextBox>().Any());
            AddAssert("boundaries band present", () => Editor.ChildrenOfType<LineBoundariesBand>().Any());
            AddAssert("word strip present", () => Editor.ChildrenOfType<LyricTimeline>().Any());
            AddAssert("detail panel present", () => Editor.ChildrenOfType<ActiveLineDetailPanel>().Any());
            AddAssert("no active line", () => state().ActiveLine.Value == null);

            // Mode churn is where a half-initialised surface would blow up.
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("setup screen shown", () => Editor.ChildrenOfType<SetupScreen>().Any());
            AddStep("switch back to compose", () => Editor.Mode.Value = EditorScreenMode.Compose);
            AddUntilStep("compose shown again", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddAssert("still ready", () => Editor.ReadyForUse);
        }

        [Test]
        public void TestEmptyStatePointsAtTheFirstLineAffordance()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddUntilStep("detail panel explains how to start", () => detailHeader().Contains("no lyrics yet"));
            AddAssert("it names the add affordance", () => detailHeader().Contains("add @ playhead"));
        }

        [Test]
        public void TestAddAtPlayheadAuthorsTheFirstLine()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("park the playhead", () => EditorClock.Seek(1500));

            // Click the real button, so the affordance is proven reachable and not just the operation.
            AddStep("press \"add @ playhead\"", () => addAtPlayheadButton().TriggerClick());

            AddUntilStep("a first line exists", () => EditorBeatmap.HitObjects.Count == 1);
            AddUntilStep("the line list grew a row", () =>
                Editor.ChildrenOfType<LineListPanel>().Single().ChildrenOfType<OsuTextBox>().Count() == 1);
            AddAssert("it landed at the playhead", () =>
                TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[0].Line.StartTime == 1500);
            AddUntilStep("the detail panel now shows the line", () => detailHeader().Contains("line 1:"));

            // And the map is no longer blank, so it becomes playable.
            AddAssert("no longer blank", () => !BlankBeatmap.IsBlank(EditorBeatmap));

            AddStep("undo", () => Editor.Undo());
            AddUntilStep("back to blank", () => EditorBeatmap.HitObjects.Count == 0);
            AddUntilStep("empty state returns", () => detailHeader().Contains("no lyrics yet"));
        }

        [Test]
        public void TestWordButtonsAreGreyedOutWithNoActiveLine()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddAssert("no active line", () => state().ActiveLine.Value == null);

            // There is no line to add a word to, let alone remove one from.
            AddUntilStep("add word greyed out", () => !panelButton("add word").Enabled.Value);
            AddAssert("remove word greyed out", () => !panelButton("remove word").Enabled.Value);

            AddStep("park the playhead", () => EditorClock.Seek(1500));
            AddStep("press \"add @ playhead\"", () => addAtPlayheadButton().TriggerClick());
            AddUntilStep("a first line exists", () => EditorBeatmap.HitObjects.Count == 1);

            // "new line" is two words, so both actions become possible.
            AddUntilStep("add word live", () => panelButton("add word").Enabled.Value);
            AddAssert("remove word live", () => panelButton("remove word").Enabled.Value);
        }

        [Test]
        public void TestTapTimingOnABlankMapIsANoOp()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            // The bottom bar's Time button exists on a blank map; pressing it with nothing to time
            // must do nothing at all rather than start a pass over an empty queue.
            AddStep("press Time", () => compose().ToggleTapTiming());
            AddAssert("no pass started", () => !compose().TapTiming.Active);
            AddAssert("nothing was added", () => EditorBeatmap.HitObjects.Count == 0);
            AddAssert("editor still ready", () => Editor.ReadyForUse);
        }

        [Test]
        public void TestGameplayIsRefusedOnABlankMap()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            // Nothing pending, so without the blank guard this would push the player outright
            // (rather than the save-required dialog) and the assert below would fail.
            AddAssert("nothing unsaved", () => !Editor.HasUnsavedChanges);

            // A zero-cell play has no completion ratio to score: refuse it here rather than push a
            // Player that bails on load and strands the user on an empty screen.
            AddStep("request test gameplay", () => Editor.TestGameplay());
            AddAssert("editor was not left", () => Editor.IsCurrentScreen());
            AddAssert("still blank", () => EditorBeatmap.HitObjects.Count == 0);
        }

        [Test]
        public void TestBlankMapSaves()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("save with no lines", () => Assert.That(Editor.Save(), Is.True));
            AddAssert("no unsaved changes", () => !Editor.HasUnsavedChanges);
        }

        private LyricComposeScreen compose() => Editor.ChildrenOfType<LyricComposeScreen>().Single();

        private LyricEditState state() => compose().EditState;

        private string detailHeader()
            => Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<FreestyleTextFlow>().First().Text;

        private RoundedButton addAtPlayheadButton() => panelButton("add @ playhead");

        private RoundedButton panelButton(string text)
            => Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<RoundedButton>()
                     .Single(b => b.Text.ToString() == text);
    }
}
