// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osuTK;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The editor's BACKING VOCAL lane (backlog 445): a second track across the top of the word strip,
    /// the main word blocks squeezed into the bottom of it to make room; a block per backing word,
    /// placed by when it is sung and dragged like a word; a draggable anchor below each block for where
    /// it pops up across the line, joined to the block's bottom-centre by a dotted line; and the line
    /// row's backing field that types them in.
    /// </summary>
    public partial class TestSceneTypeBeatEditorBackingVocals : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Backing";

            addLine(beatmap, 0, 1000, 5000, new[] { ("i", 1000.0, 1400.0), ("wanna", 1400.0, 2200.0), ("let", 2200.0, 2800.0), ("my", 2800.0, 3400.0), ("hair", 3400.0, 4600.0) },
                new BackingVocal("down", 3000, 3600), new BackingVocal("down", 3800, 4400));
            addLine(beatmap, 1, 5000, 8000, new[] { ("down", 5000.0, 7000.0) });

            return beatmap;
        }

        private static void addLine(Beatmap beatmap, int index, double start, double end, (string Text, double Start, double End)[] words, params BackingVocal[] backing)
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
                    Units = words.Select(w => new TimedUnit { Text = w.Text, StartTime = w.Start, EndTime = w.End, Source = TimingSource.Explicit }).ToArray(),
                    BackingVocals = backing,
                },
            });
        }

        private TypeBeatHitObject lineAt(int index) => TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[index];

        private LyricEditState state() => Editor.ChildrenOfType<LyricComposeScreen>().Single().EditState;

        private LyricTimeline strip() => Editor.ChildrenOfType<LyricTimeline>().Single();

        private void showLineOne()
        {
            AddUntilStep("strip built", () => Editor.ChildrenOfType<LyricTimeline>().SingleOrDefault() is LyricTimeline s && s.IsLoaded
                                                && s.DrawnBandEndTime(lineAt(0)) != null && s.BackingBlockCount(lineAt(0)) == 2);
            AddStep("select line 1 and bring it into view", () =>
            {
                state().SelectedLine.Value = lineAt(0);
                EditorClock.Seek(3000);
            });
            AddUntilStep("line 1 in view", () => Math.Abs(strip().TimeAt(strip().DrawWidth / 2) - 3000) < 200);

            // The line list sidebar overlays the left of the strip while it is open; out of the way, so
            // a press near a line's start lands on the strip.
            AddStep("collapse the line list", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().LineListSidebar.Expanded.Value = false);
            AddUntilStep("collapsed", () => Editor.ChildrenOfType<LyricComposeScreen>().Single().LineListSidebar.CoveredWidth <= LineListSidebar.TOGGLE_GUTTER + 1);
            AddAssert("line 1's start is on screen", () => strip().PositionOf(1000) > 20);
        }

        [Test]
        public void TestTheLaneSitsAboveTheSqueezedMainTrack()
        {
            showLineOne();

            AddAssert("the word blocks are 80% as tall as before", () =>
            {
                var strip = this.strip().ScreenSpaceDrawQuad;
                var word = this.strip().WordBlockOf(lineAt(0), 1).ScreenSpaceDrawQuad;
                return Math.Abs(word.Height - strip.Height * 0.55f * LyricTimeline.MAIN_TRACK_HEIGHT) < 1;
            });

            AddAssert("and centred in the bottom of the strip", () =>
            {
                var strip = this.strip().ScreenSpaceDrawQuad;
                var word = this.strip().WordBlockOf(lineAt(0), 1).ScreenSpaceDrawQuad;
                float mainTop = strip.TopLeft.Y + strip.Height * (1 - LyricTimeline.MAIN_TRACK_HEIGHT);
                return Math.Abs(word.Centre.Y - (mainTop + strip.Height * LyricTimeline.MAIN_TRACK_HEIGHT / 2)) < 1;
            });

            AddAssert("the backing blocks and anchors sit above them", () =>
            {
                float wordTop = strip().WordBlockOf(lineAt(0), 0).ScreenSpaceDrawQuad.TopLeft.Y;
                return Enumerable.Range(0, 2).All(i => strip().BackingBlockOf(lineAt(0), i).ScreenSpaceDrawQuad.BottomLeft.Y < wordTop
                                                       && strip().BackingAnchorOf(lineAt(0), i).ScreenSpaceDrawQuad.BottomLeft.Y < wordTop);
            });

            AddAssert("each block is placed by when it is sung", () =>
            {
                var block = strip().BackingBlockOf(lineAt(0), 0);
                return Math.Abs(strip().TimeAt(strip().ToLocalSpace(block.ScreenSpaceDrawQuad.TopLeft).X) - 3000) < 5
                       && Math.Abs(strip().TimeAt(strip().ToLocalSpace(block.ScreenSpaceDrawQuad.TopRight).X) - 3600) < 5;
            });

            AddAssert("the anchors spread evenly across the line by default", () =>
            {
                double at(int i) => strip().TimeAt(strip().ToLocalSpace(strip().BackingAnchorOf(lineAt(0), i).ScreenSpaceDrawQuad.Centre).X);
                return Math.Abs(at(0) - 2000) < 5 && Math.Abs(at(1) - 4000) < 5; // a quarter and three quarters of [1000, 5000]
            });

            AddAssert("a dotted line joins each block's bottom-centre to its anchor", () => Enumerable.Range(0, 2).All(i =>
            {
                var (blockEnd, anchorEnd) = strip().BackingConnectorEnds(lineAt(0), i);
                var block = strip().BackingBlockOf(lineAt(0), i).ScreenSpaceDrawQuad;
                var anchor = strip().BackingAnchorOf(lineAt(0), i).ScreenSpaceDrawQuad;
                var bottomCentre = (block.BottomLeft + block.BottomRight) / 2;
                return Vector2.Distance(blockEnd, bottomCentre) < 1.5f
                       && Math.Abs(anchorEnd.X - anchor.Centre.X) < 1.5f
                       && anchorEnd.Y < anchor.Centre.Y && anchorEnd.Y > anchor.TopLeft.Y - 1;
            }));

            AddAssert("the line boundaries run through both tracks", () => strip().BoundaryHandles.All(handle =>
                Math.Abs(handle.ScreenSpaceDrawQuad.Height - strip().ScreenSpaceDrawQuad.Height) < 1));
        }

        [Test]
        public void TestDraggingAnAnchorPlacesItAndDoubleClickPutsItBack()
        {
            showLineOne();

            AddStep("settle on the second anchor", () => InputManager.MoveMouseTo(strip().BackingAnchorOf(lineAt(0), 1)));
            AddStep("press", () => InputManager.PressButton(MouseButton.Left));
            AddStep("start the drag", () => InputManager.MoveMouseTo(strip().BackingAnchorOf(lineAt(0), 1).ScreenSpaceDrawQuad.Centre - new Vector2(40, 0)));
            AddStep("drag it past the line's left end", () =>
            {
                var target = strip().ToScreenSpace(new Vector2(strip().PositionOf(1000) - 30, 0));
                InputManager.MoveMouseTo(new Vector2(target.X, strip().BackingAnchorOf(lineAt(0), 1).ScreenSpaceDrawQuad.Centre.Y));
            });
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));

            AddAssert("the word now pops up at the very start of the line", () => lineAt(0).Line.BackingVocals[1].Anchor == 0);
            AddAssert("its time is unchanged", () => lineAt(0).Line.BackingVocals[1].StartTime == 3800);
            AddAssert("and the other anchor is untouched", () => lineAt(0).Line.BackingVocals[0].Anchor == null);
            AddUntilStep("drawn on the line's boundary", () => Math.Abs(strip().ToLocalSpace(strip().BackingAnchorOf(lineAt(0), 1).ScreenSpaceDrawQuad.Centre).X - strip().PositionOf(1000)) < 1);

            // Sitting on the boundary, it is still the anchor a press there takes, not the boundary.
            AddStep("double-click it", () =>
            {
                InputManager.MoveMouseTo(strip().BackingAnchorOf(lineAt(0), 1));
                InputManager.Click(MouseButton.Left);
                InputManager.Click(MouseButton.Left);
            });

            AddUntilStep("it is back on its default place", () => lineAt(0).Line.BackingVocals[1].Anchor == null);
            AddAssert("and the line's start never moved", () => lineAt(0).Line.StartTime == 1000);
        }

        [Test]
        public void TestDraggingABackingBlockRetimesOnlyItsWord()
        {
            double[] typed = null!;

            showLineOne();
            AddStep("remember the typed words", () => typed = lineAt(0).Line.Units.Select(u => u.StartTime).ToArray());

            AddStep("settle on the first block", () => InputManager.MoveMouseTo(strip().BackingBlockOf(lineAt(0), 0)));
            AddStep("press", () => InputManager.PressButton(MouseButton.Left));
            AddStep("start the drag", () => InputManager.MoveMouseTo(strip().BackingBlockOf(lineAt(0), 0).ScreenSpaceDrawQuad.Centre - new Vector2(40, 0)));
            AddStep("drag it half a second earlier", () =>
            {
                var block = strip().BackingBlockOf(lineAt(0), 0).ScreenSpaceDrawQuad;
                float dx = strip().ToScreenSpace(new Vector2(strip().PositionOf(2500), 0)).X - strip().ToScreenSpace(new Vector2(strip().PositionOf(3000), 0)).X;
                InputManager.MoveMouseTo(new Vector2(block.Centre.X + dx + 40, block.Centre.Y));
            });
            AddStep("release", () => InputManager.ReleaseButton(MouseButton.Left));

            AddAssert("the backing word moved earlier", () => lineAt(0).Line.BackingVocals[0].StartTime < 2900);
            AddAssert("keeping its length", () => Math.Abs(lineAt(0).Line.BackingVocals[0].EndTime - lineAt(0).Line.BackingVocals[0].StartTime - 600) < 1);
            AddAssert("no typed word moved", () => lineAt(0).Line.Units.Select(u => u.StartTime).SequenceEqual(typed));
        }

        [Test]
        public void TestClickingATrackPicksItAndDimsTheOther()
        {
            showLineOne();

            AddAssert("the main track is picked to begin with, the lane dimmed", () =>
                !state().BackingTrackSelected && strip().MainTrackAlpha == 1 && strip().BackingLaneAlpha == LyricTimeline.UNSELECTED_TRACK_ALPHA);

            AddStep("click the empty backing lane", () => InputManager.MoveMouseTo(laneSpaceAt(1500)));
            AddStep("click", () => InputManager.Click(MouseButton.Left));

            AddUntilStep("the backing track is picked", () => state().BackingTrackSelected && state().SelectedBackingIndex.Value == -1);
            AddUntilStep("and the main track is the dimmed one", () =>
                strip().BackingLaneAlpha == 1 && strip().MainTrackAlpha == LyricTimeline.UNSELECTED_TRACK_ALPHA);
            AddAssert("the typed-word actions grey out", () => !panelButton("subdivide (D)").Enabled.Value && !panelButton("insert pause (P)").Enabled.Value);

            AddStep("click a backing block", () =>
            {
                InputManager.MoveMouseTo(strip().BackingBlockOf(lineAt(0), 1));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("that backing word is selected", () => state().SelectedBackingIndex.Value == 1);

            AddStep("click a word block", () =>
            {
                InputManager.MoveMouseTo(strip().WordBlockOf(lineAt(0), 2));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("back on the main track", () => !state().BackingTrackSelected && state().SelectedUnitIndex.Value == 2 && state().SelectedBackingIndex.Value == -1);
            AddUntilStep("with the lane dimmed again", () => strip().BackingLaneAlpha == LyricTimeline.UNSELECTED_TRACK_ALPHA);
        }

        [Test]
        public void TestAddWordAndRemoveWordWorkOnThePickedTrack()
        {
            int words = 0;

            showLineOne();
            AddStep("remember the typed words", () => words = lineAt(0).Line.Units.Count);

            AddStep("pick the backing lane at 1500", () =>
            {
                InputManager.MoveMouseTo(laneSpaceAt(1500));
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("backing track picked", () => state().BackingTrackSelected);

            AddStep("add word", () => clickPanelButton("add word"));
            AddUntilStep("a backing word lands at the playhead", () => lineAt(0).Line.BackingVocals.Count == 3
                                                                 && lineAt(0).Line.BackingVocals[0].Text == TypeBeatEditorOperations.NEW_WORD_TEXT
                                                                 && Math.Abs(lineAt(0).Line.BackingVocals[0].StartTime - EditorClock.CurrentTime) < 1);
            AddAssert("selected, and no typed word was added", () => state().SelectedBackingIndex.Value == 0 && lineAt(0).Line.Units.Count == words);
            AddUntilStep("the strip draws it", () => strip().BackingBlockCount(lineAt(0)) == 3);

            AddStep("remove word", () => clickPanelButton("remove word"));
            AddUntilStep("the selected backing word goes", () => lineAt(0).Line.BackingVocals.Select(w => w.Text).SequenceEqual(new[] { "down", "down" }));
            AddAssert("still no typed word touched", () => lineAt(0).Line.Units.Count == words);

            AddStep("select the second backing word and press Delete", () =>
            {
                state().SelectBackingWord(1);
                InputManager.Key(Key.Delete);
            });
            AddUntilStep("Delete removes it", () => lineAt(0).Line.BackingVocals.Count == 1);
            AddAssert("the line is still there", () => TypeBeatEditorOperations.OrderedLines(EditorBeatmap).Count == 2);
        }

        [Test]
        public void TestTheLineListShowsAndEditsBackingVocalsInBrackets()
        {
            AddUntilStep("strip built", () => Editor.ChildrenOfType<LyricTimeline>().SingleOrDefault()?.IsLoaded == true);

            AddUntilStep("a line with backing vocals ends with them in brackets", () => row(0).TextBox.Text == "i wanna let my hair (down down)");
            AddUntilStep("a line without any shows no brackets", () => row(1).TextBox.Text == "down");

            AddStep("click into line 2's text", () =>
            {
                InputManager.MoveMouseTo(row(1).TextBox);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("focused", () => row(1).TextBox.HasFocus);
            AddStep("type backing vocals after it", () => row(1).TextBox.Text = "down (hey  ho)");
            AddStep("commit", () => InputManager.Key(Key.Enter));

            AddUntilStep("they are line 2's backing vocals", () => lineAt(1).Line.BackingVocals.Select(w => w.Text).SequenceEqual(new[] { "hey", "ho" }));
            AddAssert("and the typed line is untouched", () => lineAt(1).Line.RawText == "down" && lineAt(1).Line.Units.Count == 1);
            AddUntilStep("the row shows them as stored", () => row(1).TextBox.Text == "down (hey ho)");

            AddStep("click into line 1's text", () =>
            {
                InputManager.MoveMouseTo(row(0).TextBox);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("focused", () => row(0).TextBox.HasFocus);
            AddStep("retype the lyric and drop the brackets", () => row(0).TextBox.Text = "i wanna let my hair loose");
            AddStep("commit", () => InputManager.Key(Key.Enter));

            AddUntilStep("the lyric took the edit", () => lineAt(0).Line.RawText == "i wanna let my hair loose");
            AddAssert("and its backing vocals are gone", () => lineAt(0).Line.BackingVocals.Count == 0);
            AddUntilStep("so are the brackets", () => row(0).TextBox.Text == "i wanna let my hair loose");
        }

        private LineListPanel.LineRow row(int index) => Editor.ChildrenOfType<LineListPanel.LineRow>().Single(r => r.HitObject == lineAt(index));

        private typebeat.Game.Graphics.UserInterfaceV2.RoundedButton panelButton(string text)
            => Editor.ChildrenOfType<ActiveLineDetailPanel>().Single()
                     .ChildrenOfType<typebeat.Game.Graphics.UserInterfaceV2.RoundedButton>().Single(b => b.Text.ToString() == text);

        private void clickPanelButton(string text)
        {
            InputManager.MoveMouseTo(panelButton(text));
            InputManager.Click(MouseButton.Left);
        }

        /// <summary>A screen-space point in the backing lane's empty space at <paramref name="time"/>: below the blocks, above the anchors.</summary>
        private Vector2 laneSpaceAt(double time)
        {
            var quad = strip().ScreenSpaceDrawQuad;
            float x = strip().ToScreenSpace(new Vector2(strip().PositionOf(time), 0)).X;
            return new Vector2(x, quad.TopLeft.Y + quad.Height * 0.24f);
        }
    }
}
