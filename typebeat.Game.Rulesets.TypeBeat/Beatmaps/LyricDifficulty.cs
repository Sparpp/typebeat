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
    /// multiplier) divided by the time you have for it; that load feeds two accumulators, a fast
    /// DENSITY that decays with rest and a slow ENDURANCE (a time-constant moving average of the
    /// same load), and their weighted sum is the word's "strain". A duration-weighted soft maximum
    /// (log-sum-exp) over the strains is remapped to stars. The soft max means difficulty spikes
    /// dominate and a subset can never rate above its superset, while summing over every word
    /// (rather than one peak bucket) means a sustained hard stretch actually counts, the thing that
    /// separates otherwise-equal-peak diffs (e.g. an Insane that keeps a Hard's peak chorus but adds
    /// a dense a cappella ending). Endurance is the part that survives a rest: density is deliberately
    /// leaky enough now that a single burst is forgotten within a second or two, so it is endurance
    /// that carries "this section has been hard for a while" into the aggregate.
    ///
    /// A flat per-decade LENGTH bonus is added to that (see <c>length_stars</c>), which is the only
    /// place the game prices a map for being long: pp carried a length factor until backlog 152 and
    /// no longer does.
    ///
    /// A FREESTYLE slot counts as a quarter of a cell throughout (see <c>freestyle_cost_weight</c>),
    /// rather than as nothing at all, which is what it counted as until backlog 211.
    ///
    /// Kept byte-for-byte in step with the website's port
    /// (typebeat-web: Typebeat.Web.Packages.Lyrics.LyricDifficulty) so the in-game star rating and
    /// the stored beatmaps.difficulty_rating always agree. Any change here must be mirrored there,
    /// and its LyricPace.VERSION bumped so existing rows recompute.
    ///
    /// Rate-adjusting mods (DoubleTime/Nightcore/HalfTime) scale every real-time interval by
    /// <c>rate</c>: a faster clock shrinks each word's time budget, raising the rating; a slower
    /// clock lowers it.
    ///
    /// The LITERATE mod moves the rating the same way, through <c>literate</c>: it is
    /// IApplicableAfterBeatmapConversion, so the cells it produces ARE the authored chars and every
    /// supported punctuation mark becomes a real typed cell with a target time of its own. That
    /// lengthens words, changes a line's rhythm and therefore changes the map's difficulty, so the
    /// mod is priced through this rating exactly as a rate is and carries no flat pp multiplier of
    /// its own (docs/pp.md). At <c>literate: false</c> every word is the stripped, lower-case stream
    /// this method has always measured, so no existing rating moves.
    /// </summary>
    public static class LyricDifficulty
    {
        private const double back_to_back_grace_ms = 100; // gap below which inter-line pressure is full
        private const double back_to_back_tau_ms = 600; // pressure decay constant
        private const double back_to_back_bonus = 0.70; // max inter-line multiplier
        private const double variation_weight = 0.40; // how much rhythm cv scales a line
        private const double variation_cap = 1.5; // cv is clamped here
        private const double strain_decay_per_s = 0.12; // density carried per second of rest
        private const double endurance_tau_s = 8.0; // burst memory: the ema's time constant, seconds
        private const double endurance_weight = 1.5; // how much sustained load adds on top of density
        private const double spike_focus = 14; // w: how sharply the hardest strains dominate
        private const double reference_duration_s = 0.4; // duration weight unit
        // Maps the aggregate to stars. Calibrated against the LIVE RANKED CATALOGUE rather than
        // local reference maps: measured across all 31 ranked difficulties, "(It Goes Like)
        // Nanana x Cola [Extreme]" is the hardest thing published and sits at 7.81 here, so the
        // whole ranked pool reads inside a single star decade without anything being cut to fit.
        // An earlier pass fitted this to a local map harder than anything ranked and cut the whole
        // catalogue to a mean 0.45 of its old rating; the lesson is that the calibration has to
        // come from what players can actually play. Stars are LINEAR in this constant, so moving
        // it alone is a pure rescale and cannot reorder anything (per_char_floor_ms can, and
        // did).
        private const double star_scale = 0.23;
        private const double star_power = 1.3; // stretches the hard end so top ratings spread

        /// <summary>
        /// The map's LENGTH, priced here and nowhere else (backlog 152). Stars gain
        /// <c>length_stars · max(0, log10(cells/reference_cells))</c> on top of the strain curve, so
        /// a decade more typing is worth 0.12 of a star and the live catalogue moves by at most
        /// +0.17.
        ///
        /// <para>ADDITIVE, NOT A MULTIPLIER, and that is the whole design. Length is a SOFT signal:
        /// "there is simply more of it" is worth the same on a 2 star map and on an 8 star one,
        /// where a multiplier would move the hardest maps the most, which is exactly the wrong
        /// shape. Rhythm density and pace stay the HARD signals, through the strain model above.</para>
        ///
        /// <para>pp used to carry this instead, as <c>max(0.1, 1 + 0.50·log10(notes/100))</c>, worth
        /// up to 1.70x. It was deleted in the same change: two length terms would double count, and
        /// a length term inside pp paid a long map far more than the difficulty it actually adds.
        /// pp now sees length only through this rating, i.e. as
        /// <c>((SR + bonus)/SR)^sr_exponent</c>.</para>
        ///
        /// <para>THE max(0, ·) CLAMP is not decorative: it gives a sub-100-cell map exactly nothing,
        /// which is what keeps every short synthetic fixture rating byte-identically to what it did
        /// before this term existed. Without it a 10-cell fixture would lose 0.12 of a star.</para>
        ///
        /// <para>Note the bonus is INDEPENDENT OF RATE (a clock change does not add or remove cells)
        /// and DEPENDENT ON LITERATE (its punctuation marks are real cells). So a rate ratio such as
        /// <c>sr_dt/difficulty_rating</c> compresses slightly, both sides having gained the same
        /// constant, which is accepted and uncompensated.</para>
        /// </summary>
        private const double length_stars = 0.12;

        private const double reference_cells = 100; // the length bonus' pivot: 100 cells is the zero point

        /// <summary>
        /// What one FREESTYLE slot is worth, as a fraction of an ordinary cell (backlog 211).
        ///
        /// <para>A freestyle slot is a real keypress with a real deadline (backlog 209 gave it its own
        /// character target, so it is timed like any other cell), it just has no letter to find. It
        /// used to be worth NOTHING here, excluded from the cell stream outright, while paying full
        /// price in scoring: a freestyle section was an accuracy and combo farm inside a star rating
        /// that never saw it. A quarter is the price of the deadline without the letter.</para>
        ///
        /// <para>The weight enters in exactly three places, all of them cell COUNTS rather than cell
        /// TEXT: the per-word <c>cost</c>, the per-character window floor those same words are
        /// measured against, and the length bonus' cell accumulator. See <see cref="Word.Weight"/>.
        /// It deliberately does NOT enter the run factor or the word-repetition key, which read the
        /// stream TEXT: a marker carries no glyph, so putting one into that string would perturb the
        /// bigram statistics of the letters either side of it.</para>
        ///
        /// <para>At 0 this file computes exactly what it computed before backlog 211, and on a map
        /// with no markers at all it does so at any weight: every number below is bit-identical when
        /// the freestyle count is zero.</para>
        /// </summary>
        private const double freestyle_cost_weight = 0.25;
        private const double per_char_floor_ms = 50; // min plausible real-time per typed character; floors a word's window at chars × this (see the strain loop)
        private const double min_span_ms = 50; // floor a word's sung span (cv guard)
        private const double repeat_window_ms = 20_000; // "last 20 seconds" for word repetition

        private readonly struct Word
        {
            public readonly string Text; // the word's FIXED-KEY cells (word-repetition key; markerless)
            public readonly int Chars; // Text.Length, i.e. fixed-key cells only
            public readonly int Freestyle; // freestyle slots in the word, which carry no text
            public readonly int Runs;
            public readonly double StartMs; // real-time onset (beatmap time / rate)
            public readonly double SpanMs; // beatmap-time sung span (final-word duration fallback)
            public readonly int LineIndex;

            /// <summary>
            /// The word's PRICED cell count: a fixed-key cell is worth 1 and a freestyle slot
            /// <see cref="freestyle_cost_weight"/>. Fractional, and equal to <see cref="Chars"/>
            /// exactly (not merely nearly) on a word with no markers.
            /// </summary>
            public double Weight => Chars + freestyle_cost_weight * Freestyle;

            public Word(string text, int chars, int freestyle, int runs, double startMs, double spanMs, int lineIndex)
            {
                Text = text;
                Chars = chars;
                Freestyle = freestyle;
                Runs = runs;
                StartMs = startMs;
                SpanMs = spanMs;
                LineIndex = lineIndex;
            }
        }

        /// <summary>
        /// Stars for the given lyric lines, under a clock <paramref name="rate"/> (1 = no mod) and
        /// the cell stream <paramref name="literate"/> selects (false = the default stripped,
        /// lower-case stream; true = the authored chars, marks included, i.e. the Literate mod).
        /// </summary>
        public static double Compute(IEnumerable<LyricLine> lines, double rate = 1, bool literate = false)
        {
            var lineList = lines as IReadOnlyList<LyricLine> ?? lines.ToList();

            if (lineList.Count == 0 || rate <= 0)
                return 0;

            var words = new List<Word>();
            double[] lineMultipliers = new double[lineList.Count];
            double? prevLineEndMs = null;
            // The map's priced cell count, for the length bonus at the end. Counted HERE, off the
            // very cells the strain model measures, rather than taken as a parameter: a caller
            // supplied count is a second definition of "how long is this map", and the client and
            // the server would eventually disagree about it on the same map. FRACTIONAL, because a
            // freestyle slot is a quarter of a cell here too (freestyle_cost_weight): one knob, and
            // a map cannot be long for the length bonus while being short for the strain model.
            double cells = 0;

            for (int li = 0; li < lineList.Count; li++)
            {
                var line = lineList[li];
                string[] tokens = line.RawText.Split(' ');

                var charDurations = new List<double>(); // per-char durations in this line (for rhythm cv)
                double lastUnitEndMs = line.StartTime;

                for (int j = 0; j < tokens.Length; j++)
                {
                    string text = cellStream(tokens[j], literate);
                    int freestyle = freestyleSlots(tokens[j]);

                    // A token with neither fixed-key cells nor freestyle slots is not typed at all.
                    // A token of NOTHING BUT markers ("&&&&") is: it used to fall out here, taking a
                    // whole mashable word out of the map, and is now a word of weight 0 + n/4.
                    if (text.Length == 0 && freestyle == 0)
                        continue;

                    cells += text.Length + freestyle_cost_weight * freestyle;

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

                    // The line's rhythm cv is measured over the FIXED-KEY cells only, so a marker
                    // neither splits the line finer nor stretches it: it has no glyph whose arrival
                    // could be early or late relative to its neighbours, which is the thing cv is
                    // about. A word of nothing but markers therefore adds no sample at all.
                    if (text.Length > 0)
                    {
                        double perChar = spanMs / text.Length;

                        for (int c = 0; c < text.Length; c++)
                            charDurations.Add(perChar);
                    }

                    words.Add(new Word(text, text.Length, freestyle, countRuns(text), unitStart / rate, spanMs, li));
                }

                double pressure;

                if (prevLineEndMs is not double prevEnd)
                {
                    pressure = 0; // first line, no run-up
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

            // Per-word strain, from two accumulators over the same per-word load (cost × line
            // multiplier, per second you have for the word):
            //
            //  - DENSITY, the fast one: load plus whatever survives strain_decay_per_s of rest. This
            //    is what spikes on a burst and what falls away again within a second or two of quiet.
            //  - ENDURANCE, the slow one: an exponential moving average of the same load with an
            //    endurance_tau_s time constant, so a stretch that stays busy keeps a floor under the
            //    rating long after any single burst has decayed out of density.
            //
            // Windows are floored per-character (below) so a mistimed onset can't spike a word,
            // without over-capping legitimately fast multi-character words.
            double perCharFloorMs = per_char_floor_ms / rate;
            double density = 0;
            double endurance = 0;
            double prevStartMs = words[0].StartMs;
            double maxStrain = double.NegativeInfinity;
            double[] strains = new double[words.Count];
            double[] durations = new double[words.Count];

            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];

                // Time budget for this word: until the next word begins (final word → its own span).
                double intervalMs = i + 1 < words.Count ? words[i + 1].StartMs - w.StartMs : w.SpanMs / rate;
                // Per-character floor: typing a word takes at least ~50 ms/char of real time, so its
                // window can't drop below cells × that. A flat floor treated a 1-char and a 7-char word
                // alike; over-capping fast multi-char words (exactly what separates a dense "Insane"
                // ending from a "Hard") while under-guarding crammed long words. Cells, not chars:
                // a freestyle slot buys a quarter of the floor, the same quarter it pays in cost.
                double durationS = Math.Max(intervalMs, w.Weight * perCharFloorMs) / 1000.0;
                durations[i] = durationS;

                // The run factor reads the word's TEXT, so it is computed over the fixed-key cells
                // alone: a marker has no glyph to repeat or to alternate with. A word with no fixed
                // keys at all has no such structure either, and takes the neutral 1.
                double run = 0.5 + 0.5 * (w.Chars > 0 ? (double)w.Runs / w.Chars : 1);
                double rep = repetitionFactor(words, i);
                double cost = (w.Weight + 1) * run * rep;
                double load = cost * lineMultipliers[w.LineIndex] / durationS;

                // Decay interval = the previous word's window; floor it by that word's char count.
                double dtFloorMs = i == 0 ? 0 : words[i - 1].Weight * perCharFloorMs;
                double dt = i == 0 ? 0 : Math.Max(w.StartMs - prevStartMs, dtFloorMs) / 1000.0;
                double carried = i == 0 ? 0 : density * Math.Pow(strain_decay_per_s, dt);
                density = load + carried;

                // Spacing-invariant EMA: alpha derives from ELAPSED TIME, not from the word index, so
                // a burst of short words and a burst of long words at the same characters per second
                // build the same endurance. An index-based alpha would instead reward whichever
                // section happened to be chopped into more tokens.
                if (i == 0)
                    endurance = load;
                else
                    endurance += (1 - Math.Exp(-dt / endurance_tau_s)) * (load - endurance);

                strains[i] = density + endurance_weight * endurance;

                // maxStrain tracks the COMBINED value, which is what the aggregate below subtracts:
                // Math.Exp((strains[i] - maxStrain) / spike_focus) is only bounded by 1 when maxStrain
                // is the max over strains. Tracking density alone would let that exponent go positive.
                if (strains[i] > maxStrain)
                    maxStrain = strains[i];

                prevStartMs = w.StartMs;
            }

            double sum = 0;

            for (int i = 0; i < words.Count; i++)
                sum += durations[i] / reference_duration_s * Math.Exp((strains[i] - maxStrain) / spike_focus);

            // Duration-weighted soft maximum over the strains, remapped to stars by a power curve,
            // plus the flat per-decade length bonus (see length_stars).
            double raw = maxStrain / spike_focus + Math.Log(sum);
            double stars = star_scale * Math.Pow(raw, star_power) + length_stars * Math.Max(0, Math.Log10(cells / reference_cells));

            // THERE IS NO CEILING HERE, deliberately (backlog 118). One used to live on this line,
            // a flat 10 chosen to keep a star BADGE sane, and it truncated far more than a badge:
            // this same method produces the pp INPUTS, the ratings the server stores as sr_dt
            // (rate 1.50) and sr_ht (0.75), and PerformancePoints prices a rate play purely
            // through the RATIO of those to the base rating. A ceiling makes that ratio wrong the
            // moment either side touches it, and it is the up-rate side that touches it: measured
            // over the five real reference maps, sr_dt hit the old 10 on three of them while no
            // base rating and no sr_ht came near it, and the hardest ranked difficulty published
            // reads 7.81, so every truncation the ceiling ever performed was on a number it was
            // not chosen for. On two of those three the truncated ratio also pushed
            // HalfTimeMultiplier's mirror past 1.0 and dropped it onto its flat fallback: Siames
            // "The Wolf" rates sr_dt 16.33 rather than 10.00, which is Double Time's factor 2.85
            // rather than 1.07, and an HT mirror of 0.762 rather than the flat 0.70. Bounding a
            // star READOUT is a presentation decision and belongs at the surface that draws one.
            //
            // The FLOOR stays. A negative rating describes no map, and callers divide by this.
            return Math.Max(stars, 0);
        }

        /// <summary>
        /// The cells of one token, i.e. what the player actually has to type for it.
        ///
        /// <para>WITHOUT LITERATE that is the typeable characters, lower-cased: marks are not cells
        /// at all (<see cref="Typeability.IsTypeable"/> excludes them on purpose) and case is folded
        /// because the caret matches case-insensitively.</para>
        ///
        /// <para>WITH LITERATE the cells ARE the authored chars, so every supported
        /// <see cref="Typeability.PUNCTUATION"/> mark joins them and case is KEPT. Both of those
        /// carry real difficulty and both are load-bearing here: a mark lengthens its word (raising
        /// <c>cost</c>, and the per-character window floor with it) and splits the line's rhythm
        /// finer (moving <c>cv</c>), while keeping case means a capital counts as a distinct char
        /// for the run factor and for word repetition, which is what it is under this mod (a
        /// held Shift, and a target a lower-case press is judged WRONG against).</para>
        ///
        /// <para>Freestyle slots are not in this STRING under either stream, and that is a statement
        /// about text, not about price. They carry no fixed key, so there is no glyph to put here
        /// and none of the text statistics built on this string (the run factor, the word-repetition
        /// key) can say anything about them; a marker sitting in the middle of a word would instead
        /// falsify the bigram structure of the letters either side of it. They ARE priced, at
        /// <see cref="freestyle_cost_weight"/>, through <see cref="freestyleSlots"/> and
        /// <see cref="Word.Weight"/>. Literate does not constrain them either, so the quarter is the
        /// same under both streams.</para>
        /// </summary>
        private static string cellStream(string token, bool literate)
        {
            var sb = new StringBuilder(token.Length);

            foreach (char c in token)
            {
                if (Typeability.IsTypeable(c))
                    sb.Append(literate ? c : char.ToLowerInvariant(c));
                else if (literate && Typeability.IsPunctuation(c))
                    sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// The token's FREESTYLE slots, the other half of its cell count (see
        /// <see cref="cellStream"/>): cells the player must hit on time with any key at all.
        /// Independent of the Literate stream, since that mod does not constrain a slot's key.
        /// </summary>
        private static int freestyleSlots(string token)
        {
            int n = 0;

            foreach (char c in token)
            {
                if (Typeability.IsFreestyle(c))
                    n++;
            }

            return n;
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
