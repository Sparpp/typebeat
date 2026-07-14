// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Extensions.Color4Extensions;
using typebeat.Game.Graphics.UserInterface;

namespace typebeat.Game.Overlays.Dialog
{
    public partial class PopupDialogButton : DialogButton
    {
        public PopupDialogButton(HoverSampleSet sampleSet = HoverSampleSet.Button)
            : base(sampleSet)
        {
            Height = 50;
            BackgroundColour = Color4Extensions.FromHex(@"150e14");
            TextSize = 18;
        }
    }
}
