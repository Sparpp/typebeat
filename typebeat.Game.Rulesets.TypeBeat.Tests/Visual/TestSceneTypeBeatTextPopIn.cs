// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Colour;
using osu.Framework.Testing;
using osu.Framework.Timing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatTextPopIn : OsuTestScene
    {
        private DrawableTypeBeatRuleset drawableRuleset = null!;
        private ManualClock songClock = null!;
        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display => stage.DisplayAt(0)!;
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;
        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        [TearDownSteps]
        public void RestoreSettings() => AddStep("restore pop-in defaults", () =>
        {
            config.SetValue(TypeBeatRulesetSetting.TextPopIn, false);
            config.SetValue(TypeBeatRulesetSetting.TextPopInAmount, TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT);
        });

        private void load(string text, bool hardRock = false, bool splitStretch = false)
        {
            AddStep("create lyrics with pop-in", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.TextPopInAmount, TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT);
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, true);
                config.SetValue(TypeBeatRulesetSetting.SyllableBrightness, 50f);
                var ruleset = new TypeBeatRuleset();
                Mod[] mods = hardRock ? new Mod[] { new TypeBeatModHardRock() } : Array.Empty<Mod>();
                var line = new LyricLine
                {
                    RawText = text, StartTime = 0, EndTime = 6000, SingEndTime = 5000,
                    Units = new[]
                    {
                        new TimedUnit
                        {
                            Text = text, StartTime = 1000, EndTime = 5000,
                            SyllableBoundaries = splitStretch ? new[] { 2000.0 } : Array.Empty<double>(),
                            SyllableSplits = splitStretch ? new[] { 3 } : Array.Empty<int>(),
                        },
                    },
                };
                var beatmap = new Beatmap
                {
                    HitObjects = new List<Rulesets.Objects.HitObject>
                    {
                        new TypeBeatHitObject { StartTime = 0, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;
                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, mods);
                songClock = new ManualClock { CurrentTime = 0 };
                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(songClock),
                    Child = drawableRuleset = new DrawableTypeBeatRuleset(ruleset, playable, mods),
                };
            });
            AddUntilStep("lyrics loaded", () => drawableRuleset.IsLoaded && stage.IsLoaded && display.IsLoaded);
        }

        private bool colourIs(int index, Color4 colour) => display.CellColour(index).Equals((ColourInfo)colour);
        private void at(string text, Func<double> time) => AddStep(text, () => songClock.CurrentTime = time());

        [Test]
        public void SyllablePopFinishesAtGreatWindowAndKeepsLayout()
        {
            float width = 0;
            Vector2 position = Vector2.Zero;
            load("cat");
            at("before approach", () => 1000 - engine.Windows.GreatEarly - LyricLineDisplay.TEXT_POP_IN_DURATION_MS);
            AddUntilStep("slightly smaller", () => Math.Abs(display.CellVisualScale(0) - 0.95f) < 0.00001);
            AddAssert("not highlighted yet", () => colourIs(0, TypeBeatStyle.UntypedChar));
            AddStep("record stable layout", () => { width = display.FullOnScreenWidth; position = display.CellScreenPosition(0); });
            at("halfway through approach", () => 1000 - engine.Windows.GreatEarly - 70);
            AddUntilStep("subtle eased growth", () => display.CellVisualScale(0) > 0.99f && display.CellVisualScale(0) < 1f);
            at("Great window opens", () => 1000 - engine.Windows.GreatEarly);
            AddUntilStep("full size exactly at opening", () => display.CellVisualScale(0) == 1f && colourIs(0, TypeBeatStyle.SungCharForBrightness(50)));
            AddAssert("normal syllable lights together", () => Enumerable.Range(0, 3).All(i => colourIs(i, TypeBeatStyle.SungCharForBrightness(50)) && display.CellVisualScale(i) == 1));
            AddAssert("layout stays fixed", () => Math.Abs(display.FullOnScreenWidth - width) < 0.01 && Vector2.Distance(display.CellScreenPosition(0), position) < 0.01);
            at("seek backwards", () => 1000 - engine.Windows.GreatEarly - 200);
            AddUntilStep("seek restores smaller grey text", () => display.CellVisualScale(0) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE && colourIs(0, TypeBeatStyle.UntypedChar));
            AddStep("increase amount during play", () => config.SetValue(TypeBeatRulesetSetting.TextPopInAmount, 12f));
            AddUntilStep("larger amount applies immediately", () => display.CellVisualScale(0) == 0.88f);
            AddAssert("amount keeps layout fixed", () => Math.Abs(display.FullOnScreenWidth - width) < 0.01 && Vector2.Distance(display.CellScreenPosition(0), position) < 0.01);
            at("adjusted Great window opens", () => 1000 - engine.Windows.GreatEarly);
            AddUntilStep("adjusted amount still finishes at opening", () => display.CellVisualScale(0) == 1f && colourIs(0, TypeBeatStyle.SungCharForBrightness(50)));
            at("seek before adjusted approach", () => 1000 - engine.Windows.GreatEarly - 200);
            AddUntilStep("adjusted amount survives seeking", () => display.CellVisualScale(0) == 0.88f && colourIs(0, TypeBeatStyle.UntypedChar));
            AddStep("set amount to zero", () => config.SetValue(TypeBeatRulesetSetting.TextPopInAmount, 0f));
            AddUntilStep("zero amount keeps full size ahead of window", () => display.CellVisualScale(0) == 1f && colourIs(0, TypeBeatStyle.UntypedChar));
            AddStep("disable during play", () => config.SetValue(TypeBeatRulesetSetting.TextPopIn, false));
            AddUntilStep("disabled restores full size", () => Enumerable.Range(0, 3).All(i => display.CellVisualScale(i) == 1));
        }

        [Test]
        public void PopInPreservesJudgementFeedback()
        {
            load("cat");
            at("first target", () => 1000);
            AddUntilStep("first character available", () => display.CellVisualScale(0) == 1 && engine.ActiveLineIndex == 0);
            AddStep("type a Great", () => engine.ProcessKey('c', songClock.CurrentTime));
            AddUntilStep("judgement pulse preserved", () => display.CellVisualScale(0) > 1);
            AddStep("disable pop-in during pulse", () => config.SetValue(TypeBeatRulesetSetting.TextPopIn, false));
            AddAssert("pulse still present", () => display.CellVisualScale(0) > 1);
            at("feedback settles", () => 1140);
            AddUntilStep("normal typed size restored", () => display.CellVisualScale(0) == 1);
            AddAssert("judgement stays Great", () => display.Line.Cells[0].State == CellState.Correct);
        }

        [TestCase(false, "meeeeeee", false)]
        [TestCase(false, "heyyyyy", true)]
        [TestCase(true, "hello", false)]
        public void CharacterTimingUsesEachCharactersGreatWindow(bool hardRock, string text, bool splitStretch)
        {
            load(text, hardRock, splitStretch);
            int index = splitStretch ? 3 : 0;
            AddAssert("live timing mode selected", () => engine.HardRockFromMod == hardRock && (!splitStretch || display.Line.IsCharTimedStretch(index)));
            at("character window opens", () => display.Line.Cells[index].TargetTime - engine.Windows.GreatEarly);
            AddUntilStep("current character full size and lit", () => display.CellVisualScale(index) == 1 && colourIs(index, TypeBeatStyle.SungCharForBrightness(50)));
            AddAssert("next character still approaching", () => display.CellVisualScale(index + 1) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE && colourIs(index + 1, TypeBeatStyle.UntypedChar));
            at("next character window", () => display.Line.Cells[index + 1].TargetTime - engine.Windows.GreatEarly);
            AddUntilStep("next character lights separately", () => display.CellVisualScale(index + 1) == 1 && colourIs(index + 1, TypeBeatStyle.SungCharForBrightness(50)));
            at("past previous character's Great window", () => Math.Max(display.Line.Cells[index + 1].TargetTime,
                display.Line.Cells[index].TargetTime + engine.Windows.GreatLate + 1));
            AddUntilStep("previous point window has closed", () => colourIs(index, TypeBeatStyle.UntypedChar));
            AddStep("disable visual effect", () => config.SetValue(TypeBeatRulesetSetting.TextPopIn, false));
            AddAssert("per-character light persists independently", () => colourIs(index + 1, TypeBeatStyle.SungCharForBrightness(50)) && colourIs(index + 2, TypeBeatStyle.UntypedChar));
        }
    }
}
