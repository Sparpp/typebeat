// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Testing;
using osu.Framework.Timing;
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
    /// Backlog 334: the gameplay lyric FONT SIZE setting, at its minimum (24), its default (42) and
    /// its maximum (72), with the line spacing at its own minimum (40) so the pitch floor is what is
    /// being exercised. The clock is FROZEN (a manual clock under the ruleset) at an instant where
    /// line 0 is the player's, its first word is 1000 ms away (so its first-word cue is up) and line
    /// 1 is the preview row below it, which makes every assertion a single-frame geometric fact.
    ///
    /// <para>What each case asserts, all in SCREEN space against the glyphs themselves (a cell's
    /// <c>ScreenSpaceDrawQuad</c>), with <c>u</c> the screen size of one lyric-font pixel
    /// (the stage's screen scale times the requested size):</para>
    /// <list type="bullet">
    /// <item>both carets start at the top of the glyph row and are exactly as tall as it;</item>
    /// <item>the sweep rail sits 6/42 of the font size under the glyph row and is 3/42 of it thick;</item>
    /// <item>the first-word cue bar hangs the same 6/42 under the glyph row and is 4/42 thick;</item>
    /// <item>each syllable marker hangs from the bottom of the glyph row, centred on its cell's left
    /// edge, and clears the rail;</item>
    /// <item>the preview row's glyphs start below the active row's rail and cue bar (no overlap at
    /// the minimum spacing), and the pitch is the floor, not the 40 the slider says;</item>
    /// <item>a long line is shrunk to fit and its every glyph is inside the playfield.</item>
    /// </list>
    /// There is no screenshot capture in this test project, so these bounds ARE the visual check.
    /// </summary>
    public partial class TestSceneTypeBeatLyricFontSize : OsuTestScene
    {
        private const float min_line_spacing = 40f;

        // cells: b0 a1 n2 a3 n4 a5 _6 c7 a8 k9 e10; the two syllable boundaries cut "ba|na|na".
        private static readonly int[] expected_marks = { 2, 4 };

        private const string long_text =
            "every single word of this line keeps on running far past where any sane lyric would stop and still goes on";

        private DrawableTypeBeatRuleset drawableRuleset = null!;
        private ManualClock manualClock = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();

        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        /// <summary>Screen pixels per stage-local unit.</summary>
        private float screenScale => stage.ScreenSpaceDrawQuad.Width / stage.DrawWidth;

        [TearDownSteps]
        public void TearDownSteps()
        {
            AddStep("restore the defaults", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.LyricFontSize, TypeBeatStyle.LYRIC_FONT_SIZE);
                config.SetValue(TypeBeatRulesetSetting.LineSpacing, 96f);
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, true);
            });
        }

        private void load(float fontSize, double time)
        {
            AddStep($"font size {fontSize}, minimum spacing", () =>
            {
                config.SetValue(TypeBeatRulesetSetting.LyricFontSize, fontSize);
                config.SetValue(TypeBeatRulesetSetting.LineSpacing, min_line_spacing);
                // Compare full-size glyph geometry with carets and rails, independently of pop-in.
                config.SetValue(TypeBeatRulesetSetting.TextPopIn, false);
            });

            AddStep("create drawable ruleset", () =>
            {
                var ruleset = new TypeBeatRuleset();

                var lines = new[]
                {
                    markedLine(0, 2000),
                    markedLine(10000, 12000),
                    longLine(20000),
                };

                var beatmap = new Beatmap
                {
                    HitObjects = lines.Select((l, i) => (Rulesets.Objects.HitObject)new TypeBeatHitObject
                    {
                        StartTime = l.StartTime,
                        LineIndex = i,
                        Line = l,
                        Granularity = TimingGranularity.Syllable,
                    }).ToList(),
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                manualClock = new ManualClock { CurrentTime = time };

                Child = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Clock = new FramedClock(manualClock),
                    Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable),
                };
            });

            AddUntilStep("stage built at the size", () => drawableRuleset.IsLoaded && stage.IsLoaded && stage.FontSize == fontSize);
            AddUntilStep("clock settled", () => Math.Abs(stage.Clock.CurrentTime - time) < 1);
        }

        private static LyricLine markedLine(double start, double firstWord) => new LyricLine
        {
            RawText = "banana cake",
            StartTime = start,
            EndTime = start + 9000,
            SingEndTime = firstWord + 3000,
            Units = new[]
            {
                new TimedUnit
                {
                    Text = "banana",
                    StartTime = firstWord,
                    EndTime = firstWord + 1500,
                    SyllableBoundaries = new[] { firstWord + 500, firstWord + 1000 },
                },
                new TimedUnit { Text = "cake", StartTime = firstWord + 1500, EndTime = firstWord + 3000 },
            },
        };

        private static LyricLine longLine(double start)
        {
            string[] words = long_text.Split(' ');
            var units = new TimedUnit[words.Length];

            for (int i = 0; i < words.Length; i++)
                units[i] = new TimedUnit { Text = words[i], StartTime = start + 500 + i * 300, EndTime = start + 500 + (i + 1) * 300 };

            return new LyricLine
            {
                RawText = long_text,
                StartTime = start,
                EndTime = start + 9000,
                SingEndTime = start + 500 + words.Length * 300,
                Units = units,
            };
        }

        /// <summary>
        /// Carets, rail, cue and markers all in proportion to the text, and the preview row clear of
        /// the active one at the minimum spacing.
        /// </summary>
        [TestCase(24f)]
        [TestCase(42f)]
        [TestCase(72f)]
        public void TestEverythingBesideTheRowScalesWithIt(float fontSize)
        {
            load(fontSize, 1000);

            AddUntilStep("line 0 is the player's and its cue is up", () =>
                engine.ActiveLineIndex == 0 && stage.ApproachCueTargetLine == 0 && stage.FirstWordCueAlpha > 0.1f);

            AddAssert("line 0 is not shrunk (the proportions below are at the requested size)", () =>
                stage.DisplayAt(0)!.FullOnScreenWidth, () => Is.EqualTo(stage.DisplayAt(0)!.FullSweepWidth).Within(0.01f));

            // One lyric-font pixel on screen, and the glyph row as it is actually drawn.
            float u() => screenScale * fontSize / TypeBeatStyle.LYRIC_FONT_SIZE;
            Quad glyph() => stage.DisplayAt(0)!.CellScreenQuad(0);

            AddAssert("typing caret: top of the glyph row", () => stage.PlayerCaretScreenQuad.TopLeft.Y, () => Is.EqualTo(glyph().TopLeft.Y).Within(0.5f));
            AddAssert("typing caret: as tall as the glyph row", () => stage.PlayerCaretScreenQuad.Height, () => Is.EqualTo(glyph().Height).Within(0.5f));
            AddAssert("sung caret: top of the glyph row", () => stage.SungCaretScreenQuad.TopLeft.Y, () => Is.EqualTo(glyph().TopLeft.Y).Within(0.5f));
            AddAssert("sung caret: as tall as the glyph row", () => stage.SungCaretScreenQuad.Height, () => Is.EqualTo(glyph().Height).Within(0.5f));

            AddAssert("rail drops 6/42 of the size under the glyphs", () =>
                stage.DisplayAt(0)!.RailScreenQuad.TopLeft.Y - glyph().BottomLeft.Y, () => Is.EqualTo(LyricLineDisplay.SWEEP_RAIL_OFFSET * u()).Within(0.1f));
            AddAssert("rail is 3/42 of the size thick", () =>
                stage.DisplayAt(0)!.RailScreenQuad.Height, () => Is.EqualTo(LyricLineDisplay.SWEEP_RAIL_HEIGHT * u()).Within(0.1f));

            AddAssert("cue bar hangs 6/42 of the size under the glyphs", () =>
                stage.FirstWordCueScreenQuad.TopLeft.Y - glyph().BottomLeft.Y, () => Is.EqualTo(LyricStage.CUE_BAR_OFFSET * u()).Within(0.1f));
            AddAssert("cue bar is 4/42 of the size thick", () =>
                stage.FirstWordCueScreenQuad.Height, () => Is.EqualTo(4 * u()).Within(0.1f));
            AddAssert("cue bar starts at the first glyph", () =>
                stage.FirstWordCueScreenQuad.TopLeft.X, () => Is.EqualTo(glyph().TopLeft.X).Within(0.5f));

            AddAssert("each marker hangs from the glyph row", () =>
                expected_marks.All(i => Math.Abs(stage.DisplayAt(0)!.SyllableMarkerScreenQuad(i).TopLeft.Y - glyph().BottomLeft.Y) < 0.1f));
            AddAssert("each marker is centred on its cell's left edge", () =>
                expected_marks.All(i => Math.Abs(stage.DisplayAt(0)!.SyllableMarkerScreenQuad(i).Centre.X - stage.DisplayAt(0)!.CellScreenQuad(i).TopLeft.X) < 0.5f));
            AddAssert("each marker clears the rail", () =>
                expected_marks.All(i => stage.DisplayAt(0)!.SyllableMarkerScreenQuad(i).BottomLeft.Y < stage.DisplayAt(0)!.RailScreenQuad.TopLeft.Y));

            AddAssert("the pitch is the font floor, not the slider's 40", () =>
                stage.RowPitch, () => Is.EqualTo(Math.Max(min_line_spacing, fontSize * LyricStage.MIN_ROW_PITCH_RATIO)).Within(1e-4));

            AddAssert("the preview row starts below the active row's rail", () =>
                stage.DisplayAt(1)!.CellScreenQuad(0).TopLeft.Y, () => Is.GreaterThan(stage.DisplayAt(0)!.RailScreenQuad.BottomLeft.Y));
            AddAssert("and below its cue bar", () =>
                stage.DisplayAt(1)!.CellScreenQuad(0).TopLeft.Y, () => Is.GreaterThan(stage.FirstWordCueScreenQuad.BottomLeft.Y));
        }

        /// <summary>
        /// A long line at the size is shrunk to fit, never clipped: the auto-shrink guard in
        /// <see cref="LyricLineDisplay"/> bounds its WIDTH, whatever size it was requested at, so
        /// every glyph is inside the playfield.
        /// </summary>
        [TestCase(42f)]
        [TestCase(72f)]
        public void TestALongLineFitsThePlayfield(float fontSize)
        {
            load(fontSize, 21000);

            AddUntilStep("the long line is the player's", () => engine.ActiveLineIndex == 2);

            AddAssert("it is shrunk", () => stage.DisplayAt(2)!.FullOnScreenWidth, () => Is.LessThan(stage.DisplayAt(2)!.FullSweepWidth));

            AddAssert("every glyph is inside the playfield", () =>
            {
                var display = stage.DisplayAt(2)!;
                var field = drawableRuleset.Playfield.ScreenSpaceDrawQuad.AABBFloat;

                return Enumerable.Range(0, display.CellCount).All(i =>
                {
                    var cell = display.CellScreenQuad(i).AABBFloat;
                    return cell.Left >= field.Left && cell.Right <= field.Right;
                });
            });

            AddAssert("and the caret still spans the (shrunk) glyph row", () =>
                stage.PlayerCaretScreenQuad.Height, () => Is.EqualTo(stage.DisplayAt(2)!.CellScreenQuad(0).Height).Within(0.5f));
        }
    }
}
