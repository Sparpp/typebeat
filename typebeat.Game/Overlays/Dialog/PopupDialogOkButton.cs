// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.UserInterface;

namespace typebeat.Game.Overlays.Dialog
{
    public partial class PopupDialogOkButton : PopupDialogButton
    {
        [BackgroundDependencyLoader]
        private void load(OsuColour colours)
        {
            ButtonColour = colours.Pink;
        }

        public PopupDialogOkButton()
            : base(HoverSampleSet.DialogOk)
        {
        }
    }
}
