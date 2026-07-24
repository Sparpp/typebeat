// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Colour;
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
    /// The rendered half of FREESTYLE characters (backlog 22), driven through the real input path:
    /// an open slot shimmers through width-matched glyphs (never the authoring marker, never moving
    /// the line), any key fills it and the pressed char then stays put, backspace reopens it, and
    /// the slot wears the distinctive freestyle colour throughout.
    /// </summary>
    public partial class TestSceneTypeBeatFreestyle : OsuManualInputManagerTestScene
    {
        private const string text = "ab&cd"; // cell 2 is the freestyle slot
        private const int slot = 2;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display => stage.DisplayAt(0)!;

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
        public void TestShimmerThenTypeThenBackspace()
        {
            AddAssert("slot is a freestyle cell", () => engine.Lines[0].Cells[slot].IsFreestyle);
            AddAssert("slot shows a pool glyph, not the marker", () =>
            {
                string glyph = display.CellText(slot);
                return glyph.Length == 1
                       && glyph[0] != Typeability.FREESTYLE_MARKER
                       && display.ShimmerPool.Contains(glyph[0]);
            });
            // The gameplay font is proportional, so the width-grouped pool must be a strict subset
            // of the candidates (this is what stops the line jittering as the glyph is substituted).
            AddAssert("pool was width-grouped against the real font", () =>
                display.ShimmerPool.Count > 1 && display.ShimmerPool.Count < FreestyleGlyphs.CANDIDATES.Length);
            AddAssert("slot wears the freestyle colour", () => display.CellColour(slot).Equals((ColourInfo)TypeBeatStyle.FreestyleChar));
            AddAssert("ordinary cells do not", () => !display.CellColour(0).Equals((ColourInfo)TypeBeatStyle.FreestyleChar));

            string firstGlyph = null!;
            float width = 0;

            AddStep("capture glyph and line width", () =>
            {
                firstGlyph = display.CellText(slot);
                width = display.FullOnScreenWidth;
            });

            AddUntilStep("glyph shimmers to another one", () => display.CellText(slot) != firstGlyph);
            AddAssert("every substitution stays in the pool", () => display.ShimmerPool.Contains(display.CellText(slot)[0]));
            AddAssert("line width did not move", () => Precision.AlmostEquals(display.FullOnScreenWidth, width, 0.01f));

            AddStep("type a, b", () =>
            {
                InputManager.Key(Key.A);
                InputManager.Key(Key.B);
            });
            AddAssert("caret reached the slot", () => engine.CaretIndex == slot);

            AddStep("press X on the freestyle slot", () => InputManager.Key(Key.X));
            AddAssert("slot accepted the arbitrary key", () => engine.Lines[0].Cells[slot].State == CellState.Correct
                                                              && engine.Lines[0].Cells[slot].TypedChar == 'x');
            AddAssert("the pressed char is displayed", () => display.CellText(slot) == "x");
            AddAssert("still the freestyle colour once filled", () => display.CellColour(slot).Equals((ColourInfo)TypeBeatStyle.FreestyleChar));

            // The shimmer must be frozen now: let several shimmer ticks pass and re-check.
            AddWaitStep("let the shimmer clock run", 10);
            AddAssert("filled slot no longer shimmers", () => display.CellText(slot) == "x");

            // Backspace is gated on allow-wrong-input (backlog 24); this scene is about the
            // rendering of a reopened slot, so switch the gate on to reach that state.
            AddStep("allow wrong input (backspace gate)", () => engine.AllowWrongInput = true);
            AddStep("backspace", () => InputManager.Key(Key.BackSpace));
            AddAssert("slot reopened", () => engine.Lines[0].Cells[slot].State == CellState.Untyped
                                             && engine.Lines[0].Cells[slot].TypedChar == null);
            AddUntilStep("shimmer resumed", () => display.CellText(slot) != "x" && display.ShimmerPool.Contains(display.CellText(slot)[0]));

            AddStep("press 7 into the reopened slot", () => InputManager.Key(Key.Number7));
            AddAssert("the new char landed", () => display.CellText(slot) == "7" && engine.Lines[0].Cells[slot].TypedChar == '7');
        }
    }
}
