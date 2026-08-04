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
    /// The caret-style setting dresses BOTH heads on the lyric stack: the typing caret and the sung
    /// playhead. The playhead is the interesting one, because it rides a CONTINUOUS fractional cell
    /// index rather than sitting on a discrete cell, so a cell-covering style has to be handed an
    /// interpolated advance; before that it rendered whatever placeholder width the caret happened
    /// to be constructed with, which matches no character on screen.
    ///
    /// <para>The line here is a run of one repeated letter, so every cell has the same advance and
    /// the expected covered width is the same wherever between two cells the playhead currently sits.
    /// The interpolation itself is pinned by <c>CaretCellWidthTest</c>, which needs no font.</para>
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
        // live gameplay binding exactly as the settings dropdown does.
        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        private void createRuleset(CaretStyle initialStyle)
        {
            AddStep($"caret style = {initialStyle}", () => config.SetValue(TypeBeatRulesetSetting.CaretStyle, initialStyle));

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

        /// <summary>The on-screen advance of the cells the playhead is between; uniform for this line.</summary>
        private float cellWidth => activeDisplay.CellWidthAt(0);

        private bool bothCaretsCoverACell() =>
            Precision.AlmostEquals(stage.SungCaretVisualWidth, cellWidth, 0.5f)
            && Precision.AlmostEquals(stage.PlayerCaretVisualWidth, cellWidth, 0.5f);

        private bool bothCaretsAreBeams() =>
            Precision.AlmostEquals(stage.SungCaretVisualWidth, beam_width, 0.01f)
            && Precision.AlmostEquals(stage.PlayerCaretVisualWidth, beam_width, 0.01f);

        [Test]
        public void TestBeamStaysABeamForBothCarets()
        {
            createRuleset(CaretStyle.Line);

            // The untouched-setting path: nothing about either caret's shape may have moved.
            AddAssert("both carets are the 3px beam", bothCaretsAreBeams);
            AddAssert("sung caret is on the chosen style", () => stage.SungCaretStyle == CaretStyle.Line);
        }

        [TestCase(CaretStyle.Block)]
        [TestCase(CaretStyle.Outline)]
        [TestCase(CaretStyle.Underline)]
        public void TestCellStylesCoverARealCellOnBothCarets(CaretStyle style)
        {
            createRuleset(style);

            // THE regression: the sung caret was never handed a cell width, so every cell-covering
            // style drew the caret's placeholder width instead of the character actually being sung.
            AddUntilStep("both carets cover a measured cell", bothCaretsCoverACell);
            AddAssert("the covered width is not the beam", () => stage.SungCaretVisualWidth > beam_width + 1f);
            AddAssert("sung caret is on the chosen style", () => stage.SungCaretStyle == style);
        }

        [Test]
        public void TestStyleChangeAppliesLiveToBothCarets()
        {
            createRuleset(CaretStyle.Line);
            AddAssert("both carets are the 3px beam", bothCaretsAreBeams);

            // Switching INTO a cell style with the caret already fed (so the stored cell width has
            // not changed) is the ordering that can strand a beam-sized block on screen.
            AddStep("switch to Block", () => config.SetValue(TypeBeatRulesetSetting.CaretStyle, CaretStyle.Block));
            AddUntilStep("both carets cover a measured cell", bothCaretsCoverACell);

            AddStep("switch to Underline", () => config.SetValue(TypeBeatRulesetSetting.CaretStyle, CaretStyle.Underline));
            AddUntilStep("both carets still cover a measured cell", bothCaretsCoverACell);

            // And back: the beam must return to exactly its old fixed width, never a cell width.
            AddStep("switch back to Line", () => config.SetValue(TypeBeatRulesetSetting.CaretStyle, CaretStyle.Line));
            AddUntilStep("both carets are the 3px beam again", bothCaretsAreBeams);
        }
    }
}
