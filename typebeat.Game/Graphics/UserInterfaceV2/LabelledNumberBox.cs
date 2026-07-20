// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using typebeat.Game.Graphics.UserInterface;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    public partial class LabelledNumberBox : LabelledTextBox
    {
        protected override OsuTextBox CreateTextBox() => new OsuNumberBox();
    }
}
