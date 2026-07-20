// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Audio.Sample;
using osu.Framework.Bindables;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Extensions.LocalisationExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Threading;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Input;
using typebeat.Game.Input.Bindings;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Overlays;
using osuTK;
using osuTK.Graphics;
using osuTK.Input;

namespace typebeat.Game.Screens.Menu
{
    public partial class ButtonSystem : Container, IStateful<ButtonSystemState>, IKeyBindingHandler<GlobalAction>
    {
        public const float BUTTON_WIDTH = 140f;
        public const float WEDGE_WIDTH = 20;

        public event Action<ButtonSystemState>? StateChanged;

        public Action<UIEvent>? OnExit;
        public Action? OnImportLyrics;
        public Action? OnSolo;
        public Action? OnSettings;

        private readonly IBindable<bool> isIdle = new BindableBool();

        private OsuLogo? logo;

        /// <summary>
        /// Assign the <see cref="OsuLogo"/> that this ButtonSystem should manage the position of.
        /// </summary>
        /// <param name="logo">The instance of the logo to be assigned. If null, we are suspending from the screen that uses this ButtonSystem.</param>
        public void SetOsuLogo(OsuLogo? logo)
        {
            this.logo = logo;

            if (this.logo != null)
            {
                this.logo.Action = onOsuLogo;

                // osuLogo.SizeForFlow relies on loading to be complete.
                buttonArea.Flow.Position = new Vector2(WEDGE_WIDTH * 2 - (BUTTON_WIDTH + this.logo.SizeForFlow / 4), 0);

                updateLogoState();
            }
            else
            {
                // We should stop tracking as the facade is now out of scope.
                logoTracking?.Dispose();
                logoTracking = null;
            }
        }

        private readonly ButtonArea buttonArea;

        private readonly MainMenuButton backButton;

        private readonly List<MainMenuButton> buttonsTopLevel = new List<MainMenuButton>();
        private readonly List<MainMenuButton> buttonsPlay = new List<MainMenuButton>();

        private Sample? sampleBackToLogo;
        private Sample? sampleLogoSwoosh;

        private readonly LogoTrackingContainer logoTrackingContainer;

        public bool ReturnToTopOnIdle { get; set; } = true;

        public ButtonSystem()
        {
            RelativeSizeAxes = Axes.Both;

            Child = logoTrackingContainer = new LogoTrackingContainer
            {
                RelativeSizeAxes = Axes.Both,
                Child = buttonArea = new ButtonArea()
            };

            buttonArea.AddRange(new Drawable[]
            {
                new MainMenuButton(ButtonSystemStrings.Settings, string.Empty, OsuIcon.Settings, new Color4(85, 85, 85, 255), (_, _) => OnSettings?.Invoke(), Key.O, Key.S)
                {
                    Padding = new MarginPadding { Right = WEDGE_WIDTH },
                },
                backButton = new MainMenuButton(ButtonSystemStrings.Back, @"back-to-top", OsuIcon.PrevCircle, new Color4(51, 58, 94, 255), (_, _) =>
                {
                    State = ButtonSystemState.TopLevel;
                })
                {
                    Padding = new MarginPadding { Right = WEDGE_WIDTH },
                    VisibleStateMin = ButtonSystemState.Play,
                    VisibleStateMax = ButtonSystemState.Play,
                },
                logoTrackingContainer.LogoFacade.With(d => d.Scale = new Vector2(0.74f))
            });

            buttonArea.Flow.CentreTarget = logoTrackingContainer.LogoFacade;
        }

        [Resolved]
        private OsuGame? game { get; set; }

        [BackgroundDependencyLoader]
        private void load(AudioManager audio, IdleTracker? idleTracker, GameHost host)
        {
            buttonsPlay.Add(new MainMenuButton(ButtonSystemStrings.Solo, @"button-default-select", OsuIcon.Player, new Color4(102, 68, 204, 255), (_, _) => OnSolo?.Invoke(), Key.P)
            {
                Padding = new MarginPadding { Left = WEDGE_WIDTH },
            });
            buttonsPlay.ForEach(b => b.VisibleState = ButtonSystemState.Play);

            buttonsTopLevel.Add(new MainMenuButton(ButtonSystemStrings.Play, @"button-play-select", OsuIcon.Logo, new Color4(102, 68, 204, 255), (_, _) => State = ButtonSystemState.Play, Key.P, Key.M,
                Key.L)
            {
                Padding = new MarginPadding { Left = WEDGE_WIDTH },
            });
            // type!beat: Import snaps to the brand's deep lime (#8db12e — the accent itself is too
            // light behind white text); Exit was osu-pink #ee3399, now the website's coral --bad.
            buttonsTopLevel.Add(new MainMenuButton(ButtonSystemStrings.Import, @"button-default-select", OsuIcon.Beatmap, new Color4(141, 177, 46, 255), (_, _) => OnImportLyrics?.Invoke(), Key.B,
                Key.I));

            if (host.CanExit)
                buttonsTopLevel.Add(new MainMenuButton(ButtonSystemStrings.Exit, string.Empty, OsuIcon.CrossCircle, new Color4(255, 106, 90, 255), (_, e) => OnExit?.Invoke(e), Key.Q));

            buttonArea.AddRange(buttonsPlay);
            buttonArea.AddRange(buttonsTopLevel);

            buttonArea.ForEach(b =>
            {
                if (b is MainMenuButton)
                {
                    b.Origin = Anchor.CentreLeft;
                    b.Anchor = Anchor.CentreLeft;
                }
            });

            isIdle.ValueChanged += idle => updateIdleState(idle.NewValue);

            if (idleTracker != null) isIdle.BindTo(idleTracker.IsIdle);

            sampleBackToLogo = audio.Samples.Get(@"Menu/back-to-logo");
            sampleLogoSwoosh = audio.Samples.Get(@"Menu/osu-logo-swoosh");
        }

        private void updateIdleState(bool isIdle)
        {
            if (!ReturnToTopOnIdle)
                return;

            if (isIdle && State != ButtonSystemState.Exit && State != ButtonSystemState.EnteringMode)
                State = ButtonSystemState.Initial;
        }

        /// <summary>
        /// Triggers the <see cref="logo"/> if the current <see cref="State"/> is <see cref="ButtonSystemState.Initial"/>.
        /// </summary>
        /// <returns><c>true</c> if the <see cref="logo"/> was triggered, <c>false</c> otherwise.</returns>
        private bool triggerInitialOsuLogo()
        {
            if (State == ButtonSystemState.Initial)
            {
                StopSamplePlayback();
                logo?.TriggerClick();
                return true;
            }

            return false;
        }

        protected override bool OnKeyDown(KeyDownEvent e)
        {
            if (e.Repeat || e.ControlPressed || e.ShiftPressed || e.AltPressed || e.SuperPressed)
                return false;

            if (e.Key >= Key.F1 && e.Key <= Key.F35)
                return false;

            if (e.Key >= Key.Mute && e.Key <= Key.TrackNext)
                return false;

            switch (e.Key)
            {
                case Key.Escape:
                    return false;
            }

            if (triggerInitialOsuLogo())
                return true;

            return base.OnKeyDown(e);
        }

        protected override bool OnJoystickPress(JoystickPressEvent e)
        {
            if (triggerInitialOsuLogo())
                return true;

            return base.OnJoystickPress(e);
        }

        protected override bool OnMidiDown(MidiDownEvent e)
        {
            if (triggerInitialOsuLogo())
                return true;

            return base.OnMidiDown(e);
        }

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            if (e.Repeat)
                return false;

            switch (e.Action)
            {
                case GlobalAction.Back:
                    return goBack();

                case GlobalAction.Select:
                    logo?.TriggerClick();
                    return true;

                default:
                    return false;
            }
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }

        private bool goBack()
        {
            switch (State)
            {
                case ButtonSystemState.TopLevel:
                    State = ButtonSystemState.Initial;

                    // Samples are explicitly played here in response to user interaction and not when transitioning due to idle.
                    StopSamplePlayback();
                    sampleBackToLogo?.Play();

                    return true;

                case ButtonSystemState.Play:
                    StopSamplePlayback();
                    backButton.TriggerClick();
                    return true;

                default:
                    return false;
            }
        }

        public void StopSamplePlayback()
        {
            buttonsPlay.ForEach(button => button.StopSamplePlayback());
            buttonsTopLevel.ForEach(button => button.StopSamplePlayback());
            logo?.StopSamplePlayback();
        }

        private bool onOsuLogo()
        {
            switch (state)
            {
                default:
                    return false;

                case ButtonSystemState.Initial:
                    State = ButtonSystemState.TopLevel;
                    return true;

                case ButtonSystemState.TopLevel:
                    buttonsTopLevel.First().TriggerClick();
                    return false;

                case ButtonSystemState.Play:
                    buttonsPlay.First().TriggerClick();
                    return false;
            }
        }

        private ButtonSystemState state = ButtonSystemState.Initial;

        public override bool HandleNonPositionalInput => state != ButtonSystemState.Exit;
        public override bool HandlePositionalInput => state != ButtonSystemState.Exit;

        public ButtonSystemState State
        {
            get => state;

            set
            {
                if (state == value) return;

                ButtonSystemState lastState = state;
                state = value;

                updateLogoState(lastState);

                Logger.Log($"{nameof(ButtonSystem)}'s state changed from {lastState} to {state}");

                buttonArea.FinishTransforms(true);

                using (buttonArea.BeginDelayedSequence(lastState == ButtonSystemState.Initial ? 150 : 0))
                {
                    buttonArea.ButtonSystemState = state;

                    foreach (var b in buttonArea.OfType<MainMenuButton>())
                        b.ButtonSystemState = state;
                }

                StateChanged?.Invoke(State);
            }
        }

        private ScheduledDelegate? logoDelayedAction;
        private IDisposable? logoTracking;

        private void updateLogoState(ButtonSystemState lastState = ButtonSystemState.Initial)
        {
            if (logo == null) return;

            switch (state)
            {
                case ButtonSystemState.Exit:
                case ButtonSystemState.Initial:
                    logoDelayedAction?.Cancel();
                    logoDelayedAction = Scheduler.AddDelayed(() =>
                    {
                        logoTracking?.Dispose();
                        logoTracking = null;

                        game?.Toolbar.Hide();

                        logo?.ClearTransforms(targetMember: nameof(Position));
                        logo?.MoveTo(new Vector2(0.5f), 800, Easing.OutExpo);
                        logo?.ScaleTo(1, 800, Easing.OutExpo);
                    }, buttonArea.Alpha * 150);

                    if (lastState == ButtonSystemState.TopLevel)
                        sampleLogoSwoosh?.Play();
                    break;

                case ButtonSystemState.TopLevel:
                case ButtonSystemState.Play:
                    switch (lastState)
                    {
                        case ButtonSystemState.TopLevel: // coming from toplevel to play
                            break;

                        case ButtonSystemState.Initial:
                            logo.ClearTransforms(targetMember: nameof(Position));

                            bool impact = logo.Scale.X > 0.6f;

                            logo.ScaleTo(0.5f, 200, Easing.In);

                            logoTracking?.Dispose();
                            logoTracking = logoTrackingContainer.StartTracking(logo, 200, Easing.In);

                            logoDelayedAction?.Cancel();
                            logoDelayedAction = Scheduler.AddDelayed(() =>
                            {
                                if (impact)
                                    logo?.Impact();

                                game?.Toolbar.Show();
                            }, 200);
                            break;

                        default:
                            logo.ClearTransforms(targetMember: nameof(Position));

                            logoTracking?.Dispose();
                            logoTracking = logoTrackingContainer.StartTracking(logo, 0, Easing.In);

                            logo.ScaleTo(0.5f, 200, Easing.OutQuint);
                            break;
                    }

                    break;

                case ButtonSystemState.EnteringMode:
                    logoTracking?.Dispose();
                    logoTracking = logoTrackingContainer.StartTracking(logo, lastState == ButtonSystemState.Initial ? MainMenu.FADE_OUT_DURATION : 0, Easing.InSine);
                    break;
            }
        }
    }

    public enum ButtonSystemState
    {
        Exit,
        Initial,
        TopLevel,
        Play,
        EnteringMode,
    }
}
