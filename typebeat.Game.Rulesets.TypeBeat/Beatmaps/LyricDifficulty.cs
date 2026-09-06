// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Star rating for a lyric map: how fast the map asks you to type, measured against how fast
    /// the fastest humans can type, over sliding windows of many durations (backlog 269).
    ///
    /// <para>THE TIMELINE. Every typeable word is a block running from its onset to the end of its
    /// sung span, in REAL seconds (beatmap time divided by the clock <c>rate</c>, span floored at
    /// <c>min_span_ms</c>), and the block's CELLS are spread uniformly across that span into
    /// <c>timeline_bin_ms</c> bins. A word's cells are its fixed-key characters plus
    /// <c>freestyle_cost_weight</c> per freestyle slot, plus ONE TRAILING SPACE when the next word
    /// belongs to the same line: the spacebar is a real keypress and the pace figures have always
    /// counted it, so the rating counts it too.</para>
    ///
    /// <para>THE CAPABILITY CURVE. <c>S(t) = capability_base_wpm + capability_burst_wpm *
    /// (capability_ref_seconds / t) ^ capability_exponent</c> is the WPM the fastest humans sustain
    /// for t seconds; it sits within 1.5% of the burst, 15 s, 60 s, 120 s and 1 hour typing records.
    /// A window of t seconds holding <c>c</c> cells therefore has ratio
    /// <c>(c / 5) / (t / 60) / S(t)</c>, its pace as a fraction of the record pace FOR ITS OWN
    /// DURATION, which is what makes a 1.4 second burst and a 60 second verse comparable numbers.
    /// Windows are drawn from a fixed schedule (see <see cref="windowSchedule"/>).</para>
    ///
    /// <para>THE FEATS. Greedily take the highest-ratio window at ANY scheduled duration whose bins
    /// are all still unconsumed, record it, consume its bins, repeat. Feats come out in
    /// non-increasing ratio order and no second of the map is counted twice: a two-word burst is
    /// found as a 1.4 second window, a sustained verse as a 60 second one, and the two do not
    /// double count each other. Stars are the weighted sum
    /// <c>stars_at_human_peak * (1 - feat_decay) * sum_k feat_decay^k * ratio_k</c> plus the length
    /// bonus, so record pace for the whole map rates <c>stars_at_human_peak</c> and one lone
    /// record-pace feat rates 0.75 of it. Every feat is scored against HUMAN ABILITY rather than
    /// against the map's own peak, which is what makes a cut version unable to outrate the full
    /// version it was cut from.</para>
    ///
    /// <para>A MAP SHORTER THAN THE SMALLEST WINDOW rates the length term alone, i.e. very close to
    /// zero. <c>window_min_s</c> of bins do not fit on its timeline, so no scheduled window fits,
    /// no feat is found and the feats sum is 0. Windows are deliberately NOT clamped down to the
    /// map: the ratio only means anything against <c>S(t)</c> at the window's own duration, and a
    /// map with under a second and a half of singing in it is not a difficulty. Every synthetic
    /// fixture in the test suites is built long enough to clear that.</para>
    ///
    /// <para>Kept byte-for-byte in step with the website's port
    /// (typebeat-web: Typebeat.Web.Packages.Lyrics.LyricDifficulty) so the in-game star rating and
    /// the stored beatmaps.difficulty_rating always agree. Any change here must be mirrored there,
    /// and its LyricPace.VERSION bumped so existing rows recompute. The model itself is
    /// docs/sr-feats-model.js in the parent superrepo, a pure function of (map, rate, constants)
    /// that this file is a literal port of.</para>
    ///
    /// <para>Rate-adjusting mods (DoubleTime/Nightcore/HalfTime) divide every beatmap-time interval
    /// by <c>rate</c> while the window schedule stays in REAL seconds: a faster clock packs the same
    /// cells into fewer seconds, raising every ratio; a slower clock lowers them.</para>
    ///
    /// <para>The LITERATE mod moves the rating the same way, through <c>literate</c>: it is
    /// IApplicableAfterBeatmapConversion, so the cells it produces ARE the authored chars and every
    /// supported punctuation mark becomes a real typed cell with a target time of its own. That
    /// makes the map denser, so the mod is priced through this rating exactly as a rate is and
    /// carries no flat pp multiplier of its own (docs/pp.md). At <c>literate: false</c> every word
    /// is the stripped, lower-case stream this method has always measured.</para>
    /// </summary>
    public static class LyricDifficulty
    {
        // The capability curve S(t), the WPM the fastest humans sustain for t seconds. Fitted to the
        // published typing records: within 1.5% of the burst, 15 s, 60 s, 120 s and 1 hour marks.
        private const double capability_base_wpm = 220; // the asymptote a human holds indefinitely
        private const double capability_burst_wpm = 221; // how much more a burst is worth at the reference
        private const double capability_ref_seconds = 1.35; // the burst record's own duration
        private const double capability_exponent = 0.35; // how fast the burst premium decays with t

        // What a map that types at the record pace THROUGHOUT is worth. Chosen 2026-09-06 against
        // the live ranked catalogue: the hardest thing published lands just above 10, so the pool
        // reads inside a single star decade without anything being cut to fit. Stars are LINEAR in
        // this constant, so moving it alone is a pure rescale and cannot reorder anything.
        private const double stars_at_human_peak = 10.6;

        // The feats: geometric weight per rank, so the hardest feat is (1 - feat_decay) of a
        // saturated map and each subsequent one is a quarter of the last. The sum is normalised by
        // (1 - feat_decay) so an infinite run of record-pace feats converges on stars_at_human_peak.
        private const double feat_decay = 0.25;
        private const int feat_max = 8; // hard cap on feats, before the weight epsilon below bites
        private const double feat_floor = 0.05; // a window under 5% of record pace is not a feat
        private const double feat_min_seconds = 0; // secondary feats must span at least this long (0 = any window)

        private const double timeline_bin_ms = 50; // timeline resolution
        private const double window_min_s = 1.36; // the smallest scheduled window: the 51-char burst record

        // Stop taking feats once feat_decay^k drops below this. At feat_decay 0.25 that is five
        // feats, well inside feat_max; the rule is kept because it is the prototype's, and the port
        // has to reproduce the prototype rather than merely resemble it.
        private const double feat_weight_epsilon = 1e-3;

        // Guards the uniform spread below against a zero-width block. A span is floored at
        // min_span_ms before the rate divide, so this can only be reached by an absurd rate.
        private const double density_epsilon = 1e-9;

        /// <summary>
        /// The map's LENGTH, priced here and nowhere else (backlog 152). Stars gain
        /// <c>length_stars · max(0, log10(cells/reference_cells))</c> on top of the feats sum, so
        /// a decade more typing is worth 0.12 of a star.
        ///
        /// <para>ADDITIVE, NOT A MULTIPLIER, and that is the whole design. Length is a SOFT signal:
        /// "there is simply more of it" is worth the same on a 2 star map and on an 8 star one,
        /// where a multiplier would move the hardest maps the most, which is exactly the wrong
        /// shape. Pace against human capability stays the HARD signal, through the feats above.</para>
        ///
        /// <para>pp used to carry this instead, as <c>max(0.1, 1 + 0.50·log10(notes/100))</c>, worth
        /// up to 1.70x. It was deleted in the same change: two length terms would double count, and
        /// a length term inside pp paid a long map far more than the difficulty it actually adds.
        /// pp now sees length only through this rating, i.e. as
        /// <c>((SR + bonus)/SR)^sr_exponent</c>.</para>
        ///
        /// <para>THE CELLS ARE THE SAME CELLS THE FEATS ARE MEASURED ON, which since backlog 269
        /// means they INCLUDE the inter-word SPACES (one per word whose successor is on the same
        /// line) as well as the quarter each freestyle slot is worth. One definition of "how much
        /// typing is in this map", so a map cannot be long for the length bonus while being short
        /// for the pace model.</para>
        ///
        /// <para>THE max(0, ·) CLAMP is not decorative: it gives a sub-100-cell map exactly nothing.
        /// Without it a 10-cell fixture would lose 0.12 of a star.</para>
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
        /// <para>The weight enters through <see cref="Word.Weight"/> alone, i.e. as a cell COUNT
        /// rather than as a character of the stream, and that count reaches both places cells are
        /// read: the density spread over the timeline bins, and the length bonus' accumulator. It
        /// deliberately does NOT enter the cell stream TEXT: a marker carries no glyph, and putting
        /// one into that string would make it a character the Literate stream had to have an opinion
        /// about.</para>
        ///
        /// <para>At 0 this file computes exactly what it computed before backlog 211, and on a map
        /// with no markers at all it does so at any weight: every number below is bit-identical when
        /// the freestyle count is zero.</para>
        /// </summary>
        private const double freestyle_cost_weight = 0.25;
        private const double min_span_ms = 50; // floor a word's sung span, in beatmap time, before the rate divide

        private readonly struct Word
        {
            public readonly int Chars; // fixed-key cells only
            public readonly int Freestyle; // freestyle slots in the word, which carry no text
            public readonly double StartMs; // real-time onset (beatmap time / rate)
            public readonly double SpanMs; // real-time sung span (floored, then / rate)
            public readonly int LineIndex;

            /// <summary>
            /// The word's PRICED cell count BEFORE its trailing space: a fixed-key cell is worth 1
            /// and a freestyle slot <see cref="freestyle_cost_weight"/>. Fractional, and equal to
            /// <see cref="Chars"/> exactly (not merely nearly) on a word with no markers.
            /// </summary>
            public double Weight => Chars + freestyle_cost_weight * Freestyle;

            public Word(int chars, int freestyle, double startMs, double spanMs, int lineIndex)
            {
                Chars = chars;
                Freestyle = freestyle;
                StartMs = startMs;
                SpanMs = spanMs;
                LineIndex = lineIndex;
            }
        }

        /// <summary>A word reduced to what the timeline needs: when it is sung, for how long, and how many cells it costs.</summary>
        private readonly struct Block
        {
            public readonly double StartMs;
            public readonly double SpanMs;
            public readonly double Cells;

            public Block(double startMs, double spanMs, double cells)
            {
                StartMs = startMs;
                SpanMs = spanMs;
                Cells = cells;
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

            for (int li = 0; li < lineList.Count; li++)
            {
                var line = lineList[li];
                string[] tokens = line.RawText.Split(' ');

                for (int j = 0; j < tokens.Length; j++)
                {
                    string text = cellStream(tokens[j], literate);
                    int freestyle = freestyleSlots(tokens[j]);

                    // A token with neither fixed-key cells nor freestyle slots is not typed at all.
                    // A token of NOTHING BUT markers ("&&&&") is: it used to fall out here, taking a
                    // whole mashable word out of the map, and is now a word of weight 0 + n/4.
                    if (text.Length == 0 && freestyle == 0)
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

                    // Both figures leave this loop in REAL time: the span floor is applied in
                    // beatmap time (it is a floor on the AUTHORING, not on the player's clock) and
                    // the divide happens after it, exactly as the onset's does.
                    words.Add(new Word(text.Length, freestyle, unitStart / rate, Math.Max(unitEnd - unitStart, min_span_ms) / rate, li));
                }
            }

            if (words.Count == 0)
                return 0;

            var blocks = new List<Block>(words.Count);

            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];

                // THE TRAILING SPACE, decided in CONSTRUCTION order (before the sort below) because
                // "the next word" means the next word of the lyric, not the next word in time. A
                // word ending its line is followed by a line break rather than a spacebar press, so
                // it gets none; every other word carries the space the player has to type after it.
                double space = i + 1 < words.Count && words[i + 1].LineIndex == w.LineIndex ? 1 : 0;

                blocks.Add(new Block(w.StartMs, w.SpanMs, w.Weight + space));
            }

            // Stable, so two words sharing an onset keep their lyric order and the density
            // accumulation below sums in a fixed order on both ports.
            var ordered = blocks.OrderBy(b => b.StartMs).ToList();

            double cells = 0;
            double t0 = ordered[0].StartMs;
            double t1 = double.NegativeInfinity;

            foreach (var b in ordered)
            {
                cells += b.Cells;

                double end = b.StartMs + b.SpanMs;

                if (end > t1)
                    t1 = end;
            }

            // The timeline, in fixed bins, each holding the cells whose sung span overlaps it. A
            // word's cells are spread UNIFORMLY across its span rather than dropped at its onset:
            // the player types a long word across the whole time it is sung, and a window that
            // catches half of it should see half of its cells.
            int nb = Math.Max(1, (int)Math.Ceiling((t1 - t0) / timeline_bin_ms) + 1);
            double[] dens = new double[nb];

            foreach (var b in ordered)
            {
                double a = (b.StartMs - t0) / timeline_bin_ms;
                double z = (b.StartMs + b.SpanMs - t0) / timeline_bin_ms;
                int i0 = (int)Math.Floor(a);
                int i1 = Math.Min(nb - 1, (int)Math.Floor(z));
                double per = b.Cells / Math.Max(density_epsilon, z - a);

                for (int i = i0; i <= i1; i++)
                {
                    double lo = Math.Max(a, i);
                    double hi = Math.Min(z, i + 1);

                    // Partial bins are prorated, so a word straddling a boundary is not counted
                    // twice and a word shorter than a bin is not stretched to fill one.
                    if (hi > lo)
                        dens[i] += per * (hi - lo);
                }
            }

            // Prefix sums, accumulated in loop order (never a LINQ Sum, never a float): the cells in
            // any window are one subtraction, and the two ports have to agree on the last bit.
            double[] pre = new double[nb + 1];

            for (int i = 0; i < nb; i++)
                pre[i + 1] = pre[i] + dens[i];

            var schedule = windowSchedule((t1 - t0) / 1000.0);
            int[] windowBins = new int[schedule.Count];
            double[] windowDenominators = new double[schedule.Count];

            for (int i = 0; i < schedule.Count; i++)
            {
                double t = schedule[i];

                // Math.Floor(x + 0.5) rather than Math.Round, which is BANKER'S rounding in .NET and
                // would disagree with the prototype on a half.
                windowBins[i] = Math.Max(1, (int)Math.Floor(t * 1000 / timeline_bin_ms + 0.5));
                // cells / this = the window's WPM as a fraction of the record pace for its duration.
                windowDenominators[i] = 5 * (t / 60) * capability(t);
            }

            // The greedy feats. consumed marks the bins already spent by a feat, cpre is its prefix
            // sum so "is this whole window still free" is one subtraction.
            byte[] consumed = new byte[nb];
            int[] cpre = new int[nb + 1];
            double featSum = 0;

            for (int k = 0; k < feat_max; k++)
            {
                double weight = Math.Pow(feat_decay, k);

                if (weight < feat_weight_epsilon)
                    break;

                for (int i = 0; i < nb; i++)
                    cpre[i + 1] = cpre[i] + consumed[i];

                double bestRatio = 0;
                int bestBin = 0;
                int bestWb = 0;

                for (int s = 0; s < schedule.Count; s++)
                {
                    int wb = windowBins[s];

                    // A window longer than the map fits nowhere.
                    if (wb > nb)
                        continue;

                    // A SECONDARY feat can be required to span a minimum duration, so that a
                    // two-word burst cannot be a feat of its own. At 0 nothing is excluded.
                    if (k > 0 && schedule[s] < feat_min_seconds)
                        continue;

                    double denominator = windowDenominators[s];

                    for (int i = 0; i + wb <= nb; i++)
                    {
                        if (cpre[i + wb] - cpre[i] > 0)
                            continue;

                        double ratio = (pre[i + wb] - pre[i]) / denominator;

                        if (ratio > bestRatio)
                        {
                            bestRatio = ratio;
                            bestBin = i;
                            bestWb = wb;
                        }
                    }
                }

                if (bestRatio < feat_floor)
                    break;

                featSum += weight * bestRatio;

                for (int i = bestBin; i < bestBin + bestWb; i++)
                    consumed[i] = 1;
            }

            double length = length_stars * Math.Max(0, Math.Log10(cells / reference_cells));
            double stars = stars_at_human_peak * (1 - feat_decay) * featSum + length;

            // THERE IS NO CEILING HERE, deliberately (backlog 118). One used to live on this line,
            // a flat 10 chosen to keep a star BADGE sane, and it truncated far more than a badge:
            // this same method produces the pp INPUTS, the ratings the server stores as sr_dt
            // (rate 1.50) and sr_ht (0.75), and PerformancePoints prices a rate play purely
            // through the RATIO of those to the base rating. A ceiling makes that ratio wrong the
            // moment either side touches it, and it is the up-rate side that touches it. Bounding a
            // star READOUT is a presentation decision and belongs at the surface that draws one.
            //
            // The FLOOR stays. A negative rating describes no map, and callers divide by this.
            return Math.Max(stars, 0);
        }

        /// <summary>
        /// The WPM the fastest humans sustain for <paramref name="seconds"/>, i.e. the pace a
        /// window of that duration is scored against.
        /// </summary>
        private static double capability(double seconds)
            => capability_base_wpm + capability_burst_wpm * Math.Pow(capability_ref_seconds / seconds, capability_exponent);

        /// <summary>
        /// The window durations, in seconds: <c>window_min_s</c>, then 1.5 to 15 in steps of 1.5,
        /// 20 to 60 in steps of 5, and 90 upwards in steps of 30, keeping only what fits on a map
        /// of <paramref name="mapSeconds"/>. Coarse at the long end because <c>S(t)</c> is nearly
        /// flat there, fine at the short end because that is where bursts live.
        /// </summary>
        private static List<double> windowSchedule(double mapSeconds)
        {
            var ws = new List<double> { window_min_s };

            for (double t = 1.5; t <= 15; t += 1.5)
                ws.Add(t);

            for (double t = 20; t <= 60; t += 5)
                ws.Add(t);

            for (double t = 90; t <= mapSeconds; t += 30)
                ws.Add(t);

            double limit = Math.Max(mapSeconds, window_min_s);
            var kept = new List<double>(ws.Count);

            foreach (double t in ws)
            {
                if (t <= limit)
                    kept.Add(t);
            }

            return kept;
        }

        /// <summary>
        /// The cells of one token, i.e. what the player actually has to type for it.
        ///
        /// <para>WITHOUT LITERATE that is the typeable characters, lower-cased: marks are not cells
        /// at all (<see cref="Typeability.IsTypeable"/> excludes them on purpose) and case is folded
        /// because the caret matches case-insensitively.</para>
        ///
        /// <para>WITH LITERATE the cells ARE the authored chars, so every supported
        /// <see cref="Typeability.PUNCTUATION"/> mark joins them and case is KEPT. A mark is a real
        /// keypress with a target time of its own, so it lengthens its word and raises the density
        /// of the bins that word covers, which is the whole reason the mod is priced through this
        /// rating rather than by a flat multiplier.</para>
        ///
        /// <para>Freestyle slots are not in this STRING under either stream, and that is a statement
        /// about text, not about price. They carry no fixed key, so there is no glyph to put here.
        /// They ARE priced, at <see cref="freestyle_cost_weight"/>, through
        /// <see cref="freestyleSlots"/> and <see cref="Word.Weight"/>. Literate does not constrain
        /// them either, so the quarter is the same under both streams.</para>
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
    }
}
