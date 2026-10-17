// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using typebeat.Game.Beatmaps;
using osuTK.Input;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
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

        [Test]
        public void TestOriginalViewGreysOnlySubdivisionsInsideAnOriginalCharacter()
        {
            Container[] subdivisionVisuals() => strip().ChildrenOfType<Drawable>()
                .Where(drawable => drawable.GetType().Name == "SyllableHandle" && drawable.Alpha > 0)
                .OrderBy(handle => handle.X)
                .Select(handle => handle.ChildrenOfType<Container>().Single(container => container.Masking)).ToArray();

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricTimeline>().Any());
            AddStep("prepare mixed applicable and in-character subdivisions", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(2000);
                EditorBeatmap.Metadata.Language = BeatmapLanguage.Japanese;
                TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "shika");
                TypeBeatEditorOperations.SetWordOriginal(EditorBeatmap, lineAt(0), 0, "しか", proposeRomanisation: false);
                TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "s|hi|ka");
                state().SelectedLine.Value = lineAt(0);
                state().ShowOriginalLyrics.Value = false;
            });
            AddUntilStep("both romanized subdivisions are bright", () => subdivisionVisuals().Length == 2
                && subdivisionVisuals().All(visual => visual.Colour.AverageColour == TypeBeatStyle.TypedChar));
            AddStep("show originals", () => state().ShowOriginalLyrics.Value = true);
            AddUntilStep("only the subdivision inside shi is grey", () => subdivisionVisuals().Length == 2
                && subdivisionVisuals()[0].Colour.AverageColour == TypeBeatStyle.UntypedChar
                && subdivisionVisuals()[1].Colour.AverageColour == TypeBeatStyle.TypedChar);
            AddStep("hover the inapplicable subdivision", () => InputManager.MoveMouseTo(subdivisionVisuals()[0]));
            AddAssert("hover keeps it grey", () => subdivisionVisuals()[0].Colour.AverageColour == TypeBeatStyle.UntypedChar);
            AddAssert("both romanized timings remain authored", () =>
                TypeBeatEditorOperations.PipeDisplayText(lineAt(0).Line, "japanese") == "s|hi|ka"
                && lineAt(0).Line.Units[0].SyllableBoundaries.Count == 2);
            AddStep("move text cuts without changing their count", () =>
                TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "shi|k|a"));
            AddUntilStep("applicability refreshes after an edit", () => subdivisionVisuals().Length == 2
                && subdivisionVisuals()[0].Colour.AverageColour == TypeBeatStyle.TypedChar
                && subdivisionVisuals()[1].Colour.AverageColour == TypeBeatStyle.UntypedChar);
            AddStep("return to romanized lyrics", () => state().ShowOriginalLyrics.Value = false);
            AddUntilStep("all subdivisions are bright again", () => subdivisionVisuals().Length == 2
                && subdivisionVisuals().All(visual => visual.Colour.AverageColour == TypeBeatStyle.TypedChar));
        }

        [Test]
        public void TestExplicitCustomReadingSubdivisionBecomesApplicableAndCanBeUndone()
        {
            Container? visual() => strip().ChildrenOfType<Drawable>()
                .SingleOrDefault(drawable => drawable.GetType().Name == "SyllableHandle" && drawable.Alpha > 0)?
                .ChildrenOfType<Container>().Single(container => container.Masking);
            OsuTextBox original() => Editor.ChildrenOfType<WordScriptEditor>().Single().ChildrenOfType<OsuTextBox>()
                .Single(box => box.PlaceholderText.ToString() == "original");
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricTimeline>().Any());
            AddStep("prepare a custom reading with an unlinked cut", () =>
            {
                EditorClock.Stop();
                EditorBeatmap.Metadata.Language = BeatmapLanguage.Japanese;
                TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "open");
                TypeBeatEditorOperations.SetWordOriginal(EditorBeatmap, lineAt(0), 0, "オープン", proposeRomanisation: false);
                TypeBeatEditorOperations.SetLineText(EditorBeatmap, lineAt(0), "o|pen");
                state().SelectedLine.Value = lineAt(0);
                state().ShowOriginalLyrics.Value = true;
            });
            AddUntilStep("unlinked cut is grey", () => visual()?.Colour.AverageColour == TypeBeatStyle.UntypedChar);
            AddStep("author the original cut", () =>
            {
                InputManager.MoveMouseTo(original());
                InputManager.Click(MouseButton.Left);
                original().Text = "オー|プン";
                InputManager.Key(Key.Enter);
            });
            AddUntilStep("linked cut is bright", () => visual()?.Colour.AverageColour == TypeBeatStyle.TypedChar);
            AddAssert("both authored spellings shown", () => original().Text == "オー|プン"
                && TypeBeatEditorOperations.PipeDisplayText(lineAt(0).Line) == "o|pen");
            AddStep("undo the explicit link", () => Editor.Undo());
            AddUntilStep("unlinked cut is grey again", () => visual()?.Colour.AverageColour == TypeBeatStyle.UntypedChar);
            AddAssert("romanized cut survived undo", () => TypeBeatEditorOperations.PipeDisplayText(lineAt(0).Line) == "o|pen");
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
