// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Visual;
using osuTK;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Where a line's first word starts exactly on the line's start, the yellow line boundary and the
    /// word's start edge sit on the same pixels (backlog 473). Hovering there shows the resize cursor,
    /// which is the WORD edge's feedback, so a press there must grab the word edge too. The boundary runs
    /// the full height of the strip and the block does not, so above and below the block the boundary is
    /// still the thing a press grabs. A backing word starting on the line's start is the same case in the
    /// backing lane.
    /// </summary>
    public partial class TestSceneTypeBeatEditorEdgeOverBoundary : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Edge over boundary";

            addLine(beatmap, 0, 1000, 3000, ("hello", 1000, 2000), ("there", 2000, 2800));
            // "You" starts exactly on line 2's start: its start edge and the boundary overlap.
            addLine(beatmap, 1, 3000, 6000, ("You", 3000, 4400), ("are", 4400, 5600));

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, double start, double end, params (string Text, double Start, double End)[] words)
        {
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = string.Join(" ", words.Select(w => w.Text)),
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = words[^1].End,
                    Units = words.Select(w => new TimedUnit { Text = w.Text, StartTime = w.Start, EndTime = w.End }).ToArray(),
                },
            });
        }

        [Test]
        public void TestPressOnTheOverlapDragsTheWordEdge()
        {
            showBothLines();

            AddStep("hover the overlap, just inside 'You'", () => InputManager.MoveMouseTo(onBlock(block(), 2)));
            AddUntilStep("the resize cursor shows", () => strip().ProvidingUserCursor);

            AddStep("press", () => InputManager.PressButton(MouseButton.Left));
            AddStep("drag right", () => InputManager.MoveMouseTo(onBlock(block(), strip().PositionOf(3700) - strip().PositionOf(3000))));
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));

            AddAssert("line 2's start did not move", () => lineAt(1).Line.StartTime == 3000 && lineAt(0).Line.EndTime == 3000);
            AddAssert("'You' was resized from its start", () => you().StartTime > 3300 && you().EndTime == 4400);

            AddStep("undo", () => Editor.Undo());
            AddUntilStep("one undo puts 'You' back", () => you().StartTime == 3000 && lineAt(1).Line.StartTime == 3000);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TestPressOnTheBoundaryOutsideTheBlockDragsTheLine(bool above)
        {
            showBothLines();

            AddStep(above ? "hover the boundary above 'You'" : "hover the boundary below 'You'", () => InputManager.MoveMouseTo(offBlock(above, 3000)));
            AddAssert("no resize cursor there", () => !strip().ProvidingUserCursor);

            AddStep("press", () => InputManager.PressButton(MouseButton.Left));
            // Left, into the gap after "there", so the drag moves the line without squishing a word.
            AddStep("drag left", () => InputManager.MoveMouseTo(offBlock(above, 2900)));
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));

            AddAssert("line 2's start moved left", () => lineAt(1).Line.StartTime < 3000 && lineAt(0).Line.EndTime == lineAt(1).Line.StartTime);
            AddAssert("'You' kept its timing", () => you().StartTime == 3000 && you().EndTime == 4400);
        }

        [Test]
        public void TestPressOnABackingWordsEdgeOnTheBoundaryDragsTheBackingWord()
        {
            showBothLines();

            AddStep("a backing word on line 2's start", () => Assert.That(TypeBeatEditorOperations.AddBackingVocal(EditorBeatmap, lineAt(1), 3000, "ooh"), Is.EqualTo(0)));
            AddUntilStep("it is drawn there", () => strip().BackingBlockCount(lineAt(1)) == 1 && backingBlock().Alpha > 0 && ooh().StartTime == 3000);

            AddStep("hover the overlap, just inside 'ooh'", () => InputManager.MoveMouseTo(onBlock(backingBlock(), 2)));
            AddUntilStep("the resize cursor shows", () => strip().ProvidingUserCursor);

            double end = 0;
            AddStep("press", () =>
            {
                end = ooh().EndTime;
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("drag right", () => InputManager.MoveMouseTo(onBlock(backingBlock(), strip().PositionOf(3200) - strip().PositionOf(3000))));
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));

            AddAssert("line 2's start did not move", () => lineAt(1).Line.StartTime == 3000 && lineAt(0).Line.EndTime == 3000);
            AddAssert("'ooh' was resized from its start", () => ooh().StartTime > 3100 && ooh().EndTime == end);
            AddAssert("'You' kept its timing", () => you().StartTime == 3000 && you().EndTime == 4400);
        }

        private void showBothLines()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("view the two lines", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(2500);
            });
            AddUntilStep("strip sized", () => strip().IsLoaded && strip().DrawWidth > 0);
            // Clear of the line list panel, which overlaps the strip's left side in the test window.
            AddUntilStep("boundary on screen", () => screen(2900, 0).X > 700 && strip().PositionOf(4400) < strip().DrawWidth);
        }

        private LyricTimeline strip() => Editor.ChildrenOfType<LyricTimeline>().Single();

        private Drawable block() => strip().WordBlockOf(lineAt(1), 0);

        private Drawable backingBlock() => strip().BackingBlockOf(lineAt(1), 0);

        /// <summary>A point <paramref name="dx"/> strip pixels right of line 2's start, at the height of the middle of <paramref name="target"/>.</summary>
        private Vector2 onBlock(Drawable target, float dx)
        {
            var s = strip();
            float y = s.ToLocalSpace(target.ScreenSpaceDrawQuad.Centre).Y;
            return s.ToScreenSpace(new Vector2(s.PositionOf(3000) + dx, y));
        }

        /// <summary>At <paramref name="time"/>, a few pixels above or below "You"'s block (on line 2's boundary at its start).</summary>
        private Vector2 offBlock(bool above, double time)
        {
            var s = strip();
            var quad = s.ToLocalSpace(block().ScreenSpaceDrawQuad);
            float y = above ? quad.TopLeft.Y - 6 : quad.BottomLeft.Y + 6;
            return s.ToScreenSpace(new Vector2(s.PositionOf(time), y));
        }

        private Vector2 screen(double time, float y) => strip().ToScreenSpace(new Vector2(strip().PositionOf(time), y));

        private TypeBeatHitObject lineAt(int index) => EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.StartTime).ElementAt(index);

        private TimedUnit you() => lineAt(1).Line.Units[0];

        private BackingVocal ooh() => lineAt(1).Line.BackingVocals[0];
    }
}
