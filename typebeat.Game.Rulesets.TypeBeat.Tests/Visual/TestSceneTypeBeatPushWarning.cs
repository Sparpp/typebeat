// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// THE PUSH WARNING (backlog 263), through the real input stack. A player lagging behind on a line
    /// the song has already left keeps it only as far as the drag cutoff, where the engine force-seals
    /// it and lands the caret on the next line. That used to arrive with no notice at all; now the
    /// stage counts it down with the cue bars' own depleting bar, RIGHT-ALIGNED at the end of the line
    /// and in the palette's one red, so the signal reads as the opposite of a cue.
    ///
    /// <para>The map is the drag shape from <see cref="TestSceneTypeBeatFletcher"/> with line 0's window
    /// stretched, so there is room to watch the bar be ABSENT before it has any business showing. Line 0
    /// runs [1000, 6000) with its vocals over by 2 s, and every target sits well short of the line's end,
    /// so it earns no boundary seal grace and its cutoff is exactly 6000 + FLETCHER_DRAG_GRACE_MS = 7500.
    /// The warning therefore covers [6000, 7500): it opens at the very instant the song leaves the line's
    /// own grace, which is the instant the seal becomes permitted but for drag protection, so the player
    /// watches precisely the borrowed time drain away. Line 1's window runs to 30 s, so nothing else is
    /// due to be taken from anybody while the assertions run.</para>
    /// </summary>
    public partial class TestSceneTypeBeatPushWarning : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;
        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();

        private double currentTime => Player.GameplayClockContainer.CurrentTime;

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "PushWarning";

            addLine(beatmap, 0, "ab", 1000, 6000, 2000, 1000, 2000);
            addLine(beatmap, 1, "cd", 6000, 30000, 8000, 6000, 8000);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, double unitStart, double unitEnd)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = new[] { new TimedUnit { Text = text, StartTime = unitStart, EndTime = unitEnd } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Line = line,
                Granularity = TimingGranularity.Word,
            });
        }

        /// <summary>
        /// Leave a character owed and let the clock run: nothing while the line is still comfortably the
        /// player's, the red bar through the last 1500 ms of the borrowed time, and nothing again once
        /// the push has actually landed them on line 1.
        /// </summary>
        [Test]
        public void TestWarningCountsDownTheCutoffAndClearsWhenThePushLands()
        {
            AddStep("load player with no mods", () => LoadPlayer(Array.Empty<Mod>()));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddAssert("the default stack is unpinned, so a push is possible at all", () => engine.FletcherEnabled);

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0 && currentTime > 0);

            // One character of two, so the player is dragging on line 0 rather than finishing it.
            AddStep("press A only", () => InputManager.Key(Key.A));
            AddAssert("line 0 is still owed a character", () =>
                engine.ActiveLineIndex == 0 && !engine.IsLineComplete && engine.CaretIndex == 1);

            // Ordinary play. The cutoff exists (the readout names 7500 from the very first frame of the
            // drag) but it is more than a cue-lead away, so the stage must draw nothing: the warning is
            // a countdown, not a status light.
            AddAssert("the cutoff is known from the first frame of the drag", () =>
                engine.DragCutoffAt == 7500 && !stage.PushWarningVisible && currentTime < 5800);

            // ...and it stays dark for all of it. Checked EVERY frame rather than sampled once, because
            // the opening edge is the claim: the bar covers the final CUE_LEAD_MS (1500) of a cutoff at
            // EndTime + SealGraceMs + FLETCHER_DRAG_GRACE_MS, and with those two constants both 1500 that
            // is EndTime + SealGraceMs = 6000 exactly, the instant the song leaves the line's own grace
            // and the seal becomes permitted but for drag protection. Widen the window and this goes red.
            AddUntilStep("dark until the borrowed time is what is left", () =>
            {
                Assert.That(stage.PushWarningVisible, Is.False, "the warning must not open before the line's grace runs out at 6000");
                return currentTime >= 5900;
            });

            // The bar, bounded before the cutoff so it cannot be satisfied by a later state. Target line
            // as well as visibility: alpha alone cannot tell "warned about the right line" from a bar
            // hanging off some other row.
            AddUntilStep("red bar shown for line 0 inside the last 1500 ms", () =>
                stage.PushWarningVisible
                && stage.PushWarningTargetLine == 0
                && engine.ActiveLineIndex == 0
                && currentTime >= 6000
                && currentTime < 7500);

            // And it is gone the moment the thing it warned about has happened: the line force-sealed,
            // the caret was landed on line 1, and line 1's own cutoff (30000 + 1500) is nowhere near.
            AddUntilStep("push lands and the warning clears", () =>
                engine.ActiveLineIndex == 1
                && engine.NextUnsealedLineIndex == 1
                && !stage.PushWarningVisible
                && stage.PushWarningTargetLine == -1);

            AddAssert("the pushed line lost its untyped character", () =>
                engine.Lines[0].Cells[1].State == CellState.Missed);
        }

        /// <summary>
        /// The other way the warning ends: the player beats it. Typing the owed character out mid
        /// countdown leaves the line owing nothing, and a line that owes nothing seals on its ordinary
        /// deadline with nobody pushed, so the bar goes out at once.
        /// </summary>
        [Test]
        public void TestTypingTheLineOutMidWarningPutsItOut()
        {
            AddStep("load player with no mods", () => LoadPlayer(Array.Empty<Mod>()));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0 && currentTime > 0);

            AddStep("press A only", () => InputManager.Key(Key.A));

            AddUntilStep("red bar shown for line 0", () =>
                stage.PushWarningVisible && stage.PushWarningTargetLine == 0 && currentTime < 7500);

            // Late, so it is judged late, which is the honest penalty for dragging. It is still the
            // character the line was owed, and paying it is what calls the push off.
            AddStep("press B", () => InputManager.Key(Key.B));
            AddAssert("line 0 is finished rather than force-sealed", () =>
                engine.Lines[0].Cells[1].State == CellState.Correct);

            AddUntilStep("the warning goes out", () =>
                !stage.PushWarningVisible && stage.PushWarningTargetLine == -1);

            AddAssert("and nothing was taken from the player", () =>
                engine.Lines[0].Cells[0].State == CellState.Correct && !engine.IsFinished);
        }

        /// <summary>
        /// WHERE the bar is, which is half of what makes it read as a warning rather than a cue. The
        /// cue bars grow out of the START of the line the player is about to gain; this one is anchored
        /// by its TopRight at the END of the line the player is about to lose, so it depletes leftward
        /// back into the line. Asserted on the DRAWN quad, so the anchoring is what is under test and
        /// not the field it is configured from.
        /// </summary>
        [Test]
        public void TestWarningIsRightAlignedAtTheEndOfTheLine()
        {
            AddStep("load player with no mods", () => LoadPlayer(Array.Empty<Mod>()));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0 && currentTime > 0);
            AddStep("press A only", () => InputManager.Key(Key.A));

            // Bounded well short of the 7500 cutoff so the assertion below still has the bar to look at.
            AddUntilStep("red bar shown for line 0", () =>
                stage.PushWarningVisible && stage.PushWarningTargetLine == 0 && currentTime < 7000);

            AddAssert("its right edge is the end of the line and it runs leftward from there", () =>
            {
                var d = stage.DisplayAt(0)!;
                float lineEnd = d.ToScreenSpace(d.PositionOfCell(d.Line.Cells.Count)).X;

                return stage.PushWarningVisible
                       && Math.Abs(stage.PushWarningScreenRightEdge.X - lineEnd) < 1f
                       && stage.PushWarningScreenLeftEdge.X < lineEnd - 1f;
            });
        }
    }
}
