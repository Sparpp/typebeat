// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Overlays.Dialog;

namespace typebeat.Game.Screens.ImportLyrics
{
    /// <summary>
    /// Confirms tearing down an in-flight lyric import. Shown when the user tries to leave
    /// <see cref="ImportLyricsScreen"/> while an alignment is running — which, for server-side
    /// alignment, is a multi-minute job a stray Esc would otherwise abandon silently (and, until
    /// the cancel wired up alongside this dialog, leave running on the server for nobody).
    /// </summary>
    public partial class ConfirmCancelImportDialog : PopupDialog
    {
        public ConfirmCancelImportDialog(Action onConfirm, Action? onCancel = null)
        {
            HeaderText = "Cancel this import?";
            BodyText = "The alignment in progress will be stopped and nothing will be imported.";

            Icon = FontAwesome.Solid.ExclamationTriangle;

            Buttons = new PopupDialogButton[]
            {
                new PopupDialogDangerousButton
                {
                    Text = "Stop the import",
                    Action = onConfirm,
                },
                new PopupDialogCancelButton
                {
                    Text = "Keep importing",
                    Action = onCancel,
                },
            };
        }
    }
}
