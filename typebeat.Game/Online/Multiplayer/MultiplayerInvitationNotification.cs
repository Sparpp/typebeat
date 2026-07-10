// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics.Sprites;
using typebeat.Game.Localisation;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Overlays.Notifications;

namespace typebeat.Game.Online.Multiplayer
{
    public partial class MultiplayerInvitationNotification : UserAvatarNotification
    {
        protected override IconUsage CloseButtonIcon => FontAwesome.Solid.Times;

        public MultiplayerInvitationNotification(APIUser user, Room room)
            : base(user, NotificationsStrings.InvitedYouToTheMultiplayer(user.Username, room.Name))
        {
        }
    }
}
