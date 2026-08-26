// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Components.Menus;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Undo/redo driven by REAL key combinations (Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z) rather than by
    /// calling <c>Editor.Undo()</c> through the test accessor: the platform-action routing from a
    /// key press to the change handler is exactly the surface the accessor skips, so it is pinned
    /// here end to end. Covers the layered undo of a focused line text box (an in-progress edit is
    /// reverted before the editor history is touched), the tap-timing pass swallowing the mutating
    /// actions for its duration, the no-op while a transaction is open, and the Edit-menu
    /// enabled/disabled transitions.
    /// </summary>
    public partial class TestSceneTypeBeatEditorUndo : EditorTestScene
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
            beatmap.BeatmapInfo.Metadata.Title = "Undo";

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
        public void TestCtrlZUndoesLineDeleteAndBothRedoBindingsWork()
        {
            AddAssert("2 lines", () => EditorBeatmap.HitObjects.Count == 2);

            AddStep("delete first line via ops", () => TypeBeatEditorOperations.DeleteLine(EditorBeatmap, lineAt(0)));
            AddAssert("1 line", () => EditorBeatmap.HitObjects.Count == 1);

            pressCtrlZ();
            AddUntilStep("ctrl+z restored the line", () => EditorBeatmap.HitObjects.Count == 2);
            AddAssert("restored text matches", () => lineAt(0).Line.RawText == "hello world");

            // Windows maps BOTH Ctrl+Y and Ctrl+Shift+Z to Redo; a user may press either.
            pressCtrlY();
            AddUntilStep("ctrl+y redid the delete", () => EditorBeatmap.HitObjects.Count == 1);

            pressCtrlZ();
            AddUntilStep("undone again", () => EditorBeatmap.HitObjects.Count == 2);

            pressCtrlShiftZ();
            AddUntilStep("ctrl+shift+z redid the delete", () => EditorBeatmap.HitObjects.Count == 1);
        }

        [Test]
        public void TestCtrlZUndoesWordRetime()
        {
            AddAssert("first word starts at 1000", () => lineAt(0).Line.Units[0].StartTime == 1000);

            AddStep("stamp the word's start at 1600", () => TypeBeatEditorOperations.StampUnitStart(EditorBeatmap, lineAt(0), 0, 1600));
            AddAssert("word start moved", () => lineAt(0).Line.Units[0].StartTime == 1600);
            AddAssert("hand timing promoted granularity", () => lineAt(0).Granularity == TimingGranularity.Word);

            pressCtrlZ();
            AddUntilStep("ctrl+z reverted the word start", () => lineAt(0).Line.Units[0].StartTime == 1000);
            AddAssert("granularity back to Line", () => lineAt(0).Granularity == TimingGranularity.Line);
        }

        [Test]
        public void TestCtrlZUndoesTextEditWithNoTextBoxFocused()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddUntilStep("rows built", () => rows().Count == 2);
            AddAssert("no text box has focus", () => Editor.ChildrenOfType<OsuTextBox>().All(b => !b.HasFocus));

            AddStep("commit a text edit via ops", () => TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "goodbye world"));
            AddAssert("text committed", () => lineAt(0).Line.RawText == "goodbye world");

            pressCtrlZ();
            AddUntilStep("ctrl+z reverted the text", () => lineAt(0).Line.RawText == "hello world");
        }

        /// <summary>
        /// The layered-undo decision for the line list's text boxes: while a box is focused with an
        /// UNCOMMITTED edit, Ctrl+Z reverts the box to the committed text (and Redo is inert), so an
        /// editor-level undo cannot rebuild the rows and destroy the edit mid-keystroke; once the
        /// box is pristine the same combination steps into the editor history as usual.
        /// </summary>
        [Test]
        public void TestFocusedDirtyTextBoxGetsLayeredUndo()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddUntilStep("rows built", () => rows().Count == 2);

            AddStep("commit a text edit via ops", () => TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "howdy world"));
            AddUntilStep("row shows the committed text", () => textBoxOf(0).Text == "howdy world");

            AddStep("click into the text box", () =>
            {
                InputManager.MoveMouseTo(textBoxOf(0));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("box focused", () => textBoxOf(0).HasFocus);

            AddStep("type an in-progress edit", () => textBoxOf(0).Text = "howdy world wip");

            // Redo must not vaporise the in-progress edit either.
            pressCtrlY();
            AddAssert("redo left the edit alone", () => textBoxOf(0).Text == "howdy world wip");
            AddAssert("and the beatmap alone", () => lineAt(0).Line.RawText == "howdy world");

            // First Ctrl+Z: text-level revert, editor history untouched.
            pressCtrlZ();
            AddUntilStep("box reverted to the committed text", () => textBoxOf(0).Text == "howdy world");
            AddAssert("box kept focus", () => textBoxOf(0).HasFocus);
            AddAssert("no editor undo happened", () => lineAt(0).Line.RawText == "howdy world" && EditorBeatmap.HitObjects.Count == 2);

            // Second Ctrl+Z on the now-pristine box: the editor history takes it.
            pressCtrlZ();
            AddUntilStep("editor undo reverted the commit", () => lineAt(0).Line.RawText == "hello world");
        }

        [Test]
        public void TestUndoSwallowedDuringTapPassThenWorksAfter()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("commit a text edit via ops", () => TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "renamed line"));
            AddAssert("text committed", () => lineAt(0).Line.RawText == "renamed line");

            AddStep("start a pass over the whole sheet", () =>
            {
                state().SelectedLine.Value = null;
                state().ClearMultiLineSelection();
                compose().ToggleTapTiming();
            });
            AddUntilStep("recording", () => compose().TapTiming.Active);

            // Record-then-commit: nothing may mutate the beatmap mid-pass. With the overlay
            // focused (the normal state of a pass) its blanket key swallow blocks the combo.
            pressCtrlZ();
            AddAssert("pass still recording", () => compose().TapTiming.Active);
            AddAssert("mid-pass ctrl+z mutated nothing", () => lineAt(0).Line.RawText == "renamed line" && lineAt(0).Line.StartTime == 1000);

            // Steal focus from the overlay (a whole-sheet pass keeps every row visible, so a text
            // box is clickable). Ctrl+Z now arrives as a platform action instead of a raw key the
            // focused overlay could swallow; the pass must still refuse it.
            AddStep("click a text box mid-pass", () =>
            {
                InputManager.MoveMouseTo(textBoxOf(0));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("box focused, overlay not", () => textBoxOf(0).HasFocus);

            pressCtrlZ();
            AddAssert("pass still recording after unfocused ctrl+z", () => compose().TapTiming.Active);
            AddAssert("still nothing mutated", () => lineAt(0).Line.RawText == "renamed line" && lineAt(0).Line.StartTime == 1000);

            AddStep("cancel the pass", () => compose().TapTiming.Cancel());
            AddUntilStep("no longer recording", () => !compose().TapTiming.Active);

            pressCtrlZ();
            AddUntilStep("undo works again after the pass", () => lineAt(0).Line.RawText == "hello world");
        }

        [Test]
        public void TestCtrlZUndoesCommittedTapPass()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());

            AddStep("start a pass over the whole sheet", () =>
            {
                state().SelectedLine.Value = null;
                state().ClearMultiLineSelection();
                compose().ToggleTapTiming();
            });
            AddUntilStep("recording", () => compose().TapTiming.Active);
            AddUntilStep("song running", () => EditorClock.IsRunning);

            tapAfterTheClockAdvances();
            AddUntilStep("first word timed", () => compose().TapTiming.Session?.TappedCount == 1);

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
            AddAssert("the pass landed", () => lineAt(0).Line.StartTime == recorded[0] && lineAt(1).Line.StartTime == recorded[1]);

            pressCtrlZ();
            AddUntilStep("ctrl+z reverted the whole pass", () =>
                lineAt(0).Line.StartTime == 1000 && lineAt(1).Line.StartTime == 3000);
        }

        [Test]
        public void TestCtrlZIsIgnoredWhileTransactionOpen()
        {
            AddStep("commit a text edit via ops", () => TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "renamed line"));
            AddAssert("text committed", () => lineAt(0).Line.RawText == "renamed line");

            // An open transaction is what a mid-drag word block holds; RestoreState must refuse to
            // interleave with it rather than restore a snapshot under the drag's feet.
            AddStep("open a transaction", () => EditorBeatmap.BeginChange());

            pressCtrlZ();
            AddAssert("undo refused mid-transaction", () => lineAt(0).Line.RawText == "renamed line");

            AddStep("close the transaction", () => EditorBeatmap.EndChange());

            pressCtrlZ();
            AddUntilStep("undo works once the transaction closed", () => lineAt(0).Line.RawText == "hello world");
        }

        /// <summary>
        /// The setup section's beatdrop stamp is a bare bindable write; it is undoable only
        /// because <c>EditorBeatmap.IntroBeatdrop</c> wraps its propagation in a
        /// BeginChange/EndChange transaction (as PreviewTime does), which is what pushes the undo
        /// state. This pins that wiring: were it dropped, the stamp would become silently
        /// non-undoable, folded into whichever state the next operation happens to push.
        /// </summary>
        [Test]
        public void TestBeatdropStampIsUndoableWithCtrlZ()
        {
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("type!beat setup section shown", () => Editor.ChildrenOfType<TypeBeatSetupSection>().Any());

            AddAssert("no beatdrop initially", () => EditorBeatmap.IntroBeatdrop.Value == null);

            AddStep("seek to 2500", () => EditorClock.Seek(2500));
            // The section may sit below the fold of the scrolled setup screen, so the button's
            // action is invoked directly; the surface under test is the undo wiring behind it.
            AddStep("stamp the beatdrop at the playhead", () => stampButton().Action!.Invoke());
            AddAssert("beatdrop stamped", () => EditorBeatmap.IntroBeatdrop.Value == 2500);

            pressCtrlZ();
            AddUntilStep("ctrl+z reverted the beatdrop", () => EditorBeatmap.IntroBeatdrop.Value == null);

            pressCtrlY();
            AddUntilStep("ctrl+y restored the beatdrop", () => EditorBeatmap.IntroBeatdrop.Value == 2500);
        }

        [Test]
        public void TestMenuUndoRedoStatesFollowHistory()
        {
            AddUntilStep("menu bar present", () => Editor.ChildrenOfType<EditorMenuBar>().Any());

            AddAssert("fresh editor: undo and redo disabled", () => undoItem().Action.Disabled && redoItem().Action.Disabled);

            AddStep("delete first line via ops", () => TypeBeatEditorOperations.DeleteLine(EditorBeatmap, lineAt(0)));
            AddUntilStep("undo enabled, redo disabled", () => !undoItem().Action.Disabled && redoItem().Action.Disabled);

            pressCtrlZ();
            AddUntilStep("undo disabled, redo enabled", () => undoItem().Action.Disabled && !redoItem().Action.Disabled);

            pressCtrlY();
            AddUntilStep("undo enabled, redo disabled again", () => !undoItem().Action.Disabled && redoItem().Action.Disabled);
        }

        private void pressCtrlZ() => AddStep("press ctrl+z", () =>
        {
            InputManager.PressKey(Key.ControlLeft);
            InputManager.Key(Key.Z);
            InputManager.ReleaseKey(Key.ControlLeft);
        });

        private void pressCtrlY() => AddStep("press ctrl+y", () =>
        {
            InputManager.PressKey(Key.ControlLeft);
            InputManager.Key(Key.Y);
            InputManager.ReleaseKey(Key.ControlLeft);
        });

        private void pressCtrlShiftZ() => AddStep("press ctrl+shift+z", () =>
        {
            InputManager.PressKey(Key.ControlLeft);
            InputManager.PressKey(Key.ShiftLeft);
            InputManager.Key(Key.Z);
            InputManager.ReleaseKey(Key.ShiftLeft);
            InputManager.ReleaseKey(Key.ControlLeft);
        });

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

        private LyricEditState state() => compose().EditState;

        private TypeBeatHitObject lineAt(int index) => TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[index];

        private List<LineListPanel.LineRow> rows()
            => Editor.ChildrenOfType<LineListPanel>().Single().ChildrenOfType<LineListPanel.LineRow>()
                     .OrderBy(r => r.HitObject.LineIndex).ToList();

        private OsuTextBox textBoxOf(int index) => rows()[index].ChildrenOfType<OsuTextBox>().Single();

        // The Edit menu is Items[1] (File, Edit, View, Timing); undo and redo are its first two entries.
        private MenuItem undoItem() => Editor.ChildrenOfType<EditorMenuBar>().Single().Items[1].Items[0];

        private MenuItem redoItem() => Editor.ChildrenOfType<EditorMenuBar>().Single().Items[1].Items[1];

        private FormButton stampButton()
            => Editor.ChildrenOfType<TypeBeatSetupSection>().Single()
                     .ChildrenOfType<FormButton>().Single(b => b.ButtonText.ToString() == "Set @ playhead");
    }
}
