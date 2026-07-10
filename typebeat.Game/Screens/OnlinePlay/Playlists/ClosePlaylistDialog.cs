// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Localisation;
using typebeat.Game.Online.Rooms;
using typebeat.Game.Overlays.Dialog;

namespace typebeat.Game.Screens.OnlinePlay.Playlists
{
    public partial class ClosePlaylistDialog : DeletionDialog
    {
        public ClosePlaylistDialog(Room room, Action closeAction)
        {
            HeaderText = DialogStrings.ClosePlaylistHeaderText;
            BodyText = room.Name;
            DangerousAction = closeAction;
        }
    }
}
