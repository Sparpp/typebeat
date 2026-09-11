// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Boundary-window typing-pace statistics for a lyric map. A line's typing window is the
    /// time between its start and end boundaries, the time a player actually gets, however
    /// leniently or strictly the map is bounded, not the perfect-play playhead. Per-line rates
    /// are averaged unweighted across lines, so instrumental gaps between lines never dilute
    /// the pace. <see cref="TargetWpm"/> is the same unweighted mean taken over the FASTEST
    /// fifth of those lines that hold at least three words, rather than over all of them, which
    /// is why both figures live here: they are one estimator over two selections of the same
    /// pool.
    ///
    /// <para>THE WORD CONVENTION IS THE TYPING-TEST ONE: a word is <see cref="CHARS_PER_WORD"/>
    /// typeable cells, so WPM is exactly CPM/5. This file used to do the opposite, count real
    /// words, on the argument that the CPM:WPM ratio then carried the map's true word length.
    /// It did carry it, implicitly, which is the problem: a reader had to divide two numbers to
    /// recover it, and every WPM the map advertised was in a private unit that agreed with no
    /// typing test anywhere, not even with our own HUD (Gameplay/TypingEngine.cs, LiveWpm and
    /// LiveRollingWpm, have always divided characters by 5). So the ratio is now published
    /// outright as <see cref="AverageCharsPerWord"/>, and WPM is comparable with MonkeyType,
    /// with the in-game counter and with the number on the results screen.</para>
    ///
    /// <para>The two are coherent BY CONSTRUCTION, and the identity to hold on to is: a line
    /// whose average word is exactly <see cref="CHARS_PER_WORD"/> cells long has the same WPM
    /// under both conventions. Above 5 the new figure reads higher than the old one, below 5
    /// lower, in exact proportion. <see cref="AverageCharsPerWord"/> counts the inter-word
    /// space cells, because the 5 does too: a "word" in a typing test is five keystrokes, and
    /// the space after a word is a keystroke.</para>
    /// </summary>
    public readonly struct LyricPaceStatistics
    {
        /// <summary>
        /// Typeable cells per word, the typing-test convention. Same 5 as
        /// <c>TypingEngine.LiveWpm</c> and <c>LyricWpmCurve</c>; all three must agree or the
        /// map's advertised pace stops meaning what the HUD shows.
        /// </summary>
        public const double CHARS_PER_WORD = 5.0;

        /// <summary>
        /// Mean of per-line (typeable cells / boundary window) rates, divided by
        /// <see cref="CHARS_PER_WORD"/>. Derived from <see cref="AverageCpm"/> rather than
        /// accumulated separately so the two can never drift apart by a rounding step.
        /// </summary>
        public double AverageWpm => AverageCpm / CHARS_PER_WORD;

        /// <summary>Mean of per-line (typeable cells / boundary window) rates.</summary>
        public double AverageCpm { get; init; }

        /// <summary>
        /// The pace to SUSTAIN: the average WPM across the FASTEST fifth of the map's lyric lines of
        /// at least <see cref="target_line_min_words"/> words
        /// (<see cref="target_line_fraction"/> of those, rounded up, at least one). Exactly the
        /// estimator <see cref="AverageWpm"/> is, restricted to the demanding lines, so the two are
        /// read as a pair: the average is what the map asks on the whole and the target is what its
        /// hard stretches ask.
        ///
        /// <para>Since the eligibility floor (backlog 274) the pool is a SUBSET of the lines the
        /// average is taken over, so <see cref="AverageWpm"/> &lt;= this is NO LONGER GUARANTEED: a
        /// two-word interjection can raise the average without being able to raise the target. It
        /// still holds wherever the map's fastest lines clear the floor, which is the common case,
        /// and equality is still exactly the map on which every counted line runs at the same rate.
        /// Selection is by LINE, not by keystroke and not by time, so a long instrumental gap between
        /// lines cannot dilute it and a burst inside one line cannot inflate it: a line is one vote
        /// whatever it holds, which is the same convention <see cref="AverageCpm"/> already
        /// uses.</para>
        ///
        /// <para>0 for a map with no counted line, alongside every other figure here.</para>
        /// </summary>
        public double TargetWpm { get; init; }

        /// <summary>
        /// Typeable cells per word over the WHOLE map (<see cref="TypeableCellCount"/> /
        /// <see cref="WordCount"/>), inter-word spaces included. This is the number the old
        /// CPM:WPM ratio encoded implicitly; 0 for a map with no words.
        ///
        /// <para>A map total, not a mean of per-line ratios, so it is the length of the average
        /// word the player types rather than the average of the lines' averages. WPM and CPM go
        /// the other way (unweighted per-line means) because they are RATES and a per-line mean
        /// is what keeps a long instrumental gap from diluting them, while this is a pure count
        /// ratio with no time in it to dilute.</para>
        /// </summary>
        public double AverageCharsPerWord => WordCount == 0 ? 0 : (double)TypeableCellCount / WordCount;

        /// <summary>Total typeable cells (chars + inter-word spaces) across all lines.</summary>
        public int TypeableCellCount { get; init; }

        /// <summary>Total words (tokens containing at least one typeable char) across all lines.</summary>
        public int WordCount { get; init; }

        /// <summary>Guards degenerate data (zero/near-zero windows) from exploding the rate.</summary>
        private const double min_line_window_ms = 500;

        /// <summary>
        /// How much of the map <see cref="TargetWpm"/> averages: the fastest 0.20 of its ELIGIBLE
        /// lines, rounded UP so every map has at least one line in the selection however short it
        /// is. A fifth is the user's own definition of the statistic, and rounding up rather than
        /// to nearest is what keeps a five-line map from selecting nothing.
        /// </summary>
        private const double target_line_fraction = 0.20;

        /// <summary>
        /// The ELIGIBILITY FLOOR for that selection (backlog 274): a line enters the pool only if it
        /// holds at least three WORDS, counted exactly as <see cref="WordCount"/> counts them (a
        /// token holding at least one typeable cell, on the same default stream). A two-word
        /// interjection is over almost as soon as it begins, so its boundary window is short enough
        /// to read as an enormous rate, and without the floor one such line could define the whole
        /// map's target.
        ///
        /// <para>FALLBACK: on a map where NO line clears the floor (every line one or two words) the
        /// pool is ALL of the counted lines instead, which is exactly the pre-274 behaviour.
        /// Filtering to nothing would leave such a map with no target at all, and a map built out of
        /// two-word lines still has a hardest stretch worth naming. It also keeps the rule for a 0
        /// (and therefore for the server's NULL <c>target_wpm</c>) where it was: a map with no
        /// COUNTED line, never a map with no eligible one.</para>
        /// </summary>
        private const int target_line_min_words = 3;

        public static LyricPaceStatistics Compute(IEnumerable<LyricLine> lines)
        {
            int totalCells = 0;
            int totalWords = 0;
            int lineCount = 0;
            double cpmSum = 0;

            // Every counted line's own rate, kept so TargetWpm can average the fastest fifth of
            // them. One entry per line, in the same order cpmSum accumulates, so a line skipped for
            // holding no cell is skipped by the average and by the target alike.
            var lineCpms = new List<double>();

            // And the same rates again for the lines that clear target_line_min_words, which is the
            // pool the target actually selects from. Filled on the same pass rather than filtered
            // out of lineCpms afterwards, because the WORD COUNT that decides eligibility is the
            // loop's own count and is not recoverable from a rate.
            var eligibleCpms = new List<double>();

            foreach (var line in lines)
            {
                // Cell arithmetic mirrors TypingLine.FromLyricLine: every typeable char is a
                // cell, plus one typeable space cell per token gap.
                //
                // Measured on the DEFAULT stream, never the authored text: a map's pace has to be
                // the pace of the play everyone shares, not of the harder Literate variant, and it
                // has to stay comparable with every figure computed before punctuation existed.
                // ToDefaultStream is idempotent, so on an already-stripped line this is exactly the
                // old arithmetic (lower-casing cannot change any count).
                string[] tokens = Typeability.ToDefaultStream(line.RawText).Split(' ');

                int cells = tokens.Length - 1;
                int words = 0;

                foreach (string token in tokens)
                {
                    int typeable = 0;

                    foreach (char ch in token)
                    {
                        // Freestyle slots are keypresses too, so they count towards the pace.
                        if (Typeability.IsCell(ch))
                            typeable++;
                    }

                    cells += typeable;

                    if (typeable > 0)
                        words++;
                }

                if (cells <= 0)
                    continue;

                double windowMinutes = Math.Max(line.EndTime - line.StartTime, min_line_window_ms) / 60000.0;

                // WPM is not accumulated here: it is CPM/5 by definition (see AverageWpm), so a
                // second sum could only introduce a way for the two to disagree.
                double lineCpm = cells / windowMinutes;

                cpmSum += lineCpm;
                lineCpms.Add(lineCpm);

                if (words >= target_line_min_words)
                    eligibleCpms.Add(lineCpm);

                totalCells += cells;
                totalWords += words;
                lineCount++;
            }

            if (lineCount == 0)
                return default;

            // The fastest fifth, by line: sort the ELIGIBLE per-line rates DESCENDING and take the
            // head of the list. The count rounds up, so a pool of four lines still selects its one
            // hardest line rather than an empty slice, and Math.Max is belt and braces around a
            // retune of target_line_fraction (at 0.20 the ceiling is already at least 1 for every
            // non-empty pool).
            //
            // The fraction is taken over the POOL and not over lineCount, so widening the map with
            // lines that cannot be selected cannot widen the selection either.
            var targetPool = eligibleCpms.Count > 0 ? eligibleCpms : lineCpms;

            targetPool.Sort((a, b) => b.CompareTo(a));

            int targetLines = Math.Max(1, (int)Math.Ceiling(target_line_fraction * targetPool.Count));
            double targetCpmSum = 0;

            for (int i = 0; i < targetLines; i++)
                targetCpmSum += targetPool[i];

            return new LyricPaceStatistics
            {
                AverageCpm = cpmSum / lineCount,
                TargetWpm = targetCpmSum / targetLines / CHARS_PER_WORD,
                TypeableCellCount = totalCells,
                WordCount = totalWords,
            };
        }
    }
}
