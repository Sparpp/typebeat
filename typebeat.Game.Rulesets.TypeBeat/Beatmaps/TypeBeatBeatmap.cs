// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The playable typebeat beatmap: exposes typing-pace statistics (lines, boundary-window
    /// WPM/CPM) which song select's statistics display computes live from the hit objects —
    /// always correct, never stale realm data.
    /// </summary>
    public class TypeBeatBeatmap : Beatmap<TypeBeatHitObject>
    {
        /// <summary>Display normalisation caps for the statistic bars.</summary>
        private const float max_display_wpm = 150;

        public override IEnumerable<BeatmapStatistic> GetStatistics()
        {
            if (HitObjects.Count == 0)
                yield break;

            var pace = LyricPaceStatistics.Compute(HitObjects.Select(h => h.Line));

            yield return new BeatmapStatistic
            {
                Name = "Lines",
                Content = HitObjects.Count.ToString("N0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.AlignLeft },
                BarDisplayLength = Math.Min(1, HitObjects.Count / 100f),
            };

            yield return new BeatmapStatistic
            {
                Name = "Average WPM",
                Content = pace.AverageWpm.ToString("0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Keyboard },
                BarDisplayLength = (float)Math.Min(1, pace.AverageWpm / max_display_wpm),
            };

            yield return new BeatmapStatistic
            {
                Name = "Average CPM",
                Content = pace.AverageCpm.ToString("0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Font },
                BarDisplayLength = (float)Math.Min(1, pace.AverageCpm / (max_display_wpm * 5)),
            };
        }
    }
}
