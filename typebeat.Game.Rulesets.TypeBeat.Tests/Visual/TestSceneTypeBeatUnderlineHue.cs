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
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// Backlog 228: the UNDERLINE PACE HUE on real drawables. The RULES are pinned by
    /// <c>UnderlinePaceTest</c> over the pure functions; what is pinned HERE is everything the pure
    /// functions cannot see, which is the whole of the wiring:
    ///
    /// <list type="bullet">
    /// <item>The map-wide distribution actually reaches the rail, so a line that is UNIFORMLY FAST
    /// renders red while the typical lines around it stay exactly the rail that shipped before this
    /// feature. This is the decided distribution basis, and a per-line implementation would grey that
    /// line out, so this scene is what catches such a regression.</item>
    /// <item>A PREVIEW line carries the same hues as an active one and is dimmed by the line dim
    /// rather than by anything of its own, which is free only because the bands live inside the
    /// display's dimmed content container.</item>
    /// <item>The FLASHLIGHT fades EVERY band. Missing one leaks the shape of a line the mod is meant
    /// to be hiding, which is gameplay information and not a cosmetic bug.</item>
    /// <item>The bands TILE the line and keep it exactly as wide as the single flat track did, so
    /// cutting the rail up cannot move a character.</item>
    /// </list>
    ///
    /// <para>The fixture is five two-word lines with three tie groups of speed: one fast line (line
    /// 1, which is also line 0's preview at the start of the map), one breathy line (line 4), three
    /// typical ones. Cross-checked in <c>UnderlinePaceTest</c>, where the same shape is built from
    /// the same numbers.</para>
    /// </summary>
    public partial class TestSceneTypeBeatUnderlineHue : OsuTestScene
    {
        private const int fast_line = 1;
        private const int slow_line = 4;

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display(int line) => stage.DisplayAt(line)!;

        private static Color4 neutral => UnderlinePace.NeutralColour;

        private Color4 bandColour(int line, int band) => display(line).PaceTrackColour(band).TopLeft.SRGB;

        /// <summary>
        /// "aa bb" sung over two words of <paramref name="wordMs"/> each, with the line's BOUNDARY
        /// left far past its vocals (up to the next line's start). Only the sung times decide the
        /// pace; the generous boundary is so a line does not seal itself out from under the scene
        /// while the steps run on a real-time clock.
        /// </summary>
        private static LyricLine twoWords(double start, double wordMs, double endTime)
        {
            double singEnd = start + 2 * wordMs;

            return new LyricLine
            {
                RawText = "aa bb",
                StartTime = start,
                EndTime = endTime,
                SingEndTime = singEnd,
                Units = new[]
                {
                    new TimedUnit { Text = "aa", StartTime = start, EndTime = start + wordMs },
                    new TimedUnit { Text = "bb", StartTime = start + wordMs, EndTime = singEnd },
                },
            };
        }

        [SetUpSteps]
        public void SetUpSteps()
        {
            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                // Line 1 is the fast one and line 4 the breathy one; the rest are typical.
                // The BOUNDARIES are pushed far out (line 0 owns the first ten minutes) because the
                // scene clock runs on real time and is shared by every test in the fixture: a line
                // that sealed itself out from under a later test would change which display is the
                // active one mid-assertion. The SUNG times, which are the only thing the pace reads,
                // are the same three tie groups UnderlinePaceTest builds.
                var lines = new[]
                {
                    twoWords(0, 2000, 600000),
                    twoWords(700000, 200, 800000),
                    twoWords(900000, 2000, 1000000),
                    twoWords(1100000, 2000, 1200000),
                    twoWords(1300000, 10000, 2000000),
                };

                var beatmap = new Beatmap
                {
                    HitObjects = lines.Select((line, i) => (Rulesets.Objects.HitObject)new TypeBeatHitObject
                    {
                        StartTime = line.StartTime,
                        LineIndex = i,
                        Line = line,
                        Granularity = TimingGranularity.Word,
                    }).ToList(),
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);

            // The fixture's own shape, asserted rather than trusted: every line is "aa bb", so every
            // one is cut into exactly two bands (the word plus its gap, then the last word).
            AddAssert("every line is two bands over five cells", () =>
                Enumerable.Range(0, 5).All(k => display(k).CellCount == 5
                                                && display(k).PaceTrackCount == 2
                                                && display(k).PaceTrackRange(0) == (0, 3)
                                                && display(k).PaceTrackRange(1) == (3, 5)));
        }

        /// <summary>
        /// THE HEADLINE, and the reason per-line percentiles were rejected: line 1 is uniformly fast,
        /// so every segment in it is typical OF IT, and only a whole-map distribution can call it
        /// fast. It must red.
        /// </summary>
        [Test]
        public void TestAUniformlyFastLineRendersRedAgainstTheMap()
        {
            AddAssert("the fast line's bands are all red", () =>
                Enumerable.Range(0, 2).All(b => bandColour(fast_line, b).R > neutral.R
                                                && bandColour(fast_line, b).B < neutral.B
                                                && bandColour(fast_line, b).A > neutral.A));

            AddAssert("the breathy line's bands are all green", () =>
                Enumerable.Range(0, 2).All(b => bandColour(slow_line, b).G > neutral.G
                                                && bandColour(slow_line, b).B < neutral.B
                                                && bandColour(slow_line, b).A > neutral.A));

            // And the map's typical middle is untouched: the rail those lines draw is byte-identical
            // to the one that shipped before any of this existed.
            AddAssert("the typical lines keep the pre-task rail", () =>
                new[] { 0, 2, 3 }.All(k => Enumerable.Range(0, 2).All(b => bandColour(k, b) == neutral)));
        }

        /// <summary>
        /// The rendered colours ARE the map-wide rule's output, not something the display re-derived:
        /// every band on screen is compared against <see cref="UnderlinePace.BuildBands"/> run over
        /// the engine's own lines.
        /// </summary>
        [Test]
        public void TestEveryBandOnScreenIsTheMapWideRulesOwnColour()
        {
            AddAssert("every band matches the whole-map precompute", () =>
            {
                var expected = UnderlinePace.BuildBands(engine.Lines);

                for (int k = 0; k < expected.Length; k++)
                {
                    if (display(k).PaceTrackCount != expected[k].Length)
                        return false;

                    for (int b = 0; b < expected[k].Length; b++)
                    {
                        if (bandColour(k, b) != expected[k][b].Colour)
                            return false;

                        if (display(k).PaceTrackRange(b) != (expected[k][b].StartCell, expected[k][b].EndCellExclusive))
                            return false;
                    }
                }

                return true;
            });
        }

        /// <summary>
        /// A PREVIEW line (the one after the active line) carries its hues exactly as the active line
        /// does, and the only thing different about it is the line dim, which multiplies the whole
        /// content container down. That is free because the bands are children of that container, and
        /// this test is what would catch them being moved out of it.
        /// </summary>
        [Test]
        public void TestThePreviewLineIsHuedAndDimmedWithItsText()
        {
            // Line 1 is the fast line AND line 0's preview, so at the top of the map the hued line is
            // the dimmed one.
            AddUntilStep("the preview line is dimmed", () => display(fast_line).ContentAlpha < 0.99f);

            AddAssert("the active line is not", () => display(0).ContentAlpha, () => Is.EqualTo(1f).Within(0.001f));

            AddAssert("and it still carries the same red the rule gives it", () =>
            {
                var expected = UnderlinePace.BuildBands(engine.Lines)[fast_line];

                return Enumerable.Range(0, 2).All(b => bandColour(fast_line, b) == expected[b].Colour)
                       && bandColour(fast_line, 0).R > neutral.R;
            });

            // The dim is on the container, so the bands' OWN alphas are untouched by it: nothing about
            // the hue is spent on the dimming, and the flashlight (below) still has the whole of the
            // band alpha to itself.
            AddAssert("the bands' own alphas are untouched", () =>
                Enumerable.Range(0, 2).All(b => display(fast_line).PaceTrackAlpha(b) == 1f));
        }

        /// <summary>
        /// The Flashlight mod hides the rail by fading it, and the rail is now several drawables. Every
        /// one of them has to fade, in BOTH seams: a band left lit outlines a line the mod is meant to
        /// be hiding.
        /// </summary>
        [Test]
        public void TestFlashlightFadesEveryBandAndBringsThemAllBack()
        {
            AddStep("hide the fast line for flashlight", () => display(fast_line).HideForFlashlight());

            AddUntilStep("every band went dark", () =>
                Enumerable.Range(0, 2).All(b => display(fast_line).PaceTrackAlpha(b) == 0f));

            AddStep("light a window on it", () => display(fast_line).SetFlashlightWindow(new LineWindow(0, 3, false, false), showSweep: true));

            AddUntilStep("every band came back", () =>
                Enumerable.Range(0, 2).All(b => display(fast_line).PaceTrackAlpha(b) == 1f));

            // And the other seam's "no sweep" arm (a line lit only by the window's spill, which does
            // not get the playhead rail) takes every band down too.
            AddStep("light it without the sweep", () => display(fast_line).SetFlashlightWindow(new LineWindow(0, 3, false, false), showSweep: false));

            AddUntilStep("every band went dark again", () =>
                Enumerable.Range(0, 2).All(b => display(fast_line).PaceTrackAlpha(b) == 0f));
        }

        /// <summary>
        /// Cutting the rail into bands must not move a character or resize a line. The bands tile the
        /// line exactly (no hole at either end, none in the middle), and the display is still as wide
        /// as every cell's slot laid end to end, which is the same pin the space-error-dot, freestyle
        /// and flashlight-cue-in scenes carry for their own overlays.
        /// </summary>
        [Test]
        public void TestTheBandsTileTheLineAndLeaveItsWidthAlone()
        {
            AddAssert("the bands tile every line", () =>
            {
                for (int k = 0; k < 5; k++)
                {
                    var d = display(k);

                    if (Math.Abs(d.PaceTrackX(0)) > 0.01f)
                        return false;

                    float covered = 0;

                    for (int b = 0; b < d.PaceTrackCount; b++)
                    {
                        if (Math.Abs(d.PaceTrackX(b) - covered) > 0.01f)
                            return false;

                        covered += d.PaceTrackWidth(b);
                    }

                    if (Math.Abs(covered - d.FullSweepWidth) > 0.01f)
                        return false;
                }

                return true;
            });

            // Measured on the lines the song has not reached: the ACTIVE line's sung glow is 6px
            // wide, centred on the playhead and deliberately not AlwaysPresent, so it inflates that
            // one line's auto-size box by up to half its width while it is lit. That is pre-existing
            // and orthogonal; the lines below carry the pin exactly.
            AddAssert("every unsung line is still its full width", () =>
                Enumerable.Range(1, 4).All(k => Math.Abs(display(k).DrawWidth - display(k).FullOnScreenWidth) < 0.01f));

            // Even with the whole rail faded out: the bands are AlwaysPresent for exactly this, and
            // the auto-size box would otherwise collapse onto whatever is currently lit, snapping
            // the line sideways as the flashlight window slides.
            // On the preview line, which is the one off the active line that is actually on screen
            // (the stack only shows three at a time, so the rest sit at alpha 0 and their transforms
            // do not run).
            AddStep("fade the preview line's rail out", () => display(fast_line).HideForFlashlight());

            AddUntilStep("its rail is dark", () =>
                Enumerable.Range(0, display(fast_line).PaceTrackCount).All(b => display(fast_line).PaceTrackAlpha(b) == 0f));

            AddAssert("the line did not move", () =>
                Math.Abs(display(fast_line).DrawWidth - display(fast_line).FullOnScreenWidth) < 0.01f);
        }
    }
}
