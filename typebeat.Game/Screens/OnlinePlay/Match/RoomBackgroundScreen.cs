// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Screens.OnlinePlay.Components;

namespace typebeat.Game.Screens.OnlinePlay.Match
{
    public partial class RoomBackgroundScreen : OnlinePlayBackgroundScreen
    {
        public readonly Bindable<PlaylistItem?> SelectedItem = new Bindable<PlaylistItem?>();

        public RoomBackgroundScreen(PlaylistItem? initialPlaylistItem)
        {
            PlaylistItem = initialPlaylistItem;
            SelectedItem.BindValueChanged(item => PlaylistItem = item.NewValue);
        }
    }
}
