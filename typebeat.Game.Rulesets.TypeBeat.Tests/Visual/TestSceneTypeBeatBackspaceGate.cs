// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using osu.Framework.Testing;
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
    /// Backspace is gated on allow-wrong-input (backlog 24), driven here through the real input
    /// path. In strict (default) play a wrong key is rejected instead of landing, so there is never
    /// an erasable character and the key is ignored outright: no engine call, no state change. With
    /// the setting on, a wrong char lands and backspace erases it again. FREESTYLE cells (whose
    /// press is a correct hit that keeps the pressed char) are gated identically, so the rule stays
    /// one predicate: erasing exists only where wrong input can land.
    /// </summary>
    public partial class TestSceneTypeBeatBackspaceGate : OsuManualInputManagerTestScene
    {
        // Cells: 0 'z', 1 'a', 2 freestyle, 3 'b'.
        private const string text = "za&b";
        private const int freestyle_slot = 2;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

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
        }

        [Test]
        public void TestBackspaceIsInertInStrictMode()
        {
            AddAssert("strict mode is the default", () => !engine.AllowWrongInput);

            AddStep("type z, a", () =>
            {
                InputManager.Key(Key.Z);
                InputManager.Key(Key.A);
            });
            AddAssert("caret reached the freestyle slot", () => engine.CaretIndex == freestyle_slot);

            AddStep("press Q into the freestyle slot", () => InputManager.Key(Key.Q));
            AddAssert("the freestyle slot took it as a correct hit", () =>
                engine.Lines[0].Cells[freestyle_slot].State == CellState.Correct
                && engine.Lines[0].Cells[freestyle_slot].TypedChar == 'q');

            // Freestyle cells are gated identically: the press was a CORRECT hit and retyping is
            // scoring-inert, so there is nothing worth erasing here either.
            AddStep("press Backspace over the filled freestyle slot", () => InputManager.Key(Key.BackSpace));
            AddAssert("freestyle slot untouched", () =>
                engine.Lines[0].Cells[freestyle_slot].State == CellState.Correct
                && engine.Lines[0].Cells[freestyle_slot].TypedChar == 'q'
                && engine.CaretIndex == freestyle_slot + 1);

            AddStep("hold Backspace over an ordinary correct cell", () =>
            {
                InputManager.Key(Key.BackSpace);
                InputManager.Key(Key.BackSpace);
                InputManager.Key(Key.BackSpace);
            });
            AddAssert("every earlier cell survived", () =>
                engine.Lines[0].Cells[0].State == CellState.Correct && engine.Lines[0].Cells[0].TypedChar == 'z'
                && engine.Lines[0].Cells[1].State == CellState.Correct && engine.Lines[0].Cells[1].TypedChar == 'a'
                && engine.CaretIndex == freestyle_slot + 1);

            // Typing carries on normally: the swallowed key changed nothing at all.
            AddStep("press B", () => InputManager.Key(Key.B));
            AddAssert("line completed as if backspace was never pressed", () => engine.IsLineComplete && engine.LiveAccuracy == 1);
        }

        [Test]
        public void TestBackspaceErasesWhenWrongInputIsAllowed()
        {
            AddStep("allow wrong input", () => engine.AllowWrongInput = true);

            AddStep("type z", () => InputManager.Key(Key.Z));

            AddStep("press Q (wrong for 'a', typed through)", () => InputManager.Key(Key.Q));
            AddAssert("wrong char landed", () =>
                engine.Lines[0].Cells[1].State == CellState.Wrong
                && engine.Lines[0].Cells[1].TypedChar == 'q'
                && engine.CaretIndex == 2);

            AddStep("press Backspace", () => InputManager.Key(Key.BackSpace));
            AddAssert("wrong char erased", () =>
                engine.Lines[0].Cells[1].State == CellState.Untyped
                && engine.Lines[0].Cells[1].TypedChar == null
                && engine.CaretIndex == 1);

            AddStep("retype a", () => InputManager.Key(Key.A));
            AddAssert("cell fixed", () => engine.Lines[0].Cells[1].State == CellState.Correct);

            // Freestyle slot: erasable too, under the same single gate.
            AddStep("fill the freestyle slot with X", () => InputManager.Key(Key.X));
            AddAssert("slot filled", () => engine.Lines[0].Cells[freestyle_slot].TypedChar == 'x');

            AddStep("press Backspace", () => InputManager.Key(Key.BackSpace));
            AddAssert("freestyle slot reopened", () =>
                engine.Lines[0].Cells[freestyle_slot].State == CellState.Untyped
                && engine.Lines[0].Cells[freestyle_slot].TypedChar == null
                && engine.CaretIndex == freestyle_slot);
        }
    }
}
