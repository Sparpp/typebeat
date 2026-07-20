// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics;
using typebeat.Game.Graphics;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Overlays.Dashboard.Home
{
    public partial class DashboardNewBeatmapPanel : DashboardBeatmapPanel
    {
        public DashboardNewBeatmapPanel(APIBeatmapSet beatmapSet)
            : base(beatmapSet)
        {
        }

        protected override Drawable CreateInfo() => new DrawableDate(BeatmapSet.Ranked ?? DateTimeOffset.Now, 10, false)
        {
            Colour = ColourProvider.Foreground1
        };
    }
}
