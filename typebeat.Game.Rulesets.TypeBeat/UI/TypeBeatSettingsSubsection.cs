// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Logging;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Overlays.Settings;
using typebeat.Game.Rulesets.TypeBeat.Configuration;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The ruleset's section in Settings > Rulesets: the monkeytype-style caret choice, the
    /// physical keyboard layout, and the local auto-aligner install/enable controls.
    /// (LyricOffsetMs/LyricLabPath surfacing remains deferred to M7.)
    /// </summary>
    public partial class TypeBeatSettingsSubsection : RulesetSettingsSubsection
    {
        protected override LocalisableString Header => "type!beat";

        [Resolved(CanBeNull = true)]
        private ILocalAlignerManager? alignerManager { get; set; }

        [Resolved(CanBeNull = true)]
        private INotificationOverlay? notifications { get; set; }

        private SettingsButton installButton = null!;

        public TypeBeatSettingsSubsection(Ruleset ruleset)
            : base(ruleset)
        {
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            var config = (TypeBeatRulesetConfigManager)Config;

            Children = new Drawable[]
            {
                new SettingsEnumDropdown<CaretStyle>
                {
                    LabelText = "Caret style",
                    Current = config.GetBindable<CaretStyle>(TypeBeatRulesetSetting.CaretStyle),
                },
                new SettingsEnumDropdown<KeyboardLayout>
                {
                    LabelText = "Keyboard layout",
                    Current = config.GetBindable<KeyboardLayout>(TypeBeatRulesetSetting.KeyboardLayout),
                },
                new SettingsCheckbox
                {
                    LabelText = "Allow wrong keypresses",
                    TooltipText = "Type wrong characters through (shown red, backspace to fix) instead of rejecting them. The space key stays strict.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.AllowWrongInput),
                },
                new SettingsSlider<float>
                {
                    LabelText = "Lyric line spacing",
                    Current = config.GetBindable<float>(TypeBeatRulesetSetting.LineSpacing),
                    KeyboardStep = 2f,
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
                    TooltipText = "One-time download of the AI that times lyrics word-by-word on your own machine — recommended if you have a good GPU. Installs the GPU build automatically when an NVIDIA card is detected.",
                    Action = startInstall,
                },
            };

            if (alignerManager == null)
                installButton.Enabled.Value = false;
        }

        private void startInstall()
        {
            if (alignerManager == null)
                return;

            installButton.Enabled.Value = false;

            var notification = new ProgressNotification
            {
                Text = "Installing the local auto-aligner...",
                CompletionText = "Local auto-aligner installed — your imports now align on this machine.",
                State = ProgressNotificationState.Active,
            };

            notifications?.Post(notification);

            Task.Run(async () =>
            {
                try
                {
                    var result = await alignerManager.InstallAsync(
                        line => Schedule(() => notification.Text = line),
                        notification.CancellationToken).ConfigureAwait(false);

                    Schedule(() =>
                    {
                        if (result.Success)
                        {
                            notification.State = ProgressNotificationState.Completed;
                            installButton.Text = "Reinstall local auto-aligner";
                        }
                        else
                        {
                            notification.State = ProgressNotificationState.Cancelled;
                            notifications?.Post(new SimpleErrorNotification { Text = $"Aligner install failed: {result.Error}" });
                        }

                        installButton.Enabled.Value = true;
                    });
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Local aligner install failed");
                    Schedule(() =>
                    {
                        notification.State = ProgressNotificationState.Cancelled;
                        installButton.Enabled.Value = true;
                    });
                }
            });
        }
    }
}
