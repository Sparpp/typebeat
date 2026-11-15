// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
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
    /// The ruleset's own settings, shown first in Settings > Gameplay (backlog 479 folded the separate
    /// "type!beat" section into Gameplay): typing behaviour, lyric appearance, both caret heads,
    /// visual feedback and the timing tint. Several of these settled out of
    /// <see cref="TypeBeatExperimentalSettingsSubsection"/>: first space to skip a word, manual
    /// newlines, the space error dot and the syllable markers, then (backlog 479) the sync metric,
    /// pace bar reveal, judgement-coloured bars, the line push warning edges and the whole timing tint
    /// group. Each move is of the CONTROLS and not of the settings: Realm keys the stored rows by enum
    /// member name, so every value a player already has reads exactly as it did. Only the local
    /// auto-aligner and Demucs stay in Experimental. (LyricOffsetMs and LyricLabPath have no control
    /// here.)
    /// </summary>
    public partial class TypeBeatSettingsSubsection : RulesetSettingsSubsection
    {
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

        /// <summary>
        /// The heading the timing tint's settings sit under, the last group, right after "Visual
        /// feedback". It names the feature, so the rows under it do not repeat it.
        /// </summary>
        public const string TIMING_TINT_HEADING = "Timing tint";

        /// <summary>Caption of the <see cref="TypeBeatRulesetSetting.ShowTimingTint"/> switch.</summary>
        public const string TIMING_TINT_CAPTION = "Tint typed characters";

        public const string TIMING_TINT_MIN_CAPTION = "Minimum strength";

        public const string TIMING_TINT_MAX_CAPTION = "Maximum strength";

        public const string TIMING_TINT_EARLY_COLOUR_CAPTION = "Early colour";

        public const string TIMING_TINT_LATE_COLOUR_CAPTION = "Late colour";

        public const string TIMING_TINT_HINT = "Shade typed characters by when they were typed: orange when too early and blue when too late (both adjustable below), stronger the further outside the Great window. "
                                               + "Great hits stay white. Display only; does not affect grades, scores or judgements.";

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
        /// <param name="config">The ruleset's own settings.</param>
        /// <remarks>
        /// There is no keyboard layout control here any more (backlog 383): typing follows the OS's
        /// own layout through its text input, and shortcuts follow the OS's keyboard map
        /// (<see cref="Game.Input.OsKeyboardLayout"/>), so there is nothing left for a player to choose.
        /// </remarks>
        internal Drawable[] BuildControls(TypeBeatRulesetConfigManager config)
        {
            var lyricFont = config.GetBindable<string>(TypeBeatRulesetSetting.LyricFont);
            var judgementIndicator = config.GetBindable<JudgementIndicatorMode>(TypeBeatRulesetSetting.JudgementIndicator);

            return new Drawable[]
            {
                CreateSubsectionHeader("Typing"),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Space to skip current word",
                    HintText = "Press space in the middle of a word to jump to the next one. Freestyle slots refuse spaces. Backspace can reclaim skipped letters. In Gatekeeper mode, space is rejected like any other wrong key. Applies from the next play.",
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
                    HintText = "Use the lyric font chosen by the mapper for gameplay when available. It takes priority over your typing font; your choice is used when the map has no usable font. Applies from the next play.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.UseMapFonts),
                }),
                new SettingsItemV2(new FormDropdown<string>

                {
                    Caption = "Typing font",
                    HintText = "Font for gameplay when the map has no usable font or Use map fonts is off, and for editor lyrics when Use typing font in Editor is enabled. OpenDyslexic is bundled; you can also pick an installed system font. Editor changes apply immediately; gameplay changes apply from the next play.",
                    Items = buildFontItems(lyricFont.Value),
                    Current = lyricFont,
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Use typing font in Editor",
                    HintText = "Use your selected typing font for editor lyrics. When disabled, use the built-in editor font. Map fonts do not apply in the editor.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.UseTypingFontInEditor),
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
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Approach bars",
                    HintText = "Show pace-coloured bars that rise over the Indicator Lead-In and arrive at the point set by Approach Bar Alignment. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ApproachBars),
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Approach Bar Alignment",
                    HintText = "Sets where approach bars arrive, from the start to the centre of the Great window. Independent of Indicator Alignment. Disabled while Approach bars are off. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.ApproachBarAlignment),
                    KeyboardStep = 1f,
                    LabelFormat = FormatAlignment,
                }),
                new SettingsItemV2(new FormEnumDropdown<JudgementIndicatorMode>
                {
                    Caption = "Judgement Indicator",
                    HintText = "Choose how upcoming syllables or letters show judgement timing. Display only.",
                    Current = judgementIndicator,
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Indicator Alignment",
                    HintText = "Sets when the fade or fill and text pop-in complete, from the start to the centre of the Great window. Approach bars have their own alignment. Disabled when Judgement Indicator is None and Text pop-in is off. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.IndicatorAlignment),
                    KeyboardStep = 1f,
                    LabelFormat = FormatAlignment,
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Indicator Lead-In",
                    HintText = "How many milliseconds the indicator takes to fade or fill, and approach bars take to rise, before reaching their alignment points. Zero makes them appear instantly. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.SyllableFadeInDuration),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0} ms",
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Indicator Brightness",
                    HintText = "How much untyped syllables brighten while they are within the Great timing window. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.SyllableBrightness),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0}%",
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Text pop-in",
                    HintText = "Gently grow upcoming syllables or characters to full size by the indicator's alignment point. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.TextPopIn),
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Pop-in amount",
                    HintText = "How much smaller upcoming text starts before growing to full size. Higher values make the pop stronger; 0% removes the size change. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.TextPopInAmount),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0}%",
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Show syllable markers",
                    HintText = "Show a small triangle at each syllable boundary inside a word. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowSyllableMarkers),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Show backing vocals",
                    HintText = "Pop a map's backing vocals up above the lyric as they are sung. They are never typed. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowBackingVocals),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Show word pace colours",
                    HintText = "Colour each word or subdivision using the selected pace colour mode. Turn this off for a plain underline. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowPaceColours),
                }),
                new SettingsItemV2(new FormDropdown<PaceColourMode>
                {
                    Caption = "Pace colour mode",
                    HintText = "Map-relative compares pace with the map's average WPM: at 100% maximum change, half the average is fully green and 50% above it is fully red. Acceleration-based compares pace with the preceding word or subdivision. Display only.",
                    Items = new[] { PaceColourMode.MapRelative, PaceColourMode.AccelerationBased },
                    Current = config.GetBindable<PaceColourMode>(TypeBeatRulesetSetting.PaceColourMode),
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Pace colour maximum change",
                    HintText = "Sets how strongly pace changes are coloured. Lower values make colours more sensitive. Defaults to 100%. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.PaceColourMaxChange),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0}%",
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Pace colour opacity curve",
                    HintText = "Controls how quickly pace colours gain opacity. Linear changes evenly; exponential keeps small pace differences more transparent and strengthens larger ones. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.PaceColourOpacityCurve),
                    KeyboardStep = 1f,
                    LabelFormat = v => v == 0 ? "Linear" : v == 100 ? "Exponential" : $"{v:0}% exponential",
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = "Pace colour gradient",
                    HintText = "Blends between neighbouring word pace colours. 0% keeps sharp band edges; 100% gives smooth transitions. Approach bars use sharp bands and disable this control at 0%. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.PaceColourGradient),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0}%",
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Show sync metric",
                    HintText = "Show keypress timing beside wpm and shade typed characters by how closely they match the beat. Display only; does not affect grades, scores or judgements.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowSyncMetric),
                }),
                new SettingsItemV2(new FormEnumDropdown<PaceBarRevealMode>
                {
                    Caption = "Pace bar reveal",
                    HintText = "Build the pace bar as Approach bars arrive, or colour in a faint gray bar. Off shows the full pace bar immediately. Requires Approach bars. Display only.",
                    Current = config.GetBindable<PaceBarRevealMode>(TypeBeatRulesetSetting.PaceBarReveal),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Colour typed-letter bars by judgement",
                    HintText = "Colour the underline beneath judged letters blue for Great, green for Ok, orange for Meh, and red for a resolved miss. Recoverable wrong inputs stay neutral until corrected or the line ends. Display only.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.JudgementColouredBars),
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Red screen edges before forced line changes",
                    HintText = "Fade the screen edges to red during the final 1.5 seconds before you are forced onto the next line. Display only; does not affect health, scores or timing.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.LinePushWarningEdges),
                }),
                CreateSubsectionHeader(TIMING_TINT_HEADING),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = TIMING_TINT_CAPTION,
                    HintText = TIMING_TINT_HINT,
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowTimingTint),
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = TIMING_TINT_MIN_CAPTION,
                    HintText = "How strongly a character is tinted when it lands just outside the Great window. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.TimingTintMinStrength),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0}%",
                }),
                new SettingsItemV2(new FormSliderBar<float>
                {
                    Caption = TIMING_TINT_MAX_CAPTION,
                    HintText = "How strongly a character is tinted at the edge of the Meh window and beyond. Set it below the minimum to tint every off-time character the same. Display only.",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.TimingTintMaxStrength),
                    KeyboardStep = 1f,
                    LabelFormat = v => $"{v:0}%",
                }),
                tintColourRow(config, TypeBeatRulesetSetting.TimingTintEarlyColour, TIMING_TINT_EARLY_COLOUR_CAPTION,
                    "The colour a character typed too early shades towards. Type a hex code or click the swatch for a picker; clear the field to go back to the default.",
                    TypeBeatStyle.EarlyTint),
                tintColourRow(config, TypeBeatRulesetSetting.TimingTintLateColour, TIMING_TINT_LATE_COLOUR_CAPTION,
                    "The colour a character typed too late shades towards. Type a hex code or click the swatch for a picker; clear the field to go back to the default.",
                    TypeBeatStyle.LateTint),
            };
        }

        /// <summary>The label both alignment sliders share: 0% opens the Great window, 100% is its centre.</summary>
        internal static LocalisableString FormatAlignment(float value) =>
            value == 0 ? "Great window start" : value == 100 ? "Great window centre" : $"{value:0}% towards centre";

        /// <summary>
        /// A colour row for one of the timing tint's two stored colours. The setting is a
        /// <c>#rrggbb</c> string (see <see cref="TypeBeatRulesetConfigManager.FormatTintColour"/>) and
        /// <see cref="FormColourSwatch"/> edits a <see cref="Colour4"/>, so the row keeps the two in
        /// step both ways; an empty hex commit resets the colour, and through it the setting, to the
        /// default. A <see cref="SettingsItemV2"/> like every other row here, so it lines up with them,
        /// carries the revert-to-default button and is found by the settings search.
        /// </summary>
        private static SettingsItemV2 tintColourRow(TypeBeatRulesetConfigManager config, TypeBeatRulesetSetting setting, string caption, string hint, Colour4 fallback)
        {
            var stored = config.GetBindable<string>(setting);
            var colour = new Bindable<Colour4>(TypeBeatRulesetConfigManager.TintColour(stored.Default, fallback))
            {
                Value = TypeBeatRulesetConfigManager.TintColour(stored.Value, fallback),
            };

            bool syncing = false;

            colour.BindValueChanged(e =>
            {
                if (syncing) return;

                syncing = true;
                stored.Value = TypeBeatRulesetConfigManager.FormatTintColour(e.NewValue);
                syncing = false;
            });

            stored.BindValueChanged(e =>
            {
                if (syncing) return;

                syncing = true;
                colour.Value = TypeBeatRulesetConfigManager.TintColour(e.NewValue, fallback);
                syncing = false;
            });

            return new SettingsItemV2(new FormColourSwatch
            {
                Caption = caption,
                HintText = hint,
                Current = colour,
            });
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
