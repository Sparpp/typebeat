// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Fletcher's on-screen half, through the real input stack. The stack no longer scrolls when the
    /// SONG crosses a line boundary, it scrolls when the PLAYER does: finish line 0 in the first
    /// second of a line that runs for half a minute and the stage centres line 1 immediately, while
    /// the song is still on line 0. The sung sweep stays behind on the song's line, which is the
    /// divergence the mod exists to show.
    /// </summary>
    public partial class TestSceneTypeBeatFletcher : PlayerTestScene
    {
        protected override bool HasCustomSteps => true;

        protected override Ruleset CreatePlayerRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)Player.DrawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;
        private LyricStage stage => Player.ChildrenOfType<LyricStage>().Single();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Test";
            beatmap.BeatmapInfo.Metadata.Title = "Fletcher";

            // Line 0's vocals are over by 2 s but its window runs to 30 s, and line 1's vocals do not
            // arrive until 50 s. Typing "ab" in the first second therefore finishes line 0 half a
            // minute before the song leaves it: all the headroom the assertions need.
            addLine(beatmap, 0, "ab", 1000, 30000, 2000, 1000, 2000);
            addLine(beatmap, 1, "cd", 30000, 60000, 51000, 50000, 51000);

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
            AddStep("load player with Fletcher", () => LoadPlayer(new Mod[] { new TypeBeatModFletcher() }));
            AddUntilStep("player loaded", () => Player.IsLoaded && Player.Alpha == 1);
            AddAssert("engine has the mod applied", () => engine.FletcherEnabled);

            AddUntilStep("line 0 active", () => engine.ActiveLineIndex == 0);

            AddAssert("line 0 is centred", () => stage.DisplayAt(0)!.Y == 0 && stage.DisplayAt(1)!.Y > 0);

            AddStep("press A", () => InputManager.Key(Key.A));
            AddStep("press B", () => InputManager.Key(Key.B));

            // Rush freedom: the caret is on line 1 the instant line 0 is finished, with the song still
            // on line 0 (it does not seal until 30 s).
            AddAssert("caret rolled on to line 1 while the song is still on line 0", () =>
                engine.ActiveLineIndex == 1 && engine.NextUnsealedLineIndex == 0 && engine.CaretIndex == 0);

            // Cursorhead centering: the stack scrolled because the PLAYER crossed the boundary.
            AddUntilStep("stack re-centres on the player's line", () =>
                stage.DisplayAt(1)!.Y == 0 && stage.DisplayAt(0)!.Y < 0);

            AddAssert("the song has not moved on", () => engine.NextUnsealedLineIndex == 0);

            // The player caret is on line 1, the sung caret stayed with the song on line 0: the two
            // heads have visibly come apart, which is the whole point of the mod.
            AddAssert("player caret and sung caret are on different lines", () =>
                stage.PlayerCaretVisible && stage.PlayerCaretPosition.Y > stage.SungCaretPosition.Y);

            // Typing on ahead is accepted, judged early (the song is 48 s from these vocals) and is
            // inside the 5-char cap, so the run continues rather than being blocked.
            AddStep("press C", () => InputManager.Key(Key.C));
            AddAssert("the rushed char landed", () =>
                engine.Lines[1].Cells[0].State == CellState.Correct
                && engine.Lines[1].Cells[0].JudgedDelta < 0);
        }
    }
}
