// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Rulesets.TypeBeat.UI;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Components.Timelines.Summary.Parts;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Full-height marker on the compose waveform timeline at the intro beatdrop timestamp.
    /// Hidden while the beatdrop is unset. The value itself is edited in the setup screen
    /// (<see cref="TypeBeatSetupSection"/>); it drives intro track timing on game startup.
    /// </summary>
    public partial class BeatdropMarkerPart : TimelinePart
    {
        private readonly Box marker;

        public BeatdropMarkerPart()
        {
            RelativeSizeAxes = Axes.Both;

            marker = new Box
            {
                RelativePositionAxes = Axes.X,
                RelativeSizeAxes = Axes.Y,
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopCentre,
                Width = 3,
                Colour = TypeBeatStyle.ErrorChar,
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
            marker.Alpha = EditorBeatmap.IntroBeatdrop.Value.HasValue ? 0.9f : 0;
            marker.X = (float)(EditorBeatmap.IntroBeatdrop.Value ?? 0);
        }
    }
}
