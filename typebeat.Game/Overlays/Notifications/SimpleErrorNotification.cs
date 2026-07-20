// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Sprites;

namespace typebeat.Game.Overlays.Notifications
{
    public partial class SimpleErrorNotification : SimpleNotification
    {
        public override string PopInSampleName => "UI/notification-error";

        public SimpleErrorNotification()
        {
            Icon = FontAwesome.Solid.Bomb;
        }
    }
}
