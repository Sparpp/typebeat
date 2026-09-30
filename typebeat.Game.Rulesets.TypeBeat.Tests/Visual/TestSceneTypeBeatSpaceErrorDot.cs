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
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The optional space error dot marks a wrong character typed into a word gap. This scene
    /// drives the setting live and checks the drawable on screen.
    ///
    /// <para>The RULE is pinned by <c>SpaceErrorDotTest</c> over
    /// <see cref="LyricLineDisplay.ComputeSpaceErrorDots"/>. What is left for here is the wiring the
    /// pure function cannot see: the setting reaching the display, the dot being drawn where the
    /// player would look for it, and a backspace over the mistyped space taking it away with no
    /// event of its own (the dot is pull-based, exactly as every cell colour is).</para>
    ///
    /// <para>Keys are fed straight to the engine at chosen times rather than through the input
    /// manager, as <c>TestSceneTypeBeatGapTypoDim</c> does and for the same reason: the real input
    /// path can only press "now".</para>
    /// </summary>
    public partial class TestSceneTypeBeatSpaceErrorDot : OsuTestScene
    {
        private const string text = "ab cd"; // cells: a0 b1 _2 c3 d4
        private const int gap = 2;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display => stage.DisplayAt(0)!;

        private TypingCell cell(int index) => engine.Lines[0].Cells[index];

        private void press(int index, char c) => engine.ProcessKey(c, cell(index).TargetTime);

        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        private void setDot(bool enabled) =>
            AddStep($"space error dot {(enabled ? "on" : "off")}", () => config.SetValue(TypeBeatRulesetSetting.UseSpaceErrorDot, enabled));

        [SetUpSteps]
        public void SetUpSteps()
        {
            // Both settings written explicitly rather than inherited from whatever ran before: the
            // config manager is cached per ShortName across the whole fixture.
            AddStep("word skipping off", () => config.SetValue(TypeBeatRulesetSetting.SpaceSkipsWord, false));
            setDot(false);

            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                var line = new LyricLine
                {
                    RawText = text,
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 300000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "ab", StartTime = 0, EndTime = 150000 },
                        new TimedUnit { Text = "cd", StartTime = 150000, EndTime = 300000 },
                    },
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

            // The fixture's own shape, asserted rather than trusted: one gap, at cell 2, with a dot
            // drawable of its own, and nothing lit while the setting is off.
            AddAssert("one gap, at cell 2, with a dot of its own", () =>
                cell(gap).Expected == ' '
                && display.CellCount == text.Length
                && display.SpaceErrorDotCount == 1
                && !display.SpaceErrorDotVisibleAt(gap));
        }

        /// <summary>Type the first word, then place a wrong character in its following space.</summary>
        private void mistypeTheGap()
        {
            AddStep("type 'ab', then mistype the space", () =>
            {
                press(0, 'a');
                press(1, 'b');
                press(gap, 'x');
            });

            AddUntilStep("the gap holds the typo", () => cell(gap).State == CellState.Wrong);
        }

        /// <summary>A wrong character inside the gap draws its dot.</summary>
        [Test]
        public void TestAMistypedGapShowsTheDot()
        {
            setDot(true);
            mistypeTheGap();

            AddUntilStep("the dot is drawn in the gap", () => display.SpaceErrorDotVisibleAt(gap));
        }

        /// <summary>With the setting off, the wrong character appears instead of the dot.</summary>
        [Test]
        public void TestTheDotStaysHiddenWhileTheSettingIsOff()
        {
            mistypeTheGap();

            AddWaitStep("let the repaint land", 3);
            AddAssert("no dot", () => display.SpaceErrorDotVisibleAt(gap), () => Is.False);

            // Turning it on mid-play reveals the dot for the standing gap typo.
            float width = 0;
            var cellPositions = Array.Empty<osuTK.Vector2>();

            AddStep("measure the line", () =>
            {
                width = display.FullOnScreenWidth;
                cellPositions = Enumerable.Range(0, display.CellCount).Select(display.CellScreenPosition).ToArray();
            });

            setDot(true);
            AddUntilStep("turning it on lights it", () => display.SpaceErrorDotVisibleAt(gap));

            // The dot is an overlay, kept out of the auto-size box exactly as the retype selection
            // is: showing it must not move a single character, or a setting nobody can see the point
            // of would shuffle the text a player is reading.
            AddAssert("and nothing moved", () =>
                display.FullOnScreenWidth == width
                && Enumerable.Range(0, display.CellCount).All(i => display.CellScreenPosition(i) == cellPositions[i]));
        }

        /// <summary>A clean word earns nothing: the dot marks an error, not a space.</summary>
        [Test]
        public void TestACleanWordShowsNoDot()
        {
            setDot(true);

            AddStep("type 'ab' cleanly, then space", () =>
            {
                press(0, 'a');
                press(1, 'b');
                press(gap, ' ');
            });

            AddUntilStep("the space was accepted", () => cell(gap).State == CellState.Correct);
            AddAssert("and no dot is drawn", () => display.SpaceErrorDotVisibleAt(gap), () => Is.False);
        }

        /// <summary>
        /// Backspacing over the mistyped space clears the dot. Nothing clears it explicitly: the
        /// gap returns to <see cref="CellState.Untyped"/> and the rule is read again on the next
        /// repaint, which is the whole reason the dot is computed from cell state rather than latched
        /// when the space landed.
        /// </summary>
        [Test]
        public void TestBackspacingOverTheSpaceClearsTheDot()
        {
            setDot(true);
            mistypeTheGap();
            AddUntilStep("dotted to start with", () => display.SpaceErrorDotVisibleAt(gap));

            AddStep("backspace over the space", () => engine.ProcessBackspace());
            AddAssert("the gap is open again", () => cell(gap).State == CellState.Untyped);

            AddUntilStep("and the dot is gone", () => !display.SpaceErrorDotVisibleAt(gap));
        }
    }
}
