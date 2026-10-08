// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Extensions.Color4Extensions;
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
        [Test]
        public void WrongInputStaysNeutralUntilItsActualResultIsKnown()
        {
            load("abcd");
            AddStep("enable judgement bars", () => config.SetValue(TypeBeatRulesetSetting.JudgementColouredBars, true));
            at("word window", () => 1000);
            AddStep("make a recoverable typo", () => engine.ProcessKey('x', songClock.CurrentTime));
            AddUntilStep("pending typo is neutral", () => barIs(0, TypeBeatStyle.UntypedChar));
            AddStep("correct it", () =>
            {
                engine.ProcessBackspace();
                engine.ProcessKey('a', songClock.CurrentTime);
            });
            AddUntilStep("correction receives its actual Ok", () => barIs(0, TypeBeatStyle.PaceSlowAccent));
            AddStep("mistype an already judged letter", () =>
            {
                engine.ProcessBackspace();
                engine.ProcessKey('x', songClock.CurrentTime);
            });
            AddUntilStep("original award survives a later typo", () => barIs(0, TypeBeatStyle.PaceSlowAccent));
            AddStep("leave the next letter wrong", () => engine.ProcessKey('x', songClock.CurrentTime));
            AddUntilStep("new pending typo is neutral", () => barIs(1, TypeBeatStyle.UntypedChar));
            at("line seals after drag grace", () => engine.EffectiveSealTime(display.Line) + TypingEngine.FLETCHER_DRAG_GRACE_MS + 1);
            AddUntilStep("unfixed typo turns red at its final result", () => barIs(1, TypeBeatStyle.ErrorChar));
            AddAssert("earlier Ok is not revoked at seal", () => barIs(0, TypeBeatStyle.PaceSlowAccent));

            bool barIs(int i, Color4 colour) => display.JudgementBarAlpha(i) > 0 && display.JudgementBarColour(i).Equals((ColourInfo)colour);
        }

        [TestCase(false, false, PaceBarRevealMode.Build)]
        [TestCase(true, false, PaceBarRevealMode.Build)]
        [TestCase(true, true, PaceBarRevealMode.Build)]
        [TestCase(true, true, PaceBarRevealMode.ColourIn)]
        public void TypingFeedbackKeepsTheEntireLineStationary(bool approachBars, bool judgementColours, PaceBarRevealMode mode)
        {
            Vector2 untouchedPosition = Vector2.Zero;
            Vector2 bounds = Vector2.Zero;
            load("abcde");
            AddStep("configure feedback", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, false);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, approachBars);
                config.SetValue(TypeBeatRulesetSetting.PaceBarReveal, mode);
                config.SetValue(TypeBeatRulesetSetting.JudgementColouredBars, judgementColours);
            });
            at("syllable target", () => 1000);
            AddWaitStep("let the line layout settle", 2);
            AddStep("record the line's bounds and an untouched letter", () =>
            {
                untouchedPosition = display.CellScreenPosition(2);
                bounds = display.DrawSize;
            });
            AddStep("type the first Great", () => engine.ProcessKey('a', songClock.CurrentTime));
            AddUntilStep("individual judgement pulse remains", () => display.CellVisualScale(0) > 1);
            AddWaitStep("allow the pulsing glyph's bounds to update", 2);
            AddAssert("line stays still during the pulse", stationary);
            at("pulse finishes", () => 1140);
            AddUntilStep("first glyph returns to normal size", () => display.CellVisualScale(0) == 1);
            AddAssert("line stays still after the pulse", stationary);
            AddStep("type to the right edge", () =>
            {
                foreach (char c in "bcde")
                    engine.ProcessKey(c, songClock.CurrentTime);
            });
            AddUntilStep("rightmost glyph pulses", () => display.CellVisualScale(4) > 1);
            AddWaitStep("allow the last glyph's bounds to update", 2);
            AddAssert("right-edge pulses cannot move the line either", stationary);

            bool stationary() => Vector2.Distance(display.CellScreenPosition(2), untouchedPosition) < 0.01
                                  && Vector2.Distance(display.DrawSize, bounds) < 0.01;
        }

        [TestCase(false, PaceBarRevealMode.Build)]
        [TestCase(true, PaceBarRevealMode.Build)]
        [TestCase(false, PaceBarRevealMode.ColourIn)]
        [TestCase(true, PaceBarRevealMode.ColourIn)]
        public void PaceRailRevealsOnlyAtApproachArrivalsAndReconstructsOnSeek(bool hardRock, PaceBarRevealMode mode)
        {
            float width = 0;
            Vector2 position = Vector2.Zero;
            load("cat dog", hardRock, source: new LyricLine
            {
                RawText = "cat dog", StartTime = 0, EndTime = 6000, SingEndTime = 4000,
                Units = new[]
                {
                    new TimedUnit { Text = "cat", StartTime = 1000, EndTime = 1800 },
                    new TimedUnit { Text = "dog", StartTime = 2600, EndTime = 4000 },
                },
            });
            AddStep("enable pace reveal", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.PaceBarReveal, mode);
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, true);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInDuration, 400f);
                config.SetValue(TypeBeatRulesetSetting.SyllableFadeInEnd, 0f);
                var bands = new[] { new PaceBand(0, 4, Color4.Red), new PaceBand(4, 7, Color4.Green) };
                display.SetPaceBands(bands, bands);
                width = display.FullOnScreenWidth;
                position = display.CellScreenPosition(0);
            });
            at("text aligns before the bar", () => 900);
            AddUntilStep("pace rail stays hidden while bar approaches", () => display.FloatingBarAlpha(0) > 0 && display.RevealedPaceWidth == 0);
            AddUntilStep("only Colour in previews the future rail", () => previewMatchesMode());
            AddAssert("preview uses faint gray", () => display.UnrevealedPaceBarColour.Equals((ColourInfo)TypeBeatStyle.UntypedChar.Opacity(0.25f)));
            AddStep("type before arrival with judgement colours enabled", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementColouredBars, true);
                engine.ProcessKey('c', songClock.CurrentTime);
            });
            AddUntilStep("judgement colour also waits for the Approach bar", () => display.JudgementBarAlpha(0) == 0);
            at("first bar arrives", () => 1000);
            AddUntilStep("exact arriving span is revealed", () => Math.Abs(display.RevealedPaceWidth - firstSpanWidth()) < 0.01);
            AddUntilStep("judgement colour appears on the arriving segment", () => display.JudgementBarAlpha(0) > 0);
            AddAssert("judgement colour keeps priority over pace colours", () => display.JudgementBarColour(0).Equals((ColourInfo)TypeBeatStyle.SungAccent));
            AddAssert("remaining pace colours are still hidden", () => display.RevealedPaceWidth < display.FullSweepWidth);
            AddUntilStep("gray preview shrinks to the remaining span", () => previewMatchesMode());
            AddAssert("preview and pace rail align vertically", () => Math.Abs(display.UnrevealedPaceBarScreenQuad.TopLeft.Y - display.RailScreenQuad.TopLeft.Y) < 0.01);
            AddAssert("preview begins exactly after the coloured span", () => Math.Abs(display.UnrevealedPaceBarScreenQuad.TopLeft.X
                - display.RailScreenQuad.TopLeft.X - display.RevealedPaceWidth * display.FullOnScreenWidth / display.FullSweepWidth) < 0.01);
            if (hardRock)
            {
                at("just before second letter", () => display.Line.Cells[1].TargetTime - 1);
                AddUntilStep("second letter waits for its own bar", () => Math.Abs(display.RevealedPaceWidth - firstSpanWidth()) < 0.01);
                at("second letter arrives", () => display.Line.Cells[1].TargetTime);
                AddUntilStep("rail grows one letter at a time under HR", () => display.RevealedPaceWidth > firstSpanWidth() && display.RevealedPaceWidth < display.PaceTrackWidth(0));
            }
            at("final letter arrives", () => 4000);
            AddUntilStep("completed rail stays coloured", () => display.RevealedPaceWidth == display.FullSweepWidth);
            AddUntilStep("finished rail has no gray remainder", () => display.UnrevealedPaceBarWidth == 0);
            at("seek before first arrival", () => 900);
            AddUntilStep("seek removes future colours", () => display.RevealedPaceWidth == 0);
            AddUntilStep("seek reconstructs the gray preview", () => previewMatchesMode());
            AddStep("disable assembly", () => config.SetValue(TypeBeatRulesetSetting.PaceBarReveal, PaceBarRevealMode.Off));
            AddUntilStep("Off restores the full pace rail", () => display.RevealedPaceWidth == display.FullSweepWidth && display.UnrevealedPaceBarAlpha == 0);
            AddStep("enable assembly without Approach bars", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.ApproachBars, false);
                config.SetValue(TypeBeatRulesetSetting.PaceBarReveal, mode);
            });
            AddUntilStep("no Approach bars leaves a full rail", () => display.RevealedPaceWidth == display.FullSweepWidth && display.UnrevealedPaceBarAlpha == 0);
            AddStep("restore Approach bars", () => config.SetValue(TypeBeatRulesetSetting.ApproachBars, true));
            AddUntilStep("reveal resumes at current song time", () => display.RevealedPaceWidth == 0 && previewMatchesMode());
            AddAssert("assembly never changes lyric layout", () => Math.Abs(display.FullOnScreenWidth - width) < 0.01 && Vector2.Distance(display.CellScreenPosition(0), position) < 0.01);

            AddStep("switch reveal style live", () => config.SetValue(TypeBeatRulesetSetting.PaceBarReveal,
                mode == PaceBarRevealMode.Build ? PaceBarRevealMode.ColourIn : PaceBarRevealMode.Build));
            AddUntilStep("style changes without revealing future colours", () => display.RevealedPaceWidth == 0
                && (mode == PaceBarRevealMode.Build ? display.UnrevealedPaceBarAlpha > 0 : display.UnrevealedPaceBarAlpha == 0));
            AddStep("restore chosen reveal style", () => config.SetValue(TypeBeatRulesetSetting.PaceBarReveal, mode));
            AddUntilStep("original reveal style returns", () => previewMatchesMode());
            AddStep("enable Recite", () => display.SetReciteEnabled(true));
            at("Recite fade settles", () => 1050);
            AddUntilStep("Recite keeps both rail portions visible", () => display.RevealedPaceWidth > 0 && previewMatchesMode());
            AddStep("hide with flashlight", () => display.HideForFlashlight());
            at("flashlight fade settles", () => 1200);
            AddUntilStep("gray preview cannot bypass flashlight", () => display.SweepFillAlpha == 0 && display.UnrevealedPaceBarAlpha == 0);
            AddStep("flashlight spill excludes the sweep", () => display.SetFlashlightWindow(new LineWindow(0, display.Line.Cells.Count - 1, false, false), false));
            at("spill settles", () => 1350);
            AddUntilStep("spill does not reveal gray rail", () => display.UnrevealedPaceBarAlpha == 0);
            AddStep("restore the active sweep", () => display.SetFlashlightWindow(new LineWindow(0, display.Line.Cells.Count - 1, false, false), true));
            at("active sweep settles", () => 1500);
            AddUntilStep("gray preview follows restored rail visibility", () => display.SweepFillAlpha == 1 && previewMatchesMode());

            bool previewMatchesMode() => Math.Abs(display.UnrevealedPaceBarWidth - (display.FullSweepWidth - display.RevealedPaceWidth)) < 0.01
                && (mode == PaceBarRevealMode.ColourIn ? display.UnrevealedPaceBarAlpha > 0 : display.UnrevealedPaceBarAlpha == 0);

            float firstSpanWidth() => hardRock
                ? display.CellWidthAt(0) * display.FullSweepWidth / display.FullOnScreenWidth
                : display.PaceTrackWidth(0);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void JudgementBarColoursUseAwardedTiersAndCanBeEnabledAfterTyping(bool hardRock)
        {
            load("abcde", hardRock);
            at("Great", () => 1000);
            AddStep("type Great", () => engine.ProcessKey('a', songClock.CurrentTime));
            at("Ok", () => spanEnd(1) + (engine.Windows.GreatLate + engine.Windows.OkLate) / 2);
            AddStep("type Ok", () => engine.ProcessKey('b', songClock.CurrentTime));
            at("Meh", () => spanEnd(2) + (engine.Windows.OkLate + engine.Windows.MehLate) / 2);
            AddStep("type Meh", () => engine.ProcessKey('c', songClock.CurrentTime));
            at("correct off-time input", () => spanEnd(3) + engine.Windows.MehLate + 20);
            AddStep("type correct letter outside window", () => engine.ProcessKey('d', songClock.CurrentTime));
            at("wrong character", () => hardRock ? display.Line.Cells[4].TargetTime : songClock.CurrentTime);
            AddStep("type wrong letter", () => engine.ProcessKey('x', songClock.CurrentTime));
            AddAssert("experimental overlay starts disabled", () => display.JudgementBarAlpha(0) == 0);
            AddStep("enable judgement colours after the play", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.JudgementColouredBars, true);
                config.SetValue(TypeBeatRulesetSetting.ShowPaceColours, false);
            });
            AddUntilStep("all awarded colours appear", () => barIs(0, TypeBeatStyle.SungAccent) && barIs(1, TypeBeatStyle.PaceSlowAccent)
                && barIs(2, Color4.Orange) && barIs(3, Color4.Orange) && barIs(4, TypeBeatStyle.UntypedChar));
            AddAssert("bars align below their letters", () => Math.Abs(display.JudgementBarScreenQuad(0).TopLeft.Y - display.RailScreenQuad.TopLeft.Y) < 0.01);
            AddStep("hide with flashlight", () => display.HideForFlashlight());
            at("flashlight fade settles", () => songClock.CurrentTime + 100);
            AddUntilStep("judgement colours cannot bypass flashlight", () => display.JudgementBarAlpha(0) == 0);
            AddStep("disable experimental colours", () => config.SetValue(TypeBeatRulesetSetting.JudgementColouredBars, false));
            AddUntilStep("all overlays hide", () => display.JudgementBarAlpha(0) == 0 && display.JudgementBarAlpha(1) == 0);

            double spanEnd(int i) => hardRock ? display.Line.Cells[i].TargetTime : 5000;
            bool barIs(int i, Color4 colour) => display.JudgementBarAlpha(i) > 0 && display.JudgementBarColour(i).Equals((ColourInfo)colour);
        }
    }
}
