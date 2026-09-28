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
    /// <see cref="ImportLyricsScreen"/> while an alignment is running, which is a multi-minute
    /// local aligner job (vocal separation, then forced alignment) that a stray Esc would otherwise
    /// throw away silently. The dialog still guards that path: the server-side aligner is retired,
    /// but the LOCAL run it fronted for is every bit as long, so the confirmation stays.
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
