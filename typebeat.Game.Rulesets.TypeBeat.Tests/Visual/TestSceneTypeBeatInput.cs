// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Testing;
using osu.Framework.Utils;
using typebeat.Game.Beatmaps;
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
    /// Verifies the #1 integration risk of the port: that raw keyboard input actually reaches
    /// the playfield's key handler through the full DrawableRuleset chain
    /// (RulesetInputManager -> KeyBindingContainer -> playfield subtree), including that typing
    /// letters is NOT swallowed by the vestigial Z/X key-binding actions, that key events drive
    /// the regression-anchored engine, and that engine judgements surface as osu results.
    /// </summary>
    public partial class TestSceneTypeBeatInput : OsuManualInputManagerTestScene
    {
        private DrawableTypeBeatRuleset drawableRuleset = null!;
        private int osuResults;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                var line = new LyricLine
                {
                    // 'z' first: proves the Z key-binding (TypeBeatAction.Button1) does not eat typing.
                    RawText = "za b",
                    StartTime = 0,
                    EndTime = 600000,
                    SingEndTime = 300000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "za", StartTime = 0, EndTime = 150000 },
                        new TimedUnit { Text = "b", StartTime = 150000, EndTime = 300000 },
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

                osuResults = 0;
                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
                drawableRuleset.Playfield.NewResult += (_, _) => osuResults++;
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
        }

        [Test]
        public void TestRawKeysReachEngineAndScoring()
        {
            AddStep("press Z", () => InputManager.Key(Key.Z));
            AddAssert("'z' cell accepted (binding did not swallow it)", () => engine.Lines[0].Cells[0].State == CellState.Correct);
            AddAssert("caret advanced", () => engine.CaretIndex == 1);
            AddAssert("osu result applied for the cell", () => osuResults == 1);

            AddStep("press X (wrong char for 'a')", () => InputManager.Key(Key.X));
            AddAssert("rejected: cell untouched, caret stays", () => engine.Lines[0].Cells[1].State == CellState.Untyped && engine.CaretIndex == 1);
            AddAssert("no osu result for a rejected key", () => osuResults == 1);
            AddAssert("combo broken, streak counted", () => engine.Combo == 0 && engine.ConsecutiveWrongKeys == 1);

            AddStep("press A (correct char)", () => InputManager.Key(Key.A));
            AddAssert("cell accepted at the real time", () => engine.Lines[0].Cells[1].State == CellState.Correct);
            AddAssert("streak reset by the accepted char", () => engine.ConsecutiveWrongKeys == 0);
            AddAssert("osu result applied on the correct press", () => osuResults == 2);

            AddStep("press space", () => InputManager.Key(Key.Space));
            AddAssert("space cell accepted", () => engine.Lines[0].Cells[2].State == CellState.Correct && engine.CaretIndex == 3);

            AddStep("press B", () => InputManager.Key(Key.B));
            AddAssert("line complete", () => engine.IsLineComplete);
            AddAssert("engine accuracy reflects the one error", () => Precision.AlmostEquals(engine.LiveAccuracy, 4.0 / 5));
        }
    }
}
