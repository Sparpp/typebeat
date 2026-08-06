// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Utils;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The lyric stack carries two heads, and each is dressed from its OWN setting: the typing caret
    /// from <see cref="TypeBeatRulesetSetting.CaretStyle"/>, the sung playhead from
    /// <see cref="TypeBeatRulesetSetting.SungCaretStyle"/>. Two contracts are pinned here.
    ///
    /// <para>INDEPENDENCE: moving one setting must leave the other head untouched, in both
    /// directions, both at construction and live. That is the whole point of the split, and nothing
    /// about the rendering itself would notice if the two bindings were quietly collapsed back onto
    /// one key, so it has to be asserted head by head rather than "both carets did the thing".</para>
    ///
    /// <para>CELL SIZING: the playhead is the interesting head, because it rides a CONTINUOUS
    /// fractional cell index rather than sitting on a discrete cell, so a cell-covering style has to
    /// be handed an interpolated advance; before that it rendered whatever placeholder width the
    /// caret happened to be constructed with, which matches no character on screen. The line here is
    /// a run of one repeated letter, so every cell has the same advance and the expected covered
    /// width is the same wherever between two cells the playhead currently sits. The interpolation
    /// itself is pinned by <c>CaretCellWidthTest</c>, which needs no font.</para>
    /// </summary>
    public partial class TestSceneTypeBeatCaretStyle : OsuTestScene
    {
        private const string text = "aaaaaaaaaa";

        /// <summary>The classic beam is a fixed 3px (Caret's beam_width), whatever the cell under it.</summary>
        private const float beam_width = 3f;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay activeDisplay => stage.DisplayAt(0)!;

        // The same cached-per-ShortName manager the playfield resolves, so SetValue here drives the
        // live gameplay binding exactly as the settings dropdowns do. It is shared across the whole
        // fixture, so createRuleset always writes BOTH keys rather than letting an earlier test's
        // value leak in as an unstated precondition.
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        private void createRuleset(CaretStyle typingStyle, CaretStyle playheadStyle)
        {
            AddStep($"typing caret = {typingStyle}, playhead = {playheadStyle}", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.CaretStyle, typingStyle);
                config.SetValue(TypeBeatRulesetSetting.SungCaretStyle, playheadStyle);
            });

            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                // Vocals stretched over five minutes: the playhead creeps, so it stays parked near
                // the first cell for the whole test rather than racing off the line.
                var line = new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 300000,
                    Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 300000 } },
                };

                var beatmap = new Beatmap
                {
                    HitObjects = new List<Rulesets.Objects.HitObject>
                    {
                        new TypeBeatHitObject
                        {
                            StartTime = line.StartTime,
                            LineIndex = 0,
                            Line = line,
                            Granularity = TimingGranularity.Word,
                        },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
            AddUntilStep("sung caret shown", () => stage.SungCaretVisible);
        }

        /// <summary>The on-screen advance of the cells the heads are on; uniform for this line.</summary>
        private float cellWidth => activeDisplay.CellWidthAt(0);

        private bool playheadCoversACell() => Precision.AlmostEquals(stage.SungCaretVisualWidth, cellWidth, 0.5f);

        private bool typingCaretCoversACell() => Precision.AlmostEquals(stage.PlayerCaretVisualWidth, cellWidth, 0.5f);

        private bool playheadIsABeam() => Precision.AlmostEquals(stage.SungCaretVisualWidth, beam_width, 0.01f);

        private bool typingCaretIsABeam() => Precision.AlmostEquals(stage.PlayerCaretVisualWidth, beam_width, 0.01f);

        [Test]
        public void TestBothHeadsOnLineStayBeams()
        {
            createRuleset(CaretStyle.Line, CaretStyle.Line);

            // The untouched-settings path (and the shipped default pairing for the playhead):
            // nothing about either head's shape may have moved.
            AddAssert("typing caret is the 3px beam", typingCaretIsABeam);
            AddAssert("playhead is the 3px beam", playheadIsABeam);
            AddAssert("both heads report Line", () => stage.PlayerCaretStyle == CaretStyle.Line && stage.SungCaretStyle == CaretStyle.Line);
        }

        [TestCase(CaretStyle.Block)]
        [TestCase(CaretStyle.Outline)]
        [TestCase(CaretStyle.Underline)]
        public void TestPlayheadCellStyleCoversARealCellAndLeavesTheTypingCaretAlone(CaretStyle style)
        {
            createRuleset(CaretStyle.Line, style);

            // THE original regression: the sung caret was never handed a cell width, so every
            // cell-covering style drew the caret's placeholder width instead of the character
            // actually being sung.
            AddUntilStep("playhead covers a measured cell", playheadCoversACell);
            AddAssert("the covered width is not the beam", () => stage.SungCaretVisualWidth > beam_width + 1f);
            AddAssert("playhead is on the chosen style", () => stage.SungCaretStyle == style);

            // The split: the typing caret's own setting was never touched, so it must not have moved.
            AddAssert("typing caret stayed on Line", () => stage.PlayerCaretStyle == CaretStyle.Line);
            AddAssert("typing caret is still the 3px beam", typingCaretIsABeam);
        }

        [TestCase(CaretStyle.Block)]
        [TestCase(CaretStyle.Outline)]
        [TestCase(CaretStyle.Underline)]
        public void TestTypingCaretCellStyleCoversARealCellAndLeavesThePlayheadAlone(CaretStyle style)
        {
            createRuleset(style, CaretStyle.Line);

            AddUntilStep("typing caret covers a measured cell", typingCaretCoversACell);
            AddAssert("the covered width is not the beam", () => stage.PlayerCaretVisualWidth > beam_width + 1f);
            AddAssert("typing caret is on the chosen style", () => stage.PlayerCaretStyle == style);

            // The other direction of the split.
            AddAssert("playhead stayed on Line", () => stage.SungCaretStyle == CaretStyle.Line);
            AddAssert("playhead is still the 3px beam", playheadIsABeam);
        }

        [Test]
        public void TestEachHeadFollowsOnlyItsOwnSettingLive()
        {
            createRuleset(CaretStyle.Line, CaretStyle.Line);
            AddAssert("both heads are the 3px beam", () => typingCaretIsABeam() && playheadIsABeam());

            // Switching INTO a cell style with the head already fed (so its stored cell width has not
            // changed) is the ordering that can strand a beam-sized block on screen.
            AddStep("typing caret -> Block", () => config.SetValue(TypeBeatRulesetSetting.CaretStyle, CaretStyle.Block));
            AddUntilStep("typing caret covers a measured cell", typingCaretCoversACell);
            AddAssert("playhead did not follow", () => stage.SungCaretStyle == CaretStyle.Line && playheadIsABeam());

            // Now move the OTHER setting, and check the first head holds its (different) style: the
            // two can be on two distinct cell styles at once.
            AddStep("playhead -> Underline", () => config.SetValue(TypeBeatRulesetSetting.SungCaretStyle, CaretStyle.Underline));
            AddUntilStep("playhead covers a measured cell", playheadCoversACell);
            AddAssert("typing caret held Block", () => stage.PlayerCaretStyle == CaretStyle.Block && typingCaretCoversACell());

            // And back, one head at a time: the beam must return to exactly its old fixed width,
            // never a cell width, and only for the head whose setting moved.
            AddStep("typing caret -> Line", () => config.SetValue(TypeBeatRulesetSetting.CaretStyle, CaretStyle.Line));
            AddUntilStep("typing caret is the 3px beam again", typingCaretIsABeam);
            AddAssert("playhead still covers a measured cell", playheadCoversACell);

            AddStep("playhead -> Line", () => config.SetValue(TypeBeatRulesetSetting.SungCaretStyle, CaretStyle.Line));
            AddUntilStep("playhead is the 3px beam again", playheadIsABeam);
            AddAssert("typing caret is still the 3px beam", typingCaretIsABeam);
        }
    }
}
