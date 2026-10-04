// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Graphics;
using osuTK.Input;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit.Timing;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatEditor
    {
        [Test]
        public void TestLineBoundaryBandRefreshesAfterAddingAndRemovingALine()
        {
            LineBoundariesBand band = null!;
            TypeBeatHitObject added = null!;
            int expectedCount = 0;
            AddUntilStep("line boundary band loaded", () =>
            {
                band = Editor.ChildrenOfType<LineBoundariesBand>().SingleOrDefault()!;
                return band?.IsLoaded == true && band.DrawnElementCount > 0;
            });
            AddStep("add a line", () =>
            {
                added = TypeBeatEditorOperations.AddLine(EditorBeatmap, 9000, "new line")!;
                expectedCount = EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Sum(line => 2 + line.Line.Units.Count);
            });
            AddUntilStep("new line shades and ticks are drawn", () => band.DrawnElementCount == expectedCount);
            AddStep("remove the added line", () =>
            {
                TypeBeatEditorOperations.DeleteLine(EditorBeatmap, added);
                expectedCount = EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Sum(line => 2 + line.Line.Units.Count);
            });
            AddUntilStep("removed line disappears from the band", () => band.DrawnElementCount == expectedCount);
        }

        [Test]
        public void TestMovingSubdivisionsBetweenWordsRebuildsAllHandles()
        {
            Drawable[] visibleHandles() => strip().ChildrenOfType<Drawable>()
                .Where(drawable => drawable.GetType().Name == "SyllableHandle" && drawable.Alpha > 0).ToArray();

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricTimeline>().Any());
            AddStep("prepare one cut on each word", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(2000);
                TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "a|bc de|f");
                state().SelectedLine.Value = lineAt(0);
            });
            AddUntilStep("both original handles shown", () => visibleHandles().Length == 2);
            AddStep("move one subdivision to the other word in one commit", () =>
                TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "abc d|e|f"));
            AddAssert("the total cut count stayed the same", () => lineAt(0).Line.Units.Sum(unit => unit.SyllableBoundaries.Count) == 2);
            AddUntilStep("both handles belong to the second word", () =>
            {
                var handles = visibleHandles().OrderBy(handle => handle.X).ToArray();
                var boundaries = lineAt(0).Line.Units[1].SyllableBoundaries;
                return handles.Length == 2 && boundaries.Count == 2
                       && Math.Abs(handles[0].X - strip().PositionOf(boundaries[0])) < 1
                       && Math.Abs(handles[1].X - strip().PositionOf(boundaries[1])) < 1;
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TestResizingPastASubdivisionCompletesTheDragAndRestoresUndo(bool grid)
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricTimeline>().Any());
            AddStep("prepare subdivided word", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(2000);
                // The shared smoke fixture has two lyric tokens stored as one unit. Undo
                // decodes states, so prepare a real one-token word with reload-stable timing.
                TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "hello");
                TypeBeatEditorOperations.SetUnitTiming(EditorBeatmap, lineAt(0), 0, 1400, 2600);
                TypeBeatEditorOperations.AddSyllableBoundary(EditorBeatmap, lineAt(0), 0, 1800);
                state().SelectedLine.Value = lineAt(0);
            });
            AddUntilStep("word and subdivision shown", () => strip().IsLoaded && strip().DrawWidth > 0
                && lineAt(0).Line.Units[0].SyllableBoundaries.Count == 1);

            AddAssert("fixture has one lyric token and one timed word", () => lineAt(0).Line.RawText == "hello"
                && lineAt(0).Line.Units.Count == 1 && lineAt(0).Line.Units[0].Text == "hello");

            if (grid)
            {
                AddStep("enable grid snapping", () =>
                {
                    var button = Editor.ChildrenOfType<TimingGridControls>().Single()
                        .ChildrenOfType<RoundedButton>().Single(b => b.Text.ToString() == "Snap to Grid: Off");
                    InputManager.MoveMouseTo(button);
                    InputManager.Click(MouseButton.Left);
                });
            }

            dragStripHandle(() => strip().PositionOf(1400) + 3, () => strip().PositionOf(2000), midDrag: () =>
            {
                AddUntilStep("edge crossed the subdivision", () => lineAt(0).Line.Units[0].SyllableBoundaries.Count == 0);
                AddAssert("drag still owns its interaction", () => state().InteractionPinned);
            });

            AddUntilStep("release cleared the interaction pin", () => !state().InteractionPinned);
            AddAssert("word reached the snapped caret", () => Math.Abs(lineAt(0).Line.Units[0].StartTime - 2000) < 1);
            AddStep("undo the completed drag", () => Editor.Undo());
            AddUntilStep("undo restores both the edge and its subdivision", () => lineAt(0).Line.Units[0].StartTime == 1400
                && lineAt(0).Line.Units[0].SyllableBoundaries.Count == 1);

            dragStripHandle(() => strip().PositionOf(2600) - 3, () => strip().PositionOf(2200));
            AddUntilStep("another drag completes", () => !state().InteractionPinned
                && lineAt(0).Line.Units[0].EndTime < 2600);
        }
    }
}
