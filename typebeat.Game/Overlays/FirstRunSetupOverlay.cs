// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Configuration;
using typebeat.Game.Database;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.FirstRunSetup;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Screens;
using typebeat.Game.Screens.Menu;

namespace typebeat.Game.Overlays
{
    [Cached]
    public partial class FirstRunSetupOverlay : WizardOverlay
    {
        [Resolved]
        private IPerformFromScreenRunner performer { get; set; } = null!;

        [Resolved]
        private INotificationOverlay notificationOverlay { get; set; } = null!;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private readonly Bindable<bool> showFirstRunSetup = new Bindable<bool>();

        public FirstRunSetupOverlay()
            : base(OverlayColourScheme.Purple)
        {
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            AddStep<ScreenWelcome>();
            AddStep<ScreenUIScale>();
            AddStep<ScreenBehaviour>();
            AddStep<ScreenLocalAligner>();

            Header.Title = FirstRunSetupOverlayStrings.FirstRunSetupTitle;
            Header.Description = FirstRunSetupOverlayStrings.FirstRunSetupDescription;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            config.BindWith(OsuSetting.ShowFirstRunSetup, showFirstRunSetup);

            if (showFirstRunSetup.Value) Show();
        }

        public override void Show()
        {
            // if we are valid for display, only do so after reaching the main menu.
            performer.PerformFromScreen(screen =>
            {
                // Hides the toolbar for us.
                if (screen is MainMenu menu)
                    menu.ReturnToOsuLogo();

                base.Show();
            }, new[] { typeof(MainMenu) });
        }

        protected override void PopOut()
        {
            base.PopOut();

            if (CurrentStepIndex != null)
            {
                notificationOverlay.Post(new SimpleNotification
                {
                    Text = FirstRunSetupOverlayStrings.ClickToResumeFirstRunSetupAtAnyPoint,
                    Icon = FontAwesome.Solid.Redo,
                    Activated = () =>
                    {
                        Show();
                        return true;
                    },
                });
            }
        }

        protected override void ShowNextStep()
        {
            base.ShowNextStep();

            if (CurrentStepIndex == null)
                showFirstRunSetup.Value = false;
        }
    }
}
