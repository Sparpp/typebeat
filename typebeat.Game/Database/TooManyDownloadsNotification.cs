// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics;
using typebeat.Game.Overlays.Notifications;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Database
{
    public partial class TooManyDownloadsNotification : SimpleNotification
    {
        public TooManyDownloadsNotification()
        {
            Text = BeatmapsetsStrings.DownloadLimitExceeded;
            Icon = FontAwesome.Solid.ExclamationCircle;
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colours)
        {
            IconContent.Colour = colours.RedDark;
        }
    }
}
