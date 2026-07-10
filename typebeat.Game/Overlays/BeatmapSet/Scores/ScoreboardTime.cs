// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Localisation;
using typebeat.Game.Extensions;
using typebeat.Game.Graphics;

namespace typebeat.Game.Overlays.BeatmapSet.Scores
{
    public partial class ScoreboardTime : DrawableDate
    {
        public ScoreboardTime(DateTimeOffset date, float textSize = OsuFont.DEFAULT_FONT_SIZE, bool italic = true)
            : base(date, textSize, italic)
        {
        }

        protected override LocalisableString Format()
            => Date.ToShortRelativeTime(TimeSpan.FromHours(1));
    }
}
