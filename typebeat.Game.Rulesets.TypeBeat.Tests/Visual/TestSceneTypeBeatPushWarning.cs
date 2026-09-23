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

        /// <summary>
        /// The clock the STAGE draws on, which is the frame-stable gameplay clock and the one
        /// <see cref="LyricStage"/> compares the cutoff against. Sampling this rather than the
        /// container's own <c>GameplayClockContainer.CurrentTime</c> is what makes an
        /// assertion about the bar and the time it was read at describe the SAME frame: the
        /// container's clock runs ahead and the frame-stable one catches up to it in 60 fps steps, so
        /// the two disagree exactly when the catch-up is mid-flight.
        /// </summary>
        private double gameplayTime => stage.Clock.CurrentTime;

        // Which map LoadPlayer builds. CreateBeatmap runs from inside LoadPlayer, which is a custom
        // step, so a step chooses the fixture first. Every test sets it explicitly rather than
        // inheriting whatever the previous one left.
        private bool wordAnchorMap;

        private void useWordAnchorMap() => AddStep("use the word-anchor map", () => wordAnchorMap = true);

        // A fourth fixture: the push lands LATER than the fixed lead the bar measures from the next
        // line's word, which is what tells a fixed lead apart from one stretched to the push.
        private bool fixedLeadMap;

        private void useFixedLeadMap() => AddStep("use the fixed-lead map", () => fixedLeadMap = true);

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "PushWarning";

            if (wordAnchorMap)
            {
                // The shape where "the following line begins" and "its first word begins" are two
                // different instants: line 1's boundary sits on line 0's end (6000, what the editor's
                // shared boundary produces) while its vocals only start at 6800. Line 0 is the same
                // drag shape as above, so its cutoff is still 7500 and the only thing that moves is
                // where the warning opens.
                addLine(beatmap, 0, "ab", 1000, 6000, 2000, 1000, 2000);
                addLine(beatmap, 1, "cd", 6000, 30000, 9000, 6800, 9000);
                return beatmap;
            }

            if (fixedLeadMap)
            {
                // Line 0 authors the largest seal grace a line may carry (700 ms), so its cutoff is
                // 6000 + 700 + 1500 = 8200 while line 1's first word begins at 6100. The bar is a
                // fixed 1.5 s from that word - [6100, 7600) - and NOT stretched over the 2100 ms the
                // whole warning span happens to be, which is the difference between a fixed lead and
                // a window fitted to the push.
                addLine(beatmap, 0, "ab", 1000, 6000, 2000, 1000, 2000, sealGrace: 700);
                addLine(beatmap, 1, "cd", 6000, 30000, 9000, 6100, 9000);
                return beatmap;
            }

            addLine(beatmap, 0, "ab", 1000, 6000, 2000, 1000, 2000);
            addLine(beatmap, 1, "cd", 6000, 30000, 8000, 6000, 8000);

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, string text, double start, double end, double singEnd, double unitStart, double unitEnd, double sealGrace = 0)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                SealGraceMs = sealGrace,
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

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0 && gameplayTime > 0);

            // One character of two, so the player is dragging on line 0 rather than finishing it.
            AddStep("press A only", () => InputManager.Key(Key.A));
            AddAssert("line 0 is still owed a character", () =>
                engine.ActiveLineIndex == 0 && !engine.IsLineComplete && engine.CaretIndex == 1);

            // Ordinary play. The cutoff exists (the readout names 7500 from the very first frame of the
            // drag) but it is more than a cue-lead away, so the stage must draw nothing: the warning is
            // a countdown, not a status light.
            AddAssert("the cutoff is known from the first frame of the drag", () =>
                engine.DragCutoffAt == 7500 && !stage.PushWarningVisible && gameplayTime < 6000);

            double lastDark = -1;
            double firstLit = -1;
            float firstLitAlpha = 1;
            float lastLitAlpha = 0;

            // The opening EDGE, checked every frame. The bar's window opens when the line that is about
            // to take the player counts as starting - its FIRST WORD - and closes on the cutoff at
            // EndTime + SealGraceMs + FLETCHER_DRAG_GRACE_MS. Line 1's first unit starts on its boundary
            // here, so both land on 6000 and the window is [6000, 7500) exactly as it was when the rule
            // was a fixed CUE_LEAD_MS before the cutoff.
            //
            // The assertion is a function of the time the frame LANDED ON rather than of the frame count,
            // which is what keeps it honest on a loaded machine. A sweep that asserted "dark" flatly and
            // then checked "are we at 5900 yet" asserts the wrong half whenever a frame straddles 6000,
            // and it straddles constantly: gameplay time advances ~200 ms per frame here, so the frames
            // that would satisfy it occupy 94 ms of every 200 (that is how this test went from a load
            // flake to a hard 20-of-20 failure, the phase having simply stopped being lucky). Stated per
            // frame, "the bar is up exactly when the sampled time is inside [6000, 7500)" needs no frame
            // to land anywhere in particular and pins the edge with no tolerance at all: the last dark
            // frame and the first lit one are adjacent, so it cannot pass if the bar opened a frame early
            // or a frame late.
            //
            // Membership, not visibility: the bar FADES IN from nothing (see the alpha assertions
            // below), so "is the bar drawn yet" lags the window by a few frames and cannot pin an edge
            // on its own. The bar being COUNTED DOWN is the crisp fact, and the two are pinned apart.
            AddUntilStep("dark below 6000 and counting from 6000, on every frame", () =>
            {
                double t = gameplayTime;
                bool open = stage.PushWarningWindowOpen;

                if (open)
                {
                    if (firstLit < 0)
                    {
                        firstLit = t;
                        firstLitAlpha = stage.PushWarningAlpha;
                    }

                    lastLitAlpha = stage.PushWarningAlpha;

                    Assert.That(stage.PushWarningTargetLine, Is.EqualTo(0), $"at {t:F0} ms the bar warned about the wrong line");
                    Assert.That(engine.ActiveLineIndex, Is.EqualTo(0), $"at {t:F0} ms the player was no longer on the line being counted down");
                }
                else
                    lastDark = t;

                Assert.That(open, Is.EqualTo(t >= 6000),
                    $"at {t:F0} ms the warning was {(open ? "up before" : "still dark after")} the next line's first word at 6000");

                // Stop once the window is old enough to have shown how bright it gets, or the moment it
                // shuts. Sampling right up to the cutoff would make the far end of the sweep a race
                // against a frame that steps over it; the alpha only has to be READ late in the window,
                // not at the last instant of it.
                return t >= 7000 || (t > 6000 && !open);
            });

            AddAssert("it opened on the frame that crossed the next line's word, and not one frame either side", () =>
                lastDark < 6000 && firstLit >= 6000 && firstLit < 7500);

            // THE ENTRANCE, which the edge cannot pin: the bar must not arrive at half brightness
            // between one frame and the next (what it did when it borrowed the cues' ramp whole), and
            // it must have climbed to its full strength by the time it has drained into the push. The
            // opening frame is dim by construction, the fade starting from nothing; on the ~200-270 ms
            // frames this scene runs on that is under a third of what the cues open at.
            AddAssert("it fades in rather than snapping on, and is bright as it drains", () =>
                firstLitAlpha < 0.5f && lastLitAlpha > 0.7f);

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

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0 && gameplayTime > 0);

            AddStep("press A only", () => InputManager.Key(Key.A));

            // Stops on the FIRST frame the bar is up, which is a frame no sweep can miss, and leaves the
            // whole of the countdown ahead of the keypress below. Bounding it with "and the clock is
            // under 7500" instead would let it be satisfied on a frame 10 ms short of the cutoff, where
            // the force-seal beats the key and the line is pushed rather than finished.
            AddUntilStep("red bar shown for line 0", () =>
                stage.PushWarningVisible && stage.PushWarningTargetLine == 0);

            // Late, so it is judged late, which is the honest penalty for dragging. It is still the
            // character the line was owed, and paying it is what calls the push off.
            AddStep("press B", () =>
            {
                Assert.That(gameplayTime, Is.LessThan(7500), "the frame ran past the cutoff before the key could be pressed");
                InputManager.Key(Key.B);
            });
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

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0 && gameplayTime > 0);
            AddStep("press A only", () => InputManager.Key(Key.A));

            // The first frame the bar is up, so the whole 1500 ms window is still ahead of the assertion
            // below and it is certain to have a bar to look at.
            AddUntilStep("red bar shown for line 0", () =>
                stage.PushWarningVisible && stage.PushWarningTargetLine == 0);

            AddAssert("its right edge is the end of the line and it runs leftward from there", () =>
            {
                Assert.That(gameplayTime, Is.LessThan(7500), "the frame ran past the cutoff before the bar could be measured");

                var d = stage.DisplayAt(0)!;
                float lineEnd = d.ToScreenSpace(d.PositionOfCell(d.Line.Cells.Count)).X;

                return stage.PushWarningVisible
                       && Math.Abs(stage.PushWarningScreenRightEdge.X - lineEnd) < 1f
                       && stage.PushWarningScreenLeftEdge.X < lineEnd - 1f;
            });
        }

        /// <summary>
        /// WHEN the window opens, since a line counts as starting at its first word. Line 1's boundary
        /// passes at 6000 and its first word begins at 6800; the unpinned caret cannot act on the
        /// boundary, so the warning must stay dark right through it and open with the word, still
        /// draining into the same push at 7500. Nothing about the PUNISHMENT moves - same cutoff, same
        /// force-seal - only the moment the player is told about it.
        /// </summary>
        [Test]
        public void TestWarningOpensWithTheNextLinesWordNotItsBoundary()
        {
            useWordAnchorMap();
            AddStep("load player with no mods", () => LoadPlayer(Array.Empty<Mod>()));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0 && gameplayTime > 0);
            AddStep("press A only", () => InputManager.Key(Key.A));
            AddAssert("the push being warned about is the same one", () =>
                engine.DragCutoffAt == 7500 && engine.ActiveLineIndex == 0);

            // The next line's boundary comes and goes with the bar still dark: the player is on line 0,
            // and a boundary they cannot type from is not a moment anything happens at. Swept per
            // frame, so no frame has to land anywhere in particular - the bar is shut on every frame
            // below the word and counting on every frame from it, and the two facts are read off the
            // times the frames actually landed on.
            double lastDark = -1;
            double firstLit = -1;

            AddUntilStep("dark through the boundary, counting from the word, on every frame", () =>
            {
                double t = gameplayTime;
                bool open = stage.PushWarningWindowOpen;

                if (open)
                {
                    if (firstLit < 0)
                        firstLit = t;

                    Assert.That(stage.PushWarningTargetLine, Is.EqualTo(0), $"at {t:F0} ms the bar warned about the wrong line");
                    Assert.That(engine.ActiveLineIndex, Is.EqualTo(0), $"at {t:F0} ms the player was no longer on the line being counted down");
                }
                else if (t < 6800)
                    lastDark = t;

                Assert.That(open, Is.EqualTo(t >= 6800),
                    $"at {t:F0} ms the warning was {(open ? "up before" : "still dark after")} the next line's word at 6800");

                // Stop with half a second of window still ahead of the sweep, so the exit never races
                // the cutoff: this test is about the OPENING edge, and the other one reads the far end.
                return t >= 7000;
            });

            AddAssert("the boundary at 6000 passed with the bar dark, which the word at 6800 opened", () =>
                lastDark > 6000 && firstLit >= 6800 && firstLit < 7500);
        }

        /// <summary>
        /// The bar is a FIXED lead, not a window fitted to the punishment. Here the push lands at 8200 -
        /// a full 2.1 s after the next line's first word at 6100 - so a bar that stretched itself over
        /// the whole span would still be counting at 8000, while a fixed 1.5 s lead is out by 7600.
        /// </summary>
        [Test]
        public void TestWarningIsAFixedLeadFromTheWordNotTheSpanToThePush()
        {
            useFixedLeadMap();
            AddStep("load player with no mods", () => LoadPlayer(Array.Empty<Mod>()));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0 && gameplayTime > 0);
            AddStep("press A only", () => InputManager.Key(Key.A));
            AddAssert("the push being warned about is the 8200 one", () =>
                engine.DragCutoffAt == 8200 && gameplayTime < 6100);

            double lastLit = -1;
            double firstDark = -1;

            AddUntilStep("lit from the word at 6100, out again at 7600, on every frame", () =>
            {
                double t = gameplayTime;
                bool open = stage.PushWarningWindowOpen;

                if (open)
                {
                    lastLit = t;
                    Assert.That(stage.PushWarningTargetLine, Is.EqualTo(0), $"at {t:F0} ms the bar warned about the wrong line");
                }
                else if (t > 6100)
                    firstDark = t;

                Assert.That(open, Is.EqualTo(t >= 6100 && t < 7600),
                    $"at {t:F0} ms the warning was {(open ? "up before" : "still dark after")} its fixed lead from the word at 6100");

                return t >= 7600;
            });

            // Out a good second before the push, with the push still ahead: that is what "fixed" means,
            // and a window stretched to the punishment would fail every one of these.
            AddAssert("a full 1.5 s from the word, and out well before the push", () =>
                lastLit < 7600 && firstDark >= 7600 && firstDark < 8200 && engine.DragCutoffAt == 8200);
        }
    }
}
