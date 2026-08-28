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
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The Recite mod (backlog 229) hides every character the player has not typed yet and keeps
    /// everything else: what the player typed, and the map's playhead (the sung underline sweep and
    /// the sung caret). Driven through the real input path, asserting on per-cell alpha, because
    /// hiding is by ALPHA on cells that stay AlwaysPresent: a cell that vanished from the layout
    /// would collapse the auto-size box and slide the whole line sideways on every keypress.
    /// </summary>
    public partial class TestSceneTypeBeatRecite : OsuManualInputManagerTestScene
    {
        // 20 cells, no spaces, with a FREESTYLE slot at index 10 (the authoring marker '&').
        private const string active_text = "abcdefghij&lmnopqrst";
        private const int freestyle_slot = 10;
        private const int flashlight_radius = 5;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay activeDisplay => stage.DisplayAt(0)!;
        private LyricLineDisplay upcomingDisplay => stage.DisplayAt(1)!;

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create drawable ruleset with Recite", () => create(new TypeBeatModRecite()));
            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
        }

        private void create(params Mod[] mods)
        {
            var ruleset = new TypeBeatRuleset();

            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

            addLine(beatmap, 0, active_text);
            addLine(beatmap, 1, "secondline");

            var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

            Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable, mods);
        }

        private static void addLine(Beatmap beatmap, int index, string text)
        {
            var line = new LyricLine
            {
                RawText = text,
                StartTime = 0,
                // Sung over 20s so the underline sweep visibly advances inside a test step, but the
                // line does not SEAL for ten minutes, so no cell is ever resolved as a miss underneath
                // the assertions.
                EndTime = 600000,
                SingEndTime = 20000,
                Units = new[] { new TimedUnit { Text = text, StartTime = 0, EndTime = 20000 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = line.StartTime,
                LineIndex = index,
                Line = line,
                Granularity = TimingGranularity.Word,
            });
        }

        private static bool allHidden(LyricLineDisplay display) =>
            Enumerable.Range(0, display.CellCount).All(i => display.CellAlpha(i) < 0.05f);

        [Test]
        public void TestUntypedHiddenTypedRevealedBackspaceRehides()
        {
            // Nothing has been typed, so the whole lyric is hidden, on the active line and on the
            // preview line alike (the preview line needs no path of its own: its cells are Untyped).
            AddUntilStep("every untyped lyric char hidden", () =>
                Enumerable.Range(0, activeDisplay.CellCount).Where(i => i != freestyle_slot).All(i => activeDisplay.CellAlpha(i) < 0.05f));
            AddUntilStep("upcoming preview line hidden too", () => allHidden(upcomingDisplay));

            // The freestyle slot is the one deliberate exception: it shimmers a random pool glyph
            // that says nothing about the lyric, so hiding it would only make the section invisible.
            AddAssert("freestyle slot stays visible", () => activeDisplay.CellAlpha(freestyle_slot) > 0.5f);

            float width = 0;
            float lastCharX = 0;

            AddStep("capture the line's layout", () =>
            {
                width = activeDisplay.FullOnScreenWidth;
                lastCharX = activeDisplay.CellScreenPosition(activeDisplay.CellCount - 1).X;
            });

            AddStep("type the first char", () => InputManager.Key(Key.A));
            AddAssert("it landed correct", () => engine.CaretIndex == 1 && engine.Lines[0].Cells[0].State == CellState.Correct);

            AddUntilStep("the typed char is revealed", () => activeDisplay.CellAlpha(0) > 0.9f);
            AddAssert("the next char is still hidden", () => activeDisplay.CellAlpha(1) < 0.05f);

            // The hiding is by alpha on AlwaysPresent cells, so revealing one cannot move the line.
            AddAssert("the line did not move", () =>
                Precision.AlmostEquals(activeDisplay.FullOnScreenWidth, width, 0.01f)
                && Precision.AlmostEquals(activeDisplay.CellScreenPosition(activeDisplay.CellCount - 1).X, lastCharX, 0.01f));

            AddStep("type two more", () =>
            {
                InputManager.Key(Key.B);
                InputManager.Key(Key.C);
            });
            AddUntilStep("all three revealed", () =>
                activeDisplay.CellAlpha(0) > 0.9f && activeDisplay.CellAlpha(1) > 0.9f && activeDisplay.CellAlpha(2) > 0.9f);

            // Backspace is gated on allow-wrong-input (backlog 24).
            AddStep("allow wrong input (backspace gate)", () => engine.AllowWrongInput = true);
            AddStep("backspace", () => InputManager.Key(Key.BackSpace));
            AddAssert("the cell is untyped again", () => engine.Lines[0].Cells[2].State == CellState.Untyped);

            AddUntilStep("the backspaced char hides again", () => activeDisplay.CellAlpha(2) < 0.05f);
            AddAssert("the chars still typed stay visible", () => activeDisplay.CellAlpha(0) > 0.9f && activeDisplay.CellAlpha(1) > 0.9f);
        }

        /// <summary>
        /// The one requirement a copy-paste of the flashlight's hiding would break: Flashlight fades
        /// the sweep out with the characters, and Recite must not, because the playhead is the only
        /// cue a reciting player has left.
        /// </summary>
        [Test]
        public void TestPlayheadSurvivesTheHiding()
        {
            AddUntilStep("the lyric is hidden", () =>
                Enumerable.Range(0, activeDisplay.CellCount).Where(i => i != freestyle_slot).All(i => activeDisplay.CellAlpha(i) < 0.05f));

            float sweep = 0;

            AddStep("capture the sweep", () => sweep = activeDisplay.SweepFillWidth);
            AddUntilStep("the sung sweep still advances", () => activeDisplay.SweepFillWidth > sweep + 1f);
            AddAssert("the sweep is inside the line, not saturated", () => activeDisplay.SweepFillWidth < activeDisplay.FullSweepWidth);

            // Width alone would not catch the copy-paste this pins: a sweep faded to alpha 0 still
            // advances, so the flashlight's fade would slip through a width-only assertion.
            AddAssert("the sweep is not faded out", () => activeDisplay.SweepTrackAlpha > 0.9f && activeDisplay.SweepFillAlpha > 0.9f);
            AddAssert("the sung caret is still drawn", () => stage.SungCaretVisible);
        }

        /// <summary>
        /// Recite and Flashlight are compatible on purpose: they contribute independent per-cell
        /// factors that multiply, so stacking them is exactly "hidden by either" with no arbitrary
        /// winner. Both directions are checked, so neither factor can be the one silently ignored.
        /// </summary>
        [Test]
        public void TestStacksWithFlashlightAndNeverExceedsEitherAlone()
        {
            float[] recite = null!;
            float[] flashlight = null!;
            float[] both = null!;

            AddStep("capture Recite alone", () => recite = alphas());

            AddStep("rebuild with Flashlight alone", () => create(new TypeBeatModFlashlight()));
            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
            AddUntilStep("the flashlight window settled", () => activeDisplay.CellAlpha(0) > 0.5f);
            AddStep("capture Flashlight alone", () => flashlight = alphas());

            AddStep("rebuild with both", () => create(new TypeBeatModRecite(), new TypeBeatModFlashlight()));
            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);
            AddUntilStep("both settled", () => activeDisplay.CellAlpha(0) < 0.05f);
            AddStep("capture both", () => both = alphas());

            AddAssert("stacked alpha never exceeds either alone", () =>
                Enumerable.Range(0, both.Length).All(i => both[i] <= Math.Min(recite[i], flashlight[i]) + 1e-3f));

            // Non-vacuity, both ways round.
            AddAssert("a char the flashlight lit is hidden by Recite", () =>
                Enumerable.Range(0, flashlight_radius).Any(i => flashlight[i] > 0.5f && recite[i] < 0.05f && both[i] < 0.05f));
            AddAssert("the freestyle slot Recite shows is hidden by the flashlight", () =>
                recite[freestyle_slot] > 0.5f && flashlight[freestyle_slot] < 0.05f && both[freestyle_slot] < 0.05f);
        }

        /// <summary>
        /// Recite is PURELY VISUAL: it must not move a single judgement. Both engines are fed the
        /// identical keypress script at identical EXPLICIT times (rather than through the input
        /// manager, whose timings are wall-clock), so the deltas are a function of the script alone
        /// and the comparison is exact rather than approximate.
        /// </summary>
        [Test]
        public void TestJudgementIsIdenticalWithAndWithoutTheMod()
        {
            string reciteFlags = null!;
            string plainFlags = null!;
            string reciteJudgements = null!;
            string plainJudgements = null!;

            AddStep("run the script under Recite", () =>
            {
                reciteFlags = engineFlags(engine);
                reciteJudgements = runScript(engine);
            });

            AddStep("rebuild with no mods at all", () => create());
            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);

            AddStep("run the same script with no mods", () =>
            {
                plainFlags = engineFlags(engine);
                plainJudgements = runScript(engine);
            });

            // The engine's era and judgement flags are what a mod would have to move to change a
            // judgement, and Recite never touches the engine at all (its seam is the drawable
            // ruleset, not createEngine, which is why it needs no replay CONFIG bit).
            AddAssert("engine flags untouched by the mod", () => reciteFlags == plainFlags);
            AddAssert("every judgement identical", () => reciteJudgements == plainJudgements);
            AddAssert("the script actually judged something", () => reciteJudgements.Contains("Correct") && reciteJudgements.Contains("Wrong"));
        }

        private float[] alphas() => Enumerable.Range(0, activeDisplay.CellCount).Select(i => activeDisplay.CellAlpha(i)).ToArray();

        private static string engineFlags(TypingEngine e) => string.Join(",",
            e.SyllableTiming, e.CharTimedStretch, e.WrongInputOnWordGaps, e.StrictSpaces, e.MashingEnabled,
            e.CaseSensitive, e.AllowWrongInput, e.SpaceSkipsWord, e.FletcherEnabled, e.FlexibleLineSnap,
            e.BoundedRush, e.FlexibleCaretFromMod, e.WindowScale);

        /// <summary>Three correct chars and one typo, at fixed times, then the whole line's resolved
        /// state as a string: cell state, the char that landed, and the delta it was judged on.</summary>
        private static string runScript(TypingEngine e)
        {
            e.AllowWrongInput = true;

            e.ProcessKey('a', 100);
            e.ProcessKey('b', 250);
            e.ProcessKey('z', 400); // wrong: cell 2 expects 'c', so it types through as a typo
            e.ProcessKey('d', 900);

            return string.Join("|", e.Lines[0].Cells.Select(c => $"{c.State}/{c.TypedChar}/{c.JudgedDelta}"));
        }
    }
}
