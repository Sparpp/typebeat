// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using typebeat.Game.Graphics.Fonts;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The ruleset's section in Settings > Rulesets: the two monkeytype-style head choices (typing
    /// caret and song playhead, kept adjacent so the pair reads as a pair), the physical keyboard
    /// layout, the typing surface's look, the one gameplay toggle (space to skip a word), and the
    /// local auto-aligner install/enable controls.
    /// (LyricOffsetMs/LyricLabPath surfacing remains deferred to M7.)
    /// </summary>
    public partial class TypeBeatSettingsSubsection : RulesetSettingsSubsection
    {
        // Blank: the enclosing settings section is itself titled "type!beat", so a subsection
        // heading here would just repeat it. CreateHeader is suppressed so no gap is left.
        protected override LocalisableString Header => default;

        protected override Drawable CreateHeader() => Empty();

        [Resolved(CanBeNull = true)]
        private ILocalAlignerManager? alignerManager { get; set; }

        [Resolved(CanBeNull = true)]
        private INotificationOverlay? notifications { get; set; }

        [Resolved(CanBeNull = true)]
        private LyricFontManager? fontManager { get; set; }

        private SettingsButton installButton = null!;

        /// <summary>
        /// The caret shapes the PLAYER's typing caret may wear: every <see cref="CaretStyle"/> except
        /// <see cref="CaretStyle.Highlight"/>, which is a sung-playhead-only choice (it means "draw no
        /// head and light the sung syllable group instead", and the typing caret marks where YOU are,
        /// which no syllable group can stand in for). Public so a test can pin the exclusion against
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
                // member, and CaretStyle.Highlight is not a shape the typing caret can wear (it
                // means "no head, light the sung syllable instead", which is only meaningful for the
                // playhead). Listing the four shapes explicitly is what keeps it off this dropdown.
                new SettingsDropdown<CaretStyle>
                {
                    LabelText = "Typing caret style",
                    TooltipText = "Shape of the head that follows YOUR typing along the lyric line. Cosmetic only: it never changes where a character is judged.",
                    Items = TYPING_CARET_STYLES,
                    Current = config.GetBindable<CaretStyle>(TypeBeatRulesetSetting.CaretStyle),
                },
                // The playhead keeps the full enum, Highlight included.
                new SettingsEnumDropdown<CaretStyle>
                {
                    LabelText = "Song playhead style",
                    TooltipText = "Shape of the second head on the same line: the song's playhead, which follows the VOCALS rather than you. It stays the accent colour and never blinks, so the two are easy to tell apart whatever shapes you pick. Highlight removes the playhead altogether and lights up the syllable being sung instead.",
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
                new SettingsCheckbox
                {
                    LabelText = "Space to skip current word",
                    TooltipText = "Press space in the middle of a word to give up on it and jump to the next one. Everything you had not typed of that word counts as a miss, so one bad character costs a word instead of your whole run. Applies from the next play.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.SpaceSkipsWord),
                },
                new SettingsCheckbox
                {
                    LabelText = "Use local auto-aligner",
                    TooltipText = "When the local aligner is installed, time imported lyrics on this machine (no server queue, nothing uploaded). Turn off to always use the type!beat server instead.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.LocalAlignerEnabled),
                },
                installButton = new SettingsButton
                {
                    Text = alignerManager?.IsInstalled == true ? "Reinstall local auto-aligner" : "Install local auto-aligner (~2 GB)",
                    TooltipText = "One-time download of the AI that times lyrics word-by-word on your own machine, recommended if you have a good GPU. Installs the GPU build automatically when an NVIDIA card is detected.",
                    Action = startInstall,
                },
            };

            if (alignerManager == null)
                installButton.Enabled.Value = false;
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

        private void startInstall()
        {
            if (alignerManager == null)
                return;

            installButton.Enabled.Value = false;

            var notification = new ProgressNotification
            {
                Text = "Installing the local auto-aligner...",
                CompletionText = "Local auto-aligner ready. Your imports now align on this machine.",
                State = ProgressNotificationState.Active,
            };

            notifications?.Post(notification);

            Task.Run(async () =>
            {
                try
                {
                    var result = await alignerManager.InstallAsync(
                        line => notification.Text = line,
                        notification.CancellationToken).ConfigureAwait(false);

                    // Drive the notification to its terminal state directly off the worker thread: the
                    // Text/State setters self-marshal to the update thread (the osu ProgressNotification
                    // idiom), so the Ok -> Completed / failure -> error transition fires regardless of
                    // whether this settings subsection is still alive. Setting State = Completed swaps
                    // the running toast for the CompletionText notification; without this flip the
                    // notification was left on the last script line with a live spinner (looked hung),
                    // since the bootstrap's final step emits no output to overwrite it.
                    if (result.Success)
                        notification.State = ProgressNotificationState.Completed;
                    else
                    {
                        notification.State = ProgressNotificationState.Cancelled;
                        notifications?.Post(new SimpleErrorNotification { Text = $"Aligner install failed: {result.Error}" });
                    }

                    // Only the install button belongs to this subsection, so it stays marshalled here.
                    Schedule(() =>
                    {
                        if (result.Success)
                            installButton.Text = "Reinstall local auto-aligner";

                        installButton.Enabled.Value = true;
                    });
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Local aligner install failed");
                    notification.Text = "Local auto-aligner install failed unexpectedly, see logs.";
                    notification.State = ProgressNotificationState.Cancelled;
                    Schedule(() => installButton.Enabled.Value = true);
                }
            });
        }
    }
}
