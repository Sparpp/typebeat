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
    /// Backlog 225: the SYLLABLE MARKERS on screen. "banana" is subtimed into three syllables, so a
    /// tiny triangle is drawn in the gap before each of the two interior boundaries; "cake" beside
    /// it is not subtimed and is drawn exactly as it always was.
    ///
    /// <para>The RULE is pinned by <c>SyllableMarkerTest</c> over
    /// <see cref="TypingLine.SyllableMarkerCells"/>. What is left for here is the wiring that list
    /// cannot see: the setting reaching the display, the mark landing on the character EDGE rather
    /// than inside a cell, and the promise that has to hold for a setting that is ON by default,
    /// which is that turning it either way moves no character at all.</para>
    /// </summary>
    public partial class TestSceneTypeBeatSyllableMarker : OsuTestScene
    {
        // cells: b0 a1 n2 a3 n4 a5 _6 c7 a8 k9 e10; the two boundaries cut "ba|na|na".
        private const string text = "banana cake";

        private static readonly int[] expected_marks = { 2, 4 };

        private DrawableTypeBeatRuleset drawableRuleset = null!;

        protected override Ruleset CreateRuleset() => new TypeBeatRuleset();

        private TypingEngine engine => ((TypeBeatPlayfield)drawableRuleset.Playfield).Engine;
        private LyricStage stage => drawableRuleset.ChildrenOfType<LyricStage>().Single();
        private LyricLineDisplay display => stage.DisplayAt(0)!;

        private TypeBeatRulesetConfigManager config => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;

        private void setMarkers(bool enabled) =>
            AddStep($"syllable markers {(enabled ? "on" : "off")}", () => config.SetValue(TypeBeatRulesetSetting.ShowSyllableMarkers, enabled));

        [SetUpSteps]
        public void SetUpSteps()
        {
            // Written explicitly rather than inherited: the config manager is cached per ShortName
            // across the whole fixture, so a previous test's toggle would otherwise carry over.
            setMarkers(true);

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
                        new TimedUnit
                        {
                            Text = "banana",
                            StartTime = 0,
                            EndTime = 150000,
                            SyllableBoundaries = new[] { 50000d, 100000 },
                        },
                        new TimedUnit { Text = "cake", StartTime = 150000, EndTime = 300000 },
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
                            Granularity = TimingGranularity.Syllable,
                        },
                    },
                };
                beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;

                var playable = CreateWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

                Child = drawableRuleset = (DrawableTypeBeatRuleset)ruleset.CreateDrawableRulesetWith(playable);
            });

            AddUntilStep("first line active", () => engine.ActiveLineIndex == 0);

            // The fixture's own shape, asserted rather than trusted: the subtimed word carries two
            // interior boundaries and the word beside it carries none.
            AddAssert("two marks, inside the first word only", () =>
                engine.Lines[0].SyllableMarkerCells, () => Is.EqualTo(expected_marks));
        }

        /// <summary>The headline: a drawable per mark, drawn, and nowhere else on the line.</summary>
        [Test]
        public void TestTheMarksAreDrawnAtTheirBoundaries()
        {
            AddAssert("a drawable per mark", () => display.SyllableMarkerCount, () => Is.EqualTo(expected_marks.Length));

            AddUntilStep("both marks are drawn", () => expected_marks.All(display.SyllableMarkerVisibleAt));

            AddAssert("and nothing else on the line is", () =>
                Enumerable.Range(0, display.CellCount)
                          .Where(i => !expected_marks.Contains(i))
                          .All(i => !display.SyllableMarkerVisibleAt(i)));
        }

        /// <summary>
        /// A mark sits on the character EDGE, which is the whole placement claim: its X is the left
        /// edge of the cell that opens the new syllable, so it falls in the inter-character gap
        /// rather than under a glyph. Compared against the same left edges the caret is placed from.
        /// </summary>
        [Test]
        public void TestEachMarkSitsInTheGapBeforeItsCell()
        {
            AddUntilStep("laid out", () => display.FullOnScreenWidth > 0);

            AddAssert("each mark is on its cell's left edge", () =>
                expected_marks.All(i => display.SyllableMarkerLocalX(i) == display.PositionOfCell(i).X));

            AddAssert("which is not the middle of a glyph", () =>
                expected_marks.All(i => display.SyllableMarkerLocalX(i) < display.PositionOfCell(i + 1).X));
        }

        /// <summary>
        /// The promise a default-ON adornment has to keep: toggling it moves NO character. The marks
        /// straddle a cell edge rather than sitting inside a cell's own slot, so they are kept out
        /// of the auto-size box outright instead of by being transparent, and this is the pin on
        /// that, taken in BOTH directions because the shipped default means most players will only
        /// ever exercise the off one.
        /// </summary>
        [Test]
        public void TestTogglingTheMarksMovesNoCharacter()
        {
            float width = 0;
            var cellPositions = Array.Empty<osuTK.Vector2>();

            AddUntilStep("both marks are drawn", () => expected_marks.All(display.SyllableMarkerVisibleAt));

            AddStep("measure the line", () =>
            {
                width = display.FullOnScreenWidth;
                cellPositions = Enumerable.Range(0, display.CellCount).Select(display.CellScreenPosition).ToArray();
            });

            setMarkers(false);
            AddUntilStep("the marks go out", () => expected_marks.All(i => !display.SyllableMarkerVisibleAt(i)));
            AddAssert("and nothing moved", () => nothingMoved(width, cellPositions));

            setMarkers(true);
            AddUntilStep("the marks come back", () => expected_marks.All(display.SyllableMarkerVisibleAt));
            AddAssert("and nothing moved", () => nothingMoved(width, cellPositions));
        }

        /// <summary>
        /// A mark may never outlive the characters it sits between. It is a statement about a
        /// boundary BETWEEN two cells, so it is drawn at the darker of their two hiding factors, and
        /// under Flashlight that means a mark on the window's dark edge stays out rather than
        /// announcing a syllable boundary the player is not allowed to see yet.
        /// </summary>
        [Test]
        public void TestFlashlightHidesAMarkOnItsDarkEdge()
        {
            AddUntilStep("both marks are drawn", () => expected_marks.All(display.SyllableMarkerVisibleAt));

            AddStep("hide the whole line", () => display.HideForFlashlight());
            AddUntilStep("every mark goes out with it", () => expected_marks.All(i => !display.SyllableMarkerVisibleAt(i)));

            // Countable slots are the non-space typeable cells, which on "banana" are its own 0..5.
            // Lighting 2..5 leaves the 'a' at cell 1 dark, so the mark at cell 2 straddles the edge.
            AddStep("light the line from its second syllable on", () => display.SetFlashlightWindow(new LineWindow(2, 5, false, false), true));

            AddUntilStep("the mark inside the lit run comes back", () => display.SyllableMarkerVisibleAt(4));
            AddAssert("the mark on the dark edge does not", () => display.SyllableMarkerVisibleAt(2), () => Is.False);
        }

        /// <summary>
        /// The same rule under Recite, where it matters more: the marks alone would spell out the
        /// syllable count of every word the mod is hiding. They stay out until BOTH cells they sit
        /// between have been typed, which is exactly when the boundary stops being a spoiler.
        /// </summary>
        [Test]
        public void TestReciteHidesAMarkUntilBothItsCellsAreTyped()
        {
            AddUntilStep("both marks are drawn", () => expected_marks.All(display.SyllableMarkerVisibleAt));

            AddStep("recite the line", () => display.SetReciteEnabled(true));
            AddUntilStep("every mark goes out with the words", () => expected_marks.All(i => !display.SyllableMarkerVisibleAt(i)));

            AddStep("type into the second syllable", () =>
            {
                for (int i = 0; i <= 2; i++)
                    engine.ProcessKey(engine.Lines[0].Cells[i].Expected, engine.Lines[0].Cells[i].TargetTime);
            });

            AddUntilStep("the mark between two typed cells returns", () => display.SyllableMarkerVisibleAt(2));
            AddAssert("the one still inside hidden text does not", () => display.SyllableMarkerVisibleAt(4), () => Is.False);
        }

        private bool nothingMoved(float width, IReadOnlyList<osuTK.Vector2> cellPositions) =>
            display.FullOnScreenWidth == width
            && Enumerable.Range(0, display.CellCount).All(i => display.CellScreenPosition(i) == cellPositions[i]);
    }
}
