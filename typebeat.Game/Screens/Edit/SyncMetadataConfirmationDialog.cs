// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Dialog;

namespace typebeat.Game.Screens.Edit
{
    public partial class SyncMetadataConfirmationDialog : DangerousActionDialog
    {
        public SyncMetadataConfirmationDialog(Action syncAction)
        {
            BodyText = EditorDialogsStrings.SyncMetadataConfirmationBody;
            DangerousAction = syncAction;
        }
    }
}
