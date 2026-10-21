// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.Containers;
using typebeat.Game.Overlays;
using osuTK;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    /// <summary>
    /// A form row editing ONE colour two ways, in a single card: a hex code field, and inset on the
    /// card's right edge a swatch that opens the same popover <see cref="FormColourPalette"/> uses
    /// (the framework's saturation/value square with a hue slider, plus its own hex box). Both edit
    /// <see cref="Current"/>. Laid out like <see cref="FormButton"/>, whose button sits inside its card
    /// the same way, and an <see cref="IFormControl"/>, so it can sit in a settings row.
    ///
    /// <para>The hex field accepts what <see cref="Colour4.TryParseHex"/> does (with or without the
    /// <c>#</c>, three or six digits) and always stores an OPAQUE colour. An EMPTY commit resets to the
    /// bindable's default, and anything unparseable puts the field back to the current colour.</para>
    /// </summary>
    public partial class FormColourSwatch : CompositeDrawable, IHasCurrentValue<Colour4>, IFormControl
    {
        /// <summary>Width of the inset swatch, matching <see cref="FormButton"/>'s icon button.</summary>
        private const float swatch_width = 90;

        /// <summary>Gap between the swatch and the card's edges, matching <see cref="FormButton"/>'s inset.</summary>
        private const float swatch_inset = 5;

        public Bindable<Colour4> Current
        {
            get => current.Current;
            set => current.Current = value;
        }

        private readonly BindableWithCurrent<Colour4> current = new BindableWithCurrent<Colour4>();

        public LocalisableString Caption { get; init; }

        public LocalisableString HintText { get; init; }

        /// <summary>The hex code field (exposed so tests can type into it).</summary>
        public FormTextBox HexBox { get; private set; } = null!;

        private Swatch swatch = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChildren = new Drawable[]
            {
                HexBox = new FormTextBox
                {
                    Caption = Caption,
                    HintText = HintText,
                    PlaceholderText = "#rrggbb",
                    LengthLimit = 9,
                    // Keep the caption and the typed code clear of the swatch drawn over the card.
                    ContentRightInset = swatch_width + swatch_inset,
                },
                new Container
                {
                    Anchor = Anchor.CentreRight,
                    Origin = Anchor.CentreRight,
                    RelativeSizeAxes = Axes.Y,
                    Width = swatch_width + swatch_inset,
                    Padding = new MarginPadding { Vertical = swatch_inset, Right = swatch_inset },
                    Child = swatch = new Swatch
                    {
                        RelativeSizeAxes = Axes.Both,
                        Current = { BindTarget = Current },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            HexBox.OnCommit += (_, _) => commitHex();
            Current.BindValueChanged(_ =>
            {
                showHex();
                ValueChanged?.Invoke();
            }, true);
        }

        /// <summary>Opens the 2D picker popover, exactly as clicking the swatch does.</summary>
        public void ShowPicker() => swatch.ShowPopover();

        private void commitHex()
        {
            string text = HexBox.Current.Value?.Trim() ?? string.Empty;

            if (text.Length == 0)
                Current.SetDefault();
            else if (Colour4.TryParseHex(text, out Colour4 parsed))
                // 1f, not 1: an integer literal binds to Opacity(byte), which sets alpha to 1/255 and
                // stored a near-transparent colour that the field then spelled with a trailing "01".
                Current.Value = parsed.Opacity(1f);

            // Always re-spelled from the colour: normalises a typed value, and puts an unparseable one
            // (or a commit that changed nothing) back to what is actually stored.
            showHex();
        }

        private void showHex() => HexBox.Current.Value = Current.Value.ToHex();

        public event Action? ValueChanged;

        public bool IsDefault => Current.IsDefault;

        public void SetDefault() => Current.SetDefault();

        public bool IsDisabled => Current.Disabled;

        public IEnumerable<LocalisableString> FilterTerms => Caption.Yield();

        public float MainDrawHeight => DrawHeight;

        /// <summary>
        /// Brightness above which the swatch's eyedropper turns dark: the YIQ lightness estimate
        /// <see cref="OsuColour.ForegroundTextColourFor"/> uses, with a higher cut than its 0.5. That
        /// 0.5 sends saturated mid-tones (the timing tint's default orange and blue among them) to a
        /// dark icon, where <see cref="FormButton"/> draws its icon white with a shadow on coloured
        /// buttons. Up here only genuinely light colours (white, pastels, pale yellow) flip.
        /// </summary>
        public const float DARK_ICON_BRIGHTNESS = 0.75f;

        /// <summary>
        /// The eyedropper's colour on a swatch of <paramref name="swatchColour"/>: white, as on
        /// <see cref="FormButton"/>, unless the swatch is light enough that white would vanish on it.
        /// </summary>
        public static Colour4 IconColourFor(Colour4 swatchColour)
        {
            float brightness = 0.299f * swatchColour.R + 0.587f * swatchColour.G + 0.114f * swatchColour.B;
            return brightness > DARK_ICON_BRIGHTNESS ? OsuColour.Gray(0.2f) : Colour4.White;
        }

        /// <summary>
        /// The colour itself, filling the inset slot on the card's right. Rounded like
        /// <see cref="FormButton"/>'s button and marked with an eyedropper at that button's icon size,
        /// white with a shadow like its icon, dark on a light colour (see <see cref="IconColourFor"/>);
        /// clicking it opens the picker.
        /// </summary>
        private partial class Swatch : OsuClickableContainer, IHasPopover
        {
            public Bindable<Colour4> Current { get; } = new Bindable<Colour4>();

            private Box fill = null!;
            private SpriteIcon icon = null!;

            [Resolved]
            private OverlayColourProvider colourProvider { get; set; } = null!;

            [BackgroundDependencyLoader]
            private void load()
            {
                Masking = true;
                CornerRadius = 4;
                CornerExponent = 2.5f;
                // A thin edge so a colour close to the card's own background still reads as a swatch.
                BorderThickness = 2;
                BorderColour = colourProvider.Background4;
                Action = this.ShowPopover;

                Children = new Drawable[]
                {
                    fill = new Box { RelativeSizeAxes = Axes.Both },
                    icon = new SpriteIcon
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Icon = FontAwesome.Solid.EyeDropper,
                        Size = new Vector2(16),
                    },
                };
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                Current.BindValueChanged(c =>
                {
                    fill.Colour = c.NewValue;

                    var iconColour = IconColourFor(c.NewValue);
                    icon.Colour = iconColour;
                    // The shadow is what lets a white icon hold on a mid-tone; under a dark icon it
                    // would only smudge it.
                    icon.Shadow = iconColour == Colour4.White;
                }, true);
            }

            public Popover GetPopover() => new FormColourPalette.ColourPickerPopover
            {
                Current = { BindTarget = Current }
            };
        }
    }
}
