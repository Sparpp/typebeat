// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Localisation;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    /// <summary>
    /// A form row editing ONE colour two ways: a hex code field, and beside it a swatch that opens the
    /// same popover <see cref="FormColourPalette"/> uses (the framework's saturation/value square with
    /// a hue slider, plus its own hex box). Both edit <see cref="Current"/>.
    ///
    /// <para>The hex field accepts what <see cref="Colour4.TryParseHex"/> does (with or without the
    /// <c>#</c>, three or six digits) and always stores an OPAQUE colour. An EMPTY commit resets to the
    /// bindable's default, and anything unparseable puts the field back to the current colour.</para>
    /// </summary>
    public partial class FormColourSwatch : CompositeDrawable, IHasCurrentValue<Colour4>
    {
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

        private FormColourPalette.ColourButton swatch = null!;

        [BackgroundDependencyLoader]
        private void load()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChild = new GridContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                ColumnDimensions = new[]
                {
                    new Dimension(),
                    new Dimension(GridSizeMode.Absolute, 5),
                    new Dimension(GridSizeMode.AutoSize),
                },
                RowDimensions = new[] { new Dimension(GridSizeMode.AutoSize) },
                Content = new[]
                {
                    new Drawable[]
                    {
                        HexBox = new FormTextBox
                        {
                            Caption = Caption,
                            HintText = HintText,
                            PlaceholderText = "#rrggbb",
                            LengthLimit = 9,
                        },
                        Empty(),
                        swatch = new FormColourPalette.ColourButton(new BindableList<Colour4>())
                        {
                            Anchor = Anchor.CentreLeft,
                            Origin = Anchor.CentreLeft,
                            Current = { BindTarget = Current },
                        },
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            HexBox.OnCommit += (_, _) => commitHex();
            Current.BindValueChanged(_ => showHex(), true);
        }

        /// <summary>Opens the 2D picker popover, exactly as clicking the swatch does.</summary>
        public void ShowPicker() => swatch.ShowPopover();

        private void commitHex()
        {
            string text = HexBox.Current.Value?.Trim() ?? string.Empty;

            if (text.Length == 0)
                Current.SetDefault();
            else if (Colour4.TryParseHex(text, out Colour4 parsed))
                Current.Value = parsed.Opacity(1);

            // Always re-spelled from the colour: normalises a typed value, and puts an unparseable one
            // (or a commit that changed nothing) back to what is actually stored.
            showHex();
        }

        private void showHex() => HexBox.Current.Value = Current.Value.ToHex();
    }
}
