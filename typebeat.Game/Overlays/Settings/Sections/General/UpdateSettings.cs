// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using typebeat.Game.Localisation;
using typebeat.Game.Online.Multiplayer;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Updater;

namespace typebeat.Game.Overlays.Settings.Sections.General
{
    public partial class UpdateSettings : SettingsSubsection
    {
        protected override LocalisableString Header => GeneralSettingsStrings.UpdateHeader;

        private SettingsButtonV2 checkForUpdatesButton = null!;

        [Resolved]
        private UpdateManager? updateManager { get; set; }

        [Resolved]
        private INotificationOverlay? notifications { get; set; }

        [Resolved]
        private OsuGame? game { get; set; }

        [BackgroundDependencyLoader]
        private void load()
        {
            // Release stream selection is deliberately not surfaced: the stream stays at whatever
            // OsuSetting.ReleaseStream defaults to (or whatever the update manager pins it to).
            Add(checkForUpdatesButton = new SettingsButtonV2
            {
                Text = GeneralSettingsStrings.CheckUpdate,
                Action = () => checkForUpdates().FireAndForget()
            });
        }

        private async Task checkForUpdates()
        {
            if (updateManager == null || game == null)
                return;

            checkForUpdatesButton.Enabled.Value = false;

            var checkingNotification = new ProgressNotification
            {
                Text = GeneralSettingsStrings.CheckingForUpdates,
            };
            notifications?.Post(checkingNotification);

            try
            {
                bool foundUpdate = await updateManager.CheckForUpdateAsync(checkingNotification.CancellationToken).ConfigureAwait(true);

                if (!foundUpdate)
                {
                    notifications?.Post(new SimpleNotification
                    {
                        Text = GeneralSettingsStrings.RunningLatestRelease(game.Version),
                        Icon = FontAwesome.Solid.CheckCircle,
                    });
                }
            }
            catch
            {
            }
            finally
            {
                checkingNotification.CompleteSilently();
                checkForUpdatesButton.Enabled.Value = true;
            }
        }
    }
}
