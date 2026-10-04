// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    public partial class OsuColourPicker : ColourPicker
    {
        private OsuHexColourPicker hexPicker = null!;

        public OsuColourPicker()
        {
            CornerRadius = 10;
            Masking = true;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Shown in a popover (every colour swatch and palette in the game opens it that way), the
            // colour preview doubles as the confirm button that closes it. Without one, the only ways
            // out were clicking elsewhere or pressing back, which can also close the panel behind it.
            if (this.FindClosestParent<Popover>() != null)
                hexPicker.PreviewAction = this.HidePopover;
        }

        protected override HSVColourPicker CreateHSVColourPicker() => new OsuHSVColourPicker();
        protected override HexColourPicker CreateHexColourPicker() => hexPicker = new OsuHexColourPicker();
    }
}
