// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Dialog;

namespace typebeat.Game.Screens.OnlinePlay.Components
{
    public partial class ConfirmExitMultiplayerMatchDialog : ConfirmDialog
    {
        public ConfirmExitMultiplayerMatchDialog(Action onConfirm)
            : base(DialogStrings.ConfirmExitMultiplayerMatchBodyText, onConfirm)
        {
        }
    }
}
