// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using typebeat.Game.Graphics.UserInterfaceV2;
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
    /// multi-gigabyte install that times imported lyrics word-by-word on this machine. Since the
    /// server-side aligner was retired that install is the ONLY automatic timing path in the game,
    /// so this is where an import without [mm:ss.xx] line stamps is sent.
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

        private InstallAlignerButton installButton = null!;

        public TypeBeatExperimentalSettingsSubsection(Ruleset ruleset)
            : base(ruleset)
        {
            FlowContent.Margin = new MarginPadding();
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
                CreateSubsectionHeader("Visual feedback"),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Show sync metric",
                    HintText = "Show keypress timing beside wpm and shade typed characters by how closely they match the beat. Display only; does not affect grades, scores or judgements.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.ShowSyncMetric),
                }),
                CreateSubsectionHeader("Lyric timing"),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = "Use local auto-aligner",
                    HintText = "Time imported lyrics word by word on this machine when the aligner is installed. Turn off to use imported line timestamps. Nothing is uploaded.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.LocalAlignerEnabled),
                }),
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = SettingsPanel.CONTENT_PADDING,
                    Child = installButton = new InstallAlignerButton
                    {
                        Caption = InstallButtonText(alignerManager),
                        TooltipText = alignerManager?.UpdateAvailable == true
                            ? "Update the local aligner scripts and clear their caches. The downloaded environment is kept."
                            : "Download the local lyric aligner (~2 GB). Uses an NVIDIA GPU automatically when available.",
                        Action = startInstall,
                    },
                },
            };

            // ILocalAlignerManager is registered CanBeNull (headless scenes have no installer), so
            // the button has to be dead rather than throwing when nothing can service the click.
            if (alignerManager == null)
                installButton.Enabled.Value = false;

            return controls;
        }

        /// <summary>
        /// What the one button offers: a first install, a plain reinstall, or, when the build ships
        /// a newer aligner than the one installed, an update naming both versions so a player can
        /// see why pressing it changes anything. Public static so the test can pin the three texts
        /// without a manager to resolve.
        /// </summary>
        public static string InstallButtonText(ILocalAlignerManager? manager)
        {
            if (manager?.IsInstalled != true)
                return "Install local auto-aligner (~2 GB)";

            if (manager.UpdateAvailable)
                return $"Update local auto-aligner (v{manager.InstalledVersion ?? "1"} → v{manager.ShippedVersion})";

            return "Reinstall local auto-aligner";
        }

        private partial class InstallAlignerButton : FormButton, IHasTooltip, IFilterable
        {
            public LocalisableString TooltipText { get; set; }
            public IEnumerable<LocalisableString> FilterTerms => new[] { Caption, TooltipText };
            public bool FilteringActive { get; set; }
            public bool MatchingFilter { set => this.FadeTo(value ? 1 : 0); }
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
                            installButton.Caption = InstallButtonText(alignerManager);

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
