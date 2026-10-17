// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using osu.Framework.Graphics.Sprites;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Mods;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The playable typebeat beatmap: exposes typing-pace statistics (word count, boundary-window
    /// WPM, text complexity and rhythm) which song select's statistics display computes live from the
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
        /// Peak (rolling-window) and target/average (per-line) pace for song select's metadata
        /// wedge, all derived from ONE materialised pass over the lyric lines: the curve sweep and
        /// the pace statistics read the same list rather than enumerating the hit objects twice.
        ///
        /// <para>Polyglot projects supported originals to their required input stream, including
        /// the shortest accepted romaji for Japanese, for all three figures. Target and average
        /// count Literate punctuation; the shared curve retains its published nomod behavior.
        /// Polyglot represents required punctuation and composition inputs as ordinary curve cells.
        /// The displayed peak is floored at this
        /// conversion's average (see <see cref="TypingPaceProfile.ApplyPeakFloor"/>), so it never reports a peak
        /// slower than the map's average typing pace.</para>
        /// </summary>
        public TypingPaceProfile? GetTypingPace(double rate = 1)
        {
            if (HitObjects.Count == 0)
                return null;

            bool literate = HitObjects.Any(h => h.Literate);
            var lines = paceLinesFor(HitObjects.Select(h => h.Line).ToList(), literate);
            var curve = LyricWpmCurve.Compute(lines, rate: rate);
            var pace = LyricPaceStatistics.Compute(lines, literate, rate);

            // Nothing to draw (no typeable cell at all, or fewer than 16 cells)
            // reports null so the wedge hides the section instead of showing a flat empty graph.
            if (pace.TypeableCellCount == 0 || curve.IsEmpty)
                return null;

            return new TypingPaceProfile
            {
                // The curve is recomputed at the playback rate because its minimum window is in
                // seconds. Target WPM is likewise recomputed by LyricPaceStatistics.Compute.
                WpmCurve = curve.Curve,
                PeakWpm = TypingPaceProfile.ApplyPeakFloor(curve.PeakWpm, pace.AverageWpm),
                TargetWpm = pace.TargetWpm,
                AverageWpm = pace.AverageWpm,
            };
        }

        private IReadOnlyList<LyricLine> paceLinesFor(IReadOnlyList<LyricLine> lines, bool literate)
        {
            var polyglot = HitObjects.FirstOrDefault(h => h.Polyglot);
            if (polyglot == null)
                return lines;
            Mod[] mods = literate
                ? new Mod[] { new TypeBeatModPolyglot(), new TypeBeatModLiterate() }
                : new Mod[] { new TypeBeatModPolyglot() };
            return PolyglotDifficulty.LinesFor(lines, mods, polyglot.PolyglotLanguage, forPace: true);
        }

        public override IEnumerable<BeatmapStatistic> GetStatistics()
        {
            if (HitObjects.Count == 0)
                yield break;

            // The statistics are figures of THIS beatmap, so a cell the mod added to it counts:
            // under Literate every authored mark is a typed cell (see LyricPaceStatistics.Compute),
            // and the caller converts with the selected mods for exactly this reason.
            var lines = HitObjects.Select(h => h.Line).ToList();
            bool literate = HitObjects.Any(h => h.Literate);
            var paceLines = paceLinesFor(lines, literate);
            var pace = LyricPaceStatistics.Compute(paceLines, literate);

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

            // A faster clock (DoubleTime/Nightcore) changes both pace figures, so song select
            // recomputes them as the selected rate mods change (see BeatmapStatistic.RateAdjusted).
            // The word count above is unaffected (a rate mod changes when the words arrive, not how
            // many there are), exactly
            // as the line count it replaced was, so it deliberately carries no RateAdjusted.
            //
            // THE WHOLE MAP: every cell the map makes the player TYPE (freestyle slots are not one)
            // over sung spans, with every pause inside or between lines capped at one playback
            // second. A long line weighs more than a short one. It
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

            // Both rate-sensitive pace figures are recomputed at the selected clock: a pause is
            // capped at one second of playback time, and the target's own window scan also changes
            // with the clock. Cache the shared pass for the two statistic rows.
            var pacedByRate = new Dictionary<double, LyricPaceStatistics> { [1] = pace };

            LyricPaceStatistics paceAt(double rate)
            {
                lock (pacedByRate)
                {
                    if (pacedByRate.TryGetValue(rate, out LyricPaceStatistics cached))
                        return cached;
                }

                var value = LyricPaceStatistics.Compute(paceLines, literate, rate);

                lock (pacedByRate)
                    pacedByRate[rate] = value;

                return value;
            }

            yield return new BeatmapStatistic
            {
                Name = "Average WPM",
                Content = baseWpm.ToString("0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Keyboard },
                BarDisplayLength = (float)Math.Min(1, baseWpm / max_display_wpm),
                RateAdjusted = rate =>
                {
                    double average = paceAt(rate).AverageWpm;
                    return (average.ToString("0"), (float?)Math.Min(1, average / max_display_wpm));
                },
            };

            yield return new BeatmapStatistic
            {
                Name = "Target WPM",
                Content = baseTargetWpm.ToString("0"),
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Bullseye },
                BarDisplayLength = (float)Math.Min(1, baseTargetWpm / max_display_wpm),
                // The model reads its window at the selected clock; use the same pace calculation
                // as the chart and the average row above.
                RateAdjusted = rate =>
                {
                    double target = paceAt(rate).TargetWpm;
                    return (target.ToString("0"), (float?)Math.Min(1, target / max_display_wpm));
                },
            };

            // These display scales use the same text and timing model as SR, with fixed catalogue
            // anchors. Cache only the rhythm scalar, keyed by both clock and judgement arm.
            var baseRating = LyricDifficulty.RateChunked(lines, 1, literate, LyricDifficulty.ShippedScores, ChunkedEndurance.Active);
            var rhythmLoads = new Dictionary<(double Rate, LyricDifficulty.JudgementArm Arm), double>
            {
                [(1, LyricDifficulty.JudgementArm.None)] = rhythmLoad(baseRating.Report),
            };

            double complexity = DifficultyMetricScale.Complexity(baseRating.TypabilityMeanZ);
            yield return new BeatmapStatistic
            {
                Name = "Complexity",
                Content = baseRating.TypabilityApplied ? complexity.ToString("0.00", CultureInfo.InvariantCulture) : "N/A",
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Font },
                BarDisplayLength = baseRating.TypabilityApplied ? DifficultyMetricScale.BarLength(complexity) : 0,
            };

            static double rhythmLoad(ChunkedEndurance.Report report)
                => Math.Max(report.WholeMapRhythmLoadPerSecond, report.HardestWindowRhythmLoadPerSecond);

            (string Content, float? BarDisplayLength) rhythmAt(double rate, LyricDifficulty.JudgementArm arm)
            {
                double load;
                lock (rhythmLoads)
                {
                    if (!rhythmLoads.TryGetValue((rate, arm), out load))
                    {
                        var settings = ChunkedEndurance.Active with
                        {
                            complexity_mod = arm switch
                            {
                                LyricDifficulty.JudgementArm.HardRock => ChunkedEndurance.mod_hardrock,
                                LyricDifficulty.JudgementArm.Easy => ChunkedEndurance.mod_easy,
                                _ => ChunkedEndurance.mod_none,
                            },
                        };
                        var report = LyricDifficulty.RateChunked(lines, rate, literate, LyricDifficulty.ShippedScores, settings).Report;
                        rhythmLoads[(rate, arm)] = load = rhythmLoad(report);
                    }
                }
                double rhythm = DifficultyMetricScale.Rhythm(load);
                return (rhythm.ToString("0.00", CultureInfo.InvariantCulture), DifficultyMetricScale.BarLength(rhythm));
            }

            var baseRhythm = rhythmAt(1, LyricDifficulty.JudgementArm.None);
            yield return new BeatmapStatistic
            {
                Name = "Rhythm",
                Content = baseRhythm.Content,
                CreateIcon = () => new SpriteIcon { Icon = FontAwesome.Solid.Music },
                BarDisplayLength = baseRhythm.BarDisplayLength,
                RateAdjusted = rate => rhythmAt(rate, LyricDifficulty.JudgementArm.None),
                ModAdjusted = (rate, mods) => rhythmAt(rate, PerformancePoints.JudgementArmFor(mods)),
            };
        }
    }
}
