// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using typebeat.Game.Graphics.UserInterface;
using typebeat.Game.Overlays;
using osuTK;

namespace typebeat.Game.Graphics.UserInterfaceV2
{
    public partial class OsuHexColourPicker : HexColourPicker
    {
        private OsuColourPreview preview = null!;

        public OsuHexColourPicker()
        {
            Padding = new MarginPadding(20);
            Spacing = 20;
        }

        /// <summary>
        /// Run when the colour preview is clicked. Setting it turns the preview into a confirm button
        /// (a check mark, hover feedback and a "Done" tooltip); null leaves it a plain preview.
        /// Settable after construction because the base class builds the preview in its constructor.
        /// </summary>
        public Action? PreviewAction
        {
            get => preview.Action;
            set => preview.Action = value;
        }

        [BackgroundDependencyLoader(true)]
        private void load(OverlayColourProvider? overlayColourProvider, OsuColour osuColour)
        {
            Background.Colour = overlayColourProvider?.Dark6 ?? osuColour.GreySeaFoamDarker;
        }

        protected override TextBox CreateHexCodeTextBox() => new OsuTextBox();
        protected override ColourPreview CreateColourPreview() => preview = new OsuColourPreview();

        private partial class OsuColourPreview : ColourPreview, IHasTooltip
        {
            private readonly Box preview;
            private readonly Box hoverLayer;
            private readonly SpriteIcon confirmIcon;

            private Action? action;

            public Action? Action
            {
                get => action;
                set
                {
                    action = value;
                    confirmIcon.Alpha = value != null ? 1 : 0;
                }
            }

            public LocalisableString TooltipText => Action != null ? "Done" : default;

            public OsuColourPreview()
            {
                InternalChild = new CircularContainer
                {
                    RelativeSizeAxes = Axes.Both,
                    Masking = true,
                    Children = new Drawable[]
                    {
                        preview = new Box
                        {
                            RelativeSizeAxes = Axes.Both
                        },
                        hoverLayer = new Box
                        {
                            RelativeSizeAxes = Axes.Both,
                            Colour = Colour4.White,
                            Alpha = 0,
                        },
                        confirmIcon = new SpriteIcon
                        {
                            Anchor = Anchor.Centre,
                            Origin = Anchor.Centre,
                            Icon = FontAwesome.Solid.Check,
                            Size = new Vector2(16),
                            Alpha = 0,
                        },
                    }
                };
            }

            protected override void LoadComplete()
            {
                base.LoadComplete();

                Current.BindValueChanged(colour =>
                {
                    preview.Colour = colour.NewValue;

                    // The same legibility rule as the colour row's eyedropper: white with a shadow,
                    // dark only on a light colour.
                    var iconColour = FormColourSwatch.IconColourFor(colour.NewValue);
                    confirmIcon.Colour = iconColour;
                    confirmIcon.Shadow = iconColour == Colour4.White;
                }, true);
            }

            protected override bool OnHover(HoverEvent e)
            {
                if (Action == null)
                    return base.OnHover(e);

                hoverLayer.FadeTo(0.15f, 100, Easing.OutQuint);
                confirmIcon.ScaleTo(1.25f, 200, Easing.OutQuint);
                return true;
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                hoverLayer.FadeOut(200, Easing.OutQuint);
                confirmIcon.ScaleTo(1, 200, Easing.OutQuint);
                base.OnHoverLost(e);
            }

            protected override bool OnClick(ClickEvent e)
            {
                if (Action == null)
                    return base.OnClick(e);

                Action.Invoke();
                return true;
            }
        }
    }
}
