// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Graphics.Cursor;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    public partial class TestSceneTypeBeatGenerateLyrics : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var map = new Beatmap();
            map.BeatmapInfo.Ruleset = ruleset;
            map.Metadata.Artist = "Editor";
            map.Metadata.Title = "Lyrics";
            map.Metadata.AudioFile = "audio.mp3";
            map.Metadata.Language = BeatmapLanguage.Japanese;
            map.HitObjects.Add(new TypeBeatHitObject
            {
                StartTime = 1000,
                LineIndex = 0,
                Line = new LyricLine
                {
                    RawText = "konnichiha sekai",
                    StartTime = 1000,
                    EndTime = 4000,
                    SingEndTime = 3000,
                    Units = new[] { new TimedUnit { Text = "konnichiha sekai", StartTime = 1000, EndTime = 3000 } },
                },
                Granularity = TimingGranularity.Line,
            });
            return map;
        }

        [Test]
        public void ButtonSitsBelowGenerateTimingAndFollowsTheLanguage()
        {
            showSetup();
            AddAssert("directly below Generate timing", () =>
            {
                var children = section().Children.ToList();
                int timing = children.FindIndex(d => d is FormButton button && button.ButtonText.ToString() == "Generate timing");
                return children[timing + 1] == button();
            });
            AddAssert("enabled for Japanese without originals", () => button().Enabled.Value);
            AddStep("select English", () => languagePicker().Current.Value = BeatmapLanguage.English);
            AddUntilStep("disabled for English", () => !button().Enabled.Value);
            AddAssert("hover tooltip explains original scripts", () => ((IHasTooltip)button()).TooltipText.ToString() == TypeBeatSetupSection.GENERATE_LYRICS_TOOLTIP);
            AddStep("scroll disabled button into view", () => Editor.ChildrenOfType<SetupScreen>().Single().ChildrenOfType<OsuScrollContainer>().First().ScrollIntoView(button(), animated: false));
            AddStep("hover the disabled button", () => InputManager.MoveMouseTo(button().ChildrenOfType<FormButton.Button>().Single()));
            AddUntilStep("tooltip appears over the disabled button", () => this.ChildrenOfType<OsuTooltipContainer.OsuTooltip>()
                .Any(t => t.Alpha > 0 && string.Concat(t.ChildrenOfType<SpriteText>().Select(text => text.Text.ToString())).Contains("non-English")));
            AddStep("select Korean", () => languagePicker().Current.Value = BeatmapLanguage.Korean);
            AddUntilStep("enabled for Korean", () => button().Enabled.Value);
            AddStep("undo language change", () => Editor.Undo());
            AddUntilStep("disabled after undo restores English", () => !button().Enabled.Value);
            AddUntilStep("language picker follows undo", () => languagePicker().Current.Value == BeatmapLanguage.English);
        }

        [Test]
        public void GeneratedOriginalsCanBeUndoneInTheEditor()
        {
            showSetup();
            AddStep("clear song metadata", () =>
            {
                EditorBeatmap.Metadata.Artist = EditorBeatmap.Metadata.ArtistUnicode = string.Empty;
                EditorBeatmap.Metadata.Title = EditorBeatmap.Metadata.TitleUnicode = string.Empty;
            });
            AddUntilStep("dictionary tooltip explains offline generation", () => button().TooltipText.ToString().Contains("Works offline"));
            AddStep("click Generate lyrics", () => button().Action?.Invoke());
            AddUntilStep("Japanese originals generated", () => TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[0].Line.Original == "こんにちは 世界");
            AddUntilStep("disabled once originals are present", () => !button().Enabled.Value);
            AddAssert("authored romaji preserved", () => TypeBeatEditorOperations.OrderedLines(EditorBeatmap)[0].Line.RawText == "konnichiha sekai");
            AddAssert("originals available to Polyglot", () => LyricOriginals.HasOriginals(EditorBeatmap));
            AddStep("undo generation", () => Editor.Undo());
            AddUntilStep("enabled again", () => button().Enabled.Value);
            AddAssert("originals removed by undo", () => !LyricOriginals.HasOriginals(EditorBeatmap));
        }

        private void showSetup()
        {
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("setup section loaded", () => Editor.ChildrenOfType<TypeBeatSetupSection>().Any());
        }

        private TypeBeatSetupSection section() => Editor.ChildrenOfType<TypeBeatSetupSection>().Single();
        private FormButton button() => section().ChildrenOfType<FormButton>().Single(b => b.ButtonText.ToString() == "Generate lyrics");
        private FormEnumDropdown<BeatmapLanguage> languagePicker() => Editor.ChildrenOfType<MetadataSection>().Single().ChildrenOfType<FormEnumDropdown<BeatmapLanguage>>().Single();
    }
}
