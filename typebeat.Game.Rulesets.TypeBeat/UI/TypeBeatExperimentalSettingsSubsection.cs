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
    /// so this is where an import without [mm:ss.xx] line stamps is sent. The install IS the opt-in:
    /// the separate "use local auto-aligner" switch was hidden by backlog 381 (see
    /// <see cref="TypeBeatRulesetConfigManager.LOCAL_ALIGNER_TOGGLE_SURFACED"/>), leaving the
    /// high-accuracy tier and the install button.
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

        /// <summary>
        /// The aligner's quality tier, stated as the trade a player is actually making: the time an
        /// import takes against how well it is timed. The figures describe aligner version 6's full
        /// tier against its default fast one (eight listening passes against two), measured on the
        /// ranked corpus: +0.55 to +0.94 words in a hundred within 200 ms on stamped lyrics (fully or
        /// sparsely), +2.4 on plain ones, and at six threads about 2 minutes against 30 seconds for a
        /// 4 minute song, both doubling on two. The checkbox sits beside the install button because
        /// it means nothing without an installed aligner. Public so the settings test pins the exact
        /// wording.
        /// </summary>
        public const string HIGH_QUALITY_CAPTION = "High-accuracy alignment (about 4x slower per import)";

        public const string HIGH_QUALITY_HINT = "The aligner listens to the song eight times instead of twice. That times about one more word in a hundred "
                                                + "correctly on stamped lyrics, and about two more when the lyrics have no timestamps at all. On a 6-core CPU "
                                                + "a 4 minute song takes roughly 2 minutes to import instead of 30 seconds; on a 2-core machine, about twice that.";

        /// <summary>
        /// Caption of the hidden <see cref="TypeBeatRulesetSetting.LocalAlignerEnabled"/> switch, kept
        /// for when <see cref="TypeBeatRulesetConfigManager.LOCAL_ALIGNER_TOGGLE_SURFACED"/> brings it
        /// back. Public so the settings test can assert its absence by the same string.
        /// </summary>
        public const string LOCAL_ALIGNER_CAPTION = "Use local auto-aligner";

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
        ///
        /// <para><paramref name="showLocalAlignerToggle"/> defaults to
        /// <see cref="TypeBeatRulesetConfigManager.LOCAL_ALIGNER_TOGGLE_SURFACED"/> (off since backlog
        /// 381: installing the aligner is the opt-in, uninstalling it the opt-out). It is a parameter
        /// rather than a read of the constant so the test can pin that the row, and only the row,
        /// comes back when the flag does.</para>
        /// </summary>
        internal Drawable[] BuildControls(TypeBeatRulesetConfigManager config, bool showLocalAlignerToggle = TypeBeatRulesetConfigManager.LOCAL_ALIGNER_TOGGLE_SURFACED)
        {
            var controls = new List<Drawable>
            {
                CreateSubsectionHeader("Visual feedback"),
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
                CreateSubsectionHeader("Lyric timing"),
            };

            if (showLocalAlignerToggle)
            {
                controls.Add(new SettingsItemV2(new FormCheckBox
                {
                    Caption = LOCAL_ALIGNER_CAPTION,
                    HintText = "Time imported lyrics word by word on this machine when the aligner is installed. Turn off to use imported line timestamps. Nothing is uploaded.",
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.LocalAlignerEnabled),
                }));
            }

            controls.AddRange(new Drawable[]
            {
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = HIGH_QUALITY_CAPTION,
                    HintText = HIGH_QUALITY_HINT,
                    Current = config.GetBindable<bool>(TypeBeatRulesetSetting.LocalAlignerHighQuality),
                }),
                new Container
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Padding = SettingsPanel.CONTENT_PADDING,
                    Child = installButton = new InstallAlignerButton
                    {
                        Caption = InstallButtonText(alignerManager),
                        TooltipText = InstallButtonTooltip(alignerManager),
                        Action = startInstall,
                    },
                },
            });

            // ILocalAlignerManager is registered CanBeNull (headless scenes have no installer), so
            // the button has to be dead rather than throwing when nothing can service the click.
            if (alignerManager == null)
                installButton.Enabled.Value = false;

            return controls.ToArray();
        }

        /// <summary>
        /// What the one button offers: a first install, a REPAIR of a venv whose setup never
        /// completed (backlog 353: it used to read as installed and stayed broken forever), a plain
        /// reinstall, or, when the installed aligner is not the version this build ships (which
        /// keeps automatic alignment off until it is fixed, backlog 410), an update of an older one
        /// or a reinstall of a newer one, naming both versions so a player can see why pressing it
        /// changes anything. Public static so the test can pin the texts without a manager to resolve.
        /// </summary>
        public static string InstallButtonText(ILocalAlignerManager? manager)
        {
            if (manager?.NeedsRepair == true)
                return "Repair local auto-aligner";

            if (manager?.IsInstalled != true)
                return "Install local auto-aligner (~2 GB)";

            var status = manager.VersionStatus;

            if (status != AlignerVersionStatus.Matches)
            {
                return $"{LocalAlignerVersion.ActionVerb(status)} local auto-aligner "
                       + $"(v{LocalAlignerVersion.Display(manager.InstalledVersion)} → v{LocalAlignerVersion.Display(manager.ShippedVersion)})";
            }

            return "Reinstall local auto-aligner";
        }

        /// <summary>
        /// The button's hint, which for a mismatched aligner says that automatic alignment is off
        /// until the button is pressed, not merely that something newer exists. Public static for
        /// the same reason as <see cref="InstallButtonText"/>.
        /// </summary>
        public static string InstallButtonTooltip(ILocalAlignerManager? manager)
        {
            if (manager?.NeedsRepair == true)
                return "The aligner's environment on this machine never finished installing, or predates the check that confirms it did. Repair tests it first and keeps it if it works (seconds); if it does not, it is deleted and downloaded again (~2 GB).";

            switch (manager?.IsInstalled == true ? manager.VersionStatus : AlignerVersionStatus.Matches)
            {
                case AlignerVersionStatus.Older:
                    return "The installed aligner is older than the one this build ships, so automatic alignment is unavailable until you update it. "
                           + "Updating replaces the scripts, clears the old aligner's caches and adds any packages the new aligner needs; the environment already downloaded is kept, so it usually takes under a minute.";

                case AlignerVersionStatus.Newer:
                case AlignerVersionStatus.Different:
                    return "The installed aligner is not the one this build ships, so automatic alignment is unavailable until you reinstall it. "
                           + "Reinstalling puts this build's scripts in place, clears the other aligner's caches and adds any packages they need; the environment already downloaded is kept, so it usually takes under a minute.";
            }

            return "One-time download of the AI that times lyrics word-by-word on your own machine, recommended if you have a good GPU. Installs the GPU build automatically when an NVIDIA card is detected.";
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
                Text = alignerManager.NeedsRepair ? "Repairing the local auto-aligner..." : "Installing the local auto-aligner...",
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
                        // Refreshed on failure too: a failed repair has deleted the broken venv,
                        // so the button now offers a plain install.
                        installButton.Caption = InstallButtonText(alignerManager);
                        installButton.TooltipText = InstallButtonTooltip(alignerManager);
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
