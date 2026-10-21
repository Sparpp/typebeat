// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Localisation;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    public partial class OsuColourPicker : ColourPicker
    {
        /// <summary>
        /// Optional preset colours shown between the HSV and hex controls.
        /// </summary>
        public BindableList<Colour4> Suggestions { get; } = new BindableList<Colour4>();

        public LocalisableString PaletteHeaderText { get; init; } = string.Empty;

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

        protected override SwatchColourPicker CreateSwatchColourPicker() => new OsuSwatchColourPicker
        {
            Colours = { BindTarget = Suggestions },
            PaletteHeaderText = PaletteHeaderText,
        };

        protected override HexColourPicker CreateHexColourPicker() => hexPicker = new OsuHexColourPicker();
    }
}
