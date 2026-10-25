// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using typebeat.Game.Graphics;
using typebeat.Game.Graphics.Containers;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The compose screen's lyric line list as a collapsible sidebar laid OVER the left of the word
    /// block view, which spans the full width underneath it. Open by default; the button on its right
    /// edge slides it out to the left and back. The list keeps its own semi-transparent panel
    /// background, so the word blocks read faintly through it.
    /// </summary>
    public partial class LineListSidebar : Container
    {
        /// <summary>The sidebar's width, as a fraction of the compose area (what the list column used to take).</summary>
        public const float WIDTH = 0.42f;

        /// <summary>
        /// Room the word block view keeps clear at its left edge for the toggle button, which stays
        /// on screen at the left edge while the sidebar is collapsed.
        /// </summary>
        public const float TOGGLE_GUTTER = 40;

        public const double SLIDE_DURATION = 250;

        /// <summary>Whether the sidebar is open. Starts open.</summary>
        public readonly BindableBool Expanded = new BindableBool(true);

        public readonly LineListPanel Panel;

        public SidebarToggleButton ToggleButton { get; }

        /// <summary>
        /// How many pixels from this container's left edge the list currently covers, including the
        /// toggle button's room; follows the slide animation frame by frame.
        /// </summary>
        public float CoveredWidth => Math.Max(0, slider.X * DrawWidth + slider.DrawWidth + TOGGLE_GUTTER);

        private readonly Container slider;

        public LineListSidebar(LineListPanel panel)
        {
            Panel = panel;
            RelativeSizeAxes = Axes.Both;

            // The slider swallows clicks and hovers inside its own bounds, so the word blocks under an
            // open sidebar are not edited through it; the toggle button sits just past its right edge
            // and still receives input there.
            Child = slider = new InputBlockingContainer
            {
                RelativeSizeAxes = Axes.Both,
                RelativePositionAxes = Axes.X,
                Width = WIDTH,
                Children = new Drawable[]
                {
                    panel,
                    ToggleButton = new SidebarToggleButton
                    {
                        Anchor = Anchor.TopRight,
                        Origin = Anchor.TopLeft,
                        Margin = new MarginPadding { Top = 6, Left = 4 },
                        Action = Expanded.Toggle,
                    },
                },
            };
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            Expanded.BindValueChanged(e =>
            {
                slider.MoveToX(e.NewValue ? 0 : -WIDTH, e.OldValue == e.NewValue ? 0 : SLIDE_DURATION, Easing.OutQuint);
                ToggleButton.Expanded = e.NewValue;
            }, true);
        }

        public partial class SidebarToggleButton : OsuClickableContainer
        {
            private Sprite icon = null!;
            private Texture collapse = null!, collapseFilled = null!, expand = null!, expandFilled = null!;
            private bool expanded = true;

            public SidebarToggleButton()
            {
                Size = new Vector2(30);
            }

            public bool Expanded
            {
                get => expanded;
                set
                {
                    expanded = value;
                    updateIcon();
                }
            }

            public override LocalisableString TooltipText => expanded ? "Collapse line list" : "Expand line list";

            [BackgroundDependencyLoader]
            private void load(TextureStore textures)
            {
                collapse = textures.Get("Icons/sidebar-collapse");
                collapseFilled = textures.Get("Icons/sidebar-collapse-filled");
                expand = textures.Get("Icons/sidebar-expand");
                expandFilled = textures.Get("Icons/sidebar-expand-filled");

                Child = icon = new Sprite
                {
                    RelativeSizeAxes = Axes.Both,
                    FillMode = FillMode.Fit,
                };
                updateIcon();
            }

            protected override bool OnHover(HoverEvent e)
            {
                updateIcon();
                return base.OnHover(e);
            }

            protected override void OnHoverLost(HoverLostEvent e)
            {
                base.OnHoverLost(e);
                updateIcon();
            }

            private void updateIcon()
            {
                if (icon == null)
                    return;

                // The outline icon at rest, the filled one under the cursor.
                icon.Texture = expanded
                    ? IsHovered ? collapseFilled : collapse
                    : IsHovered ? expandFilled : expand;
            }
        }
    }
}
