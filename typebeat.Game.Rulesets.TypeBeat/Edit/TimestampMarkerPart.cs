// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Components.Timelines.Summary.Parts;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>A timeline marker for an optional beatmap timestamp.</summary>
    public partial class TimestampMarkerPart : TimelinePart
    {
        private readonly Func<EditorBeatmap, double?> getTime;
        private readonly Box marker;

        public TimestampMarkerPart(Func<EditorBeatmap, double?> getTime, Colour4 colour)
        {
            this.getTime = getTime;
            RelativeSizeAxes = Axes.Both;

            marker = new Box
            {
                RelativePositionAxes = Axes.X,
                RelativeSizeAxes = Axes.Y,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Width = 3,
                Colour = colour,
                Alpha = 0,
            };
        }

        protected override void LoadBeatmap(EditorBeatmap beatmap)
        {
            base.LoadBeatmap(beatmap);
            Add(marker);
        }

        protected override void Update()
        {
            base.Update();

            // Position is milliseconds; TimelinePart's RelativeChildSize maps it to track fraction.
            double? time = getTime(EditorBeatmap);
            marker.Alpha = time.HasValue ? 0.9f : 0;
            marker.X = (float)(time ?? 0);
        }
    }
}
