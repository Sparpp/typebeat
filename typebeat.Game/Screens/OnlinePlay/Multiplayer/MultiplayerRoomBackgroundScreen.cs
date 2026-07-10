// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Extensions.ObjectExtensions;
using typebeat.Game.Online.Multiplayer;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Screens.OnlinePlay.Components;

namespace typebeat.Game.Screens.OnlinePlay.Multiplayer
{
    public partial class MultiplayerRoomBackgroundScreen : OnlinePlayBackgroundScreen
    {
        [Resolved]
        private MultiplayerClient client { get; set; } = null!;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            client.RoomUpdated += onRoomUpdated;
            onRoomUpdated();
        }

        private void onRoomUpdated() => Scheduler.AddOnce(() =>
        {
            if (client.Room == null)
                return;

            PlaylistItem = new PlaylistItem(client.Room.CurrentPlaylistItem);
        });

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (client.IsNotNull())
                client.RoomUpdated -= onRoomUpdated;
        }
    }
}
