// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Star rating for a lyric map. Each word carries a keystroke load (cost × line pressure/rhythm
    /// multiplier) divided by the time you have for it; that load accumulates into a per-word
    /// "strain" that decays with rest, and a duration-weighted soft maximum (log-sum-exp) over the
    /// strains is remapped to stars. The soft max means difficulty spikes dominate and a subset can
    /// never rate above its superset, while summing over every word (rather than one peak bucket)
    /// means a sustained hard stretch actually counts — the thing that separates otherwise-equal-peak
    /// diffs (e.g. an Insane that keeps a Hard's peak chorus but adds a dense a cappella ending).
    ///
    /// Kept byte-for-byte in step with the website's port
    /// (typebeat-web: Typebeat.Web.Packages.Lyrics.LyricDifficulty) so the in-game star rating and
    /// the stored beatmaps.difficulty_rating always agree. Any change here must be mirrored there,
    /// and its LyricPace.VERSION bumped so existing rows recompute.
    ///
    /// Rate-adjusting mods (DoubleTime/Nightcore/HalfTime) scale every real-time interval by
    /// <c>rate</c>: a faster clock shrinks each word's time budget, raising the rating; a slower
    /// clock lowers it.
    /// </summary>
    public static class LyricDifficulty
    {
        private const double back_to_back_grace_ms = 100; // gap below which inter-line pressure is full
        private const double back_to_back_tau_ms = 600; // pressure decay constant
        private const double back_to_back_bonus = 0.70; // max inter-line multiplier
        private const double variation_weight = 0.50; // how much rhythm cv scales a line
        private const double variation_cap = 1.5; // cv is clamped here
        private const double strain_decay_per_s = 0.05; // strain carried per second of rest
        private const double spike_focus = 14; // w — how sharply the hardest strains dominate
        private const double reference_duration_s = 0.4; // duration weight unit
        private const double star_scale = 0.277; // maps the aggregate to stars
        private const double star_power = 1.3; // stretches the hard end so top ratings spread
        private const double max_stars = 10;
        private const double per_char_floor_ms = 40; // min plausible real-time per typed character; floors a word's window at chars × this (see the strain loop)
        private const double min_span_ms = 50; // floor a word's sung span (cv guard)
        private const double repeat_window_ms = 20_000; // "last 20 seconds" for word repetition

        private readonly struct Word
        {
            public readonly string Text; // typeable, lower-case (word-repetition key)
            public readonly int Chars;
            public readonly int Runs;
            public readonly double StartMs; // real-time onset (beatmap time / rate)
            public readonly double SpanMs; // beatmap-time sung span (final-word duration fallback)
            public readonly int LineIndex;

            public Word(string text, int chars, int runs, double startMs, double spanMs, int lineIndex)
            {
                Text = text;
                Chars = chars;
                Runs = runs;
                StartMs = startMs;
                SpanMs = spanMs;
                LineIndex = lineIndex;
            }
        }

        /// <summary>Stars for the given lyric lines, under a clock <paramref name="rate"/> (1 = no mod).</summary>
        public static double Compute(IEnumerable<LyricLine> lines, double rate = 1)
        {
            var lineList = lines as IReadOnlyList<LyricLine> ?? lines.ToList();

            if (lineList.Count == 0 || rate <= 0)
                return 0;

            var words = new List<Word>();
            double[] lineMultipliers = new double[lineList.Count];
            double? prevLineEndMs = null;

            for (int li = 0; li < lineList.Count; li++)
            {
                var line = lineList[li];
                string[] tokens = line.RawText.Split(' ');

                var charDurations = new List<double>(); // per-char durations in this line (for rhythm cv)
                double lastUnitEndMs = line.StartTime;

                for (int j = 0; j < tokens.Length; j++)
                {
                    string text = typeableLower(tokens[j]);

                    if (text.Length == 0)
                        continue;

                    double unitStart, unitEnd;

                    if (j < line.Units.Count)
                    {
                        unitStart = line.Units[j].StartTime;
                        unitEnd = line.Units[j].EndTime;
                    }
                    else
                    {
                        double span = Math.Max(line.EndTime - line.StartTime, min_span_ms) / tokens.Length;
                        unitStart = line.StartTime + span * j;
                        unitEnd = unitStart + span;
                    }

                    double spanMs = Math.Max(unitEnd - unitStart, min_span_ms);
                    lastUnitEndMs = Math.Max(lastUnitEndMs, unitEnd);

                    double perChar = spanMs / text.Length;

                    for (int c = 0; c < text.Length; c++)
                        charDurations.Add(perChar);

                    words.Add(new Word(text, text.Length, countRuns(text), unitStart / rate, spanMs, li));
                }

                double pressure;

                if (prevLineEndMs is not double prevEnd)
                {
                    pressure = 0; // first line — no run-up
                }
                else
                {
                    double gap = (line.StartTime - prevEnd) / rate;
                    pressure = gap <= back_to_back_grace_ms ? 1 : Math.Exp(-(gap - back_to_back_grace_ms) / back_to_back_tau_ms);
                }

                double cv = coefficientOfVariation(charDurations);
                lineMultipliers[li] = (1 + back_to_back_bonus * pressure) * (1 + variation_weight * Math.Min(cv, variation_cap));

                prevLineEndMs = lastUnitEndMs;
            }

            if (words.Count == 0)
                return 0;

            // Per-word strain: load (cost × line multiplier) per second you have for the word, carried
            // forward with time decay. Windows are floored per-character (below) so a mistimed onset
            // can't spike a word, without over-capping legitimately fast multi-character words.
            double perCharFloorMs = per_char_floor_ms / rate;
            double strain = 0;
            double prevStartMs = words[0].StartMs;
            double maxStrain = double.NegativeInfinity;
            double[] strains = new double[words.Count];
            double[] durations = new double[words.Count];

            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];

                // Time budget for this word: until the next word begins (final word → its own span).
                double intervalMs = i + 1 < words.Count ? words[i + 1].StartMs - w.StartMs : w.SpanMs / rate;
                // Per-character floor: typing a word takes at least ~45 ms/char of real time, so its
                // window can't drop below chars × that. A flat floor treated a 1-char and a 7-char word
                // alike — over-capping fast multi-char words (exactly what separates a dense "Insane"
                // ending from a "Hard") while under-guarding crammed long words.
                double durationS = Math.Max(intervalMs, w.Chars * perCharFloorMs) / 1000.0;
                durations[i] = durationS;

                double run = 0.5 + 0.5 * ((double)w.Runs / w.Chars);
                double rep = repetitionFactor(words, i);
                double cost = (w.Chars + 1) * run * rep;
                double load = cost * lineMultipliers[w.LineIndex] / durationS;

                // Decay interval = the previous word's window; floor it by that word's char count.
                double dtFloorMs = i == 0 ? 0 : words[i - 1].Chars * perCharFloorMs;
                double dt = i == 0 ? 0 : Math.Max(w.StartMs - prevStartMs, dtFloorMs) / 1000.0;
                double carried = i == 0 ? 0 : strain * Math.Pow(strain_decay_per_s, dt);
                strain = load + carried;

                strains[i] = strain;

                if (strain > maxStrain)
                    maxStrain = strain;

                prevStartMs = w.StartMs;
            }

            double sum = 0;

            for (int i = 0; i < words.Count; i++)
                sum += durations[i] / reference_duration_s * Math.Exp((strains[i] - maxStrain) / spike_focus);

            // Duration-weighted soft maximum over the strains, remapped to stars by a power curve.
            double raw = maxStrain / spike_focus + Math.Log(sum);
            double stars = star_scale * Math.Pow(raw, star_power);

            return Math.Clamp(stars, 0, max_stars);
        }

        /// <summary>The typeable characters of a token, lower-cased.</summary>
        private static string typeableLower(string token)
        {
            var sb = new StringBuilder(token.Length);

            foreach (char c in token)
            {
                if (Typeability.IsTypeable(c))
                    sb.Append(char.ToLowerInvariant(c));
            }

            return sb.ToString();
        }

        /// <summary>Number of maximal runs of identical consecutive characters (e.g. "aaaas" = 2).</summary>
        private static int countRuns(string s)
        {
            if (s.Length == 0)
                return 0;

            int runs = 1;

            for (int i = 1; i < s.Length; i++)
            {
                if (s[i] != s[i - 1])
                    runs++;
            }

            return runs;
        }

        /// <summary>rep = max(0.6, 1 − 0.15 · repeats of this exact word within the last 20 s).</summary>
        private static double repetitionFactor(List<Word> words, int i)
        {
            double start = words[i].StartMs;
            int recent = 0;

            for (int j = i - 1; j >= 0; j--)
            {
                if (start - words[j].StartMs > repeat_window_ms)
                    break;

                if (words[j].Text == words[i].Text)
                    recent++;
            }

            return Math.Max(0.6, 1 - 0.15 * recent);
        }

        /// <summary>Population coefficient of variation (stddev / mean), 0 for fewer than two samples.</summary>
        private static double coefficientOfVariation(List<double> xs)
        {
            if (xs.Count < 2)
                return 0;

            double mean = xs.Average();

            if (mean <= 0)
                return 0;

            double variance = xs.Sum(x => (x - mean) * (x - mean)) / xs.Count;
            return Math.Sqrt(variance) / mean;
        }
    }
}
