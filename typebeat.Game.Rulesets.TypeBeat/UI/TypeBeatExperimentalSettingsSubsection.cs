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
using typebeat.Game.Screens.ImportLyrics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// The ruleset's half of Settings > Experimental: the settings that work but are not settled.
    /// What is left here is the sync metric, put back on screen for anyone who wants it (backlog 251
    /// took it off by default and cut it out of the grade), and the local auto-aligner, an opt-in
    /// multi-gigabyte install that times imported lyrics on this machine instead of on the server.
    ///
    /// <para>The four typing behaviours that used to sit above them - space skipping a word, manual
    /// newlines, the space error dot and the syllable markers - have SETTLED, so their controls now
    /// live in <see cref="TypeBeatSettingsSubsection"/> with the rest of the settled set. The move is
    /// of the CONTROLS and not of the settings: nothing about the bindables behind them changed, and
    /// none of the enum members may be renamed (Realm keys them by member name), so every stored
    /// value still reads exactly as it did.</para>
    /// </summary>
    public partial class TypeBeatExperimentalSettingsSubsection : RulesetSettingsSubsection
    {
        // Blank: the enclosing settings section is itself titled "Experimental", so a subsection
        // heading here would just repeat it. CreateHeader is suppressed so no gap is left.
        protected override LocalisableString Header => default;

        protected override Drawable CreateHeader() => Empty();

        [Resolved(CanBeNull = true)]
        private ILocalAlignerManager? alignerManager { get; set; }

        [Resolved(CanBeNull = true)]
        private INotificationOverlay? notifications { get; set; }

        private SettingsButton installButton = null!;

        public TypeBeatExperimentalSettingsSubsection(Ruleset ruleset)
            : base(ruleset)
        {
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            Children = BuildControls((TypeBeatRulesetConfigManager)Config);
        }

        /// <summary>
        /// Builds this subsection's controls against an explicitly supplied config, rather than
        /// reading <see cref="RulesetSettingsSubsection.Config"/> directly, so a headless test can
        /// pin the set of controls without standing up a game host to run the dependency loader.
        /// </summary>
        internal Drawable[] BuildControls(TypeBeatRulesetConfigManager config)
        {
            var controls = new Drawable[]
            {
                new SettingsCheckbox
                {
                    LabelText = "Show sync metric",
                    TooltipText = "Show how in time your keypresses are: a \"sync\" readout beside wpm during play, and a brightness ramp on each character you type (bright when you hit the beat, dull when you drift). Display only, and off by default: nothing about your grade, score, judgements or submitted play reads it.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowSyncMetric),
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

            // ILocalAlignerManager is registered CanBeNull (headless scenes have no installer), so
            // the button has to be dead rather than throwing when nothing can service the click.
            if (alignerManager == null)
                installButton.Enabled.Value = false;

            return controls;
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
