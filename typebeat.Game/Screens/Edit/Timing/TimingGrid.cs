// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using typebeat.Game.Graphics;

namespace typebeat.Game.Screens.Edit.Timing
{
    /// <summary>A non-interactive timing reference sharing the waveform's beat colours.</summary>
    public partial class TimingGrid : CompositeDrawable
    {
        private readonly Func<(double start, double end)> range;
        private readonly bool optional;
        private readonly List<Box> lines = new List<Box>();
        private readonly BindableBool showGrid = new BindableBool();

        [Resolved]
        private EditorBeatmap beatmap { get; set; } = null!;

        [Resolved]
        private BindableBeatDivisor divisor { get; set; } = null!;

        [Resolved]
        private EditorTimingSettings settings { get; set; } = null!;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        public override bool HandlePositionalInput => false;
        public override bool HandleNonPositionalInput => false;

        public TimingGrid(Func<(double start, double end)> range, bool optional = true)
        {
            this.range = range;
            this.optional = optional;
            RelativeSizeAxes = Axes.Both;
            Masking = true;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            showGrid.BindTo(settings.ShowGrid);
            // Hidden drawables do not update. Listen outside Update so the overlay can reappear.
            showGrid.BindValueChanged(v => Alpha = optional && !v.NewValue ? 0 : 1, true);
        }

        protected override void Update()
        {
            base.Update();
            var (start, end) = range();
            int used = 0;
            if (Alpha > 0 && DrawWidth > 0 && end > start)
            {
                foreach (var tick in EditorBeatGrid.Ticks(beatmap.ControlPointInfo, divisor.Value, start, end, (end - start) / DrawWidth * 4, settings.BeatMultiplier.Value))
                {
                    if (used == lines.Count)
                    {
                        var line = new Box { RelativeSizeAxes = Axes.Y, Origin = Anchor.TopCentre };
                        lines.Add(line);
                        AddInternal(line);
                    }
                    var box = lines[used++];
                    box.X = (float)((tick.Time - start) / (end - start) * DrawWidth);
                    box.Width = tick.IsBar ? 2 : 1;
                    box.Height = 1;
                    box.Colour = BindableBeatDivisor.GetColourFor(tick.Divisor, colours);
                    box.Alpha = tick.IsBar ? 0.55f : tick.Divisor == 1 ? 0.35f : 0.22f;
                }
            }
            for (int i = used; i < lines.Count; i++)
                lines[i].Alpha = 0;
        }
    }
}
