// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osuTK;
using osuTK.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The word paste's UI (backlog 343): one click on a word of ANOTHER line selects that line and
    /// that word, so the paste lands there and not on word zero; a paste with no word selected lands
    /// on the line's matching word; with no match it is refused with the panel's error flash.
    /// </summary>
    public partial class TestSceneWordPaste : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        /// <summary>
        /// Line 0 "eat apple now" [1000..3000], its "apple" 1400..2400 cut at 1700 ("ap|ple"). Line 1
        /// "red apple pie" [3000..5000]: red 3000..3400, apple 3400..4000, pie 4000..4600.
        /// </summary>
        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };

            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Word";
            beatmap.BeatmapInfo.Metadata.Title = "Paste";

            addLine(beatmap, 0, 1000, 3000,
                unit("eat", 1000, 1400),
                new TimedUnit
                {
                    Text = "apple", StartTime = 1400, EndTime = 2400, Source = TimingSource.Explicit, Confidence = 1,
                    SyllableBoundaries = new[] { 1700d }, SyllableSplits = new[] { 2 },
                },
                unit("now", 2500, 2900));
            addLine(beatmap, 1, 3000, 5000, unit("red", 3000, 3400), unit("apple", 3400, 4000), unit("pie", 4000, 4600));

            return beatmap;
        }

        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end, Source = TimingSource.Explicit, Confidence = 1 };

        private static void addLine(Beatmap beatmap, int index, double start, double end, params TimedUnit[] units)
            => beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Granularity = TimingGranularity.Syllable,
                Line = new LyricLine
                {
                    RawText = string.Join(' ', units.Select(u => u.Text)),
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = units[^1].EndTime,
                    Units = units,
                },
            });

        private LyricComposeScreen screen() => Editor.ChildrenOfType<LyricComposeScreen>().Single();

        private LyricEditState state() => screen().EditState;

        private TypeBeatHitObject lineAt(int index) => TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[index];

        private LyricTimeline strip() => Editor.ChildrenOfType<LyricTimeline>().Single();

        /// <summary>Selects line 0's "apple" and copies it.</summary>
        private void copyTheSourceApple()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("park the playhead between the lines", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(3000);
            });
            AddStep("select line 0", () => state().SelectedLine.Value = lineAt(0));
            AddUntilStep("line 0 active", () => state().ActiveLine.Value == lineAt(0));
            AddStep("select its apple and copy", () =>
            {
                state().SelectUnit(1);
                screen().Copy();
            });
        }

        [Test]
        public void TestOneClickOnAnotherLinesWordSelectsItAndThePasteLandsThere()
        {
            copyTheSourceApple();

            AddUntilStep("strip sized", () => strip().IsLoaded && strip().DrawWidth > 0);
            AddAssert("line 1's apple is on screen", () => strip().PositionOf(3700) > 0 && strip().PositionOf(3700) < strip().DrawWidth);

            AddStep("click line 1's apple once", () =>
            {
                InputManager.MoveMouseTo(strip().ToScreenSpace(new Vector2(strip().PositionOf(3700), strip().DrawHeight / 2)));
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("line 1 is active", () => state().ActiveLine.Value == lineAt(1));
            AddUntilStep("and its apple is selected", () => state().SelectedUnitIndex.Value == 1);

            AddStep("paste", () => screen().Paste());

            // 600 of room (up to pie) for the 1000 pattern: scaled by 0.6, the cut 300 in lands 180 in.
            AddAssert("the apple took the cut", () => lineAt(1).Line.Units[1].SyllableBoundaries.SequenceEqual(new[] { 3580d }));
            AddAssert("and the split", () => lineAt(1).Line.Units[1].SyllableSplits.SequenceEqual(new[] { 2 }));
            AddAssert("red was not re-timed", () => lineAt(1).Line.Units[0].StartTime == 3000 && lineAt(1).Line.Units[0].EndTime == 3400);
            AddAssert("pie was not crushed", () => lineAt(1).Line.Units[2].StartTime == 4000 && lineAt(1).Line.Units[2].EndTime == 4600);
        }

        [Test]
        public void TestAPasteWithNoWordSelectedLandsOnTheMatchingWord()
        {
            copyTheSourceApple();

            AddStep("select line 1, no word", () => state().SelectedLine.Value = lineAt(1));
            AddUntilStep("line 1 active, nothing selected", () => state().ActiveLine.Value == lineAt(1) && state().SelectedUnitIndex.Value == -1);

            AddStep("paste", () => screen().Paste());

            AddAssert("the apple took the cut", () => lineAt(1).Line.Units[1].SyllableBoundaries.SequenceEqual(new[] { 3580d }));
            AddAssert("word zero was left alone", () => lineAt(1).Line.Units[0].StartTime == 3000 && lineAt(1).Line.Units[0].EndTime == 3400);
            AddAssert("the pasted word is now selected", () => state().SelectedUnitIndex.Value == 1);
        }

        [Test]
        public void TestAPasteWithNoWordSelectedAndNoMatchIsRefused()
        {
            int refusals = 0;
            string before = string.Empty;

            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("park the playhead", () =>
            {
                EditorClock.Stop();
                EditorClock.Seek(3000);
            });
            AddStep("select line 0", () => state().SelectedLine.Value = lineAt(0));
            AddUntilStep("line 0 active", () => state().ActiveLine.Value == lineAt(0));
            AddStep("copy its \"eat\"", () =>
            {
                state().SelectUnit(0);
                screen().Copy();
            });

            AddStep("select line 1, no word", () => state().SelectedLine.Value = lineAt(1));
            AddUntilStep("line 1 active, nothing selected", () => state().ActiveLine.Value == lineAt(1) && state().SelectedUnitIndex.Value == -1);

            AddStep("paste", () =>
            {
                state().ActionRefused += () => refusals++;
                before = describe(lineAt(1).Line);
                screen().Paste();
            });

            AddAssert("refused", () => refusals == 1);
            AddAssert("line 1 unchanged", () => describe(lineAt(1).Line) == before);
        }

        private static string describe(LyricLine line)
            => string.Join(" ", line.Units.Select(u => $"{u.Text}:{u.StartTime}-{u.EndTime}/{string.Join(",", u.SyllableBoundaries)}"));
    }
}
