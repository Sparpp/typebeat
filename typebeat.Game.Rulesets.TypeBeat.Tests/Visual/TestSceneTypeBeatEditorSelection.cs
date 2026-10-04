// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osuTK;
using osuTK.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>Keyboard word selection and section copy/paste in compose mode.</summary>
    public partial class TestSceneTypeBeatEditorSelection : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Selection";
            beatmap.BeatmapInfo.Metadata.Title = "Clipboard";

            addLine(beatmap, 0, 1000, 2500,
                ("alpha", 1000, 1300), ("beta", 1400, 1700), ("gamma", 1800, 2100));
            addLine(beatmap, 1, 3000, 4500,
                ("delta", 3000, 3500), ("echo", 3600, 3900));
            addLine(beatmap, 2, 5000, 6500,
                ("old", 5000, 5400), ("town", 5500, 5900));
            addLine(beatmap, 3, 7000, 8500,
                ("last", 7000, 7400), ("thing", 7500, 7900));

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, double start, double end, params (string Text, double Start, double End)[] words)
        {
            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = start,
                LineIndex = index,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = string.Join(' ', words.Select(w => w.Text)),
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = words[^1].End,
                    Units = words.Select(w => new TimedUnit
                    {
                        Text = w.Text,
                        StartTime = w.Start,
                        EndTime = w.End,
                        Source = TimingSource.Explicit,
                        Confidence = 1,
                    }).ToArray(),
                },
            });
        }

        private LyricComposeScreen compose() => Editor.ChildrenOfType<LyricComposeScreen>().Single();

        private LyricEditState state() => compose().EditState;

        private LineListPanel.LineRow[] rows()
            => Editor.ChildrenOfType<LineListPanel.LineRow>().OrderBy(r => r.HitObject.LineIndex).ToArray();

        private TypeBeatHitObject lineAt(int index)
            => TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[index];

        private OsuTextBox textBoxAt(int index)
            => rows()[index].ChildrenOfType<OsuTextBox>().Single();

        private void clickRow(int index, bool shift = false)
        {
            var quad = rows()[index].ScreenSpaceDrawQuad;
            InputManager.MoveMouseTo(quad.TopLeft + new Vector2(17, quad.Height * 0.5f));

            if (shift)
                InputManager.PressKey(Key.ShiftLeft);

            InputManager.Click(MouseButton.Left);

            if (shift)
                InputManager.ReleaseKey(Key.ShiftLeft);
        }

        private void pressControl(Key key)
        {
            InputManager.PressKey(Key.ControlLeft);
            InputManager.Key(key);
            InputManager.ReleaseKey(Key.ControlLeft);
        }

        [Test]
        public void TestSelectAllTargetsWordsUnlessALineTextBoxIsFocused()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddUntilStep("line rows loaded", () => rows().Length == 4);

            AddStep("pause and select first line", () =>
            {
                EditorClock.Stop();
                clickRow(0);
            });
            AddUntilStep("first line active", () => state().ActiveLine.Value == lineAt(0));

            AddStep("press Ctrl+A away from a text box", () => pressControl(Key.A));
            AddUntilStep("all first-line word blocks selected", () => state().SelectedUnitIndices.SetEquals(new[] { 0, 1, 2 }));
            AddAssert("last selected word remains the primary focus", () => state().SelectedUnitIndex.Value == 2);

            AddStep("focus the lyric text box and clear word focus", () =>
            {
                state().ClearUnitSelection();
                InputManager.MoveMouseTo(textBoxAt(0));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("lyric box focused", () => textBoxAt(0).HasFocus);
            AddStep("press Ctrl+A in the lyric box", () => pressControl(Key.A));
            AddUntilStep("the textbox selected its lyric text", () => textBoxAt(0).SelectedText == textBoxAt(0).Text);
            AddAssert("word blocks remain unselected while editing text", () => state().SelectedUnitIndices.Count == 0);
        }

        [Test]
        public void TestSectionCopyPasteAndSingleRowMultiLinePaste()
        {
            AddUntilStep("compose shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddUntilStep("line rows loaded", () => rows().Length == 4);

            AddStep("pause and select the source section", () =>
            {
                EditorClock.Stop();
                clickRow(0);
                clickRow(1, shift: true);
            });
            AddUntilStep("two source lines selected", () => state().MultiSelectedLines.Count == 2);

            AddStep("copy source section", () => pressControl(Key.C));
            AddUntilStep("line copy reached the editor clipboard", () => compose().CanPaste.Value);

            AddStep("paste both copied lines from one starting row", () =>
            {
                clickRow(2);
                pressControl(Key.V);
            });
            AddUntilStep("single-row paste filled the next two lines", () =>
                lineAt(2).Line.RawText == "alpha beta gamma" && lineAt(3).Line.RawText == "delta echo");
            AddAssert("the first line kept relative word timing", () =>
                lineAt(2).Line.Units.Select(u => (u.Text, u.StartTime, u.EndTime)).SequenceEqual(new[]
                {
                    ("alpha", 5000d, 5300d), ("beta", 5400d, 5700d), ("gamma", 5800d, 6100d),
                }));
            AddAssert("the second line kept relative word timing", () =>
                lineAt(3).Line.Units.Select(u => (u.Text, u.StartTime, u.EndTime)).SequenceEqual(new[]
                {
                    ("delta", 7000d, 7500d), ("echo", 7600d, 7900d),
                }));

            AddStep("undo single-row paste", () => Editor.Undo());
            AddUntilStep("destination lines restored", () => lineAt(2).Line.RawText == "old town" && lineAt(3).Line.RawText == "last thing");

            AddStep("select both destination lines and paste again", () =>
            {
                clickRow(2);
                clickRow(3, shift: true);
                pressControl(Key.V);
            });
            AddUntilStep("selected section received both copied lines", () =>
                lineAt(2).Line.RawText == "alpha beta gamma" && lineAt(3).Line.RawText == "delta echo");

            AddStep("undo section paste", () => Editor.Undo());
            AddUntilStep("destination lines restored again", () => lineAt(2).Line.RawText == "old town" && lineAt(3).Line.RawText == "last thing");
            AddStep("try a multi-line paste with too few rows after the anchor", () =>
            {
                clickRow(3);
                pressControl(Key.V);
            });
            AddAssert("refused paste leaves both destination lines intact", () =>
                lineAt(2).Line.RawText == "old town" && lineAt(3).Line.RawText == "last thing");
        }
    }
}
