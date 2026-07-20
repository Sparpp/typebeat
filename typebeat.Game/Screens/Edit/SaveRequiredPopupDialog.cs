// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Dialog;

namespace typebeat.Game.Screens.Edit
{
    public partial class SaveRequiredPopupDialog : PopupDialog
    {
        public SaveRequiredPopupDialog(Action saveAndAction)
        {
            HeaderText = EditorDialogsStrings.SaveRequiredDialogHeader;

            Icon = FontAwesome.Regular.Save;

            Buttons = new PopupDialogButton[]
            {
                new PopupDialogOkButton
                {
                    Text = EditorDialogsStrings.SoundsGood,
                    Action = saveAndAction
                },
                new PopupDialogCancelButton
                {
                    Text = EditorDialogsStrings.ContinueEditing,
                },
            };
        }
    }
}
