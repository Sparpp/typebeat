// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Graphics.UserInterfaceV2;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The ruleset's own settings section (titled "type!beat"): typing behaviour,
    /// lyric appearance, both caret heads, and timing feedback. Settled behaviours include
    /// space to skip a word, manual newlines, the space error dot and the syllable markers. Those four
    /// moved here OUT of <see cref="TypeBeatExperimentalSettingsSubsection"/> once they were no longer
    /// on trial, and the move is of the CONTROLS and not of the settings: Realm keys the stored rows
    /// by enum member name, so every value a player already has reads exactly as it did. The settings
    /// still on trial (the sync metric and the local auto-aligner) stay in Experimental.
    /// (LyricOffsetMs/LyricLabPath surfacing remains deferred to M7.)
    /// </summary>
    public partial class TypeBeatSettingsSubsection : RulesetSettingsSubsection
    {
        // Blank: the enclosing settings section is itself titled "type!beat", so a subsection
        // heading here would just repeat it. CreateHeader is suppressed so no gap is left.
        protected override LocalisableString Header => default;

        protected override Drawable CreateHeader() => Empty();

        [Resolved(CanBeNull = true)]
        private LyricFontManager? fontManager { get; set; }

        /// <summary>
        /// The caret shapes the PLAYER's typing caret may wear: every <see cref="CaretStyle"/>
        /// except <see cref="CaretStyle.None"/>, which is a sung-playhead-only choice (it means
        /// "draw no head at all", and a typing caret whose whole job is to mark where YOU are has
        /// nothing left to do once it is invisible). Public so a test can pin the exclusion against
        /// the enum rather than against a copied list.
        /// </summary>
        public static readonly IReadOnlyList<CaretStyle> TYPING_CARET_STYLES = new[]
        {
            CaretStyle.Line, CaretStyle.Block, CaretStyle.Outline, CaretStyle.Underline,
        };

        public TypeBeatSettingsSubsection(Ruleset ruleset)
            : base(ruleset)
        {
            // The first group header takes the place of the suppressed subsection header.
            FlowContent.Margin = new MarginPadding();
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Children = BuildControls((TypeBeatRulesetConfigManager)Config);
        }

        /// <summary>
        /// Builds this subsection's controls against an explicitly supplied config, rather than
        /// reading <see cref="RulesetSettingsSubsection.Config"/> directly, so a headless test can pin
        /// the set of controls - and the order they appear in - without standing up a game host to run
        /// the dependency loader. That is the same seam
        /// <see cref="TypeBeatExperimentalSettingsSubsection.BuildControls"/> offers, and it covers the
        /// typing font too: <see cref="fontManager"/> is resolved CanBeNull, so an absent one simply
        /// contributes no extra faces.
        /// </summary>
        internal Drawable[] BuildControls(TypeBeatRulesetConfigManager config)
        {
            var lyricFont = config.GetBindable<string>(TypeBeatRulesetSetting.LyricFont);

            return new Drawable[]
            {
                CreateSubsectionHeader("Typing"),
                new SettingsItemV2(new FormEnumDropdown<KeyboardLayout>
                {
                    Caption = "Keyboard layout",
                    Current = config.GetBindable<KeyboardLayout>(TypeBeatRulesetSetting.KeyboardLayout),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Space to skip current word",
                    HintText = "Press space in the middle of a word to jump to the next one. Backspace can reclaim the skipped letters. In Gatekeeper mode, space is rejected like any other wrong key. Applies from the next play.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.SpaceSkipsWord),
                }),
                new SettingsItemV2(new FormCheckBox
                {

                    Caption = "Manual newlines",
                    HintText = "After finishing a line, press Space or Enter, or start typing the next line, to move on. You can move early, but the next line stays grey and ignores typing until its entry window opens; an early letter must be pressed again. If you wait, the song moves you on when the push warning ends. With this off, finished lines advance automatically. Enter also skips an unfinished line. This setting has no effect with a pinned caret. Applies from the next play.",

                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ManualNewlines),
                }),

                CreateSubsectionHeader("Lyric text"),
                new SettingsItemV2(new FormCheckBox
                {

                    Caption = "Use map fonts",
                    HintText = "Show the lyric font chosen by the mapper when available. It takes priority over your typing font; your choice is used when the map has no usable font. Applies from the next play.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.UseMapFonts),
                }),
                new SettingsItemV2(new FormDropdown<string>

                {
                    Caption = "Typing font",
                    HintText = "Font for gameplay lyric text when the map has no usable font or Use map fonts is off. OpenDyslexic is bundled; you can also pick an installed system font. Applies from the next play.",
                    Items = buildFontItems(lyricFont.Value),
                    Current = lyricFont,
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Gameplay font size",
                    HintText = "Preferred lyric text size during play. Long lines shrink individually to fit the screen. Applies from the next play.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.LyricFontSize),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0} px",
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Lyric line spacing",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.LineSpacing),
                    KeyboardStep = 2f,
                    LabelFormat = v => $"{v:0} px",
                }),

                CreateSubsectionHeader("Carets"),
                new SettingsItemV2(new FormDropdown<CaretStyle>
                {
                    Caption = "Typing caret style",
                    HintText = "Shape of the head that follows your typing along the lyric line. Cosmetic only: it never changes where a character is judged.",
                    Items = TYPING_CARET_STYLES,
                    Current = config.GetBindable<CaretStyle>(TypeBeatRulesetSetting.CaretStyle),
                }),
                new SettingsItemV2(new FormEnumDropdown<CaretStyle>
                {
                    Caption = "Song playhead style",
                    HintText = "Shape of the head that follows the vocals. None removes the playhead; the sung syllable still lights up.",
                    Current = config.GetBindable<CaretStyle>(TypeBeatRulesetSetting.SungCaretStyle),
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Caret smoothing",
                    HintText = "Time for both the typing caret and song playhead to close half the distance to their next positions. Higher values glide longer; 0 moves both instantly. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.CaretSmoothing),
                    KeyboardStep = 1f,
                    LabelFormat = v => v <= 0 ? "Instant" : $"{v:0} ms",
                }),

                CreateSubsectionHeader("Visual feedback"),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Use space error dot",
                    HintText = "Show a red dot only when you type a wrong character into a space between words. Skipping a word does not create a dot. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.UseSpaceErrorDot),
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Sung syllable brightness",
                    HintText = "How much untyped syllables brighten while they are within the Great timing window. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.SyllableBrightness),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0}%",
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Show syllable markers",
                    HintText = "Show a small triangle at each syllable boundary inside a word, including automatically split syllables. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowSyllableMarkers),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Show word pace colours",
                    HintText = "Colour each word or subdivision by its speed relative to the previous one. Turn this off for a plain underline. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowPaceColours),
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Pace colour maximum change",
                    HintText = "Sets how strongly pace changes are coloured. Lower values make colours more sensitive. Defaults to 75%. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.PaceColourMaxChange),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0}%",
                }),
            };
        }

        /// <summary>
        /// The typing-font dropdown options: the default sentinel first, then the bundled OpenDyslexic
        /// (only when its file is present), then the installed system fonts. The currently stored value
        /// is always included so a previously chosen font that is no longer available still displays
        /// rather than throwing.
        /// </summary>
        private List<string> buildFontItems(string currentValue)
        {
            var items = new List<string> { TypeBeatRulesetConfigManager.LYRIC_FONT_DEFAULT };

            if (fontManager?.IsOpenDyslexicAvailable == true)
                items.Add(LyricFontManager.OPEN_DYSLEXIC);

            if (fontManager != null)
                items.AddRange(fontManager.GetSystemFontFamilies());

            if (!string.IsNullOrEmpty(currentValue) && !items.Contains(currentValue))
                items.Add(currentValue);

            return items;
        }
    }
}
