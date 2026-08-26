// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Utils;
using osuTK;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Components.Timelines.Summary;
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
            AddAssert("word strip hosted inside the detail panel", () =>
                Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<LyricTimeline>().Any());
            AddAssert("minimal boundaries band present outside the panel", () =>
                Editor.ChildrenOfType<LineBoundariesBand>().Any()
                && !Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<LineBoundariesBand>().Any());

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
            // is parked over a different line; a selection is a manual override while paused.
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

            // The gap-double-click affordance lives on the panel-hosted word strip AND on the
            // minimal boundaries band under the waveform (covered separately below).
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
        public void TestDoubleClickEmptyBandAddsLine()
        {
            LineBoundariesBand band = null!;

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("pause with playhead over the last line", () =>
            {
                band = Editor.ChildrenOfType<LineBoundariesBand>().Single();
                EditorClock.Stop();
                EditorClock.Seek(4500);
            });
            AddUntilStep("band present + sized", () => band.IsLoaded && band.DrawWidth > 0);

            // The band mirrors the waveform's window (centred on the playhead), so 0.9 across is
            // a time well past the last line (5000ms), empty space.
            AddStep("double-click empty band space past the final line", () =>
            {
                var q = band.ScreenSpaceDrawQuad;
                InputManager.MoveMouseTo(q.TopLeft + new Vector2(q.Width * 0.9f, q.Height * 0.5f));
                InputManager.Click(MouseButton.Left);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("a third line was added", () => EditorBeatmap.HitObjects.Count == 3);
        }

        [Test]
        public void TestClickBandSelectsLineAndSeeks()
        {
            LineBoundariesBand band = null!;

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("pause with playhead over the last line", () =>
            {
                band = Editor.ChildrenOfType<LineBoundariesBand>().Single();
                EditorClock.Stop();
                EditorClock.Seek(4500);
            });
            AddUntilStep("band present + sized", () => band.IsLoaded && band.DrawWidth > 0);

            // Window is playhead-centred: [1500, 7500] at the default 6000ms zoom. A click at
            // ~0.083 across lands on ~2000ms, inside line 1 ("hello world", 1000..3000).
            AddStep("click band over line 1", () =>
            {
                var q = band.ScreenSpaceDrawQuad;
                float fraction = (float)((2000 - (EditorClock.CurrentTime - 3000)) / 6000);
                InputManager.MoveMouseTo(q.TopLeft + new Vector2(q.Width * fraction, q.Height * 0.5f));
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("line 1 selected", () =>
                Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState.SelectedLine.Value?.Line.RawText == "hello world");
            AddUntilStep("seeked to line 1 start", () => Math.Abs(EditorClock.CurrentTime - 1000) < 50);
        }

        [Test]
        public void TestLineListCtrlAndShiftClickBuildASection()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            // A third line gives a range with an interior member, so a shift+click run is
            // distinguishable from "both endpoints".
            AddStep("add a third line", () => TypeBeatEditorOperations.AddLine(EditorBeatmap, 5500, "third line"));
            AddUntilStep("three rows", () => rows().Count == 3);

            AddStep("click row 1", () => clickRow(0));
            AddUntilStep("row 1 is the single selection", () =>
                state().SelectedLine.Value == rows()[0].HitObject && state().MultiSelectedLines.Count == 0);

            AddStep("shift+click row 3", () => clickRow(2, shift: true));
            AddUntilStep("whole run selected", () => state().MultiSelectedLines.Count == 3);
            AddAssert("clicked line is primary", () => state().SelectedLine.Value == rows()[2].HitObject);

            // Shift+click again ranges from the SAME anchor (row 1), so the run shrinks.
            AddStep("shift+click row 2", () => clickRow(1, shift: true));
            AddUntilStep("run shrank to rows 1-2", () =>
                state().MultiSelectedLines.Count == 2 && !state().MultiSelectedLines.Contains(rows()[2].HitObject));

            AddStep("ctrl+click row 3", () => clickRow(2, ctrl: true));
            AddUntilStep("ctrl added row 3 back", () => state().MultiSelectedLines.Count == 3);

            AddStep("ctrl+click row 1", () => clickRow(0, ctrl: true));
            AddUntilStep("ctrl removed row 1", () =>
                state().MultiSelectedLines.Count == 2 && !state().MultiSelectedLines.Contains(rows()[0].HitObject));

            AddStep("plain click row 2", () => clickRow(1));
            AddUntilStep("section collapsed", () => state().MultiSelectedLines.Count == 0);
        }

        [Test]
        public void TestSectionSurvivesPlayback()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("select both lines", () =>
            {
                clickRow(0);
                clickRow(1, shift: true);
            });
            AddUntilStep("two lines sectioned", () => state().MultiSelectedLines.Count == 2);

            // A section is a deliberate mark; playback moving the active line must not erase it
            // (the mapper listens to the section before timing it).
            AddStep("play from the top", () =>
            {
                EditorClock.Seek(0);
                EditorClock.Start();
            });
            AddUntilStep("playhead reached line 2", () => EditorClock.CurrentTime > 3200);
            AddStep("stop", () => EditorClock.Stop());
            AddAssert("section still marked", () => state().MultiSelectedLines.Count == 2);
        }

        [Test]
        public void TestTimeButtonSitsLeftOfTest()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddUntilStep("Time button published", () =>
                Editor.ChildrenOfType<RulesetActionButton>().SingleOrDefault() is RulesetActionButton b
                && b.Alpha == 1 && b.Text.ToString() == "Time");

            AddAssert("it sits left of Test", () =>
                Editor.ChildrenOfType<RulesetActionButton>().Single().ScreenSpaceDrawQuad.TopLeft.X
                < Editor.ChildrenOfType<TestGameplayButton>().Single().ScreenSpaceDrawQuad.TopLeft.X);

            // Pin the DRAWN text, not just the model string: a real text drawable with non-zero size,
            // sitting inside the bottom bar's own row. This regressed once when the button's Height
            // carried over OsuButton's absolute Height = 40 as a 4000% relative fraction instead of
            // resetting it to 100%, ballooning the button to 40x the bar's height and pushing the
            // centred label thousands of pixels below the visible, masked strip: the model string
            // and Alpha were both still correct, nothing was ever actually seen on screen.
            AddAssert("Time button is the same height as Test (not blown out by a bad relative size)", () =>
            {
                float timeHeight = Editor.ChildrenOfType<RulesetActionButton>().Single().ScreenSpaceDrawQuad.Height;
                float testHeight = Editor.ChildrenOfType<TestGameplayButton>().Single().ScreenSpaceDrawQuad.Height;
                return Precision.AlmostEquals(timeHeight, testHeight, 1f);
            });

            AddAssert("Time label is drawn, sized, and inside the bottom bar row", () => labelIsVisibleWithText("Time"));

            AddStep("start a tap-timing pass", () => compose().ToggleTapTiming());
            AddUntilStep("armed", () => compose().TapTiming.Active);
            AddAssert("Finish label is drawn, sized, and inside the bottom bar row", () => labelIsVisibleWithText("Finish"));

            AddStep("cancel the pass", () => InputManager.Key(Key.Escape));
            AddUntilStep("idle again", () => !compose().TapTiming.Active);
            AddAssert("Time label is drawn again after cancelling", () => labelIsVisibleWithText("Time"));

            bool labelIsVisibleWithText(string expected)
            {
                var label = Editor.ChildrenOfType<RulesetActionButton>().Single().ChildrenOfType<OsuSpriteText>().Single();
                var barRow = Editor.ChildrenOfType<TestGameplayButton>().Single().ScreenSpaceDrawQuad;

                return label.Text.ToString() == expected
                       && label.DrawWidth > 0
                       && label.DrawHeight > 0
                       && label.Alpha > 0
                       && label.ScreenSpaceDrawQuad.TopLeft.Y >= barRow.TopLeft.Y - 1
                       && label.ScreenSpaceDrawQuad.BottomLeft.Y <= barRow.BottomLeft.Y + 1;
            }
        }

        [Test]
        public void TestTapTimingRecordsThenCommitsAsOneUndoStep()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("start a pass over the whole sheet", () =>
            {
                state().SelectedLine.Value = null;
                state().ClearMultiLineSelection();
                compose().ToggleTapTiming();
            });

            AddUntilStep("recording", () => compose().TapTiming.Active);
            AddAssert("entering tap mode mutated nothing", () => firstLine().Line.StartTime == 1000);
            AddUntilStep("song running", () => EditorClock.IsRunning);

            tapAfterTheClockAdvances();
            AddUntilStep("first word timed", () => compose().TapTiming.Session?.TappedCount == 1);

            // The overlay holds focus, so Space is a tap, NOT the bottom bar's play/pause.
            AddAssert("space did not toggle playback", () => EditorClock.IsRunning);
            AddAssert("still nothing committed", () => firstLine().Line.StartTime == 1000);

            tapAfterTheClockAdvances();
            AddUntilStep("second word timed", () => compose().TapTiming.Session?.TappedCount == 2);
            AddUntilStep("queue complete stops the song", () => !EditorClock.IsRunning);

            double[] recorded = null!;

            AddStep("finish (commit)", () =>
            {
                recorded = compose().TapTiming.Session!.Taps.ToArray();
                compose().ToggleTapTiming();
            });
            AddUntilStep("no longer recording", () => !compose().TapTiming.Active);
            AddAssert("both lines landed on their taps", () =>
                lineAt(0).Line.StartTime == recorded[0] && lineAt(1).Line.StartTime == recorded[1]);
            AddAssert("still two lines, ordered", () =>
                EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Count() == 2
                && lineAt(0).Line.EndTime == lineAt(1).Line.StartTime);

            // Record-then-commit means the whole pass is a SINGLE undo step.
            AddStep("undo once", () => Editor.Undo());
            AddUntilStep("one undo restored the original timing", () => firstLine().Line.StartTime == 1000);
        }

        [Test]
        public void TestTapTimingCancelLeavesNoTrace()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("start a pass", () =>
            {
                state().SelectedLine.Value = null;
                state().ClearMultiLineSelection();
                compose().ToggleTapTiming();
            });
            AddUntilStep("recording", () => compose().TapTiming.Active);

            tapAfterTheClockAdvances();
            AddUntilStep("a tap was recorded", () => compose().TapTiming.Session?.TappedCount == 1);

            AddStep("escape", () => InputManager.Key(Key.Escape));
            AddUntilStep("no longer recording", () => !compose().TapTiming.Active);

            AddAssert("the beatmap never moved", () =>
                EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Count() == 2
                && lineAt(0).Line.StartTime == 1000
                && lineAt(1).Line.StartTime == 3000);
        }

        [Test]
        public void TestTapTimingScopesToTheSelectedSection()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("select only line 2", () => clickRow(1));
            AddUntilStep("line 2 selected", () => state().SelectedLine.Value == rows()[1].HitObject);

            AddStep("start a pass", () => compose().ToggleTapTiming());
            AddUntilStep("recording", () => compose().TapTiming.Active);

            AddAssert("queue covers only the selected line", () =>
                compose().TapTiming.Session!.Queue.All(t => t.LineIndex == 1));

            AddStep("cancel", () => compose().TapTiming.Cancel());
            AddUntilStep("no longer recording", () => !compose().TapTiming.Active);
        }

        /// <summary>
        /// A pass shows ONLY the section it is recording: the lyric surfaces hide everything outside
        /// the scope for its duration, so the mapper is not reading past lines they are not timing,
        /// and the sheet comes back whole the moment the pass ends.
        /// </summary>
        [Test]
        public void TestTapTimingHidesLyricsOutsideTheScope()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("select only line 2", () => clickRow(1));
            AddUntilStep("line 2 selected", () => state().SelectedLine.Value == rows()[1].HitObject);

            AddStep("start a pass", () => compose().ToggleTapTiming());
            AddUntilStep("recording", () => compose().TapTiming.Active);
            AddAssert("the scope is not the whole sheet", () => state().TapScope?.CoversEverything == false);

            // Alpha 0 makes the row non-present, so the list collapses to the scope instead of
            // leaving a hole: hidden outright, never merely dimmed.
            AddUntilStep("line 1's row is gone", () => !rows()[0].IsPresent);
            AddAssert("line 2's row is still there", () => rows()[1].IsPresent);

            AddStep("cancel", () => compose().TapTiming.Cancel());
            AddUntilStep("no longer recording", () => !compose().TapTiming.Active);

            // Cancel is the harshest exit path (no commit, no undo entry); the sheet still returns.
            AddUntilStep("every row is back", () => rows().All(r => r.IsPresent));
            AddAssert("the scope is cleared", () => state().TapScope == null);
        }

        [Test]
        public void TestWholeSheetTapPassHidesNothing()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            // Nothing selected is the fresh-paste case: the pass covers the whole sheet, which is
            // exactly when hiding would be wrong.
            AddStep("clear any selection", () =>
            {
                state().ClearMultiLineSelection();
                state().SelectedLine.Value = null;
            });

            AddStep("start a pass", () => compose().ToggleTapTiming());
            AddUntilStep("recording", () => compose().TapTiming.Active);

            AddAssert("the scope covers everything", () => state().TapScope?.CoversEverything == true);
            AddAssert("every row is still visible", () => rows().All(r => r.IsPresent));

            AddStep("cancel", () => compose().TapTiming.Cancel());
            AddUntilStep("no longer recording", () => !compose().TapTiming.Active);
        }

        /// <summary>
        /// Taps Space once the clock has moved far enough that the tap cannot be mistaken for a
        /// double fire (the session refuses taps closer than MIN_TAP_GAP_MS apart).
        /// </summary>
        private void tapAfterTheClockAdvances()
        {
            double from = 0;

            AddStep("note the clock", () => from = EditorClock.CurrentTime);
            AddUntilStep("clock advanced past the double-fire guard", () =>
                EditorClock.CurrentTime > from + TapTimingSession.MIN_TAP_GAP_MS * 3);
            AddStep("tap space", () => InputManager.Key(Key.Space));
        }

        private LyricComposeScreen compose() => Editor.ChildrenOfType<LyricComposeScreen>().Single();

        private TypeBeatHitObject firstLine() => lineAt(0);

        private TypeBeatHitObject lineAt(int index) => TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[index];

        private LyricEditState state() => Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState;

        private List<LineListPanel.LineRow> rows()
            => Editor.ChildrenOfType<LineListPanel>().Single().ChildrenOfType<LineListPanel.LineRow>()
                     .OrderBy(r => r.HitObject.LineIndex).ToList();

        /// <summary>Clicks a row on its index column (the text box owns the rest of the row).</summary>
        private void clickRow(int index, bool ctrl = false, bool shift = false)
        {
            var q = rows()[index].ScreenSpaceDrawQuad;
            InputManager.MoveMouseTo(q.TopLeft + new Vector2(17, q.Height * 0.5f));

            if (ctrl)
                InputManager.PressKey(Key.ControlLeft);
            if (shift)
                InputManager.PressKey(Key.ShiftLeft);

            InputManager.Click(MouseButton.Left);

            if (shift)
                InputManager.ReleaseKey(Key.ShiftLeft);
            if (ctrl)
                InputManager.ReleaseKey(Key.ControlLeft);
        }

        /// <summary>
        /// The word row of the detail panel: "add word" and "remove word" sit immediately left of
        /// "subdivide" (which is itself immediately left of its inverse, "unsubdivide"), act on the
        /// active line's word selection, grey out when their action is impossible, and land a
        /// multi-word removal as a SINGLE undo step.
        /// </summary>
        [Test]
        public void TestAddAndRemoveWordButtons()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddUntilStep("word buttons present", () =>
                Editor.ChildrenOfType<ActiveLineDetailPanel>().Any()
                && wordButton("add word").DrawWidth > 0);

            AddAssert("add word, then remove word, then subdivide, then its inverse", () =>
                left("add word") < left("remove word") && left("remove word") < left("subdivide (D)")
                && left("subdivide (D)") < left("unsubdivide"));

            AddAssert("same size as subdivide (one family)", () =>
                Precision.AlmostEquals(size("add word"), size("subdivide (D)"), 0.5f)
                && Precision.AlmostEquals(size("remove word"), size("subdivide (D)"), 0.5f)
                && Precision.AlmostEquals(size("unsubdivide"), size("subdivide (D)"), 0.5f));

            AddStep("select line 1", () => clickRow(0));
            AddUntilStep("line 1 active", () => state().ActiveLine.Value == lineAt(0));
            AddAssert("both enabled on a live multi-word line", () =>
                wordButton("add word").Enabled.Value && wordButton("remove word").Enabled.Value);

            // Nothing focused: the word is appended at the end of the line.
            AddStep("click add word", () => clickWordButton("add word"));
            AddUntilStep("a word was appended", () => lineAt(0).Line.RawText == "hello world word");
            AddAssert("one unit per token", () => lineAt(0).Line.Units.Count == 3);

            AddStep("select the last two words", () => state().SelectUnitRange(1, 2));
            AddStep("click remove word", () => clickWordButton("remove word"));
            AddUntilStep("both selected words went", () => lineAt(0).Line.RawText == "hello");

            AddAssert("remove is greyed out on a one-word line", () => !wordButton("remove word").Enabled.Value);
            AddAssert("add is still available", () => wordButton("add word").Enabled.Value);

            // The two removals were one transaction, so ONE undo brings both words back.
            AddStep("undo once", () => Editor.Undo());
            AddUntilStep("both words restored by a single undo", () => lineAt(0).Line.RawText == "hello world word");

            AddStep("undo again", () => Editor.Undo());
            AddUntilStep("the insertion is undone too", () => lineAt(0).Line.RawText == "hello world");
        }

        private RoundedButton wordButton(string text)
            => Editor.ChildrenOfType<ActiveLineDetailPanel>().Single()
                     .ChildrenOfType<RoundedButton>().Single(b => b.Text.ToString() == text);

        private float left(string text) => wordButton(text).ScreenSpaceDrawQuad.TopLeft.X;

        private Vector2 size(string text) => wordButton(text).ScreenSpaceDrawQuad.Size;

        private void clickWordButton(string text)
        {
            InputManager.MoveMouseTo(wordButton(text));
            InputManager.Click(MouseButton.Left);
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
