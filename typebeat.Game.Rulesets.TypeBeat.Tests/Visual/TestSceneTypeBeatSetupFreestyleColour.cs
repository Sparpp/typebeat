// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Tests.Visual;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The setup screen half of backlog 384, in the harness <see cref="TestSceneTypeBeatSetupFonts"/>
    /// uses: the freestyle colour row sits straight under the bundle-font row, an untouched map shows
    /// the default and carries no colour, the hex field and the 2D picker popover both write the map,
    /// picking or clearing back to the default removes it again, and the Resources section no longer
    /// shows the custom sample set rows.
    /// </summary>
    public partial class TestSceneTypeBeatSetupFreestyleColour : EditorTestScene
    {
        private static readonly Colour4 teal = new Colour4((byte)0x12, (byte)0xab, (byte)0xef, (byte)255);

        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Freestyle";
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
            return beatmap;
        }

        [Test]
        public void TestRowSitsDirectlyUnderTheBundleFontRow()
        {
            showSetup();

            AddAssert("the row is present", () => row() != null);
            AddAssert("the next form control after the bundle toggle is the colour row", () =>
            {
                var controls = section().Children.Where(c => c is IFormControl || c is FormColourSwatch).ToList();
                int bundle = controls.IndexOf(controls.OfType<FormCheckBox>().Single(c => c.Caption.ToString() == TypeBeatSetupSection.BUNDLE_FONT_CAPTION));
                return controls[bundle + 1] == row();
            });
            AddAssert("and nothing but the bundle note sits between them", () =>
            {
                var children = section().Children.ToList();
                int bundle = children.FindIndex(c => c is FormCheckBox box && box.Caption.ToString() == TypeBeatSetupSection.BUNDLE_FONT_CAPTION);
                return children.IndexOf(row()) - bundle <= 2;
            });
            AddAssert("drawn below it", () =>
                row().ScreenSpaceDrawQuad.TopLeft.Y > section().ChildrenOfType<FormCheckBox>()
                                                             .Single(c => c.Caption.ToString() == TypeBeatSetupSection.BUNDLE_FONT_CAPTION)
                                                             .ScreenSpaceDrawQuad.BottomLeft.Y);
        }

        [Test]
        public void TestUntouchedMapShowsTheDefaultAndCarriesNothing()
        {
            showSetup();

            AddAssert("map carries no colour", () => EditorBeatmap.FreestyleColour.Value == null);
            AddAssert("row shows the default", () => row().Current.Value == (Colour4)TypeBeatStyle.FreestyleChar);
            AddAssert("hex field spells the default", () => hexIs("#c792ea"));
        }

        [Test]
        public void TestHexRoundTrip()
        {
            showSetup();

            commitHex("12ABEF");
            AddAssert("map carries the typed colour", () => EditorBeatmap.FreestyleColour.Value == teal);
            AddAssert("field re-spells it", () => hexIs("#12abef"));

            commitHex("not a colour");
            AddAssert("garbage leaves the map alone", () => EditorBeatmap.FreestyleColour.Value == teal);
            AddAssert("and the field is put back", () => hexIs("#12abef"));

            commitHex("#C792EA");
            AddAssert("typing the default removes the key", () => EditorBeatmap.FreestyleColour.Value == null);

            commitHex("#12abef");
            commitHex(string.Empty);
            AddAssert("clearing the field resets to no key", () => EditorBeatmap.FreestyleColour.Value == null);
            AddAssert("and shows the default again", () => row().Current.Value == (Colour4)TypeBeatStyle.FreestyleChar);
        }

        [Test]
        public void TestPickerPopoverCommitsToTheMap()
        {
            showSetup();

            AddStep("open the picker", () => row().ShowPicker());
            AddUntilStep("the 2D picker is up", () => picker() != null);
            AddAssert("it opened on the current colour", () => picker()!.Current.Value == (Colour4)TypeBeatStyle.FreestyleChar);

            AddStep("pick teal in the picker", () => picker()!.Current.Value = teal);
            AddAssert("map carries the picked colour", () => EditorBeatmap.FreestyleColour.Value == teal);
            AddAssert("hex field follows", () => hexIs("#12abef"));
        }

        [Test]
        public void TestUndoRestoresTheColour()
        {
            showSetup();

            commitHex("#12abef");
            AddAssert("map carries the colour", () => EditorBeatmap.FreestyleColour.Value == teal);
            AddStep("undo", () => Editor.Undo());
            AddAssert("map carries none again", () => EditorBeatmap.FreestyleColour.Value == null);
            AddAssert("row follows the undo", () => row().Current.Value == (Colour4)TypeBeatStyle.FreestyleChar);
        }

        [Test]
        public void TestSampleSetRowsAreGone()
        {
            showSetup();

            AddUntilStep("resources section present", () => Editor.ChildrenOfType<ResourcesSection>().Any());
            AddAssert("no custom sample set dropdown or tile grid", () =>
            {
                var resources = Editor.ChildrenOfType<ResourcesSection>().Single();
                return !resources.ChildrenOfType<Drawable>().Any(d => d.GetType().Name is "FormSampleSetChooser" or "FormSampleSet")
                       && !resources.ChildrenOfType<FormFieldCaption>().Any(c => c.Caption.ToString() == "Custom sample sets");
            });
        }

        private void commitHex(string value)
        {
            TextBox box() => row().HexBox.ChildrenOfType<TextBox>().Single();

            // Focused through the field's own click handler rather than a mouse click: the row can
            // sit below the setup screen's scroll fold, where a real click would land elsewhere.
            AddStep("focus the hex field", () => row().HexBox.TriggerClick());
            AddUntilStep("hex field focused", () => box().HasFocus);
            AddStep($"type '{value}' and press Enter", () =>
            {
                box().Text = value;
                InputManager.Key(Key.Enter);
            });
        }

        private void showSetup()
        {
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("setup screen shown", () => Editor.ChildrenOfType<SetupScreen>().Any());
            AddUntilStep("type!beat section present", () => Editor.ChildrenOfType<TypeBeatSetupSection>().Any());
        }

        private bool hexIs(string expected) => string.Equals(row().HexBox.Current.Value, expected, System.StringComparison.OrdinalIgnoreCase);

        private TypeBeatSetupSection section() => Editor.ChildrenOfType<TypeBeatSetupSection>().Single();

        private FormColourSwatch row() => section().ChildrenOfType<FormColourSwatch>().Single(r => r.HexBox.Caption.ToString() == TypeBeatSetupSection.FREESTYLE_COLOUR_CAPTION);

        private OsuColourPicker? picker() => Editor.ChildrenOfType<OsuColourPicker>().FirstOrDefault()
                                             ?? this.ChildrenOfType<OsuColourPicker>().FirstOrDefault();
    }
}
