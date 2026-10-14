// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Utils;
using osuTK;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>Shows its content at its own size, scaled down uniformly when wider than this container.</summary>
    public partial class FitToWidthContainer : CompositeDrawable
    {
        private readonly Drawable content;

        public FitToWidthContainer(Drawable content)
        {
            this.content = content;
            RelativeSizeAxes = Axes.Both;
            InternalChild = content;
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            float natural = content.DrawWidth;
            float scale = natural > 0 && DrawWidth > 0 ? Math.Min(1, DrawWidth / natural) : 1;

            // Assigned only on a change: a scale assignment invalidates the content's layout.
            if (!Precision.AlmostEquals(content.Scale.X, scale))
                content.Scale = new Vector2(scale);
        }
    }
}
