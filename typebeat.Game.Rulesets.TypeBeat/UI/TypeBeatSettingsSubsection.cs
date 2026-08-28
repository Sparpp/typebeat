// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The ruleset's own settings section (titled "type!beat"): the two monkeytype-style head choices
    /// (typing caret and song playhead, kept adjacent so the pair reads as a pair), the physical
    /// keyboard layout and the typing surface's look. Everything here is settled and cosmetic or
    /// input shaped; the settings still on trial (the two spacebar behaviours and the local
    /// auto-aligner) moved to <see cref="TypeBeatExperimentalSettingsSubsection"/>.
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
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            var config = (TypeBeatRulesetConfigManager)Config;

            var lyricFont = config.GetBindable<string>(TypeBeatRulesetSetting.LyricFont);

            Children = new Drawable[]
            {
                // A plain SettingsDropdown, not a SettingsEnumDropdown: the enum one lists EVERY
                // member, and CaretStyle.None is not a shape the typing caret can wear (it means "no
                // head at all", which is only meaningful for the playhead, since the song is also
                // shown by the lit syllable group). Listing the four shapes explicitly is what keeps
                // it off this dropdown.
                new SettingsDropdown<CaretStyle>
                {
                    LabelText = "Typing caret style",
                    TooltipText = "Shape of the head that follows YOUR typing along the lyric line. Cosmetic only: it never changes where a character is judged.",
                    Items = TYPING_CARET_STYLES,
                    Current = config.GetBindable<CaretStyle>(TypeBeatRulesetSetting.CaretStyle),
                },
                // The playhead keeps the full enum, None included.
                new SettingsEnumDropdown<CaretStyle>
                {
                    LabelText = "Song playhead style",
                    TooltipText = "Shape of the second head on the same line: the song's playhead, which follows the VOCALS rather than you. It stays the accent colour and never blinks, so the two are easy to tell apart whatever shapes you pick. None removes the playhead altogether; the syllable being sung lights up whichever option you choose, so the song stays easy to follow without it.",
                    Current = config.GetBindable<CaretStyle>(TypeBeatRulesetSetting.SungCaretStyle),
                },
                new SettingsEnumDropdown<KeyboardLayout>
                {
                    LabelText = "Keyboard layout",
                    Current = config.GetBindable<KeyboardLayout>(TypeBeatRulesetSetting.KeyboardLayout),
                },
                new SettingsSlider<float>
                {
                    LabelText = "Lyric line spacing",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.LineSpacing),
                    KeyboardStep = 2f,
                },
                new SettingsDropdown<string>
                {
                    LabelText = "Typing font",
                    TooltipText = "Font for the gameplay lyric text only (the rest of the UI is unchanged). OpenDyslexic is bundled; you can also pick any installed system font. Applies from the next play.",
                    Items = buildFontItems(lyricFont.Value),
                    Current = lyricFont,
                },
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
