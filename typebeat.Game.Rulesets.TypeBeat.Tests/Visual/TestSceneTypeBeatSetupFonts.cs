// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Testing;
using typebeat.Game.Beatmaps;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Edit;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Setup;
using typebeat.Game.Tests.Visual;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.Visual
{
    /// <summary>
    /// The editor's lyric font picker (backlog 291), in the same setup-screen harness
    /// <see cref="TestSceneTypeBeatSetupResources"/> uses: the type!beat section instantiates with
    /// the picker, the live preview and the bundle toggle; a fresh map seeds to "no map font" with
    /// the toggle dead; and picking a family drives the metadata and the bundling rule (an
    /// OS-bundled family may never end up with a bundled file).
    /// </summary>
    public partial class TestSceneTypeBeatSetupFonts : EditorTestScene
    {
        protected override Ruleset CreateEditorRuleset() => new TypeBeatRuleset();

        protected override IBeatmap CreateBeatmap(RulesetInfo ruleset)
        {
            var beatmap = new Beatmap { HitObjects = new List<Rulesets.Objects.HitObject>() };
            beatmap.BeatmapInfo.Ruleset = ruleset;
            beatmap.BeatmapInfo.Metadata.Artist = "Editor";
            beatmap.BeatmapInfo.Metadata.Title = "Fonts";
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
        public void TestPickerAndToggleInstantiateSeededToNoMapFont()
        {
            showSetup();

            AddAssert("picker is present", () => fontDropdown() != null);
            AddAssert("picker seeds to the built-in font", () => fontDropdown().Current.Value == TypeBeatSetupSection.LYRIC_FONT_NONE);
            AddAssert("bundle toggle is present and dead while no font is chosen", () =>
                bundleToggle().Current.Disabled && !bundleToggle().Current.Value);
            AddAssert("the map declares no font", () =>
                string.IsNullOrEmpty(EditorBeatmap.Metadata.LyricFont) && string.IsNullOrEmpty(EditorBeatmap.Metadata.LyricFontFile));
        }

        [Test]
        public void TestPickingAnOsBundledFamilyWarnsInsteadOfBundling()
        {
            showSetup();

            AddStep("pick an OS-bundled family", () => fontDropdown().Current.Value = osBundledInstalledFamily());

            AddAssert("the map now declares the family", () => EditorBeatmap.Metadata.LyricFont == osBundledInstalledFamily());
            AddAssert("the bundle toggle defaulted OFF", () => !bundleToggle().Current.Value);
            AddAssert("and no file was bundled", () => string.IsNullOrEmpty(EditorBeatmap.Metadata.LyricFontFile));

            AddStep("force the toggle on anyway", () => bundleToggle().Current.Value = true);
            AddAssert("the bundle is refused and rolled back", () =>
                !bundleToggle().Current.Value && string.IsNullOrEmpty(EditorBeatmap.Metadata.LyricFontFile));
        }

        [Test]
        public void TestClearingTheFontClearsEverything()
        {
            showSetup();

            AddStep("pick an OS-bundled family", () => fontDropdown().Current.Value = osBundledInstalledFamily());
            AddStep("clear it again", () => fontDropdown().Current.Value = TypeBeatSetupSection.LYRIC_FONT_NONE);

            AddAssert("the map declares no font again", () =>
                string.IsNullOrEmpty(EditorBeatmap.Metadata.LyricFont) && string.IsNullOrEmpty(EditorBeatmap.Metadata.LyricFontFile));
            AddAssert("and the toggle is dead again", () => bundleToggle().Current.Disabled);
        }

        [Test]
        public void TestToggleAlwaysAgreesWithTheMetadata()
        {
            showSetup();

            // Whatever family is picked and however its bundling attempt goes (a novel family
            // bundles by default, but its real file may be refused, a .ttc for instance), the
            // toggle may never claim a state the metadata does not hold.
            AddStep("pick any non-OS family if one is installed", () =>
            {
                string? family = fontDropdown().Items.FirstOrDefault(f =>
                    f != TypeBeatSetupSection.LYRIC_FONT_NONE && !OsBundledFontFamilies.Contains(f));

                if (family != null)
                    fontDropdown().Current.Value = family;
            });

            AddAssert("toggle state matches whether a file is bundled", () =>
                bundleToggle().Current.Value == !string.IsNullOrEmpty(EditorBeatmap.Metadata.LyricFontFile));
        }

        /// <summary>
        /// An OS-bundled family that is actually installed on the machine running the test, so it
        /// appears in the picker's items. On any supported OS at least one entry of the table is
        /// a real installed font.
        /// </summary>
        private string osBundledInstalledFamily()
        {
            var items = fontDropdown().Items.ToList();
            return items.FirstOrDefault(f => OsBundledFontFamilies.Contains(f))
                   ?? throw new System.InvalidOperationException($"no OS-bundled family among {items.Count} items: [{string.Join(", ", items.Take(10))}]");
        }

        private void showSetup()
        {
            AddStep("switch to setup", () => Editor.Mode.Value = EditorScreenMode.SongSetup);
            AddUntilStep("setup screen shown", () => Editor.ChildrenOfType<SetupScreen>().Any());
            AddUntilStep("type!beat section present", () => Editor.ChildrenOfType<TypeBeatSetupSection>().Any());
        }

        private FormDropdown<string> fontDropdown()
            => Editor.ChildrenOfType<FormDropdown<string>>().Single(d => d.Caption.ToString() == TypeBeatSetupSection.LYRIC_FONT_CAPTION);

        private FormCheckBox bundleToggle()
            => Editor.ChildrenOfType<FormCheckBox>().Single(c => c.Caption.ToString() == TypeBeatSetupSection.BUNDLE_FONT_CAPTION);
    }
}
