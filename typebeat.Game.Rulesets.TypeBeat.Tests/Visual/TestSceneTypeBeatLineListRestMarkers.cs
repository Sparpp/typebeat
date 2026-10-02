// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using osu.Framework.Utils;
using osuTK;
using osuTK.Input;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Objects;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The line list's REST view (backlog 378): a row whose text box is not focused spells the line
    /// as gameplay does and marks each syllable split with gameplay's gap triangle, consuming no
    /// width; focusing the box swaps it to the '|' pipe form, the editing syntax, without that swap
    /// ever being an edit, and with the click's caret re-mapped onto the pipe text.
    /// </summary>
    public partial class TestSceneTypeBeatLineListRestMarkers : EditorTestScene
    {
        private const string pipe_form = "Did I di|sap|point you?";
        private const string rest_form = "Did I disappoint you?";

        // "Did I " is six characters, so "di" ends at 8 and "sap" at 11.
        private static readonly int[] rest_gaps = { 8, 11 };

        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Rest markers";
            beatmap.BeatmapInfo.Metadata.Language = BeatmapLanguage.Japanese;

            beatmap.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Granularity = TimingGranularity.Word,
                Line = new LyricLine
                {
                    RawText = rest_form,
                    StartTime = 1000,
                    EndTime = 4000,
                    SingEndTime = 3800,
                    Original = "がっかりさせた？",
                    Units = new List<TimedUnit>
                    {
                        new TimedUnit { Text = "Did", StartTime = 1000, EndTime = 1300 },
                        new TimedUnit { Text = "I", StartTime = 1400, EndTime = 1600 },
                        new TimedUnit { Text = "disappoint", StartTime = 1700, EndTime = 3000 },
                        new TimedUnit { Text = "you?", StartTime = 3100, EndTime = 3800 },
                    },
                },
            });

            return beatmap;
        }

        public override void SetUpSteps()
        {
            base.SetUpSteps();

            AddStep("markers setting on", () => markerSetting().Value = true);
            AddUntilStep("row loaded", () => row()?.IsLoaded == true);
            AddStep("author di|sap|point", () => TypeBeatEditorOperations.SetLineText(EditorBeatmap, line(), pipe_form));
            AddAssert("line carries the split", () => TypeBeatEditorOperations.PipeDisplayText(line().Line) == pipe_form);
            AddUntilStep("row at rest", () => row().TextBox.Text == rest_form);
        }

        [Test]
        public void TestRestShowsPlainTextAndGapTriangles()
        {
            AddAssert("two marks", () => row().VisibleRestMarkers.Count() == 2);
            AddUntilStep("each mark's axis is its gap", () => marksSitInGaps(rest_gaps));

            // Hung from the glyph row like gameplay's, but allowed to start inside the row's descender
            // zone (LineRow.REST_MARKER_RISE), which is what makes room for a legible mark in a 28 px box.
            AddAssert("marks hang from the glyph row, rising at most into its descender zone", () => row().VisibleRestMarkers.All(m =>
                m.ScreenSpaceDrawQuad.TopLeft.Y >= row().TextFlowDrawable.ScreenSpaceDrawQuad.BottomLeft.Y
                                                   - row().TextFlowDrawable.ScreenSpaceDrawQuad.Height * LineListPanel.LineRow.REST_MARKER_RISE - 0.5f));
            AddAssert("marks stay inside the box", () => row().VisibleRestMarkers.All(m =>
                m.ScreenSpaceDrawQuad.BottomLeft.Y <= row().TextBox.ScreenSpaceDrawQuad.BottomLeft.Y + 0.5f));

            // Legible at the row's size: the row's own fraction, not gameplay's one-pixel sliver, and
            // still the same shallow wedge (the aspect is gameplay's).
            AddAssert("marks are at least three times gameplay's fraction of the row, or the band's full height", () => row().VisibleRestMarkers.All(m =>
            {
                float glyph = row().TextFlowDrawable.DrawHeight;
                float roomBelow = row().TextFlowDrawable.ToLocalSpace(row().TextBox.ScreenSpaceDrawQuad.BottomLeft).Y - glyph;
                float band = roomBelow + glyph * LineListPanel.LineRow.REST_MARKER_RISE;
                float expected = Math.Min(glyph * LineListPanel.LineRow.REST_MARKER_HEIGHT, Math.Max(1f, band - 1f));
                return Precision.AlmostEquals(m.DrawHeight, expected, 0.01f) && m.DrawHeight > glyph * LyricLineDisplay.SYLLABLE_MARKER_HEIGHT * 2f;
            }));
            AddAssert("marks keep gameplay's wedge aspect", () => row().VisibleRestMarkers.All(m =>
                Precision.AlmostEquals(m.DrawWidth, m.DrawHeight * LyricLineDisplay.SYLLABLE_MARKER_ASPECT, 0.01f)));

            // Consuming no layout: the flow holds exactly the plain characters and ends at the last
            // one's right edge, so the marks added nothing to it and pushed nothing apart.
            AddAssert("flow is the plain text alone", () => row().TextFlowDrawable.Children.Count == rest_form.Length);
            AddAssert("flow width is the plain text's width", () =>
            {
                var last = row().CharacterDrawable(rest_form.Length - 1);
                return Precision.AlmostEquals(row().TextFlowDrawable.DrawWidth, last.DrawPosition.X + last.DrawWidth, 0.01f);
            });
        }

        [Test]
        public void TestFocusShowsPipesAndNeverCommits()
        {
            int updates = 0;
            TypeBeatHitObject before = null!;
            LyricLine lineBefore = null!;
            Action<HitObject> onUpdated = _ => updates++;

            AddStep("watch for edits", () =>
            {
                before = line();
                lineBefore = before.Line;
                updates = 0;
                EditorBeatmap.HitObjectUpdated += onUpdated;
            });

            AddStep("click into the box", () =>
            {
                InputManager.MoveMouseTo(row().TextBox);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("box focused", () => row().TextBox.HasFocus);
            AddAssert("pipe form shown", () => row().TextBox.Text == pipe_form);
            AddAssert("no marks while focused", () => !row().VisibleRestMarkers.Any());

            AddStep("blur with Enter, nothing typed", () => InputManager.Key(Key.Enter));
            AddUntilStep("box blurred", () => !row().TextBox.HasFocus);
            AddUntilStep("back at rest", () => row().TextBox.Text == rest_form && row().VisibleRestMarkers.Count() == 2);

            AddAssert("no hit object update", () => updates == 0);
            AddAssert("same hit object and line", () => line() == before && ReferenceEquals(line().Line, lineBefore));
            AddStep("stop watching", () => EditorBeatmap.HitObjectUpdated -= onUpdated);
        }

        [TestCase(9, 10)] // between 's' and 'a': in front of the same 'a', past the first pipe
        [TestCase(8, 8)] // the marked gap itself: in FRONT of its pipe, never past it
        [TestCase(14, 16)] // between 'i' and 'n' of "point": past both pipes
        [TestCase(2, 2)] // before the word: nothing to skip
        public void TestClickCaretRemapsOntoPipeText(int restCaret, int pipeCaret)
        {
            AddStep($"click at rest character {restCaret}", () =>
            {
                // Just right of the character's left edge: the framework's rule puts the caret in
                // front of the first character whose midpoint the pointer has not passed.
                var quad = row().CharacterDrawable(restCaret).ScreenSpaceDrawQuad;
                InputManager.MoveMouseTo(new Vector2(quad.TopLeft.X + 1, quad.Centre.Y));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("box focused", () => row().TextBox.HasFocus);
            AddAssert("pipe form shown", () => row().TextBox.Text == pipe_form);
            AddAssert($"caret at pipe index {pipeCaret}", () => caretOf(row().TextBox) == (pipeCaret, pipeCaret));
            AddStep("blur", () => InputManager.Key(Key.Enter));
            AddUntilStep("back at rest", () => row().TextBox.Text == rest_form);
        }

        [Test]
        public void TestRetypedSplitCommitsAndRestShowsIt()
        {
            AddStep("click into the box", () =>
            {
                InputManager.MoveMouseTo(row().TextBox);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("box focused", () => row().TextBox.HasFocus);
            AddStep("retype the split", () => row().TextBox.Text = "Did I dis|appoint you?");
            AddStep("commit", () => InputManager.Key(Key.Enter));
            AddUntilStep("box blurred", () => !row().TextBox.HasFocus);

            // The commit rule is SetLineText's own; the rest view shows whatever split it stored.
            AddUntilStep("line took the new first split", () => TypeBeatEditorOperations.PipeDisplayText(line().Line).StartsWith("Did I dis|", StringComparison.Ordinal));
            AddUntilStep("rest view is the plain text", () => row().TextBox.Text == rest_form);
            AddUntilStep("marks follow the stored split", () =>
            {
                int[] gaps = LineListPanel.RestGapsOf(TypeBeatEditorOperations.PipeDisplayText(line().Line));
                return gaps.Contains(9) && !gaps.Contains(8) && row().VisibleRestMarkers.Count() == gaps.Length && marksSitInGaps(gaps);
            });
        }

        [Test]
        public void TestMarkerSettingOffHidesMarksButNotPipes()
        {
            AddStep("markers setting off", () => markerSetting().Value = false);
            AddUntilStep("no marks at rest", () => !row().VisibleRestMarkers.Any());
            AddAssert("rest text still plain", () => row().TextBox.Text == rest_form);

            AddStep("click into the box", () =>
            {
                InputManager.MoveMouseTo(row().TextBox);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("box focused", () => row().TextBox.HasFocus);
            AddAssert("pipes shown regardless", () => row().TextBox.Text == pipe_form);
            AddStep("blur", () => InputManager.Key(Key.Enter));
            AddUntilStep("back at rest", () => row().TextBox.Text == rest_form && !row().VisibleRestMarkers.Any());

            AddStep("markers setting on", () => markerSetting().Value = true);
            AddUntilStep("marks back", () => row().VisibleRestMarkers.Count() == 2);
        }

        [Test]
        public void TestOriginalViewUntouched()
        {
            AddStep("original view", () => editState().ShowOriginalLyrics.Value = true);
            AddUntilStep("row shows the original", () => row().TextBox.Text == "がっかりさせた？");
            AddAssert("no marks", () => !row().VisibleRestMarkers.Any());
            AddAssert("caption carries the pipe form", () => row().OriginalCaptionText == $"Romanized: {pipe_form}");

            AddStep("click into the box", () =>
            {
                InputManager.MoveMouseTo(row().TextBox);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("box focused", () => row().TextBox.HasFocus);
            AddAssert("still the original", () => row().TextBox.Text == "がっかりさせた？");
            AddStep("blur", () => InputManager.Key(Key.Enter));
            AddStep("romanized view", () => editState().ShowOriginalLyrics.Value = false);
            AddUntilStep("rest view back", () => row().TextBox.Text == rest_form && row().VisibleRestMarkers.Count() == 2);
        }

        /// <summary>Each visible mark's apex (its top-centre) is on the left edge of its gap's character, in screen space.</summary>
        private bool marksSitInGaps(int[] gaps)
        {
            var marks = row().VisibleRestMarkers.ToArray();

            if (marks.Length != gaps.Length)
                return false;

            for (int k = 0; k < gaps.Length; k++)
            {
                var q = marks[k].ScreenSpaceDrawQuad;
                float apexX = (q.TopLeft.X + q.TopRight.X) / 2;
                float gapX = row().CharacterDrawable(gaps[k]).ScreenSpaceDrawQuad.TopLeft.X;

                if (Math.Abs(apexX - gapX) > 0.5f)
                    return false;
            }

            return true;
        }

        private static (int Start, int End) caretOf(TextBox box)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            int start = (int)typeof(TextBox).GetField("selectionStart", flags)!.GetValue(box)!;
            int end = (int)typeof(TextBox).GetField("selectionEnd", flags)!.GetValue(box)!;
            return (start, end);
        }

        private osu.Framework.Bindables.Bindable<bool> markerSetting() =>
            ((TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!).GetBindable<bool>(TypeBeatRulesetSetting.ShowSyllableMarkers);

        private TypeBeatHitObject line() => EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().Single();

        private LineListPanel.LineRow row() => Editor.ChildrenOfType<LineListPanel.LineRow>().SingleOrDefault()!;

        private LyricEditState editState() => Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState;
    }
}
