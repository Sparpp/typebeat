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
    /// The unpinned caret's on-screen half, through the real input stack, and since backlog 208 that
    /// is the DEFAULT stack: no mods at all. The stage no longer scrolls when the SONG crosses a
    /// line boundary, it scrolls when the PLAYER does: finish line 0 in the first second of a line
    /// that runs for half a minute and the stage centres line 1 as soon as line 1 is due, while the
    /// song is still on line 0. The sung sweep stays behind on the song's line, which is the
    /// divergence the whole design exists to show. Since backlog 218 "as soon as line 1 is due" is
    /// the rush bound rather than the keypress, so the parked caret is on screen here too.
    /// (The mod NAMED Fletcher now pins the caret back; its own surface is
    /// pinned by <c>TypeBeatModFletcherTest</c> and <c>FletcherEngineTest</c>.)
    /// </summary>
    public partial class TestSceneTypeBeatFletcher : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;
        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();

        // Which map LoadPlayer builds. PlayerTestScene calls CreateBeatmap from inside LoadPlayer,
        // which is a custom step here, so a step may choose the fixture first. Every test sets it
        // explicitly rather than inheriting whatever the previous one left.
        private bool dragMap;

        private void useDragMap(bool drag) => AddStep(drag ? "use the drag map" : "use the rush map", () => dragMap = drag);

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "Fletcher";

            if (dragMap)
            {
                // THE DRAG MAP, the mirror of the rush one below: here line 0's window really does
                // close (3 s), so the SONG moves on to line 1 while a player who typed only "a" is
                // still on line 0. Line 0 cannot seal while they are on it with a character left
                // (drag protection holds it to 3000 + 1500 = 4500), so NextUnsealedLineIndex is
                // pinned at 0 for that whole second and a half: the exact state where reading the
                // sung line off the seal cursor lies. Line 1's vocals run 3000-5000 so its sweep is
                // live there, and its own window runs to 30 s so the playhead has nowhere further to
                // go. Every target sits well short of its line's end, so neither line earns the
                // boundary seal grace and both windows close on their EndTime exactly.
                addLine(beatmap, 0, "ab", 1000, 3000, 2000, 1000, 2000);
                addLine(beatmap, 1, "cd", 3000, 30000, 5000, 3000, 5000);

                return beatmap;
            }

            // Line 0's vocals are over by 2 s but its WINDOW runs to 30 s, so the song stays on line
            // 0 for half a minute: all the headroom the assertions need. Line 1's window opens at
            // 3 s and its vocals arrive at 6 s, so it activates at 4500 and the RUSH BOUND (backlog
            // 218) opens entry into it at 4500 - 1500 = 3000. Typing "ab" in the first second
            // therefore parks the caret first and rolls it on at 3000, with the song still on line 0
            // either way, which is what this scene is about.
            addLine(beatmap, 0, "ab", 1000, 30000, 2000, 1000, 2000);
            addLine(beatmap, 1, "cd", 3000, 60000, 7000, 6000, 7000);

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

        [Test]
        public void TestStackFollowsTheCaretNotThePlayhead()
        {
            useDragMap(false);
            AddStep("load player with no mods", () => LoadPlayer(Array.Empty<Mod>()));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddAssert("the default stack is unpinned, with the line-start snap armed and the rush bounded", () =>
                engine.FletcherEnabled && engine.FlexibleLineSnap && engine.BoundedRush);

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0);

            AddAssert("line 0 is centred", () => stage.DisplayAt(0)!.Y == 0 && stage.DisplayAt(1)!.Y > 0);

            AddStep("press A", () => InputManager.Key(Key.A));
            AddStep("press B", () => InputManager.Key(Key.B));

            // The rush bound (backlog 218): line 1 is not due for another three seconds, so the
            // finished caret is parked past the end of line 0 rather than handed on, and the stack
            // has not scrolled.
            AddAssert("the bound parks the finished caret on line 0", () =>
                engine.ActiveLineIndex == 0 && engine.IsLineComplete && stage.DisplayAt(0)!.Y == 0);

            // Backlog 223: the park is where the two heads stopped being able to share one boolean.
            // The TYPING caret hiding is the point of it, there is nothing left to type. The MAP
            // PLAYHEAD is the song's, and the song is still singing line 0 here (vocals to 2 s, park
            // to 3 s), so it has to stay on screen and keep sweeping. It used to take the typing
            // caret's IsLineComplete term and fade out with it, which since 218 blanks it for whole
            // seconds at a time. Waiting for the typing caret to finish its fade is what makes this
            // non-vacuous: both fades are started on the same frame with the same duration, so if the
            // playhead were still sharing the term it would be gone by now too.
            AddUntilStep("the typing caret hides once its line is done", () => !stage.PlayerCaretVisible);
            AddAssert("the map playhead stays lit through the park, still sweeping line 0", () =>
                engine.ActiveLineIndex == 0
                && engine.IsLineComplete
                && stage.SungCaretVisible
                && stage.DisplayAt(0)!.SweepFillWidth > 0);

            // Rush freedom, bounded: the caret goes when line 1 comes due (4500 - 1500 = 3000), with
            // the song still on line 0 (it does not seal until 30 s).
            AddUntilStep("caret rolls on to line 1 when the bound opens", () =>
                engine.ActiveLineIndex == 1 && engine.NextUnsealedLineIndex == 0 && engine.CaretIndex == 0);

            // Cursorhead centering: the stack scrolled because the PLAYER crossed the boundary.
            AddUntilStep("stack re-centres on the player's line", () =>
                stage.DisplayAt(1)!.Y == 0 && stage.DisplayAt(0)!.Y < 0);

            AddAssert("the song has not moved on", () => engine.NextUnsealedLineIndex == 0);

            // The player caret is on line 1, the sung caret stayed with the song on line 0: the two
            // heads have visibly come apart, which is the whole point of the unpinned caret.
            AddAssert("player caret and sung caret are on different lines", () =>
                stage.PlayerCaretVisible && stage.PlayerCaretPosition.Y > stage.SungCaretPosition.Y);

            // Typing on ahead is accepted, judged early (the song is still 3 s from these vocals) and
            // is inside the 5-char cap, so the run continues rather than being blocked.
            AddStep("press C", () => InputManager.Key(Key.C));
            AddAssert("the rushed char landed", () =>
                engine.Lines[1].Cells[0].State == CellState.Correct
                && engine.Lines[1].Cells[0].JudgedDelta < 0);
        }

        /// <summary>
        /// The other direction, and the half of it the stage got wrong until backlog 223: the player
        /// DRAGS, so the song leaves their line while they are still typing it. Drag protection is
        /// what makes this state exist at all, and it is also what made the old stage unable to see
        /// it: the seal is deliberately deferred while the caret is on the line, so the seal cursor
        /// (which is all the stage read) can never point at the row ahead. The playhead stranded at
        /// the tail of the row the player was still reading, sweep frozen 100% full, while the row
        /// actually being sung got no head, no sweep and no lit syllable.
        /// </summary>
        [Test]
        public void TestThePlayheadRidesTheSongWhileTheCaretDrags()
        {
            useDragMap(true);
            AddStep("load player with no mods", () => LoadPlayer(Array.Empty<Mod>()));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0);

            // One character of two, so the line stays unfinished and the player is dragging on it.
            AddStep("press A only", () => InputManager.Key(Key.A));
            AddAssert("line 0 is still owed a character", () =>
                engine.ActiveLineIndex == 0 && !engine.IsLineComplete && engine.CaretIndex == 1);

            // Past 3 s the vocals are on line 1 while the caret is held on line 0 by drag protection
            // (to 3000 + 1500 = 4500), so the head has to have moved a row DOWN, away from the caret:
            // the exact inverse of the rush case above, where it stays a row up.
            AddUntilStep("the playhead moves down on to the row being sung", () =>
                stage.SungCaretVisible && stage.SungCaretPosition.Y > stage.PlayerCaretPosition.Y);

            AddAssert("and it got there without the seal cursor moving", () =>
                engine.ActiveLineIndex == 0 && !engine.IsLineComplete && engine.NextUnsealedLineIndex == 0);

            AddAssert("line 1's sweep is running and line 0's stale fill was zeroed", () =>
                stage.DisplayAt(1)!.SweepFillWidth > 0 && stage.DisplayAt(0)!.SweepFillWidth == 0);

            // The lit group rides the same row, or the highlight and the sweep would be on different
            // lines (they are complements, not alternatives, since backlog 177).
            AddAssert("the lit syllable moved with it", () =>
                stage.DisplayAt(1)!.SungSyllable >= 0 && stage.DisplayAt(0)!.SungSyllable == -1);

            // The typing caret is untouched by all of this: it is the player's, and the player is
            // still on line 0 with a character owed.
            AddAssert("the typing caret stayed on the player's line", () =>
                stage.PlayerCaretVisible && engine.ActiveLineIndex == 0);
        }
    }
}
