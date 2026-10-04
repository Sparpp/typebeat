// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
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
    /// took it off by default and cut it out of the grade), the timing tint that shades typed
    /// characters by which way they missed the beat, and the local auto-aligner, an opt-in
    /// multi-gigabyte install that times imported lyrics word-by-word on this machine. The install IS
    /// the opt-in: an installed aligner runs for every import with automatic alignment on, unless
    /// that import ticks the import screen's own "use server aligner" box (backlog 413, the opt-in
    /// for a player who cannot run the local one). The "use local auto-aligner" switch backlog 381
    /// hid is deleted for good, leaving the high-accuracy tier and the install button.
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

        /// <summary>Caption of the <see cref="TypeBeatRulesetSetting.ShowTimingTint"/> switch. Public so the settings test pins it.</summary>
        public const string TIMING_TINT_CAPTION = "Tint early and late characters";

        public const string TIMING_TINT_MIN_CAPTION = "Early/late tint: minimum strength";

        public const string TIMING_TINT_MAX_CAPTION = "Early/late tint: maximum strength";

        public const string TIMING_TINT_EARLY_COLOUR_CAPTION = "Early/late tint: early colour";

        public const string TIMING_TINT_LATE_COLOUR_CAPTION = "Early/late tint: late colour";

        public const string TIMING_TINT_HINT = "Shade typed characters by when they were typed: orange when too early and blue when too late (both adjustable below), stronger the further outside the Great window. "
                                               + "Great hits stay white. Display only; does not affect grades, scores or judgements.";

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
                CreateSubsectionHeader(LocalAlignerVersion.SETTINGS_HEADING),
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
            };

            // ILocalAlignerManager is registered CanBeNull (headless scenes have no installer), so
            // the button has to be dead rather than throwing when nothing can service the click.
            if (alignerManager == null)
                installButton.Enabled.Value = false;

            return controls.ToArray();
        }

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

            return "One-time download of the AI that times lyrics word-by-word on your own machine, recommended if you have a good GPU. Installs the GPU build automatically when an NVIDIA card is detected. "
                   + "If your machine cannot run it, sign in and tick \"use server aligner\" on the import screen instead.";
        }

        // The tooltip is FormButton's own (it covers the row and its button), so the filter reads it.
        private partial class InstallAlignerButton : FormButton, IFilterable
        {
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
