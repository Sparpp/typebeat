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
    /// the fastest humans can type, over sliding windows of many durations (backlog 269, remodelled
    /// by backlog 273).
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
    /// <para>THE PEAK sets the RANGE. <c>ratio_0</c> is the best ratio at any scheduled duration
    /// anywhere on the map, and <c>stars_at_human_peak / (1 + envelope_range) * ratio_0</c> is the
    /// FLOOR of the map's range: what the hardest window alone is worth. Nothing about that figure
    /// looks at the rest of the map, and it is measured against HUMAN ABILITY rather than against
    /// the map's own peak, which is what stops a cut version outrating the full version it was cut
    /// from.</para>
    ///
    /// <para>THE ENVELOPE decides where inside that range the map lands. Every bin takes
    /// <c>env[i]</c>, the best ratio of any scheduled window CONTAINING it (a sliding-window maximum
    /// per duration, so the characters inside a sustained stretch are as hard as the stretch and a
    /// burst's characters are as hard as the burst), and <c>d = min(1, env[i] / ratio_0)</c> is how
    /// hard that bin is as a fraction of the peak. The map's DIFFICULT CHARACTERS are
    /// <c>N = sum_i cells(i) * d^envelope_power</c>, with no cutoff, so a character at 90% of the
    /// peak weighs 43%, at 70% weighs 6%, at half weighs 0.4%, and easy padding adds a little rather
    /// than exactly nothing. The range fills as <c>1 - exp(-N / envelope_chars)</c>, giving
    /// <c>stars = stars_at_human_peak / (1 + envelope_range) * ratio_0 *
    /// (1 + envelope_range * fill)</c>: record pace with the range filled rates
    /// <c>stars_at_human_peak</c>.</para>
    ///
    /// <para>WHY NOT THE OLD FEATS (backlog 269, replaced here). Stars used to be a decaying sum
    /// over greedy non-overlapping windows, so the tail credit depended on how many windows a map's
    /// difficulty happened to split into: eight two-second bursts filled eight slots while two
    /// sixty-second sections filled two, burst-built maps read about 7% too high, a map that was one
    /// long feat had nothing to add, and a cut sharing its full version's hardest part could tie it.
    /// The envelope counts CHARACTERS near the peak instead of counting windows, so how the same
    /// difficulty is chopped up cannot move the rating.</para>
    ///
    /// <para>THERE IS NO LENGTH TERM (the additive <c>0.12 * log10(cells/100)</c> of backlog 152 was
    /// deleted here by 273). Length counts only through the characters it adds: padding a map with
    /// easy singing raises N a little, so the rating rises a little, and padding it with hard
    /// singing raises it more. Two length terms would double count that.</para>
    ///
    /// <para>A MAP SHORTER THAN THE SMALLEST WINDOW rates EXACTLY ZERO. <c>window_min_s</c> of bins
    /// do not fit on its timeline, so no scheduled window fits, <c>ratio_0</c> is 0 and so is the
    /// range it scales. Windows are deliberately NOT clamped down to the map: the ratio only means
    /// anything against <c>S(t)</c> at the window's own duration, and a map with under a second and
    /// a half of singing in it is not a difficulty. Every synthetic fixture in the test suites is
    /// built long enough to clear that.</para>
    ///
    /// <para>Kept byte-for-byte in step with the website's port
    /// (typebeat-web: Typebeat.Web.Packages.Lyrics.LyricDifficulty) so the in-game star rating and
    /// the stored beatmaps.difficulty_rating always agree. Any change here must be mirrored there,
    /// and its LyricPace.VERSION bumped so existing rows recompute. The model itself is
    /// docs/sr-envelope-model.js in the parent superrepo, a pure function of (map, rate, constants)
    /// that this file is a literal port of (its 'feats' branch is the v17 model this replaced, kept
    /// as the record).</para>
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

        // What a map that types at the record pace THROUGHOUT WITH ITS RANGE FILLED is worth.
        // Chosen 2026-09-06 against the live ranked catalogue and kept by backlog 273: the hardest
        // thing published lands just above 10, so the pool reads inside a single star decade without
        // anything being cut to fit. Stars are LINEAR in this constant, so moving it alone is a pure
        // rescale and cannot reorder anything. (12.0 would hold the pre-273 catalogue MEAN, since
        // most maps fill about half their range; that one-constant alternative was considered and
        // deliberately not taken, because the anchor's MEANING is worth more than the mean.)
        private const double stars_at_human_peak = 10.6;

        /// <summary>
        /// How much the map can add ON TOP of its hardest window, as a fraction of that floor: a map
        /// whose range is completely filled rates <c>1 + envelope_range</c> times what its peak
        /// alone is worth, and <c>stars_at_human_peak</c> is divided by the same factor so that a
        /// filled record-pace map rates exactly <c>stars_at_human_peak</c> and the hardest window
        /// alone rates 7.95.
        ///
        /// <para>THE LITERAL IS THE PROTOTYPE'S. The decision (backlog 273) was "a third", and
        /// docs/sr-envelope-model.js writes that third as <c>envCap: 0.3333</c>. The reference
        /// values the whole port is checked against were produced with THAT literal, and this file
        /// has to reproduce the prototype to the last bit rather than merely agree with its
        /// intent, so 0.3333 is what is written here. The difference against 1.0/3 is 2e-4 of a
        /// star at the floor and exactly nothing at a full range.</para>
        /// </summary>
        private const double envelope_range = 0.3333;

        // How sharply a character's weight falls away from the peak: weight is (env/ratio_0)^this,
        // so 90% of the peak weighs 43%, 70% weighs 6% and half weighs 0.4%. There is deliberately
        // NO CUTOFF under it, which is what makes easy padding worth a little rather than nothing.
        private const double envelope_power = 8;

        // The difficult characters that fill 63% (1 - 1/e) of the range. 500 is about two minutes of
        // singing at the peak, so a map has to hold its hardest pace for a long time to saturate.
        private const double envelope_chars = 500;

        private const double timeline_bin_ms = 50; // timeline resolution
        private const double window_min_s = 1.36; // the smallest scheduled window: the 51-char burst record

        // Guards the uniform spread below against a zero-width block. A span is floored at
        // min_span_ms before the rate divide, so this can only be reached by an absurd rate.
        private const double density_epsilon = 1e-9;

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
        /// rather than as a character of the stream, and that count reaches the one place cells are
        /// read: the density spread over the timeline bins, which is what both the window ratios and
        /// the envelope's difficult-character sum are measured on. It
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

            double t0 = ordered[0].StartMs;
            double t1 = double.NegativeInfinity;

            foreach (var b in ordered)
            {
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
            double[] windowCapabilities = new double[schedule.Count];
            double[] windowDenominators = new double[schedule.Count];

            for (int i = 0; i < schedule.Count; i++)
            {
                double t = schedule[i];

                // Math.Floor(x + 0.5) rather than Math.Round, which is BANKER'S rounding in .NET and
                // would disagree with the prototype on a half.
                windowBins[i] = Math.Max(1, (int)Math.Floor(t * 1000 / timeline_bin_ms + 0.5));
                windowCapabilities[i] = capability(t);
                // cells / this = the window's WPM as a fraction of the record pace for its duration.
                windowDenominators[i] = 5 * (t / 60) * windowCapabilities[i];
            }

            // THE PEAK, ratio_0: the best window at any scheduled duration anywhere on the map. It
            // sets the whole range, so it is found on its own rather than as a by-product.
            //
            // Note the ARITHMETIC, which is the prototype's and not a simplification of it: the best
            // window's CELLS become a WPM (cells / 5 / minutes) and the WPM is then divided by
            // S(t), where the envelope below divides the cells by the pre-multiplied denominator in
            // one step. The two agree to within a bit or two and not always exactly, which is
            // precisely why d is clamped at 1 below: at the peak bin env/ratio_0 can land a hair
            // over 1.
            double peakRatio = 0;

            for (int w = 0; w < schedule.Count; w++)
            {
                int wb = windowBins[w];

                // A window longer than the map fits nowhere.
                if (wb > nb)
                    continue;

                double best = 0;

                for (int i = 0; i + wb <= nb; i++)
                {
                    double chars = pre[i + wb] - pre[i];

                    if (chars > best)
                        best = chars;
                }

                double ratio = best / 5 / (schedule[w] / 60) / windowCapabilities[w];

                if (ratio > peakRatio)
                    peakRatio = ratio;
            }

            // THE ENVELOPE. env[i] is the best ratio of any scheduled window CONTAINING bin i, i.e.
            // a sliding-window maximum per duration, maxed over durations. The windows of length wb
            // containing bin i are the ones starting at i - wb + 1 through i, so a MONOTONIC DEQUE
            // over the window ratios answers every bin in amortised constant time: starts are pushed
            // in order, any tail the newcomer matches or beats is dropped (it can never be the
            // maximum again), and the head is evicted once it no longer reaches i.
            double[] env = new double[nb];

            for (int w = 0; w < schedule.Count; w++)
            {
                int wb = windowBins[w];

                if (wb > nb)
                    continue;

                double denominator = windowDenominators[w];
                int ns = nb - wb + 1;
                double[] r = new double[ns];

                for (int st = 0; st < ns; st++)
                    r[st] = (pre[st + wb] - pre[st]) / denominator;

                int[] dq = new int[ns];
                int head = 0;
                int tail = 0;
                int next = 0;

                for (int i = 0; i < nb; i++)
                {
                    while (next <= i && next < ns)
                    {
                        while (tail > head && r[dq[tail - 1]] <= r[next])
                            tail--;

                        dq[tail++] = next;
                        next++;
                    }

                    while (tail > head && dq[head] < i - wb + 1)
                        head++;

                    if (tail > head && r[dq[head]] > env[i])
                        env[i] = r[dq[head]];
                }
            }

            // N, THE DIFFICULT CHARACTERS. Every cell is weighted by how close its own bin sits to
            // the peak, raised to envelope_power, with no cutoff: a slow verse contributes a little
            // rather than nothing, and a sustained stretch contributes nearly all of itself. The
            // peakRatio > 0 guard is the prototype's and is what a map too short for any window
            // takes: no window, no peak, no envelope and no rating.
            double n = 0;

            if (peakRatio > 0)
            {
                for (int i = 0; i < nb; i++)
                {
                    if (dens[i] <= 0)
                        continue;

                    double d = Math.Min(1, env[i] / peakRatio);

                    n += dens[i] * Math.Pow(d, envelope_power);
                }
            }

            // How much of the range those characters fill, and the rating that lands inside it.
            double fill = 1 - Math.Exp(-n / envelope_chars);
            double stars = stars_at_human_peak / (1 + envelope_range) * peakRatio * (1 + envelope_range * fill);

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
