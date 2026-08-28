// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Framework.Utils;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Localisation;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Storyboards;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The setup tab's Resources section is where a map's audio file is swapped, and for type!beat
    /// it was invisible in practice: the section was handed to <see cref="SetupScreen"/> with
    /// <see cref="Drawable.RelativeSizeAxes"/> on X, so the screen's
    /// <c>Width = SetupScreen.COLUMN_WIDTH</c> was read as 450 TIMES the column rather than 450
    /// pixels. The section came out ~417000px wide and centred, which put its captions ~208000px
    /// off the left edge and its values the same distance off the right, leaving a blank strip
    /// where the audio picker should be. The controls exist; nobody can find them.
    ///
    /// These asserts pin the geometry rather than the property, so the section stays usable however
    /// it is sized in future.
    /// </summary>
    public partial class TestSceneTypeBeatSetupResources : EditorTestScene
    {
        private const int seeded_video_offset = 250;

        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        // The map is given a background video so the video controls have something to act on;
        // CreateBeatmap alone cannot, since a beatmap carries no storyboard.
        protected override WorkingBeatmap CreateWorkingBeatmap(IBeatmap beatmap, Storyboard? storyboard = null)
            => base.CreateWorkingBeatmap(beatmap, storyboard ?? createVideoStoryboard());

        private static Storyboard createVideoStoryboard()
        {
            var storyboard = new Storyboard();
            storyboard.GetLayer(@"Video").Elements.Add(new StoryboardVideo(StoryboardElementSource.Beatmap, "song.mp4", seeded_video_offset));
            return storyboard;
        }

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Smoke";
            beatmap.BeatmapInfo.Metadata.AudioFile = "audio.mp3";

            var line = new LyricLine
            {
                RawText = "hello world",
                StartTime = 1000,
                EndTime = 3000,
                SingEndTime = 3000,
                Units = new[] { new TimedUnit { Text = "hello world", StartTime = 1000, EndTime = 3000 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 1000, LineIndex = 0, Line = line, Granularity = TimingGranularity.Line });

            var second = new LyricLine
            {
                RawText = "second line",
                StartTime = 3000,
                EndTime = 5000,
                SingEndTime = 5000,
                Units = new[] { new TimedUnit { Text = "second line", StartTime = 3000, EndTime = 5000 } },
            };

            beatmap.HitObjects.Add(new TypeBeatHitObject { StartTime = 3000, LineIndex = 1, Line = second, Granularity = TimingGranularity.Line });
            return beatmap;
        }

        [Test]
        public void TestAudioSwapControlIsOnScreen()
        {
            showSetup();

            AddAssert("resources section is one column wide, like its neighbours", () =>
                Precision.AlmostEquals(section<ResourcesSection>().DrawWidth, SetupScreen.COLUMN_WIDTH, 1f));

            // The neighbouring sections are the reference for "correctly sized": all three are laid
            // out by the same screen and must agree.
            AddAssert("all setup sections agree on width", () =>
                Editor.ChildrenOfType<SetupSection>().All(s => Precision.AlmostEquals(s.DrawWidth, SetupScreen.COLUMN_WIDTH, 1f)));

            AddAssert("audio track chooser is inside the setup screen horizontally", () =>
            {
                var chooser = audioChooser().ScreenSpaceDrawQuad;
                var screen = Editor.ChildrenOfType<SetupScreen>().Single().ScreenSpaceDrawQuad;

                return chooser.TopLeft.X >= screen.TopLeft.X && chooser.TopRight.X <= screen.TopRight.X;
            });
        }

        [Test]
        public void TestAudioSwapControlIsSeededFromTheMapsCurrentAudio()
        {
            showSetup();

            // A user who cannot see WHICH file is loaded cannot tell the control is the audio swap.
            AddAssert("chooser shows the map's current audio file", () =>
                audioChooser().Current.Value?.Name == "audio.mp3");
        }

        [Test]
        public void TestUndoingALaterEditDoesNotUnswapTheAudio()
        {
            // The swap copies the new file into the set and DELETES the old one, so an undo that
            // put the old filename back would leave the map pointing at a file that is gone.
            AddStep("edit a line", () => deleteFirstLine());
            AddStep("swap the audio file", () => EditorBeatmap.Metadata.AudioFile = "audio(1).wav");
            AddStep("edit another line", () => deleteFirstLine());

            AddStep("undo", () => Editor.Undo());
            AddUntilStep("the line edit was undone", () => EditorBeatmap.HitObjects.Count == 1);
            AddAssert("the audio swap was NOT undone", () => EditorBeatmap.Metadata.AudioFile == "audio(1).wav");

            AddStep("undo again", () => Editor.Undo());
            AddUntilStep("back to both lines", () => EditorBeatmap.HitObjects.Count == 2);
            AddAssert("the audio swap still stands", () => EditorBeatmap.Metadata.AudioFile == "audio(1).wav");
        }

        [Test]
        public void TestVideoOffsetControlIsOnScreenAndSeeded()
        {
            showSetup();

            AddAssert("offset box sits with the video picker in Resources", () =>
                section<ResourcesSection>().ChildrenOfType<FormNumberBox>().Any(isVideoOffsetBox));

            AddAssert("offset box is one column wide, like its neighbours", () =>
                Precision.AlmostEquals(offsetBox().DrawWidth, SetupScreen.COLUMN_WIDTH, 1f));

            // A box that does not show the offset the map already carries cannot be trusted to be
            // editing that offset.
            AddAssert("box shows the map's current video offset", () => offsetBox().Current.Value == "250");
            AddAssert("box is live while the map has a video", () => !offsetBox().Current.Disabled);
        }

        [Test]
        public void TestCommittingAnOffsetRetimesTheMapsVideo()
        {
            showSetup();

            commitOffset("-1500");

            // The setup screen mutates the working beatmap's storyboard; the editor saves its own.
            // If those were ever different objects the box would appear to work and save nothing.
            AddUntilStep("the edited storyboard carries the new offset", () => EditorBeatmap.Storyboard.PrimaryVideo!.StartTime == -1500);

            AddAssert("editor and working beatmap share one storyboard", () => ReferenceEquals(EditorBeatmap.Storyboard, Beatmap.Value.Storyboard));
            AddAssert("the video file is untouched", () => EditorBeatmap.Storyboard.PrimaryVideo!.Path == "song.mp4");
            AddAssert("still exactly one video element", () =>
                EditorBeatmap.Storyboard.GetLayer(@"Video").Elements.OfType<StoryboardVideo>().Count() == 1);
        }

        [Test]
        public void TestUnparseableOffsetRestoresTheCommittedValue()
        {
            showSetup();

            commitOffset("-1500");
            AddUntilStep("offset applied", () => EditorBeatmap.Storyboard.PrimaryVideo!.StartTime == -1500);

            // A lone minus is what the box holds mid-typing, and a number this size does not fit the
            // format's int field. Neither may reach the map, and neither may be left standing in the
            // box as though it had. (Letters and a decimal point are not typeable here at all: the
            // box is a whole-number box, which is what keeps a fractional offset, silently fatal to
            // the video element on the next load, out of the file.)
            commitOffset("-");
            AddAssert("a lone minus changed nothing", () => EditorBeatmap.Storyboard.PrimaryVideo!.StartTime == -1500);
            AddUntilStep("the box is restored to what the map carries", () => offsetBox().Current.Value == "-1500");

            commitOffset("99999999999999999999");
            AddAssert("an out-of-range offset changed nothing", () => EditorBeatmap.Storyboard.PrimaryVideo!.StartTime == -1500);
            AddUntilStep("the box is restored again", () => offsetBox().Current.Value == "-1500");
        }

        [Test]
        public void TestEmptyingTheBoxRemovesTheOffset()
        {
            showSetup();

            commitOffset("-1500");
            AddUntilStep("offset applied", () => EditorBeatmap.Storyboard.PrimaryVideo!.StartTime == -1500);

            commitOffset(string.Empty);

            AddUntilStep("an emptied box reads as no offset", () => EditorBeatmap.Storyboard.PrimaryVideo!.StartTime == 0);
            AddUntilStep("and the box shows the map's own value", () => offsetBox().Current.Value == "0");
        }

        [Test]
        public void TestClearingTheVideoDisablesTheOffsetBox()
        {
            showSetup();

            AddAssert("box starts live", () => !offsetBox().Current.Disabled);

            AddStep("clear the video", () => videoChooser().Current.Value = null);

            AddUntilStep("the map has no video", () => EditorBeatmap.Storyboard.PrimaryVideo == null);
            AddAssert("offset box is dead", () => offsetBox().Current.Disabled);
            AddAssert("and shows nothing", () => string.IsNullOrEmpty(offsetBox().Current.Value));
        }

        private void commitOffset(string text)
        {
            AddStep("click into the offset box", () =>
            {
                InputManager.MoveMouseTo(offsetBox());
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("box focused", () => offsetTextBox().HasFocus);
            AddStep($"enter \"{text}\"", () => offsetTextBox().Text = text);
            AddStep("commit", () => InputManager.Key(Key.Enter));
        }

        private void deleteFirstLine()
            => TypeBeatEditorOperations.DeleteLine(EditorBeatmap, EditorBeatmap.HitObjects.OfType<TypeBeatHitObject>().First());

        private void showSetup()
        {
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("setup screen shown", () => Editor.ChildrenOfType<SetupScreen>().Any());
            AddUntilStep("resources section present", () => Editor.ChildrenOfType<ResourcesSection>().Any());
        }

        private T section<T>() where T : SetupSection => Editor.ChildrenOfType<T>().Single();

        private FormBeatmapFileSelector audioChooser()
            => Editor.ChildrenOfType<FormBeatmapFileSelector>().Single(f => f.Caption.Equals(EditorSetupStrings.AudioTrack));

        private FormBeatmapFileSelector videoChooser()
            => Editor.ChildrenOfType<FormBeatmapFileSelector>().Single(f => f.Caption.Equals(EditorSetupStrings.Video));

        // The setup screen carries several number boxes (the type!beat section has its own), so the
        // caption is what identifies this one.
        private static bool isVideoOffsetBox(FormNumberBox box) => box.Caption.ToString() == ResourcesSection.VIDEO_OFFSET_CAPTION;

        private FormNumberBox offsetBox() => Editor.ChildrenOfType<FormNumberBox>().Single(isVideoOffsetBox);

        private OsuTextBox offsetTextBox() => offsetBox().ChildrenOfType<OsuTextBox>().Single();
    }
}
