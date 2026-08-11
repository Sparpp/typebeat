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
    /// WPM/CPM) which song select's statistics display computes live from the hit objects,
    /// always correct, never stale realm data.
    /// </summary>
    public class TypeBeatBeatmap : Beatmap<TypeBeatHitObject>, IHasTypingPace
    {
        /// <summary>Display normalisation caps for the statistic bars.</summary>
        private const float max_display_wpm = 150;

        /// <summary>
        /// Peak (rolling-window) and average (per-line) pace for song select's metadata wedge, both
        /// derived from ONE materialised pass over the lyric lines: the curve sweep and the pace
        /// averages read the same list rather than enumerating the hit objects twice.
        /// </summary>
        public TypingPaceProfile? GetTypingPace()
        {
            if (HitObjects.Count == 0)
                return null;

            var lines = HitObjects.Select(h => h.Line).ToList();
            var curve = LyricWpmCurve.Compute(lines);
            var pace = LyricPaceStatistics.Compute(lines);

            // Nothing to draw (no typeable cell at all, or fewer than one rolling window's worth)
            // reports null so the wedge hides the section instead of showing a flat empty graph.
            if (pace.TypeableCellCount == 0 || curve.IsEmpty)
                return null;

            return new TypingPaceProfile
            {
                WpmCurve = curve.Curve,
                PeakWpm = curve.PeakWpm,
                PeakCpm = curve.PeakCpm,
                AverageWpm = pace.AverageWpm,
                AverageCpm = pace.AverageCpm,
            };
        }

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

            // A faster clock (DoubleTime/Nightcore) means more words/characters per real minute, so
            // WPM and CPM scale linearly with the mod rate; song select re-renders these live as the
            // selected rate mods change (see BeatmapStatistic.RateAdjusted). Line count is unaffected.
            double baseWpm = pace.AverageWpm;
            double baseCpm = pace.AverageCpm;

            yield return new BeatmapStatistic
            {
                Name = "Average WPM",
                Content = baseWpm.ToString("0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Keyboard },
                BarDisplayLength = (float)Math.Min(1, baseWpm / max_display_wpm),
                RateAdjusted = rate => ((baseWpm * rate).ToString("0"), (float?)Math.Min(1, baseWpm * rate / max_display_wpm)),
            };

            yield return new BeatmapStatistic
            {
                Name = "Average CPM",
                Content = baseCpm.ToString("0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Font },
                BarDisplayLength = (float)Math.Min(1, baseCpm / (max_display_wpm * 5)),
                RateAdjusted = rate => ((baseCpm * rate).ToString("0"), (float?)Math.Min(1, baseCpm * rate / (max_display_wpm * 5))),
            };
        }
    }
}
