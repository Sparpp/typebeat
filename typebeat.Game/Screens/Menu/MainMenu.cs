// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Diagnostics;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Threading;
using typebeat.Game.Audio;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Input.Bindings;
using typebeat.Game.IO;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Dialog;
using typebeat.Game.Overlays.Volume;
using typebeat.Game.Rulesets;
using typebeat.Game.Screens.Backgrounds;
using typebeat.Game.Screens.ImportLyrics;
using typebeat.Game.Screens.Select;
using typebeat.Game.Seasonal;
using osuTK;
using osuTK.Graphics;

namespace typebeat.Game.Screens.Menu
{
    public partial class MainMenu : OsuScreen, IHandlePresentBeatmap, IKeyBindingHandler<GlobalAction>, ISamplePlaybackDisabler
    {
        public const float FADE_IN_DURATION = 300;

        public const float FADE_OUT_DURATION = 400;

        public override bool HideOverlaysOnEnter => Buttons == null || Buttons.State == ButtonSystemState.Initial;

        public override bool AllowUserExit => false;

        public override bool AllowExternalScreenChange => true;

        public override bool? AllowGlobalTrackControl => true;

        private MenuSideFlashes sideFlashes;

        protected ButtonSystem Buttons;

        [Resolved]
        private GameHost host { get; set; }

        [Resolved]
        private INotificationOverlay notifications { get; set; }

        [Resolved]
        private MusicController musicController { get; set; }

        [Resolved]
        private Storage storage { get; set; }

        [Resolved(canBeNull: true)]
        private IDialogOverlay dialogOverlay { get; set; }

        // used to stop kiai fountain samples when navigating to other screens
        IBindable<bool> ISamplePlaybackDisabler.SamplePlaybackDisabled => samplePlaybackDisabled;
        private readonly Bindable<bool> samplePlaybackDisabled = new Bindable<bool>();

        protected override BackgroundScreen CreateBackground() => new BackgroundScreenDefault();

        protected override bool PlayExitSound => false;

        private Bindable<double> holdDelay;
        private Bindable<bool> showMobileDisclaimer;

        private HoldToExitGameOverlay holdToExitGameOverlay;

        private bool exitConfirmedViaDialog;
        private bool exitConfirmedViaHoldOrClick;

        private ParallaxContainer buttonsContainer;
        private SongTicker songTicker;
        private Container logoTarget;
        private MenuTipDisplay menuTipDisplay;
        private FillFlowContainer bottomElementsFlow;

        private Sample reappearSampleSwoosh;

        [CanBeNull]
        private IDisposable logoProxy;

        [BackgroundDependencyLoader(true)]
        private void load(SettingsOverlay settings, OsuConfigManager config, SessionStatics statics, AudioManager audio)
        {
            holdDelay = config.GetBindable<double>(OsuSetting.UIHoldActivationDelay);
            showMobileDisclaimer = config.GetBindable<bool>(OsuSetting.ShowMobileDisclaimer);

            if (host.CanExit)
            {
                AddInternal(holdToExitGameOverlay = new HoldToExitGameOverlay
                {
                    Action = () =>
                    {
                        exitConfirmedViaHoldOrClick = holdDelay.Value > 0;
                        this.Exit();
                    }
                });
            }

            AddRangeInternal(new[]
            {
                SeasonalUIConfig.ENABLED ? new MainMenuSeasonalLighting() : Empty(),
                new GlobalScrollAdjustsVolume(),
                buttonsContainer = new ParallaxContainer
                {
                    ParallaxAmount = 0.01f,
                    Children = new Drawable[]
                    {
                        Buttons = new ButtonSystem
                        {
                            OnSolo = loadSongSelect,
                            OnExit = e =>
                            {
                                exitConfirmedViaHoldOrClick = e is MouseEvent;
                                this.Exit();
                            }
                        }
                    }
                },
                logoTarget = new Container { RelativeSizeAxes = Axes.Both, },
                sideFlashes = SeasonalUIConfig.ENABLED ? new SeasonalMenuSideFlashes() : new MenuSideFlashes(),
                songTicker = new SongTicker
                {
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                    Margin = new MarginPadding { Right = 15, Top = 5 }
                },
                // For now, this is too much alongside the seasonal lighting.
                SeasonalUIConfig.ENABLED ? Empty() : new KiaiMenuFountains(),
                bottomElementsFlow = new FillFlowContainer
                {
                    AutoSizeAxes = Axes.Both,
                    Direction = FillDirection.Vertical,
                    Anchor = Anchor.BottomCentre,
                    Origin = Anchor.BottomCentre,
                    Spacing = new Vector2(5),
                    Children = new Drawable[]
                    {
                        menuTipDisplay = new MenuTipDisplay
                        {
                            Anchor = Anchor.TopCentre,
                            Origin = Anchor.TopCentre,
                        }
                    }
                },
                holdToExitGameOverlay?.CreateProxy() ?? Empty()
            });

            float baseDim = SeasonalUIConfig.ENABLED ? 0.84f : 1;

            Buttons.StateChanged += state =>
            {
                switch (state)
                {
                    case ButtonSystemState.Initial:
                    case ButtonSystemState.Exit:
                        ApplyToBackground(b => b.FadeColour(OsuColour.Gray(baseDim), 500, Easing.OutSine));
                        break;

                    default:
                        ApplyToBackground(b => b.FadeColour(OsuColour.Gray(baseDim * 0.8f), 500, Easing.OutSine));
                        break;
                }
            };

            Buttons.OnSettings = () => settings?.ToggleVisibility();
            Buttons.OnImportLyrics = () => this.Push(new ImportLyricsScreen());

            reappearSampleSwoosh = audio.Samples.Get(@"Menu/reappear-swoosh");
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            GetContainingInputManager();
        }

        public void ReturnToOsuLogo() => Buttons.State = ButtonSystemState.Initial;

        public override void OnEntering(ScreenTransitionEvent e)
        {
            base.OnEntering(e);
            Buttons.FadeInFromZero(500);

            if (e.Last is IntroScreen && musicController.TrackLoaded)
            {
                var track = musicController.CurrentTrack;

                // presume the track is the current beatmap's track. not sure how correct this assumption is but it has worked until now.
                if (!track.IsRunning)
                {
                    Beatmap.Value.PrepareTrackForPreview(false);
                    track.Restart();
                }
            }

            if (storage is OsuStorage osuStorage && osuStorage.Error != OsuStorageError.None)
                dialogOverlay?.Push(new StorageErrorDialog(osuStorage, osuStorage.Error));
        }

        [CanBeNull]
        private ScheduledDelegate mobileDisclaimerSchedule;

        protected override void LogoArriving(OsuLogo logo, bool resuming)
        {
            base.LogoArriving(logo, resuming);

            Buttons.SetOsuLogo(logo);

            logo.FadeColour(Color4.White, 100, Easing.OutQuint);
            logo.FadeIn(100, Easing.OutQuint);

            logoProxy = logo.ProxyToContainer(logoTarget);

            if (resuming)
            {
                // A startup intro pushes the menu and stays below it, so an intro exiting back INTO the menu
                // only ever happens for an editor beatdrop demo (IntroBeatdropDemo). Land where a real
                // startup leaves the user, on the centred logo, rather than on the expanded button list a
                // normal resume returns to.
                Buttons.State = resumingFromIntro ? ButtonSystemState.Initial : ButtonSystemState.TopLevel;
                resumingFromIntro = false;

                this.FadeIn(FADE_IN_DURATION, Easing.OutQuint);
                buttonsContainer.MoveTo(new Vector2(0, 0), FADE_IN_DURATION, Easing.OutQuint);

                sideFlashes.Delay(FADE_IN_DURATION).FadeIn(64, Easing.InQuint);
            }
            else
            {
                // copy out old action to avoid accidentally capturing logo.Action in closure, causing a self-reference loop.
                var previousAction = logo.Action;

                // we want to hook into logo.Action to display certain overlays, but also preserve the return value of the old action.
                // therefore pass the old action to displayLogin, so that it can return that value.
                // this ensures that the OsuLogo sample does not play when it is not desired.
                logo.Action = () => onLogoClick(previousAction);
            }
        }

        private bool onLogoClick(Func<bool> originalAction)
        {
            if (showMobileDisclaimer.Value)
            {
                mobileDisclaimerSchedule?.Cancel();
                mobileDisclaimerSchedule = Scheduler.AddDelayed(() =>
                {
                    dialogOverlay.Push(new MobileDisclaimerDialog(() =>
                    {
                        showMobileDisclaimer.Value = false;
                    }));
                }, 500);
            }

            return originalAction.Invoke();
        }

        protected override void LogoSuspending(OsuLogo logo)
        {
            var seq = logo.FadeOut(300, Easing.InSine)
                          .ScaleTo(0.2f, 300, Easing.InSine);

            logoProxy?.Dispose();
            logoProxy = null;

            seq.OnComplete(_ => Buttons.SetOsuLogo(null));
            seq.OnAbort(_ => Buttons.SetOsuLogo(null));
        }

        protected override void LogoExiting(OsuLogo logo)
        {
            base.LogoExiting(logo);

            logoProxy?.Dispose();
            logoProxy = null;
        }

        public override void OnSuspending(ScreenTransitionEvent e)
        {
            base.OnSuspending(e);

            Buttons.State = ButtonSystemState.EnteringMode;

            this.FadeOut(FADE_OUT_DURATION, Easing.InSine);
            buttonsContainer.MoveTo(new Vector2(-800, 0), FADE_OUT_DURATION, Easing.InSine);

            sideFlashes.FadeOut(64, Easing.OutQuint);

            bottomElementsFlow
                .ScaleTo(0.9f, 1000, Easing.OutQuint)
                .FadeOut(500, Easing.OutQuint);

            samplePlaybackDisabled.Value = true;
        }

        /// <summary>
        /// Whether this resume is the tail of an editor beatdrop demo, consumed by the next
        /// <see cref="LogoArriving"/>. See the note there for why an intro screen exiting into the menu
        /// identifies a demo.
        /// </summary>
        private bool resumingFromIntro;

        public override void OnResuming(ScreenTransitionEvent e)
        {
            resumingFromIntro = e.Last is IntroScreen;

            base.OnResuming(e);

            // Ensures any playing `ButtonSystem` samples are stopped when returning to MainMenu (as to not overlap with the 'back' sample)
            Buttons.StopSamplePlayback();

            // The swoosh reads as "you went back"; arriving off an intro is an arrival, and a startup does not play it.
            if (!resumingFromIntro)
                reappearSampleSwoosh?.Play();

            ApplyToBackground(b => (b as BackgroundScreenDefault)?.Next());

            musicController.EnsurePlayingSomething();

            // Cycle tip on resuming
            menuTipDisplay.ShowNextTip();

            bottomElementsFlow
                .ScaleTo(1, 1000, Easing.OutQuint)
                .FadeIn(1000, Easing.OutQuint);

            samplePlaybackDisabled.Value = false;
        }

        public override bool OnExiting(ScreenExitEvent e)
        {
            bool requiresConfirmation =
                // we need to have a dialog overlay to confirm in the first place.
                dialogOverlay != null
                // if the dialog has already displayed and been accepted by the user, we are good.
                && !exitConfirmedViaDialog
                // Only require confirmation if there is either an ongoing operation or the user exited via a non-hold escape press.
                && (notifications.HasOngoingOperations || !exitConfirmedViaHoldOrClick);

            if (requiresConfirmation)
            {
                if (dialogOverlay.CurrentDialog is ConfirmExitDialog exitDialog)
                {
                    if (exitDialog.Buttons.OfType<PopupDialogOkButton>().FirstOrDefault() != null)
                        exitDialog.PerformOkAction();
                    else
                        exitDialog.Flash();
                }
                else
                {
                    dialogOverlay.Push(new ConfirmExitDialog(() =>
                    {
                        exitConfirmedViaDialog = true;
                        this.Exit();
                    }, () =>
                    {
                        holdToExitGameOverlay.Abort();
                        Game.CancelRestartOnExit();
                    }));
                }

                return true;
            }

            Buttons.State = ButtonSystemState.Exit;
            OverlayActivationMode.Value = OverlayActivation.Disabled;

            songTicker.Hide();

            this.FadeOut(3000);

            bottomElementsFlow
                .FadeOut(500, Easing.OutQuint);

            return base.OnExiting(e);
        }

        public void PresentBeatmap(WorkingBeatmap beatmap, RulesetInfo ruleset)
        {
            Logger.Log($"{nameof(MainMenu)} completing {nameof(PresentBeatmap)} with beatmap {beatmap} ruleset {ruleset}");

            Beatmap.Value = beatmap;
            Ruleset.Value = ruleset;

            Schedule(loadSongSelect);
        }

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            if (e.Repeat)
                return false;

            switch (e.Action)
            {
                case GlobalAction.Back:
                    // In the case of a host being able to exit, the back action is handled by ExitConfirmOverlay.
                    Debug.Assert(!host.CanExit);

                    return host.SuspendToBackground();
            }

            return false;
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }

        private void loadSongSelect() => this.Push(new SoloSongSelect());

        private partial class MobileDisclaimerDialog : PopupDialog
        {
            public MobileDisclaimerDialog(Action confirmed)
            {
                HeaderText = ButtonSystemStrings.MobileDisclaimerHeader;
                BodyText = ButtonSystemStrings.MobileDisclaimerBody;

                Icon = FontAwesome.Solid.SmileBeam;

                Buttons = new PopupDialogButton[]
                {
                    new PopupDialogOkButton
                    {
                        Text = ButtonSystemStrings.MobileDisclaimerOkButton,
                        Action = confirmed,
                    },
                };
            }
        }
    }
}
