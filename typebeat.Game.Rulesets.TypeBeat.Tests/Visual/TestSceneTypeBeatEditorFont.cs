// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using osuTK.Input;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Graphics.Sprites;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>Editor lyric text follows the typing font and editor toggle, while ignoring map fonts.</summary>
    public partial class TestSceneTypeBeatEditor
    {
        [TestCase(false)]
        [TestCase(true)]
        public void TestTypingFontAppliesToEditorLyricsLiveAndIgnoresMapFonts(bool disableEditorFont)
        {
            AddUntilStep("compose screen shown", () => Editor.ChildrenOfType<LyricComposeScreen>().Any());
            AddStep("enable typing font with a custom map font present", () =>
            {
                lyricFontConfig().SetValue(TypeBeatRulesetSetting.UseTypingFontInEditor, true);
                lyricFontConfig().SetValue(TypeBeatRulesetSetting.UseMapFonts, true);
                EditorBeatmap.BeatmapInfo.Metadata.LyricFont = LyricFontManager.OPEN_DYSLEXIC;
                EditorBeatmap.BeatmapInfo.Metadata.LyricFontFile = "custom-map-font.ttf";
            });
            AddStep("show the original-script editor", () => TypeBeatEditorOperations.SetLineOriginal(EditorBeatmap, firstLine(), "hello source"));
            AddUntilStep("word script editor shown", () => Editor.ChildrenOfType<WordScriptEditor>().Single().WordColumnCount > 0);
            AddStep("choose the bundled typing font", () => lyricFontConfig().GetBindable<string>(TypeBeatRulesetSetting.LyricFont).Value = LyricFontManager.OPEN_DYSLEXIC);

            AddUntilStep("line input uses typing font", () =>
            {
                var input = Editor.ChildrenOfType<LineListPanel>().Single().ChildrenOfType<OsuTextBox>().FirstOrDefault();
                return input != null && input.FontFamily == LyricFontManager.OPEN_DYSLEXIC
                       && input.ChildrenOfType<OsuSpriteText>().Any(s => s.Font.Family == LyricFontManager.OPEN_DYSLEXIC);
            });

            AddUntilStep("active line preview uses typing font", () =>
                Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<FreestyleTextFlow>().First()
                    .ChildrenOfType<OsuSpriteText>().Any(s => s.Font.Family == LyricFontManager.OPEN_DYSLEXIC));

            AddUntilStep("word script inputs use typing font", () =>
                Editor.ChildrenOfType<WordScriptEditor>().Single().ChildrenOfType<OsuTextBox>().Count() >= 2
                && Editor.ChildrenOfType<WordScriptEditor>().Single().ChildrenOfType<OsuTextBox>()
                    .All(box => box.FontFamily == LyricFontManager.OPEN_DYSLEXIC
                                && box.ChildrenOfType<OsuSpriteText>().Any(s => s.Font.Family == LyricFontManager.OPEN_DYSLEXIC)));

            AddUntilStep("word timeline labels use typing font", () =>
                Editor.ChildrenOfType<LyricTimeline>().Single().ChildrenOfType<OsuSpriteText>()
                    .Any(s => s.Text.ToString() == "hello world" && s.Font.Family == LyricFontManager.OPEN_DYSLEXIC));

            OsuTextBox? focusedScriptBox = null;
            string? scriptTextBeforeFontChange = null;
            AddStep("focus a populated word script input", () =>
            {
                focusedScriptBox = Editor.ChildrenOfType<WordScriptEditor>().Single().ChildrenOfType<OsuTextBox>()
                    .First(box => !string.IsNullOrEmpty(box.Text));
                scriptTextBeforeFontChange = focusedScriptBox.Text;
                InputManager.MoveMouseTo(focusedScriptBox);
                InputManager.Click(MouseButton.Left);
            });
            AddUntilStep("populated word script input focused", () => focusedScriptBox?.HasFocus == true);
            AddStep("switch back to the built-in editor font while editing", () =>
            {
                if (disableEditorFont)
                    lyricFontConfig().SetValue(TypeBeatRulesetSetting.UseTypingFontInEditor, false);
                else
                    lyricFontConfig().SetValue(TypeBeatRulesetSetting.LyricFont, TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT);
            });
            AddUntilStep("same focused input keeps text and updates font", () =>
            {
                if (focusedScriptBox == null)
                    return false;

                var liveBoxes = Editor.ChildrenOfType<WordScriptEditor>().Single().ChildrenOfType<OsuTextBox>();
                string expectedFamily = OsuFont.GetFont(size: focusedScriptBox.FontSize).Family;
                var glyphs = focusedScriptBox.ChildrenOfType<OsuSpriteText>()
                    .Where(sprite => !string.IsNullOrEmpty(sprite.Text.ToString())).ToArray();

                return liveBoxes.Any(box => ReferenceEquals(box, focusedScriptBox))
                       && focusedScriptBox.HasFocus
                       && focusedScriptBox.Text == scriptTextBeforeFontChange
                       && focusedScriptBox.FontFamily == null
                       && glyphs.Length > 0
                       && glyphs.All(sprite => sprite.Font.Family == expectedFamily);
            });
            AddUntilStep("line input ignores the custom map font", () =>
                Editor.ChildrenOfType<LineListPanel>().Single().ChildrenOfType<OsuTextBox>().All(box => box.FontFamily == null));
            AddUntilStep("active preview ignores the custom map font", () =>
                Editor.ChildrenOfType<ActiveLineDetailPanel>().Single().ChildrenOfType<FreestyleTextFlow>().First().FontFamily == null);
            AddUntilStep("word script inputs ignore the custom map font", () =>
                Editor.ChildrenOfType<WordScriptEditor>().Single().ChildrenOfType<OsuTextBox>().All(box => box.FontFamily == null));
            AddUntilStep("timeline ignores the custom map font", () =>
                Editor.ChildrenOfType<LyricTimeline>().Single().ChildrenOfType<OsuSpriteText>()
                    .Any(s => s.Text.ToString() == "hello world" && s.Font.Family == TypeBeatStyle.Mono(16).Family));
            if (disableEditorFont)
            {
                AddAssert("disabling the editor toggle preserves the typing font preference", () =>
                    lyricFontConfig().Get<string>(TypeBeatRulesetSetting.LyricFont) == LyricFontManager.OPEN_DYSLEXIC);
                AddStep("re-enable typing font while editing", () => lyricFontConfig().SetValue(TypeBeatRulesetSetting.UseTypingFontInEditor, true));
                AddUntilStep("selected typing font returns without losing input focus or text", () =>
                    focusedScriptBox?.FontFamily == LyricFontManager.OPEN_DYSLEXIC && focusedScriptBox.HasFocus
                    && focusedScriptBox.Text == scriptTextBeforeFontChange);
            }
            AddStep("restore typing font defaults", () =>
            {
                lyricFontConfig().SetValue(TypeBeatRulesetSetting.UseTypingFontInEditor, true);
                lyricFontConfig().SetValue(TypeBeatRulesetSetting.LyricFont, TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT);
            });
        }

        private TypeBeatRulesetConfigManager lyricFontConfig()
            => (TypeBeatRulesetConfigManager)RulesetConfigs.GetConfigFor(new TypeBeatRuleset())!;
    }
}
