// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Utils;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using osuTK.Graphics;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// One WORD-sized slice of a line, with the local playhead speed the map's own per-character
    /// targets give it: the half-open cell range [<see cref="StartCell"/>,
    /// <see cref="EndCellExclusive"/>) and <see cref="Speed"/> in COUNTABLE cells per millisecond.
    /// </summary>
    public readonly record struct PaceSegment(int StartCell, int EndCellExclusive, double Speed);

    /// <summary>
    /// A render-ready underline band: a <see cref="PaceSegment"/>'s cell range plus the colour its
    /// MAP-WIDE percentile rank earned it. Speed is gone by this point on purpose: the colour is a
    /// whole-map decision, so nothing downstream should be able to re-derive it from one line.
    /// </summary>
    public readonly record struct PaceBand(int StartCell, int EndCellExclusive, Color4 Colour);

    /// <summary>
    /// The UNDERLINE PACE HUE (backlog 228): the sung-sweep rail under a lyric line is cut into one
    /// band per WORD and each band is tinted by how fast the playhead crosses it RELATIVE TO THE
    /// REST OF THE MAP. A player reading ahead sees where the line is about to get dense (red) and
    /// where it opens up (green) without any number on screen.
    ///
    /// <para>Purely display. Nothing here is read by judgement, scoring, the replay, the wire or any
    /// anti-cheat gate; it consumes <see cref="TypingCell.TargetTime"/> and produces colours.</para>
    ///
    /// <para><b>Segmentation: per word, INCLUDING the trailing gap.</b> A segment runs from a word's
    /// first cell through the word gap that closes it (<see cref="LyricLineDisplay.IsWordGap"/>),
    /// and its map-time span runs to the NEXT segment's first target (the line's sung end for the
    /// last one). So a long breath between two words is charged to the word before it and reads as
    /// slow, which is the whole reason the gap is inside the segment rather than between segments.
    /// Segments tile the line with no holes at either end, which the renderer relies on.</para>
    ///
    /// <para><b>The metric is COUNTABLE cells per millisecond</b> (<see cref="TypingCell.IsCountable"/>:
    /// typeable and not a space), over that full span. Counting the gap's TIME but not its
    /// KEYSTROKE is what maximises the breath signal, and it matches the house definition of
    /// character distance that the Flashlight window and the rush cap already measure in.</para>
    ///
    /// <para><b>The distribution is the WHOLE MAP's</b>, never one line's. A line that is uniformly
    /// brutal has to glow red, and per-line percentiles would grey it out precisely because every
    /// segment in it is typical OF IT. The rank is a mid-rank
    /// (<see cref="RanksOf"/>), so ties share one rank and a map whose segments are all equally
    /// paced ranks every one of them at 0.5 and renders entirely neutral.</para>
    ///
    /// <para><b>Rate mods need no handling at all.</b> The engine judges in MAP time and holds no
    /// rate; a rate mod's only effect on this ruleset is multiplying
    /// <see cref="TypingEngine.WindowScale"/>, and nothing anywhere rewrites a
    /// <see cref="TypingCell.TargetTime"/>. Every span, every speed and therefore every percentile
    /// rank is identical under DT/HT/NC, so the colours are too. Under the WIND UP / WIND DOWN ramps
    /// the same holds and is a DECISION rather than a coincidence: the real-time pace genuinely
    /// varies across a ramped play while the map-time pace does not, and the alternative would be to
    /// recolour the whole stack mid-play under the player's eyes, which is worse than a hue that
    /// keeps meaning "fast for this map".</para>
    ///
    /// <para><b>Line-granularity maps.</b> With no word or syllable timing the per-character targets
    /// are evenly interpolated across each line, so there is no real pace to read: a map whose
    /// segments come out exactly equal ranks every one at 0.5 and stays fully neutral, and one whose
    /// lines differ slightly in density still hues its quartiles, because the rule is deliberately
    /// about RANK and not about the size of the difference. What little variation such a map shows
    /// is word length rather than pace, and that is a limitation of the source timing, not of this
    /// rule.</para>
    ///
    /// <para>Computed ONCE per map, in <see cref="LyricStage"/>'s loader beside the flashlight stream
    /// geometry, and handed to each <see cref="LyricLineDisplay"/> at construction. It must never be
    /// reached from a per-frame path: the stage refreshes every visible cell every frame and these
    /// colours are map constants.</para>
    /// </summary>
    public static class UnderlinePace
    {
        /// <summary>
        /// Opacity of a NEUTRAL band: exactly the alpha the single flat rail carried before this
        /// feature existed, so the 25th-to-75th-percentile band (and every segment of a uniformly
        /// paced map) renders byte-identically to the shipped underline.
        /// </summary>
        public const float NEUTRAL_ALPHA = 0.20f;

        /// <summary>
        /// Opacity at the far end of either ramp (the map's slowest and fastest segments). The rail
        /// is drawn under the glyphs at a fifth alpha, where a pure hue rotation is close to
        /// invisible; lifting the outliers a third of the way up gives the hue somewhere to read
        /// from while keeping the whole rail inside the weight class it has always had. Grey stays
        /// the visual default, which is the point: only outliers are meant to catch the eye.
        /// </summary>
        public const float HUED_ALPHA = 0.34f;

        /// <summary>Bottom of the NEUTRAL BUFFER: at or above this rank a segment takes no green.</summary>
        public const double NEUTRAL_LO_RANK = 0.25;

        /// <summary>Top of the NEUTRAL BUFFER: at or below this rank a segment takes no red.</summary>
        public const double NEUTRAL_HI_RANK = 0.75;

        /// <summary>
        /// Floor under a segment's map-time span. Targets are authored data and can arrive collapsed
        /// (a line-granularity map with a zero-length line, a hand-edited timing file, the monotonic
        /// clamp folding two targets together), and a zero or negative span would make the speed
        /// infinite or sign-flipped.
        ///
        /// <para>30 ms is one character at a superhuman 2000 CPM, so nothing a human could type is
        /// ever clamped by it. Note what is NOT its job: an outlier cannot compress everyone else's
        /// colour here whatever its magnitude, because the ramp is linear in percentile RANK and a
        /// degenerate segment occupies exactly one rank slot. The floor exists to keep the number
        /// finite and the ordering deterministic.</para>
        /// </summary>
        public const double MIN_SEGMENT_SPAN_MS = 30;

        /// <summary>The colour of a segment inside the neutral buffer: the pre-228 rail, exactly.</summary>
        public static Color4 NeutralColour => TypeBeatStyle.SungAccent.Opacity(NEUTRAL_ALPHA);

        /// <summary>
        /// The colour a band wears, given its MAP-WIDE percentile rank (0 = the map's slowest
        /// segment, 1 = its fastest). Pure, so it is unit-testable beside
        /// <see cref="LyricLineDisplay.CorrectCharColour"/>, and shaped the same way: clamped,
        /// NaN-safe, and exact at both endpoints rather than float-approximately exact.
        ///
        /// <list type="bullet">
        /// <item>[<see cref="NEUTRAL_LO_RANK"/>, <see cref="NEUTRAL_HI_RANK"/>] INCLUSIVE returns
        /// <see cref="NeutralColour"/> and no hue at all. Half the map is meant to look exactly as
        /// it looked before this feature shipped.</item>
        /// <item>Above the buffer the band shades towards <see cref="TypeBeatStyle.ErrorChar"/>,
        /// linearly in rank: 0 intensity at p75, full at the map's fastest.</item>
        /// <item>Below it the band shades towards <see cref="TypeBeatStyle.PaceSlowAccent"/> on the
        /// mirror ramp: 0 intensity at p25, full at the map's slowest.</item>
        /// </list>
        ///
        /// <para>LINEAR IN RANK, not in speed, and that is the load-bearing choice: one absurd burst
        /// in a map full of ordinary lines takes the top rank and nothing else, instead of pinning
        /// the far end of a speed axis and squashing every honest variation into the neutral band.</para>
        ///
        /// <para>A NaN rank (which no comparison would catch) falls to the middle of the buffer, so
        /// the worst case is a neutral band rather than a NaN colour.</para>
        /// </summary>
        public static Color4 ColourForRank(double percentileRank)
        {
            double r = double.IsNaN(percentileRank) ? 0.5 : Math.Clamp(percentileRank, 0, 1);

            if (r >= NEUTRAL_LO_RANK && r <= NEUTRAL_HI_RANK)
                return NeutralColour;

            bool fast = r > NEUTRAL_HI_RANK;

            double t = fast
                ? (r - NEUTRAL_HI_RANK) / (1 - NEUTRAL_HI_RANK)
                : (NEUTRAL_LO_RANK - r) / NEUTRAL_LO_RANK;

            var end = fast ? TypeBeatStyle.ErrorChar : TypeBeatStyle.PaceSlowAccent;

            // Exactness at the top of the ramp is a contract, not an optimisation, for the same
            // reason CorrectCharColour states it: a componentwise lerp at t = 1 is only
            // float-approximately the end colour.
            if (t >= 1)
                return end.Opacity(HUED_ALPHA);

            // The hue and the opacity are composed rather than interpolated together: the hue walks
            // the framework's own colour ramp (linear light, like every other ramp here) while the
            // alpha is a plain linear lift, and neither has to know about the other.
            return Interpolation.ValueAt(t, TypeBeatStyle.SungAccent, end, 0d, 1d)
                                .Opacity((float)(NEUTRAL_ALPHA + (HUED_ALPHA - NEUTRAL_ALPHA) * t));
        }

        /// <summary>
        /// Cut one line into word segments and price each one. See the type doc for the rule; the
        /// short form is that a segment ends AFTER the word gap that closes its word, and its span
        /// reaches to the next segment's first vocal target so the breath is inside it.
        ///
        /// <para><paramref name="lineSungEndMs"/> closes the LAST segment, and the caller is expected
        /// to pass what <see cref="TypingLine"/>'s own sung polyline ends at (see
        /// <see cref="SungEndOf"/>). Pure, so it is unit-testable.</para>
        /// </summary>
        public static PaceSegment[] SegmentLine(IReadOnlyList<TypingCell> cells, double lineSungEndMs)
        {
            int n = cells.Count;

            if (n == 0)
                return Array.Empty<PaceSegment>();

            // Boundaries: a new segment opens on the cell AFTER each word gap. A gap that IS the
            // last cell opens nothing, which is what keeps a trailing gap inside the last word
            // rather than creating an empty segment past the end of the line.
            var starts = new List<int> { 0 };

            for (int i = 0; i < n; i++)
            {
                if (LyricLineDisplay.IsWordGap(cells[i]) && i + 1 < n)
                    starts.Add(i + 1);
            }

            var result = new PaceSegment[starts.Count];

            for (int k = 0; k < starts.Count; k++)
            {
                int start = starts[k];
                int endExclusive = k + 1 < starts.Count ? starts[k + 1] : n;

                double startTime = firstVocalTime(cells, start, endExclusive);
                double endTime = k + 1 < starts.Count
                    ? firstVocalTime(cells, endExclusive, k + 2 < starts.Count ? starts[k + 2] : n)
                    : lineSungEndMs;

                int countable = 0;

                for (int i = start; i < endExclusive; i++)
                {
                    if (cells[i].IsCountable)
                        countable++;
                }

                double span = endTime - startTime;

                // Written as a failed lower bound rather than a comparison so a NaN span (authored
                // data can carry one) falls to the floor instead of through it.
                if (!(span >= MIN_SEGMENT_SPAN_MS))
                    span = MIN_SEGMENT_SPAN_MS;

                result[k] = new PaceSegment(start, endExclusive, countable / span);
            }

            return result;
        }

        /// <summary>Cut a whole line, closing the last segment at the line's own sung end.</summary>
        public static PaceSegment[] SegmentLine(TypingLine line) => SegmentLine(line.Cells, SungEndOf(line));

        /// <summary>
        /// Where a line stops being sung: <see cref="TypingLine.SweepEndTime"/>, which IS the last
        /// anchor of that line's sung polyline. Read from the line rather than recomputed here so
        /// the band and the fill under it cannot drift apart.
        ///
        /// <para>That anchor is the LAST WORD's own end and not the line's sung-end flag (backlog
        /// 245): every other segment closes on the next segment's first vocal target, a time inside
        /// the next word block, so pricing the last one by a flag a mapper drags independently made
        /// it the one segment whose hue, and therefore whose rank among every segment in the map,
        /// moved without any word moving. It is still never earlier than the line's last typeable
        /// target, so inverted authored data cannot hand the final word a negative span.</para>
        /// </summary>
        public static double SungEndOf(TypingLine line) => line.SweepEndTime;

        /// <summary>
        /// MID-RANK percentile of each speed within the whole set, in [0, 1]: the count strictly
        /// below it plus half the count equal to it, over the total.
        ///
        /// <para>The tie handling is the reason this is a mid-rank and not a sorted position. Every
        /// segment of a uniformly paced map (a line-granularity map's, most obviously) is equal to
        /// every other, and they must all land at 0.5, dead centre of the neutral buffer, rather than
        /// being spread from 0 to 1 by the accident of their order. It also makes a one-segment map
        /// neutral for free, which is the only sane reading of "this segment's percentile" when there
        /// is nothing to compare it to.</para>
        ///
        /// <para>Pure, and deterministic regardless of sort stability: equal speeds share one rank,
        /// so which of them sorted first cannot change any output.</para>
        /// </summary>
        public static double[] RanksOf(IReadOnlyList<double> speeds)
        {
            int n = speeds.Count;
            var ranks = new double[n];

            if (n == 0)
                return ranks;

            var order = new int[n];

            for (int i = 0; i < n; i++)
                order[i] = i;

            Array.Sort(order, (a, b) => speeds[a].CompareTo(speeds[b]));

            int at = 0;

            while (at < n)
            {
                int last = at;

                // Equal speeds form one tie group and share the group's midpoint rank.
                while (last + 1 < n && speeds[order[last + 1]] == speeds[order[at]])
                    last++;

                // (#strictly below + #at-or-below) / 2, normalised.
                double rank = (at + last + 1) * 0.5 / n;

                for (int k = at; k <= last; k++)
                    ranks[order[k]] = rank;

                at = last + 1;
            }

            return ranks;
        }

        /// <summary>
        /// The whole-map precompute: cut every line into word segments, rank all of their speeds
        /// against each other, and return one render-ready <see cref="PaceBand"/> array per line, in
        /// the order the lines were given.
        ///
        /// <para>This is the ONLY place the map-wide distribution is formed, which is what makes the
        /// "not per line" decision structural rather than a convention: a display is handed colours
        /// and has no way to compute one.</para>
        /// </summary>
        public static PaceBand[][] BuildBands(IReadOnlyList<TypingLine> lines)
        {
            int m = lines.Count;
            var perLine = new PaceSegment[m][];
            var speeds = new List<double>();

            for (int k = 0; k < m; k++)
            {
                perLine[k] = SegmentLine(lines[k]);

                foreach (var segment in perLine[k])
                    speeds.Add(segment.Speed);
            }

            double[] ranks = RanksOf(speeds);

            var bands = new PaceBand[m][];
            int at = 0;

            for (int k = 0; k < m; k++)
            {
                bands[k] = new PaceBand[perLine[k].Length];

                for (int j = 0; j < perLine[k].Length; j++)
                {
                    var segment = perLine[k][j];
                    bands[k][j] = new PaceBand(segment.StartCell, segment.EndCellExclusive, ColourForRank(ranks[at++]));
                }
            }

            return bands;
        }

        /// <summary>The first TYPEABLE cell's target in [<paramref name="from"/>,
        /// <paramref name="toExclusive"/>), falling back to the range's first cell for a range with
        /// no typeable cell at all (punctuation the Literate stream kept, and nothing else).</summary>
        private static double firstVocalTime(IReadOnlyList<TypingCell> cells, int from, int toExclusive)
        {
            for (int i = from; i < toExclusive; i++)
            {
                if (cells[i].IsTypeable)
                    return cells[i].TargetTime;
            }

            return from < cells.Count ? cells[from].TargetTime : 0;
        }
    }
}
