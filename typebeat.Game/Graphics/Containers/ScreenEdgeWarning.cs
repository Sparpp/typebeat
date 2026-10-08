// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osuTK.Graphics;

namespace typebeat.Game.Graphics.Containers
{
    /// <summary>The additive red edge gradients shared by low-health and forced-line warnings.</summary>
    public partial class ScreenEdgeWarning : Container
    {
        public const float MAX_ALPHA = 0.4f;
        private const float gradient_size = 0.2f;

        public ScreenEdgeWarning()
        {
            RelativeSizeAxes = Axes.Both;
            Blending = BlendingParameters.Additive;
            Children = new Drawable[]
            {
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(Color4.White, Color4.White.Opacity(0)),
                    Width = gradient_size,
                },
                new Box
                {
                    RelativeSizeAxes = Axes.Both,
                    Colour = ColourInfo.GradientHorizontal(Color4.White.Opacity(0), Color4.White),
                    Width = gradient_size,
                    Anchor = Anchor.TopRight,
                    Origin = Anchor.TopRight,
                },
            };
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colour) => Colour = colour.Red;
    }
}
