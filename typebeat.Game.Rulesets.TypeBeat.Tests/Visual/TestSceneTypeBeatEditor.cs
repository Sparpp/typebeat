// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osuTK;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Compose;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Smoke test that the restored type!beat editor boots on a type!beat beatmap: the editor loads,
    /// exposes the EditorBeatmap with the lyric hit objects, and mode switching between Compose
    /// and Setup works without crashing (the compose surface is type!beat's own, not a circle
    /// composer).
    /// </summary>
    public partial class TestSceneTypeBeatEditor : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap
            {
                HitObjects = new List<Rulesets.Objects.HitObject>(),
            };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Smoke";

            addLine(beatmap, 0, "hello world", 1000, 3000, 3000);
            addLine(beatmap, 1, "second line", 3000, 5000, 5000);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = start, EndTime = singEnd } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = line,
                Granularity = TimingGranularity.Line,
            });
        }

        [Test]
        public void TestEditorBootsWithLyricObjects()
        {
            AddAssert("editor ready", () => Editor.ReadyForUse);
            AddAssert("editor beatmap has 2 lyric objects", () => EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Count() == 2);

            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("setup screen shown", () => Editor.ChildrenOfType<SetupScreen>().Any());

            AddStep("switch to compose", () => Editor.Mode.Value = EditorScreenMode.Compose);
            AddUntilStep("lyric compose screen shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddAssert("still ready after mode churn", () => Editor.ReadyForUse);
        }

        [Test]
        public void TestLyricComposeScreenSurfaces()
        {
            AddUntilStep("lyric compose screen shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddUntilStep("line list has a row per line", () => textBoxCount() == 2);
            AddUntilStep("lyric timeline surfaces the words", () =>
                Editor.ChildrenOfType<LyricTimeline>().Single().ChildrenOfType<typebeat.Game.Graphics.Sprites.OsuSpriteText>().Any(t => t.Text.ToString() == "hello world"));
            AddAssert("word strip hosted outside the detail panel", () =>
                !Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<LyricTimeline>().Any());

            AddStep("delete first line via ops", () =>
                TypeBeatEditorOperations.DeleteLine(EditorBeatmap, EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().First()));

            AddUntilStep("row count follows deletion", () => textBoxCount() == 1);

            AddStep("undo", () => Editor.Undo());
            AddUntilStep("row count restored after undo", () => textBoxCount() == 2);

            int textBoxCount() => Editor.ChildrenOfType<LineListPanel>().Single().ChildrenOfType<typebeat.Game.Graphics.UserInterface.OsuTextBox>().Count();
        }

        [Test]
        public void TestCtrlDoesNotSwitchToSetup()
        {
            AddStep("switch to compose", () => Editor.Mode.Value = EditorScreenMode.Compose);
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("press & release Ctrl", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.ReleaseKey(Key.ControlLeft);
            });
            AddAssert("still in compose after Ctrl", () => Editor.Mode.Value == EditorScreenMode.Compose);

            AddStep("press Ctrl+Tab", () =>
            {
                InputManager.PressKey(Key.ControlLeft);
                InputManager.Key(Key.Tab);
                InputManager.ReleaseKey(Key.ControlLeft);
            });
            AddAssert("still in compose after Ctrl+Tab", () => Editor.Mode.Value == EditorScreenMode.Compose);
        }

        [Test]
        public void TestPlaybackFollowsPlayheadOverSelection()
        {
            AddUntilStep("compose screen shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            // Select line 1 while paused. The active line should pin to it even when the playhead
            // is parked over a different line — a selection is a manual override while paused.
            // (The continuous timeline shows every line, so "which line is active" is the state
            // under test, not which words are visible.)
            AddStep("select first line", () =>
                composeScreen().EditState.SelectedLine.Value = EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().First());
            AddStep("park playhead over line 2", () => EditorClock.Seek(3500));
            AddUntilStep("still pinned to line 1", () => activeLineIs("hello world"));

            // Start playback: it must override the selection and follow the playhead onto line 2.
            AddStep("start playback", () => EditorClock.Start());
            AddUntilStep("follows to line 2", () => activeLineIs("second line"));

            // The stale selection is dropped, so pausing keeps the line we heard (no snap back).
            AddStep("stop playback", () => EditorClock.Stop());
            AddUntilStep("selection cleared by follow", () => composeScreen().EditState.SelectedLine.Value == null);
            AddAssert("stays on line 2 after pause", () => activeLineIs("second line"));

            LyricComposeScreen composeScreen() => Editor.ChildrenOfType<LyricComposeScreen>().Single();

            bool activeLineIs(string text) => composeScreen().EditState.ActiveLine.Value?.Line.RawText == text;
        }

        [Test]
        public void TestUndoRedoRestoresRemovedLine()
        {
            TypeBeatHitObject removed = null!;

            AddAssert("2 lines", () => EditorBeatmap.HitObjects.Count == 2);

            AddStep("remove last line", () =>
            {
                removed = EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Last();
                EditorBeatmap.Remove(removed);
            });

            AddAssert("1 line", () => EditorBeatmap.HitObjects.Count == 1);

            AddStep("undo", () => Editor.Undo());
            AddAssert("2 lines restored", () => EditorBeatmap.HitObjects.Count == 2);
            AddAssert("restored line text matches", () =>
                EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Last().Line.RawText == removed.Line.RawText);

            AddStep("redo", () => Editor.Redo());
            AddAssert("back to 1 line", () => EditorBeatmap.HitObjects.Count == 1);
        }

        [Test]
        public void TestSavePersistsTypeBeatFormat()
        {
            AddStep("remove a line then save", () =>
            {
                EditorBeatmap.Remove(EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Last());
                Assert.That(Editor.Save(), Is.True);
            });

            AddAssert("no unsaved changes after save", () => !Editor.HasUnsavedChanges);
        }

        [Test]
        public void TestMetadataEditIsUndone()
        {
            string original = null!;

            AddStep("record + change title", () =>
            {
                original = EditorBeatmap.Metadata.Title;
                EditorBeatmap.Metadata.Title = "Changed Title";
                EditorBeatmap.SaveState();
            });

            AddAssert("title changed", () => EditorBeatmap.Metadata.Title == "Changed Title");
            AddAssert("has unsaved changes", () => Editor.HasUnsavedChanges);

            AddStep("undo", () => Editor.Undo());

            // Before the fix, ApplyStateChange restored only hit objects, leaving the title (and
            // HasUnsavedChanges) stale.
            AddUntilStep("title reverted by undo", () => EditorBeatmap.Metadata.Title == original);
            AddAssert("no unsaved changes after full revert", () => !Editor.HasUnsavedChanges);
        }

        [Test]
        public void TestGlobalOffsetShiftIsUndoable()
        {
            double firstStart = 0;

            AddStep("record first start", () => firstStart = EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().First().Line.StartTime);

            AddStep("shift +250ms", () => TypeBeatEditorOperations.ShiftAllTimes(EditorBeatmap, 250));
            AddAssert("all lines moved +250", () =>
                EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().First().Line.StartTime == firstStart + 250);
            AddAssert("units moved too", () =>
                EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().First().Line.Units[0].StartTime == firstStart + 250);

            AddStep("undo", () => Editor.Undo());
            AddAssert("shift reverted", () =>
                EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().First().Line.StartTime == firstStart);
        }

        [Test]
        public void TestSetupSectionPresent()
        {
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("type!beat setup section shown", () => Editor.ChildrenOfType<TypeBeatSetupSection>().Any());
        }

        [Test]
        public void TestClickEmptyTimelineSeeks()
        {
            LyricTimeline timeline = null!;

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("pause with playhead over the last line", () =>
            {
                timeline = Editor.ChildrenOfType<LyricTimeline>().Single();
                EditorClock.Stop();
                EditorClock.Seek(4500);
            });
            AddUntilStep("timeline present + sized", () => timeline.IsLoaded && timeline.DrawWidth > 0);

            double before = 0;

            AddStep("click empty grey space past the final line", () =>
            {
                before = EditorClock.CurrentTime;
                var q = timeline.ScreenSpaceDrawQuad;
                // 0.9 across (right of the centred playhead) is a time past the last line (5000ms),
                // so this lands on empty background, not a word/line block.
                InputManager.MoveMouseTo(q.TopLeft + new Vector2(q.Width * 0.9f, q.Height * 0.5f));
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("empty-space click moved the playhead", () => EditorClock.CurrentTime > before + 1);
        }

        [Test]
        public void TestDoubleClickEmptyStripAddsLine()
        {
            LyricTimeline timeline = null!;

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("pause with playhead over the last line", () =>
            {
                timeline = Editor.ChildrenOfType<LyricTimeline>().Single();
                EditorClock.Stop();
                EditorClock.Seek(4500);
            });
            AddUntilStep("timeline present + sized", () => timeline.IsLoaded && timeline.DrawWidth > 0);

            // The gap-double-click affordance used to also live on the waveform's line overview
            // bars; the word strip is its home now that those bars are gone.
            AddStep("double-click empty space past the final line", () =>
            {
                var q = timeline.ScreenSpaceDrawQuad;
                InputManager.MoveMouseTo(q.TopLeft + new Vector2(q.Width * 0.9f, q.Height * 0.5f));
                InputManager.Click(MouseButton.Left);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("a third line was added", () => EditorBeatmap.HitObjects.Count == 3);
        }

        [Test]
        public void TestZoomDoesNotMovePlayhead()
        {
            LyricTimeline timeline = null!;

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("pause at 2000ms", () =>
            {
                timeline = Editor.ChildrenOfType<LyricTimeline>().Single();
                EditorClock.Stop();
                EditorClock.Seek(2000);
            });
            AddUntilStep("timeline present + sized", () => timeline.IsLoaded && timeline.DrawWidth > 0);

            double before = 0;

            AddStep("wheel-zoom over the strip", () =>
            {
                before = EditorClock.CurrentTime;
                InputManager.MoveMouseTo(timeline);
                InputManager.ScrollVerticalBy(3);
            });

            AddAssert("zoom left the playhead put", () => Math.Abs(EditorClock.CurrentTime - before) < 1);
        }
    }
}
