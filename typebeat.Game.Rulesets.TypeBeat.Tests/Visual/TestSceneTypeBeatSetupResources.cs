// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Framework.Utils;
using typebeat.Game.Beatmaps;
using typebeat.Game.Localisation;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Tests.Visual;

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
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

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
    }
}
