// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Screens;
using typebeat.Game.Localisation;
using typebeat.Game.Overlays.Dialog;
using typebeat.Game.Screens;

namespace typebeat.Game.Overlays.Settings.Sections.Maintenance
{
    public partial class StableDirectoryLocationDialog : PopupDialog
    {
        [Resolved]
        private IPerformFromScreenRunner performer { get; set; } = null!;

        public StableDirectoryLocationDialog(TaskCompletionSource<string> taskCompletionSource)
        {
            HeaderText = DialogStrings.StableDirectoryLocationHeaderText;
            BodyText = DialogStrings.StableDirectoryLocationBodyText;
            Icon = FontAwesome.Solid.QuestionCircle;

            Buttons = new PopupDialogButton[]
            {
                new PopupDialogOkButton
                {
                    Text = DialogStrings.StableDirectoryLocationOkButton,
                    Action = () => Schedule(() => performer.PerformFromScreen(screen => screen.Push(new StableDirectorySelectScreen(taskCompletionSource))))
                },
                new PopupDialogCancelButton
                {
                    Text = DialogStrings.StableDirectoryLocationCancelButton,
                    Action = () => taskCompletionSource.TrySetCanceled()
                }
            };
        }
    }
}
