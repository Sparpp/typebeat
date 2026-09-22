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
    /// WPM/CPM, average word length) which song select's statistics display computes live from the
    /// hit objects, always correct, never stale realm data.
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
        /// Bar cap for the average word length. English typing-test text sits at 5 cells per word
        /// (the constant WPM is defined on) and lyrics sit under it: the shipped maps measure 4.1
        /// to 4.6. A cap of 10 puts them a little under half a bar, which reads as "short words"
        /// at a glance and still leaves the top half for the long-worded outliers instead of
        /// pinning every map full.
        /// </summary>
        private const float max_display_chars_per_word = 10;

        /// <summary>
        /// Peak (rolling-window) and target/average (per-line) pace for song select's metadata
        /// wedge, all derived from ONE materialised pass over the lyric lines: the curve sweep and
        /// the pace statistics read the same list rather than enumerating the hit objects twice.
        ///
        /// <para>The TARGET and AVERAGE follow the mods this beatmap was converted with: a line the
        /// Literate mod stamped has its authored marks counted (see
        /// <see cref="LyricPaceStatistics.Compute"/>). The curve does NOT, deliberately: it is
        /// <see cref="LyricWpmCurve"/>, which is mirrored byte for byte on the website, and a map's
        /// published peak has to stay one figure across both. The peak therefore answers "how fast
        /// does this shared map ask" while the two figures beside it answer the same question about
        /// the stream this particular conversion produced.</para>
        /// </summary>
        public TypingPaceProfile? GetTypingPace(double rate = 1)
        {
            if (HitObjects.Count == 0)
                return null;

            var lines = HitObjects.Select(h => h.Line).ToList();
            var curve = LyricWpmCurve.Compute(lines);
            var pace = LyricPaceStatistics.Compute(lines, HitObjects.Any(h => h.Literate), rate);

            // Nothing to draw (no typeable cell at all, or fewer than one rolling window's worth)
            // reports null so the wedge hides the section instead of showing a flat empty graph.
            if (pace.TypeableCellCount == 0 || curve.IsEmpty)
                return null;

            return new TypingPaceProfile
            {
                // The curve's window is a run of CELLS, not a span of seconds, so a rate mod moves every
                // sample by exactly the rate: scaling here is the same reading as recomputing, and it
                // leaves the graph's normalised shape alone. The TARGET is the one figure that has to go
                // back through the model - see LyricPaceStatistics.Compute.
                WpmCurve = curve.Curve.Select(wpm => wpm * rate).ToArray(),
                PeakWpm = curve.PeakWpm * rate,
                TargetWpm = pace.TargetWpm,
                AverageWpm = pace.AverageWpm,
            };
        }

        public override IEnumerable<BeatmapStatistic> GetStatistics()
        {
            if (HitObjects.Count == 0)
                yield break;

            // The statistics are figures of THIS beatmap, so a cell the mod added to it counts:
            // under Literate every authored mark is a typed cell (see LyricPaceStatistics.Compute),
            // and the caller converts with the selected mods for exactly this reason.
            var pace = LyricPaceStatistics.Compute(HitObjects.Select(h => h.Line), HitObjects.Any(h => h.Literate));

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

            // A faster clock (DoubleTime/Nightcore) means more words per real minute, so both pace
            // figures scale linearly with the mod rate; song select re-renders these live as the
            // selected rate mods change (see BeatmapStatistic.RateAdjusted). The word count above is
            // unaffected (a rate mod changes when the words arrive, not how many there are), exactly
            // as the line count it replaced was, so it deliberately carries no RateAdjusted.
            //
            // THE WHOLE MAP, with the song's breaks taken out: every cell the map makes the player
            // TYPE (freestyle slots are not one) over the total time it is sung, where a pause counts
            // up to the break threshold and is dropped whole beyond it — so an instrumental between
            // two lines is not charged to the player and a long line weighs more than a short one. It
            // replaced an unweighted mean of per-line rates, which gave every line one vote and put
            // the pause after each line inside that line's own window. LyricPaceStatistics keeps both.
            double baseWpm = pace.AverageWpm;

            // The pace to SUSTAIN: the map's hardest window BY RAW SPEED, re-expressed at a fixed
            // duration (LyricPaceStatistics.TargetWpm). Typability and the rhythm bonus are left out
            // of both the window choice and the conversion, so a slower-to-type or rhythmically
            // forced stretch can win the rating while a plainly faster one still asks for more speed;
            // it is therefore not always the rating's own peak window. It is the figure the Star
            // Rating Sandbox prints beside every map, and it reads 0 when no window clears the
            // model's own duration and character floors.
            double baseTargetWpm = pace.TargetWpm;

            // THE ONE FIGURE THAT HAS TO BE READ AGAIN RATHER THAN RATED. A faster clock does not merely
            // scale the target: the model bins its timeline in REAL milliseconds, so the scan that picks
            // the hardest window runs against different readings and can even name a different window.
            // The figure is therefore recomputed through the model at the clock (see
            // LyricPaceStatistics.Compute) - memoised per rate, because that is a scan over the whole map,
            // and the caller renders these rows off the update thread for exactly this reason.
            var targets = new Dictionary<double, double>();

            double targetAt(double rate)
            {
                lock (targets)
                {
                    if (targets.TryGetValue(rate, out double cached))
                        return cached;
                }

                double value = LyricPaceStatistics
                    .Compute(HitObjects.Select(h => h.Line), HitObjects.Any(h => h.Literate), rate)
                    .TargetWpm;

                lock (targets)
                    targets[rate] = value;

                return value;
            }

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
                Name = "Target WPM",
                Content = baseTargetWpm.ToString("0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Bullseye },
                BarDisplayLength = (float)Math.Min(1, baseTargetWpm / max_display_wpm),
                // NOT a rate, and not a cached multiply either: the figure comes from the model read at
                // the clock (see targetAt above), so it is the same number the pace chart prints for the
                // same map. The Average WPM above IS a rate and simply scales, which is why the two rows
                // move by different factors on the same toggle.
                RateAdjusted = rate =>
                {
                    double target = targetAt(rate);
                    return (target.ToString("0"), (float?)Math.Min(1, target / max_display_wpm));
                },
            };

            // Still here, and still worth a column, now that the CPM it used to explain has left the
            // strip: both rates to its left are cells/5 flat, so this says how far the map's own
            // words are from the 5 the unit assumes. It is the ratio the old real-word WPM used to
            // encode implicitly (CPM:WPM), now printed rather than left to be divided out.
            //
            // One decimal is not decoration: the five shipped maps measure 4.13, 4.31, 4.57, 4.11
            // and 4.47 cells per word, so rounding to whole characters would print "4" for every
            // single one of them and the statistic would carry no information at all.
            //
            // Rate-independent, exactly like the word count: a speed mod changes when the
            // characters arrive, not how many of them make up a word. Hence no RateAdjusted.
            double baseCharsPerWord = pace.AverageCharsPerWord;

            yield return new BeatmapStatistic
            {
                Name = "Chars/word",
                Content = baseCharsPerWord.ToString("0.0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.TextWidth },
                BarDisplayLength = (float)Math.Min(1, baseCharsPerWord / max_display_chars_per_word),
            };
        }
    }
}
