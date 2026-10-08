// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Colour;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatTextPopIn
    {
        [TestCase(JudgementIndicatorMode.BottomToTopFill)]
        public void DirectionalMasksFollowGlyphBoundsAndSeek(JudgementIndicatorMode mode)
        {
            float width = 0;
            Vector2 position = Vector2.Zero;
            load("cat");
            AddStep("configure indicator", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, mode);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 400f);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 100f);
            });
            at("halfway through lead-in", () => 800);
            AddUntilStep("first glyph is half filled", () => Math.Abs(display.CellColourFillFraction(0) - 0.5f) < 0.0001);
            AddAssert("later letters wait for their own targets", () => display.CellColourFillFraction(1) == 0 && display.CellColourFillFraction(2) == 0);
            AddAssert("fill uses the selected direction", () =>
            {
                var glyph = display.CellScreenQuad(0);
                var mask = display.CellColourFillClipQuad(0);
                const double epsilon = 0.01;
                return mode switch
                {
                    JudgementIndicatorMode.BottomToTopFill => Math.Abs(mask.BottomLeft.Y - glyph.BottomLeft.Y) < epsilon
                        && Math.Abs(mask.Height - glyph.Height / 2) < epsilon && Math.Abs(mask.Width - glyph.Width) < epsilon,
                    _ => false,
                };
            });
            AddStep("record layout", () => { width = display.FullOnScreenWidth; position = display.CellScreenPosition(0); });
            at("alignment", () => 1000);
            AddUntilStep("full colour and size finish at alignment", () => colourIs(0, TypeBeatStyle.SungCharForBrightness(50)) && display.CellVisualScale(0) == 1);
            AddAssert("layout has not grown", () => Math.Abs(display.FullOnScreenWidth - width) < 0.01 && Vector2.Distance(display.CellScreenPosition(0), position) < 0.01);
            at("seek to lead-in start", () => 600);
            AddUntilStep("seeking clears masks and restores smaller text", () => display.CellColourFillAlpha(0) == 0 && display.CellVisualScale(0) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE);
            at("return to midpoint", () => 800);
            AddUntilStep("same exact fill returns", () => Math.Abs(display.CellColourFillFraction(0) - 0.5f) < 0.0001);
            AddStep("switch to fade and enable approach bars", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
            });
            AddUntilStep("obsolete mask is hidden", () => display.CellColourFillAlpha(0) == 0 && display.CellSungHighlightAmount(0) > 0 && display.FloatingBarAlpha(0) > 0);
        }

        private static LyricLine twoSyllableHello => new LyricLine
        {
            RawText = "hello", StartTime = 0, EndTime = 6000, SingEndTime = 3000,
            Units = new[]
            {
                new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 3000, SyllableBoundaries = new[] { 2000.0 }, SyllableSplits = new[] { 3 } },
            },
        };

        private const float per_character_lead_in = 1000f;

        private float expectedProgress(double target) =>
            LyricLineDisplay.SyllableFadeInProgress(songClock.CurrentTime, target, per_character_lead_in);

        private void loadPerCharacterProbe(bool hardRock, JudgementIndicatorMode mode)
        {
            load("hello", hardRock, source: twoSyllableHello);
            AddStep("configure indicator", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, mode);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, per_character_lead_in);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 100f);
            });
            AddAssert("timing mode and syllable layout", () => engine.HardRockFromMod == hardRock && engine.SyllableTiming == !hardRock
                && Enumerable.Range(0, 3).All(i => display.Line.SyllableIndexOf(i) == 0 && !display.Line.IsCharTimedStretch(i))
                && display.Line.SyllableIndexOf(3) == 1
                && display.Line.Cells[0].TargetTime < display.Line.Cells[1].TargetTime && display.Line.Cells[1].TargetTime < display.Line.Cells[2].TargetTime);
            at("between the first two letter targets", () => (display.Line.Cells[0].TargetTime + display.Line.Cells[1].TargetTime) / 2);
        }

        [Test]
        public void BottomToTopFillRisesPerCharacterUnderSyllableTiming()
        {
            loadPerCharacterProbe(false, JudgementIndicatorMode.BottomToTopFill);
            AddUntilStep("each letter is further along than the next", () => display.CellSungHighlightAmount(0) > display.CellSungHighlightAmount(1)
                && display.CellSungHighlightAmount(1) > display.CellSungHighlightAmount(2) && display.CellSungHighlightAmount(2) > 0);
            AddAssert("each letter fills on its own target", () => Enumerable.Range(0, 3).All(i =>
                Math.Abs(display.CellSungHighlightAmount(i) - expectedProgress(display.Line.Cells[i].TargetTime)) < 0.0001));
            AddAssert("partially filled letters are masked per character", () => display.CellColourFillFraction(0) == 0
                && Math.Abs(display.CellColourFillFraction(1) - display.CellSungHighlightAmount(1)) < 0.0001
                && Math.Abs(display.CellColourFillFraction(2) - display.CellSungHighlightAmount(2)) < 0.0001);
            at("after the last letter's target, inside the syllable", () => display.Line.Cells[2].TargetTime + 1);
            AddUntilStep("the whole syllable is filled", () => Enumerable.Range(0, 3).All(i => display.CellSungHighlightAmount(i) == 1));
            at("halfway through the syllable's release", () => display.Line.Syllables[0].EndTime + engine.Windows.GreatLate + LyricLineDisplay.INDICATOR_RELEASE_DURATION_MS / 2);
            AddUntilStep("release stays on the syllable end", () => Enumerable.Range(0, 3).All(i => Math.Abs(display.CellSungHighlightAmount(i) - 0.5f) < 0.0001));
        }

        [Test]
        public void BottomToTopFillUnderHardRockKeepsCharacterTargets()
        {
            loadPerCharacterProbe(true, JudgementIndicatorMode.BottomToTopFill);
            // Under Hard Rock each letter is its own point window, so the first one is already releasing.
            AddUntilStep("each letter fills and releases on its own target", () => Enumerable.Range(0, 5).All(i =>
                Math.Abs(display.CellSungHighlightAmount(i) - expectedProgress(display.Line.Cells[i].TargetTime)
                    * LyricLineDisplay.IndicatorRelease(songClock.CurrentTime, display.Line.Cells[i].TargetTime + engine.Windows.GreatLate)) < 0.0001)
                && display.CellSungHighlightAmount(0) < 1 && display.CellSungHighlightAmount(1) > display.CellSungHighlightAmount(2));
        }

        [Test]
        public void FadeInUnderSyllableTimingStillLightsTheSyllableTogether()
        {
            loadPerCharacterProbe(false, JudgementIndicatorMode.FadeIn);
            AddUntilStep("every letter shares the syllable's progress", () => Enumerable.Range(0, 3).All(i =>
                Math.Abs(display.CellSungHighlightAmount(i) - LyricLineDisplay.ExponentialIndicatorProgress(expectedProgress(display.Line.Syllables[0].StartTime))) < 0.0001));
        }

        [TestCase(JudgementIndicatorMode.None)]
        [TestCase(JudgementIndicatorMode.FadeIn)]
        [TestCase(JudgementIndicatorMode.BottomToTopFill)]
        public void HighlightReleasesAfterGreatAndLeavesJudgementFeedback(JudgementIndicatorMode mode)
        {
            load("cat");
            AddStep("choose indicator", () => config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, mode));
            at("first target", () => 1000);
            AddUntilStep("available at selected brightness", () => colourIs(0, TypeBeatStyle.SungCharForBrightness(50)));
            AddStep("type correct and wrong characters", () =>
            {
                engine.ProcessKey('c', songClock.CurrentTime);
                engine.ProcessKey('x', songClock.CurrentTime);
            });
            AddUntilStep("feedback established", () => display.Line.Cells[0].State == CellState.Correct && display.Line.Cells[1].State == CellState.Wrong);
            at("Great window end", () => 5000 + engine.Windows.GreatLate);
            AddUntilStep("untyped character stays bright through Great", () => colourIs(2, TypeBeatStyle.SungCharForBrightness(50)));
            at("halfway through release", () => 5000 + engine.Windows.GreatLate + 50);
            AddUntilStep("untyped highlight halves in 50ms", () => Math.Abs(display.CellSungHighlightAmount(2) - 0.5f) < 0.0001 && colourIs(2, TypeBeatStyle.SungCharForBrightness(25)));
            AddAssert("typed and error colours survive release", () => colourIs(0, TypeBeatStyle.TypedChar) && colourIs(1, TypeBeatStyle.ErrorChar));
            at("release complete", () => 5000 + engine.Windows.GreatLate + 100);
            AddUntilStep("untyped highlight ends in 100ms", () => display.CellSungHighlightAmount(2) == 0 && colourIs(2, TypeBeatStyle.UntypedChar));
            AddAssert("judged feedback remains", () => colourIs(0, TypeBeatStyle.TypedChar) && colourIs(1, TypeBeatStyle.ErrorChar));
            at("seek back inside Great", () => 5000 + engine.Windows.GreatLate);
            AddUntilStep("seek restores exact highlight", () => display.CellSungHighlightAmount(2) == 1 && colourIs(2, TypeBeatStyle.SungCharForBrightness(50)));
        }

        [TestCase(false, 0f)]
        [TestCase(false, 50f)]
        [TestCase(false, 100f)]
        [TestCase(true, 0f)]
        [TestCase(true, 50f)]
        [TestCase(true, 100f)]
        public void ApproachBarsRiseHoldReleaseAndUsePaceColours(bool hardRock, float alignment)
        {
            float lineWidth = 0;
            Vector2 glyphPosition = Vector2.Zero;
            load("cat dog", hardRock, source: new LyricLine
            {
                RawText = "cat dog", StartTime = 0, EndTime = 6000, SingEndTime = 4000,
                Units = new[]
                {
                    new TimedUnit { Text = "cat", StartTime = 1000, EndTime = 1800 },
                    new TimedUnit { Text = "dog", StartTime = 2600, EndTime = 4000 },
                },
            });
            AddStep("configure floating bars and coloured bands", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementIndicator, JudgementIndicatorMode.FadeIn);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 400f);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, alignment);
                var bands = new[] { new PaceBand(0, 4, Color4.Red), new PaceBand(4, 7, Color4.Green) };
                display.SetPaceBands(bands, bands);
                display.SetPaceColourGradient(100);
                lineWidth = display.FullOnScreenWidth;
                glyphPosition = display.CellScreenPosition(0);
            });
            at("before bar appears", () => 599);
            AddUntilStep("bar is hidden before lead-in", () => display.FloatingBarAlpha(0) == 0);
            at("bar lead-in starts", () => 600);
            AddUntilStep("bar starts transparent", () => display.FloatingBarAlpha(0) == 0);
            at("halfway through bar fade-in", () => 650);
            AddUntilStep("bar fades in while rising", () => Math.Abs(display.FloatingBarAlpha(0) - 0.5f) < 0.0001
                && display.FloatingBarScreenQuad(0).TopLeft.Y > display.RailScreenQuad.TopLeft.Y);
            at("bar fade-in finishes", () => 700);
            AddUntilStep("bar is fully visible after 100ms", () => display.FloatingBarAlpha(0) == 1);
            at("bar lead-in midpoint", () => 800);
            AddUntilStep("bar is below its rail", () => display.FloatingBarAlpha(0) > 0 && display.FloatingBarScreenQuad(0).TopLeft.Y > display.RailScreenQuad.TopLeft.Y);
            AddAssert("fade indicator remains active alongside bars", () => display.CellSungHighlightAmount(0) > 0);
            at("text alignment", () => 1000 - engine.Windows.GreatEarly * (1 - alignment / 100));
            AddUntilStep("text finishes while the bar follows its own target", () => colourIs(0, TypeBeatStyle.SungCharForBrightness(50)));
            if (alignment < 100)
                AddAssert("bar has not arrived at the text alignment", () => display.FloatingBarScreenQuad(0).TopLeft.Y > display.RailScreenQuad.TopLeft.Y);
            AddAssert("bar uses selected pace hue", () => display.FloatingBarColour(0).Equals((ColourInfo)Color4.Red));
            AddAssert("floating bars suppress gradients", () => display.PaceBlendWidth(0) == 0);
            if (hardRock)
                AddAssert("letter bar spans its own cell", () => Math.Abs(display.FloatingBarScreenQuad(0).Width - display.CellScreenQuad(0).Width / display.CellVisualScale(0)) < 0.01);
            else
                AddAssert("one bar spans the syllable", () => display.FloatingBarAlpha(1) == 0 && display.FloatingBarAlpha(2) == 0
                    && display.FloatingBarScreenQuad(0).Width > display.CellScreenQuad(0).Width * 2);
            at("bar alignment", () => 1000);
            AddUntilStep("bar reaches the speed underline", () => Math.Abs(display.FloatingBarScreenQuad(0).TopLeft.Y - display.RailScreenQuad.TopLeft.Y) < 0.01);
            at("bar holds inside Great", () => 1050);
            AddUntilStep("bar stops at the underline", () => Math.Abs(display.FloatingBarScreenQuad(0).TopLeft.Y - display.RailScreenQuad.TopLeft.Y) < 0.01);
            AddAssert("animation does not move glyphs or enlarge layout", () => Math.Abs(display.FullOnScreenWidth - lineWidth) < 0.01 && Vector2.Distance(display.CellScreenPosition(0), glyphPosition) < 0.01);
            AddStep("turn pace colours off", () => display.SetPaceColours(null));
            AddUntilStep("floating bar becomes neutral", () => display.FloatingBarColour(0).Equals((ColourInfo)UnderlinePace.NeutralColour));
            at("bar release midpoint", () => (hardRock ? display.Line.Cells[0].TargetTime : 1800) + engine.Windows.GreatLate + 50);
            AddUntilStep("bar fades after Great closes", () => Math.Abs(display.FloatingBarAlpha(0) - 0.5f) < 0.0001);
            at("bar release complete", () => (hardRock ? display.Line.Cells[0].TargetTime : 1800) + engine.Windows.GreatLate + 100);
            AddUntilStep("bar disappears in 100ms", () => display.FloatingBarAlpha(0) == 0);
            at("seek back into bar fade-in", () => 650);
            AddUntilStep("seeking reconstructs bar opacity", () => Math.Abs(display.FloatingBarAlpha(0) - 0.5f) < 0.0001);
            at("seek to midpoint again", () => 800);
            AddUntilStep("bar position reconstructs on seek", () => display.FloatingBarAlpha(0) > 0 && display.FloatingBarScreenQuad(0).TopLeft.Y > display.RailScreenQuad.TopLeft.Y);
            AddStep("hide with Recite", () => display.SetReciteEnabled(true));
            at("let Recite hide settle", () => 890);
            AddUntilStep("untyped bar cannot bypass Recite", () => display.FloatingBarAlpha(0) == 0);
            AddStep("restore Recite", () => display.SetReciteEnabled(false));
            at("let hiding fade settle", () => 980);
            AddUntilStep("bar returns with readable text", () => display.FloatingBarAlpha(0) > 0.99);
            AddStep("hide with flashlight", () => display.HideForFlashlight());
            at("let flashlight hide settle", () => 1070);
            AddUntilStep("bar cannot bypass flashlight", () => display.FloatingBarAlpha(0) == 0);
        }

        [Test]
        public void PaceGradientZeroKeepsAdjacentSolidBandsJoined()
        {
            load("cat dog");
            AddStep("set two pace bands", () =>
            {
                var bands = new[] { new PaceBand(0, 4, Color4.Red), new PaceBand(4, 7, Color4.Green) };
                display.SetPaceBands(bands, bands);
                display.SetPaceColourGradient(100);
            });
            AddUntilStep("full gradient has width", () => display.PaceBlendWidth(0) > 0);
            float original = 0;
            AddStep("record transition and choose half gradient", () =>
            {
                original = display.PaceBlendWidth(0);
                display.SetPaceColourGradient(50);
            });
            AddUntilStep("half gradient halves transition", () => Math.Abs(display.PaceBlendWidth(0) - original / 2) < 0.0001);
            AddStep("choose sharp transitions", () => display.SetPaceColourGradient(0));
            AddUntilStep("transition width is zero", () => display.PaceBlendWidth(0) == 0);
            AddAssert("solid bands meet at their boundary", () => Math.Abs(display.PaceTrackScreenQuad(0).TopRight.X - display.PaceTrackScreenQuad(1).TopLeft.X) < 0.01);
        }

        [Test]
        public void FreestylePopUsesDoubledWindowAndConfiguredAlignment()
        {
            load("&", hardRock: true);
            AddStep("align early Great edge", () => config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 0f));
            at("before freestyle approach", () => 1000 - engine.Windows.GreatEarly * 2 - 140);
            AddUntilStep("starts at small size", () => display.CellVisualScale(0) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE);
            at("freestyle Great opens", () => 1000 - engine.Windows.GreatEarly * 2);
            AddUntilStep("freestyle finishes at doubled Great edge", () => display.CellVisualScale(0) == 1);
            AddStep("move alignment to target", () => config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 100f));
            AddUntilStep("new alignment delays pop-in immediately", () => display.CellVisualScale(0) == LyricLineDisplay.TEXT_POP_IN_MIN_SCALE);
            at("target alignment", () => 1000);
            AddUntilStep("full size at target", () => display.CellVisualScale(0) == 1);
            AddAssert("freestyle identity colour is preserved", () => colourIs(0, TypeBeatStyle.FreestyleChar));
        }
    }
}
