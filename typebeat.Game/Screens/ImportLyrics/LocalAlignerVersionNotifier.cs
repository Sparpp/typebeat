// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Logging;
using typebeat.Game.Graphics;
using typebeat.Game.Overlays;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Overlays.Settings.Sections;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// Tells the player, once per game session, that their installed local auto-aligner is not the
    /// version this build ships and that automatic alignment is off until they update it (backlog
    /// 410). Not blocking and nothing is installed for them: the notification only says why and,
    /// clicked, opens Settings at the Experimental section, where the update button is. Added once by
    /// the desktop game beside the aligner manager it reads; with no manager (headless, tests) it
    /// does nothing.
    /// </summary>
    public partial class LocalAlignerVersionNotifier : Component
    {
        [Resolved(CanBeNull = true)]
        private ILocalAlignerManager? alignerManager { get; set; }

        [Resolved(CanBeNull = true)]
        private INotificationOverlay? notifications { get; set; }

        [Resolved(CanBeNull = true)]
        private SettingsOverlay? settings { get; set; }

        [Resolved(CanBeNull = true)]
        private OsuColour? colours { get; set; }

        private readonly Action? openSettings;

        private bool posted;

        /// <summary>How many checks have finished, posted or not, so a test can wait for one that posts nothing.</summary>
        internal int ChecksCompleted { get; private set; }

        /// <param name="openSettings">What a click on the notification does; null opens the settings overlay at its Experimental section.</param>
        public LocalAlignerVersionNotifier(Action? openSettings = null)
        {
            this.openSettings = openSettings;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            Check();
        }

        /// <summary>
        /// Reads the installed and shipped versions off the update thread (a few small file reads and
        /// a directory walk, kept off the startup frame anyway) and posts the notification if they do
        /// not match and it has not been posted this session. A repeat call never posts twice.
        /// </summary>
        public void Check()
        {
            var manager = alignerManager;

            if (manager == null)
                return;

            Task.Run(() =>
            {
                string? text = null;

                try
                {
                    text = LocalAlignerVersion.NotificationText(manager);
                }
                catch (Exception e)
                {
                    Logger.Log($"Could not read the local auto-aligner's version: {e.Message}");
                }

                Schedule(() =>
                {
                    if (text != null)
                        post(text);

                    ChecksCompleted++;
                });
            });
        }

        private void post(string text)
        {
            if (posted)
                return;

            posted = true;

            notifications?.Post(new SimpleNotification
            {
                Text = text,
                Icon = FontAwesome.Solid.ExclamationTriangle,
                IconColour = colours?.YellowDark ?? Colour4.Yellow,
                Activated = () =>
                {
                    notifications?.Hide();

                    if (openSettings != null)
                        openSettings();
                    else
                        settings?.ShowAtControl<ExperimentalSection>();

                    return true;
                },
            });
        }
    }
}
