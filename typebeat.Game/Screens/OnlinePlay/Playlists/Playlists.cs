// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Online.Rooms;
using typebeat.Game.Screens.OnlinePlay.Lounge;

namespace typebeat.Game.Screens.OnlinePlay.Playlists
{
    public partial class Playlists : OnlinePlayScreen
    {
        protected override string ScreenTitle => "Playlists";

        protected override LoungeSubScreen CreateLounge() => new PlaylistsLoungeSubScreen();

        public void Join(Room room) => Schedule(() => Lounge.Join(room, string.Empty));
    }
}
