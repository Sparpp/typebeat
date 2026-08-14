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
    /// The playable typebeat beatmap: exposes typing-pace statistics (word count, boundary-window
    /// WPM/CPM) which song select's statistics display computes live from the hit objects,
    /// always correct, never stale realm data.
    /// </summary>
    public class TypeBeatBeatmap : Beatmap<TypeBeatHitObject>, IHasTypingPace
    {
        /// <summary>Display normalisation caps for the statistic bars.</summary>
        private const float max_display_wpm = 150;

        /// <summary>
        /// Bar cap for the word count, replacing the cap of 100 the bar carried while it showed
        /// lines. The shipped maps run 4.9 to 7.1 words per line (mean about 5.6), so 100 lines is
        /// roughly 560 words: 600 keeps the bar saturating at about the same map length it used to,
        /// where reusing 100 would pin it full on every map longer than twenty lines.
        /// </summary>
        private const float max_display_words = 600;

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

            // How much typing the map is, in the unit the player thinks in. The total comes from the
            // same pass that produces Average WPM below, so the two can never disagree: a word is a
            // space-separated token of the DEFAULT stream holding at least one typeable cell, which
            // is not RawText.Split(' ').Length (that overcounts punctuation-only tokens). The one
            // definition lives in LyricPaceStatistics; never re-derive it here.
            //
            // Paragraph rather than the old AlignLeft, whose stack of rules read as "lines", and
            // distinct from the Keyboard and Font glyphs the two rate statistics below use.
            yield return new BeatmapStatistic
            {
                Name = "Words",
                Content = pace.WordCount.ToString("N0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Paragraph },
                BarDisplayLength = Math.Min(1, pace.WordCount / max_display_words),
            };

            // A faster clock (DoubleTime/Nightcore) means more words/characters per real minute, so
            // WPM and CPM scale linearly with the mod rate; song select re-renders these live as the
            // selected rate mods change (see BeatmapStatistic.RateAdjusted). The word count above is
            // unaffected (a rate mod changes when the words arrive, not how many there are), exactly
            // as the line count it replaced was, so it deliberately carries no RateAdjusted.
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
