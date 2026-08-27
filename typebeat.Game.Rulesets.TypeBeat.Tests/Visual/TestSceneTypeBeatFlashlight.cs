// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
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
    /// The reworked Flashlight mod hides characters instead of dimming pixels: only a window of
    /// chars around the caret is lit on the active line, the other stack lines are hidden entirely,
    /// and the window slides as the caret advances. Judgement is untouched (visibility is cosmetic),
    /// so this drives the caret through the full input path and asserts on per-cell alpha.
    /// </summary>
    public partial class TestSceneTypeBeatFlashlight : OsuManualInputManagerTestScene
    {
        private const string active_text = "abcdefghijklmnopqrst"; // 20 single-word letters (no spaces)
        private const int radius = 5;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypeBeatPlayfield playfield => (TypeBeatPlayfield)drawableRuleset.Playfield;
        private TypingEngine engine => playfield.Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay activeDisplay => stage.DisplayAt(0)!;
        private LyricLineDisplay upcomingDisplay => stage.DisplayAt(1)!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create drawable ruleset with Flashlight", () =>
            {
                var ruleset = new TypeBeatRuleset();

                var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                addLine(beatmap, 0, active_text);
                addLine(beatmap, 1, "secondline");

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());
                // Flashlight plus FLETCHER, i.e. the caret PINNED to the playhead. Since backlog 208
                // an unpinned caret is the default and is handed to the next line the instant the
                // current one is finished, so it is never parked past the end of a line: the
                // early-finish spill below would have nowhere to happen and the window assertions
                // would be reading a different line. Pinning is what keeps the caret where the
                // flashlight window is being measured.
                var mods = new Mod[] { new TypeBeatModFlashlight(), new TypeBeatModFletcher() };

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable, mods);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
            AddStep("allow wrong input (so any key advances the caret)", () => engine.AllowWrongInput = true);
        }

        private static void addLine(Beatmap beatmap, int index, string text)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = 0,
                EndTime = 600000,
                SingEndTime = 300000,
                Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 300000 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = line.StartTime,
                LineIndex = index,
                Line = line,
                Granularity = TimingGranularity.Word,
            });
        }

        [Test]
        public void TestWindowLitAroundCaretAndSlidesWithInput()
        {
            // Caret at index 0: the first few chars are lit, chars well past the window are hidden.
            AddUntilStep("caret-area char lit", () => activeDisplay.CellAlpha(0) > 0.5f);
            AddUntilStep("far char hidden", () => activeDisplay.CellAlpha(radius + 6) < 0.05f);

            // The whole upcoming line is hidden (you cannot read ahead).
            AddUntilStep("upcoming line fully hidden", () =>
                Enumerable.Range(0, upcomingDisplay.CellCount).All(i => upcomingDisplay.CellAlpha(i) < 0.05f));

            // Advance the caret ten characters through the input path.
            AddRepeatStep("type a char", () => InputManager.Key(Key.J), 10);
            AddAssert("caret advanced ten cells", () => engine.CaretIndex == 10);

            // The window slid: the start of the line is now dark, a char that was previously hidden
            // ahead of the window is now lit.
            AddUntilStep("line start now hidden", () => activeDisplay.CellAlpha(0) < 0.05f);
            AddUntilStep("char at new caret lit", () => activeDisplay.CellAlpha(10) > 0.5f);
            AddUntilStep("char two ahead of caret lit", () => activeDisplay.CellAlpha(12) > 0.5f);
        }

        [Test]
        public void TestNextLineStaysDarkMidLineThenLightsOnEarlyFinish()
        {
            // The upcoming line starts fully dark.
            AddUntilStep("upcoming line hidden at start", () =>
                Enumerable.Range(0, upcomingDisplay.CellCount).All(i => upcomingDisplay.CellAlpha(i) < 0.05f));

            // Advance to near the end of the active line (18 of 20 chars) but do NOT finish it. Proximity
            // alone must no longer light the next line: while you are still typing the active line, its
            // right budget is capped at the line end and the next line stays fully dark.
            AddRepeatStep("type a char", () => InputManager.Key(Key.J), 18);
            AddAssert("caret near line end, line not complete", () => engine.CaretIndex == 18 && !engine.IsLineComplete);

            AddUntilStep("next line still fully dark while typing near the end", () =>
                Enumerable.Range(0, upcomingDisplay.CellCount).All(i => upcomingDisplay.CellAlpha(i) < 0.05f));

            // The active line's own start is now dark (the window slid off it); its tail is lit.
            AddUntilStep("active line start hidden", () => activeDisplay.CellAlpha(0) < 0.05f);
            AddAssert("active line last char lit as a hard edge", () => activeDisplay.CellAlpha(activeDisplay.CellCount - 1) > 0.5f);

            // Finish the line (type the last two chars). The instant the line is complete the cap lifts
            // and the leftover budget spills into the next line's head as the early-finish reward.
            AddRepeatStep("finish the line", () => InputManager.Key(Key.J), 2);
            AddAssert("line complete", () => engine.CaretIndex == 20 && engine.IsLineComplete);

            AddUntilStep("next line first char lit on early finish", () => upcomingDisplay.CellAlpha(0) > 0.5f);
            AddUntilStep("next line second char lit on early finish", () => upcomingDisplay.CellAlpha(1) > 0.5f);

            // The far tail of the next line is still beyond the budget, so it stays dark.
            AddUntilStep("next line far char still hidden", () =>
                upcomingDisplay.CellAlpha(upcomingDisplay.CellCount - 1) < 0.05f);
        }
    }
}
