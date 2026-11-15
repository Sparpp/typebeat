// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
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
    /// A timeline drag is decided by where the mouse is RELEASED (backlog 466): every frame of the drag
    /// re-applies the edit to the map as it stood when the drag began, so a word the cursor squished on
    /// the way out comes back whole when the cursor returns. The user report: dragging a line boundary
    /// over a word and back left the word squished although the button was never let go.
    /// </summary>
    public partial class TestSceneTypeBeatEditorDragCommit : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Drag commit";

            addLine(beatmap, 0, 1000, 3000, ("hello", 1000, 2000), ("there", 2000, 2800));
            addLine(beatmap, 1, 3000, 6000, ("You", 3200, 4400), ("are", 4400, 5600));

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
        public void TestDraggingTheLineBoundaryOverAWordAndBackLeavesTheWordWhole()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("view the two lines", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(2500);
            });
            AddUntilStep("strip sized", () => strip().IsLoaded && strip().DrawWidth > 0);
            // Clear of the line list panel, which overlaps the strip's left side in the test window.
            AddUntilStep("boundary on screen", () => at(2900).X > 700 && strip().PositionOf(4400) < strip().DrawWidth);

            AddStep("grab line 2's boundary", () =>
            {
                InputManager.MoveMouseTo(at(3000));
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("drag right, over 'You'", () => InputManager.MoveMouseTo(at(3900)));
            AddUntilStep("'You' is squished mid-drag", () => you().StartTime > 3500);
            AddStep("drag back left, button still down", () => InputManager.MoveMouseTo(at(2900)));
            AddUntilStep("'You' is whole again before the release", () => you().StartTime == 3200 && you().EndTime == 4400);
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));

            AddAssert("'You' kept its timing", () => you().StartTime == 3200 && you().EndTime == 4400);
            AddAssert("the boundary moved left", () => lineAt(1).Line.StartTime < 3000 && lineAt(0).Line.EndTime == lineAt(1).Line.StartTime);
            AddAssert("line 1's words are untouched", () => lineAt(0).Line.Units[1].EndTime == 2800);
        }

        [Test]
        public void TestTheReleasePointIsWhatCommits()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("view the two lines", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(2500);
            });
            AddUntilStep("strip sized", () => strip().IsLoaded && strip().DrawWidth > 0);
            // Clear of the line list panel, which overlaps the strip's left side in the test window.
            AddUntilStep("boundary on screen", () => at(2900).X > 700 && strip().PositionOf(4400) < strip().DrawWidth);

            AddStep("drag line 2's boundary out over 'You', back, then out again", () =>
            {
                InputManager.MoveMouseTo(at(3000));
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("out", () => InputManager.MoveMouseTo(at(4200)));
            
            AddStep("back", () => InputManager.MoveMouseTo(at(3000)));
            
            AddStep("out a little", () => InputManager.MoveMouseTo(at(3600)));
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));
            AddAssert("'You' ends where it always did", () => you().EndTime == 4400);
            AddAssert("'You' starts at the release, not at the furthest point", () => you().StartTime < 4000 && you().StartTime == lineAt(1).Line.StartTime);

            AddStep("undo", () => Editor.Undo());
            AddUntilStep("one undo puts everything back", () => you().StartTime == 3200 && lineAt(1).Line.StartTime == 3000);
        }

        [Test]
        public void TestShorteningAHoldPastATickAndBackKeepsTheTick()
        {
            double tick = 0;

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("view the two lines", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(2500);
            });
            AddUntilStep("strip sized", () => strip().IsLoaded && strip().DrawWidth > 0);

            AddStep("hold the 'o' of 'You' with a tick late in it", () =>
            {
                Assert.That(TypeBeatEditorOperations.AddHold(EditorBeatmap, lineAt(1), 0, 1, 3200), Is.Not.Null);
                var h = hold();
                tick = h.StartTime + (h.EndTime - h.StartTime) * 0.6;
                Assert.That(TypeBeatEditorOperations.AddHoldTickAt(EditorBeatmap, lineAt(1), 0, tick), Is.EqualTo(HoldTickPlacement.Added));
                tick = hold().Ticks.Single();
            });
            AddUntilStep("the hold is drawn", () => strip().HoldTickCount == 1);
            AddUntilStep("the hold end is on screen, clear of the line list", () => at(hold().StartTime).X > 700 && strip().PositionOf(hold().EndTime) < strip().DrawWidth);

            double end = 0;
            AddStep("grab the hold's end", () =>
            {
                end = hold().EndTime;
                InputManager.MoveMouseTo(atHold(end));
                InputManager.PressButton(MouseButton.Left);
            });
            AddStep("drag it in, before the tick", () => InputManager.MoveMouseTo(atHold(hold().StartTime + (tick - hold().StartTime) * 0.4)));
            AddUntilStep("the tick is gone mid-drag", () => hold().Ticks.Count == 0);
            AddStep("drag it back out, button still down", () => InputManager.MoveMouseTo(atHold(end)));
            AddUntilStep("the tick is back before the release", () => hold().Ticks.SequenceEqual(new[] { tick }));
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));

            AddAssert("the hold kept its tick", () => hold().Ticks.SequenceEqual(new[] { tick }));
        }

        private LyricTimeline strip() => Editor.ChildrenOfType<LyricTimeline>().Single();

        private Vector2 at(double time)
        {
            // At the height of line 2's boundary handle, so the press lands on the handle.
            var s = strip();
            float y = s.ToLocalSpace(s.BoundaryHandles[1].ScreenSpaceDrawQuad.Centre).Y;
            return s.ToScreenSpace(new Vector2(s.PositionOf(time), y));
        }

        /// <summary>At the height of the drawn hold, so the press lands on its edge handle.</summary>
        private Vector2 atHold(double time)
        {
            var s = strip();
            float y = s.ToLocalSpace(s.HoldRegionAt(0).ScreenSpaceDrawQuad.Centre).Y;
            return s.ToScreenSpace(new Vector2(s.PositionOf(time), y));
        }

        private WordHold hold() => you().Objects.Holds.Single();

        private TypeBeatHitObject lineAt(int index) => EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().OrderBy(h => h.StartTime).ElementAt(index);

        private TimedUnit you() => lineAt(1).Line.Units[0];
    }
}
