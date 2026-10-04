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
            config.SetValue(TypeBeatRulesetSetting.ApproachBars, false);
            config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
            config.SetValue(TypeBeatRulesetSetting.PaceColourGradient, 0f);
            config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 0f);
            config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, TypeBeatRulesetConfigManager.DEFAULT_SYLLABLE_FADE_IN_DURATION_MS);
            config.SetValue(TypeBeatRulesetSetting.SyllableBrightness, 60f);
            config.SetValue(TypeBeatRulesetSetting.TextPopIn, true);
            config.SetValue(TypeBeatRulesetSetting.CaretSmoothing, TypeBeatRulesetConfigManager.DEFAULT_CARET_SMOOTHING_MS);
            config.SetValue(TypeBeatRulesetSetting.TextPopInAmount, TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT);
            config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
        });

        private void load(string text, bool hardRock = false, bool splitStretch = false, LyricLine? source = null)
        {
            AddStep("create lyrics with pop-in", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, false);
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.None);
                config.SetValue(TypeBeatRulesetSetting.PaceColourGradient, 100f);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 0f);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 100f);
                config.SetValue(TypeBeatRulesetSetting.TextPopInAmount, TypeBeatRulesetConfigManager.DEFAULT_TEXT_POP_IN_AMOUNT);
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, true);
                config.SetValue(TypeBeatRulesetSetting.SyllableBrightness, 50f);
                var ruleset = new TypeBeatRuleset();
                Mod[] mods = hardRock ? new Mod[] { new TypeBeatModHardRock() } : Array.Empty<Mod>();
                var line = source ?? new LyricLine
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
                        new TypeBeatHitObject { StartTime = line.StartTime, LineIndex = 0, Line = line, Granularity = TimingGranularity.Word },
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

        [TestCase(false)]
        [TestCase(true)]
        public void OverdoneCaretAndHighlightsFollowTheEditorSubdivisions(bool hardRock)
        {
            load("", hardRock, source: NonVisual.SubdivisionAlignmentTest.OverdoneLine());
            AddStep("disable caret smoothing for exact boundary checks", () => config.SetValue(TypeBeatRulesetSetting.CaretSmoothing, 0f));
            AddAssert("live timing uses editor cuts", () => engine.AlignSubdivisionTargets);
            at("ver subdivision onset", () => 185008.333333);
            AddUntilStep("caret reaches v at the editor boundary", () => caretAtCell(5));
            at("done subdivision onset", () => 186008.333333);
            AddUntilStep("caret reaches d at the editor boundary", () => caretAtCell(8));
            at("done Great window opens", () => 186008.333333 - engine.Windows.GreatEarly);
            AddUntilStep("d lights at its subdivision window", () => colourIs(8, TypeBeatStyle.SungCharForBrightness(50)));
            AddAssert("held letters have not opened their own windows", () => colourIs(9, TypeBeatStyle.UntypedChar) && display.CellVisualScale(9) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE);
            at("first held o Great window opens", () => 186008.333333 + 4625.0 / 13 - engine.Windows.GreatEarly);
            AddUntilStep("held o lights and finishes pop-in at its corrected window", () => colourIs(9, TypeBeatStyle.SungCharForBrightness(50)) && display.CellVisualScale(9) == 1);
            AddAssert("next held o remains grey", () => colourIs(10, TypeBeatStyle.UntypedChar));
            AddAssert("ne waits through the held vowel", () => colourIs(19, TypeBeatStyle.UntypedChar) && colourIs(20, TypeBeatStyle.UntypedChar)
                && display.CellVisualScale(19) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE && display.CellVisualScale(20) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE);
            at("before the last o window", () => display.Line.Cells[18].TargetTime - engine.Windows.GreatEarly - 1);
            AddUntilStep("ne is still grey just before the last o opens", () => colourIs(19, TypeBeatStyle.UntypedChar) && colourIs(20, TypeBeatStyle.UntypedChar));
            at("last held o window opens", () => display.Line.Cells[18].TargetTime - engine.Windows.GreatEarly);
            AddUntilStep("final o lights", () => colourIs(18, TypeBeatStyle.SungCharForBrightness(50)));
            if (!hardRock)
            {
                AddAssert("ne lights together with the final o", () => colourIs(19, TypeBeatStyle.SungCharForBrightness(50)) && colourIs(20, TypeBeatStyle.SungCharForBrightness(50))
                    && display.CellVisualScale(19) == 1 && display.CellVisualScale(20) == 1);
                at("seek back before the suffix approach", () => display.Line.Cells[18].TargetTime - engine.Windows.GreatEarly - LyricLineDisplay.TEXT_POP_IN_DURATION_MS - 1);
                AddUntilStep("suffix returns to small grey text", () => colourIs(19, TypeBeatStyle.UntypedChar) && colourIs(20, TypeBeatStyle.UntypedChar)
                    && display.CellVisualScale(19) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE && display.CellVisualScale(20) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE);
                at("suffix syllable tail", () => 190633.333333 + engine.Windows.GreatLate);
                AddUntilStep("suffix stays lit through the syllable window", () => colourIs(19, TypeBeatStyle.SungCharForBrightness(50)) && colourIs(20, TypeBeatStyle.SungCharForBrightness(50)));
                at("suffix window closes", () => 190633.333333 + engine.Windows.GreatLate + LyricLineDisplay.INDICATOR_RELEASE_DURATION_MS);
                AddUntilStep("suffix highlight closes", () => colourIs(19, TypeBeatStyle.UntypedChar) && colourIs(20, TypeBeatStyle.UntypedChar));
            }
            else
            {
                AddAssert("HR leaves ne on their own character windows", () => colourIs(19, TypeBeatStyle.UntypedChar) && colourIs(20, TypeBeatStyle.UntypedChar));
                at("n character window opens", () => display.Line.Cells[19].TargetTime - engine.Windows.GreatEarly);
                AddUntilStep("n lights on its own under HR", () => colourIs(19, TypeBeatStyle.SungCharForBrightness(50)) && colourIs(20, TypeBeatStyle.UntypedChar));
                at("e character window opens", () => display.Line.Cells[20].TargetTime - engine.Windows.GreatEarly);
                AddUntilStep("e lights on its own under HR", () => colourIs(20, TypeBeatStyle.SungCharForBrightness(50)));
            }
        }

        [TestCase(false, "cat", 0)]
        [TestCase(false, "meeeeeee", 1)]
        [TestCase(true, "hello", 1)]
        public void SyllableFadeInReachesSelectedBrightnessAtGreat(bool hardRock, string text, int index)
        {
            load(text, hardRock);
            AddStep("enable syllable fade-in", () => config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn));
            at("100ms approach opens", () => display.Line.Cells[index].TargetTime - engine.Windows.GreatEarly - 100);
            AddUntilStep("approach starts at untyped grey", () => colourIs(index, TypeBeatStyle.UntypedChar));
            at("halfway through 100ms approach", () => display.Line.Cells[index].TargetTime - engine.Windows.GreatEarly - 50);
            AddUntilStep("exponential midpoint brightness", () => colourIs(index, TypeBeatStyle.SungCharForBrightness(50 * 31f / 1023f)));
            if (text == "cat")
                AddAssert("syllable fades together", () => Enumerable.Range(0, 3).All(i => colourIs(i, TypeBeatStyle.SungCharForBrightness(50 * 31f / 1023f))));
            else
                AddAssert("next character remains grey", () => colourIs(index + 1, TypeBeatStyle.UntypedChar));
            AddStep("disable fade during approach", () => config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.None));
            AddUntilStep("disabled keeps original Great-only highlight", () => colourIs(index, TypeBeatStyle.UntypedChar));
            AddStep("enable fade again", () => config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn));
            AddUntilStep("current approach immediately restored", () => colourIs(index, TypeBeatStyle.SungCharForBrightness(50 * 31f / 1023f)));
            AddStep("increase sung brightness", () => config.SetValue(TypeBeatRulesetSetting.SyllableBrightness, 80f));
            AddUntilStep("brightness slider preserves current fade fraction", () => colourIs(index, TypeBeatStyle.SungCharForBrightness(80 * 31f / 1023f)));
            at("Great window opens", () => display.Line.Cells[index].TargetTime - engine.Windows.GreatEarly);
            AddUntilStep("full sung brightness and pop-in finish together", () => colourIs(index, TypeBeatStyle.SungCharForBrightness(80)) && display.CellVisualScale(index) == 1);
            at("seek before approach", () => display.Line.Cells[index].TargetTime - engine.Windows.GreatEarly - 100 - 1);
            AddUntilStep("seek restores untyped grey", () => colourIs(index, TypeBeatStyle.UntypedChar));
            at("100ms midpoint with scaled Great window", () =>
            {
                engine.WindowScale = 2;
                return display.Line.Cells[index].TargetTime - engine.Windows.GreatEarly - 50;
            });
            AddUntilStep("fade uses scaled judgement windows", () => colourIs(index, TypeBeatStyle.SungCharForBrightness(80 * 31f / 1023f)));
        }

        [Test]
        public void OverdoneSuffixFadesWithTheLastHeldCharacterAndPreservesFeedback()
        {
            load("", source: NonVisual.SubdivisionAlignmentTest.OverdoneLine());
            AddStep("enable fade without pop-in", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, false);
            });
            at("before the last o approach", () => display.Line.Cells[18].TargetTime - engine.Windows.GreatEarly - 100 - 1);
            AddUntilStep("suffix remains grey", () => colourIs(19, TypeBeatStyle.UntypedChar) && colourIs(20, TypeBeatStyle.UntypedChar));
            at("midway through final o approach", () => display.Line.Cells[18].TargetTime - engine.Windows.GreatEarly - 50);
            AddUntilStep("suffix and final o fade together", () => Enumerable.Range(18, 3).All(i => colourIs(i, TypeBeatStyle.SungCharForBrightness(50 * 31f / 1023f))));
            AddAssert("fade works independently of size animation", () => display.CellVisualScale(19) == 1 && display.CellVisualScale(20) == 1);
            at("final o Great window opens", () => display.Line.Cells[18].TargetTime - engine.Windows.GreatEarly);
            AddUntilStep("suffix reaches full sung brightness", () => Enumerable.Range(18, 3).All(i => colourIs(i, TypeBeatStyle.SungCharForBrightness(50))));
            at("back to first character Great window", () => display.Line.Cells[0].TargetTime - engine.Windows.GreatEarly);
            AddUntilStep("first character lights", () => colourIs(0, TypeBeatStyle.SungCharForBrightness(50)));
            AddStep("type first character", () => engine.ProcessKey('i', songClock.CurrentTime));
            AddUntilStep("character is judged", () => display.Line.Cells[0].State == CellState.Correct);
            var judgedColour = default(ColourInfo);
            AddStep("capture judgement colour and disable fade", () =>
            {
                judgedColour = display.CellColour(0);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, false);
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.None);
            });
            AddAssert("judgement colour survives fade toggle", () => display.CellColour(0).Equals(judgedColour));
        }

        [TestCase(0f)]
        [TestCase(50f)]
        [TestCase(100f)]
        public void FadeCompletionAndDurationApplyLive(float endPercent)
        {
            load("cat");
            AddStep("configure 250ms approach", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, endPercent);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 250f);
            });
            double completion() => 1000 - engine.Windows.GreatEarly * (1 - endPercent / 100.0);
            at("approach starts", () => completion() - 250);
            AddUntilStep("starts at grey", () => colourIs(0, TypeBeatStyle.UntypedChar));
            at("approach midpoint", () => completion() - 125);
            AddUntilStep("exponential midpoint brightness", () => colourIs(0, TypeBeatStyle.SungCharForBrightness(50 * 31f / 1023f)));
            AddStep("shorten duration live", () => config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 125f));
            AddUntilStep("shorter approach now starts here", () => colourIs(0, TypeBeatStyle.UntypedChar));
            at("one millisecond before completion", () => completion() - 1);
            // The last fraction can quantize to the same 8-bit colour as full brightness.
            // Pin the display's actual progress as well as the colour it draws.
            AddUntilStep("still approaching at 124 of 125ms", () => Math.Abs(display.CellSungHighlightAmount(0) - (Math.Pow(2, 10 * 124.0 / 125) - 1) / 1023) < 0.00001
                && colourIs(0, TypeBeatStyle.SungCharForBrightness((float)(50 * (Math.Pow(2, 10 * 124.0 / 125) - 1) / 1023))));
            at("configured completion", completion);
            AddUntilStep("selected brightness reached exactly at completion", () => display.CellSungHighlightAmount(0) == 1
                && colourIs(0, TypeBeatStyle.SungCharForBrightness(50)));
            AddStep("make approach instantaneous", () => config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 0f));
            at("just before instantaneous completion", () => completion() - 1);
            AddUntilStep("zero duration waits", () => colourIs(0, TypeBeatStyle.UntypedChar));
            at("instantaneous completion", completion);
            AddUntilStep("zero duration lights exactly at completion", () => colourIs(0, TypeBeatStyle.SungCharForBrightness(50)));
            at("past late window", () => 5000 + engine.Windows.GreatLate + LyricLineDisplay.INDICATOR_RELEASE_DURATION_MS);
            AddUntilStep("late boundary clears highlight", () => colourIs(0, TypeBeatStyle.UntypedChar));
        }

        [TestCase(false, "cat", 0)]
        [TestCase(false, "meeeeeee", 1)]
        [TestCase(true, "hello", 1)]
        public void ColourFillClipsGlyphsFromBelowAndPreservesFeedback(bool hardRock, string text, int index)
        {
            load(text, hardRock);
            AddStep("enable glyph fill", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.BottomToTopFill);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 200f);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 100f);
            });
            at("fill midpoint", () => display.Line.Cells[index].TargetTime - 100);
            AddUntilStep("glyph is half filled", () => Math.Abs(display.CellColourFillFraction(index) - 0.5f) < 0.0001);
            AddAssert("base remains grey", () => colourIs(index, TypeBeatStyle.UntypedChar));
            AddAssert("mask covers the lower glyph half", () =>
            {
                var glyph = display.CellScreenQuad(index);
                var clip = display.CellColourFillClipQuad(index);
                return Math.Abs(clip.BottomLeft.Y - glyph.BottomLeft.Y) < 0.01
                       && Math.Abs(clip.Height - glyph.Height * 0.5f) < 0.01
                       && Math.Abs(clip.Width - glyph.Width) < 0.01;
            });
            if (text == "cat")
                AddAssert("syllable fills together", () => Enumerable.Range(0, 3).All(i => Math.Abs(display.CellColourFillFraction(i) - 0.5f) < 0.0001));
            else
                AddAssert("next character waits for its own target", () => display.CellColourFillFraction(index + 1) == 0 && colourIs(index + 1, TypeBeatStyle.UntypedChar));
            AddStep("adjust fill brightness live", () => config.SetValue(TypeBeatRulesetSetting.SyllableBrightness, 80f));
            AddUntilStep("filled portion follows brightness", () => display.CellColourFillColour(index).Equals((ColourInfo)TypeBeatStyle.SungCharForBrightness(80))
                && Math.Abs(display.CellColourFillFraction(index) - 0.5f) < 0.0001 && colourIs(index, TypeBeatStyle.UntypedChar));
            AddStep("restore brightness", () => config.SetValue(TypeBeatRulesetSetting.SyllableBrightness, 50f));
            if (index == 0)
            {
                AddStep("type wrong character during fill", () => engine.ProcessKey('x', songClock.CurrentTime));
                AddUntilStep("error feedback replaces fill", () => display.Line.Cells[0].State == CellState.Wrong
                    && display.CellColourFillAlpha(0) == 0 && colourIs(0, TypeBeatStyle.ErrorChar));
                AddStep("erase wrong character", () => engine.ProcessBackspace());
                AddUntilStep("erasing restores current fill", () => Math.Abs(display.CellColourFillFraction(0) - 0.5f) < 0.0001 && colourIs(0, TypeBeatStyle.UntypedChar));
            }
            AddStep("switch to fade during approach", () => config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn));
            AddUntilStep("fade resumes at the same fraction", () => display.CellColourFillAlpha(index) == 0 && colourIs(index, TypeBeatStyle.SungCharForBrightness(50 * 31f / 1023f)));
            AddStep("switch back to fill", () => config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.BottomToTopFill));
            AddUntilStep("fill resumes at the same fraction", () => Math.Abs(display.CellColourFillFraction(index) - 0.5f) < 0.0001 && colourIs(index, TypeBeatStyle.UntypedChar));
            AddStep("hide with flashlight", () => display.HideForFlashlight());
            AddUntilStep("glyph fill follows flashlight hiding", () => display.CellColourFillAlpha(index) == 0);
            AddStep("reveal with flashlight", () => display.SetFlashlightWindow(new LineWindow(0, display.Line.Cells.Count - 1, false, false), true));
            // Flashlight's 90ms alpha animation runs on the song clock, which is otherwise frozen.
            // Advance within the remaining 100ms of the fill so both glyph layers can fade back in.
            at("finish flashlight reveal", () => songClock.CurrentTime + 90);
            AddUntilStep("glyph fill returns with cell", () => display.CellColourFillAlpha(index) > 0.99f);
            at("seek before approach", () => display.Line.Cells[index].TargetTime - 201);
            AddUntilStep("rewind clears fill", () => display.CellColourFillAlpha(index) == 0 && colourIs(index, TypeBeatStyle.UntypedChar));
            at("target time", () => display.Line.Cells[index].TargetTime);
            AddUntilStep("full glyph reaches selected brightness", () => display.CellColourFillAlpha(index) == 0 && colourIs(index, TypeBeatStyle.SungCharForBrightness(50)));
            if (index == 0)
            {
                AddStep("type correct first character", () => engine.ProcessKey(text[0], songClock.CurrentTime));
                AddUntilStep("judgement feedback preserved", () => display.Line.Cells[0].State == CellState.Correct && display.CellColourFillAlpha(0) == 0 && colourIs(0, TypeBeatStyle.TypedChar));
                at("seek back to approach after judgement", () => display.Line.Cells[0].TargetTime - 100);
                AddUntilStep("judged cell keeps its feedback during approach", () => display.CellColourFillAlpha(0) == 0 && colourIs(0, TypeBeatStyle.TypedChar));
            }
        }

        private bool caretAtCell(int index) => Vector2.Distance(stage.SungCaretPosition,
            display.ToSpaceOfOtherDrawable(display.SungPositionPoint(index), stage)) < 0.01;

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
                display.Line.Cells[index].TargetTime + engine.Windows.GreatLate + LyricLineDisplay.INDICATOR_RELEASE_DURATION_MS));
            AddUntilStep("previous point window has closed", () => colourIs(index, TypeBeatStyle.UntypedChar));
            AddStep("disable visual effect", () => config.SetValue(TypeBeatRulesetSetting.TextPopIn, false));
            AddAssert("per-character light persists independently", () => colourIs(index + 1, TypeBeatStyle.SungCharForBrightness(50)) && colourIs(index + 2, TypeBeatStyle.UntypedChar));
        }
    }
}
