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
    /// <see cref="ImportLyricsScreen"/> while an alignment is running, which is a multi-minute job
    /// (vocal separation, then forced alignment), on this machine or on the server aligner the
    /// player opted in to (backlog 413), that a stray Esc would otherwise throw away silently.
    /// Confirming cancels it: the local process tree is killed, or the server job withdrawn.
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
