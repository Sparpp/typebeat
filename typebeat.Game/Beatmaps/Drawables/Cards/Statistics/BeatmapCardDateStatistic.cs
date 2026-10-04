// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Graphics;
using typebeat.Game.Utils;

namespace typebeat.Game.Beatmaps.Drawables.Cards.Statistics
{
    public partial class BeatmapCardDateStatistic : BeatmapCardStatistic
    {
        private readonly DateTimeOffset dateTime;

        private BeatmapCardDateStatistic(DateTimeOffset dateTime)
        {
            this.dateTime = dateTime;

            Icon = FontAwesome.Solid.CheckCircle;
            Text = dateTime.ToLocalisedMediumDate();
        }

        public override object TooltipContent => dateTime;
        public override ITooltip GetCustomTooltip() => new DateTooltip();

        public static BeatmapCardDateStatistic? CreateFor(IBeatmapSetOnlineInfo beatmapSetInfo)
        {
            var displayDate = displayDateFor(beatmapSetInfo);

            if (displayDate == null)
                return null;

            return new BeatmapCardDateStatistic(displayDate.Value);
        }

        /// <remarks>
        /// Always the last update. osu shows a ranked set's ranked date, but the type!beat server records no ranked date (its
        /// <c>ranked_date</c> is the last update standing in), so that would be the same date under a wrong label.
        /// </remarks>
        private static DateTimeOffset? displayDateFor(IBeatmapSetOnlineInfo beatmapSetInfo) => beatmapSetInfo.LastUpdated;
    }
}
