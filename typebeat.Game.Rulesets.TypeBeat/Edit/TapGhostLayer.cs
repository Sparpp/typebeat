// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Rulesets.TypeBeat.UI;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The GHOST markers of a live tap-timing pass: one thin mark per recorded tap, drawn over a
    /// timeline surface while recording. They are pure UI, nothing behind them has been committed
    /// yet; they simply show where the taps landed so the mapper can see the pass building up (and
    /// see them vanish again when they seek backwards, which drops those taps).
    ///
    /// Hosted by both timeline surfaces (<see cref="LyricTimeline"/> and
    /// <see cref="LineBoundariesBand"/>); each just hands its own time-to-pixels mapping in.
    /// Marks are pooled: only their positions change per frame.
    /// </summary>
    public partial class TapGhostLayer : CompositeDrawable
    {
        private readonly Container marks;

        public TapGhostLayer()
        {
            RelativeSizeAxes = Axes.Both;
            InternalChild = marks = new Container { RelativeSizeAxes = Axes.Both };
        }

        /// <summary>Re-lays the ghosts for <paramref name="taps"/>; null or empty hides them all.</summary>
        public void UpdateGhosts(IReadOnlyList<double>? taps, Func<double, float> positionOf)
        {
            int count = taps?.Count ?? 0;

            while (marks.Count < count)
            {
                marks.Add(new Box
                {
                    Anchor = Anchor.TopLeft,
                    Origin = Anchor.TopCentre,
                    RelativeSizeAxes = Axes.Y,
                    Width = 2,
                    Colour = TypeBeatStyle.Caret,
                    Alpha = 0,
                });
            }

            for (int i = 0; i < marks.Count; i++)
            {
                if (i >= count)
                {
                    marks[i].Alpha = 0;
                    continue;
                }

                // The most recent tap reads brightest: it is the one the current word just snapped to.
                marks[i].Alpha = i == count - 1 ? 0.95f : 0.55f;
                marks[i].X = positionOf(taps![i]);
            }
        }
    }
}
