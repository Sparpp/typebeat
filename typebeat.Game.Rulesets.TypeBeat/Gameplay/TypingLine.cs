// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/TypingLine.cs (regression-anchored).
// Pure C#: no osu.Framework dependencies. All times are double milliseconds.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public enum CellState
    {
        Untyped,
        Correct,
        Wrong,
        Missed,
        AutoSkipped
    }

    public sealed class TypingCell
    {
        /// <summary>Display char as authored (normalized). For a freestyle cell this is the
        /// authoring marker (<see cref="Typeability.FREESTYLE_MARKER"/>), never a glyph the player
        /// has to produce; the display substitutes it (see <see cref="IsFreestyle"/>).</summary>
        public char Expected { get; }

        public bool IsTypeable { get; }

        /// <summary>
        /// FREESTYLE cell: any key on the typeable surface EXCEPT SPACE satisfies it, and the char
        /// the player actually pressed lands in <see cref="TypedChar"/> and stays on screen.
        /// Judgement is otherwise a completely normal typeable cell (same windows, points, combo,
        /// completion), and a space is rejected exactly as a wrong key on any other cell is.
        /// </summary>
        public bool IsFreestyle { get; }

        /// <summary>
        /// A COUNTABLE character: typeable and not a space. The unit of character DISTANCE in this
        /// game, shared by the Flashlight mod's visible window and the Fletcher mod's rush cap:
        /// spaces and punctuation ride along inside a run without spending its budget. A freestyle
        /// slot is countable like any other letter (the player presses a key for it).
        /// </summary>
        public bool IsCountable => IsTypeable && Expected != ' ';

        public double TargetTime { get; }

        /// <summary>
        /// Window tier this cell is judged at: the beatmap granularity normally, widened to
        /// Line for estimated lines and low-confidence words (unreliable timing gets tolerance).
        /// </summary>
        public TimingGranularity JudgeGranularity { get; }

        public CellState State { get; internal set; }

        public char? TypedChar { get; internal set; }

        /// <summary>Delta of the awarded correct keypress, or the wrong keypress.</summary>
        public double? JudgedDelta { get; internal set; }

        /// <summary>
        /// Delta of the FIRST correct judgement: set once, never cleared (survives backspace).
        /// Its presence makes later correct retypes scoring-inert, so backspace-retype cannot
        /// farm score/combo/accuracy.
        /// </summary>
        internal double? FirstCorrectDelta { get; set; }

        internal TypingCell(char expected, bool isTypeable, double targetTime, TimingGranularity judgeGranularity)
        {
            Expected = expected;
            IsTypeable = isTypeable;
            IsFreestyle = isTypeable && Typeability.IsFreestyle(expected);
            TargetTime = targetTime;
            JudgeGranularity = judgeGranularity;
            State = CellState.Untyped;
        }
    }

    public sealed class TypingLine
    {
        public LyricLine Source { get; }

        /// <summary>
        /// The line exactly as it is shown and exactly as it must be typed: the concatenated cell
        /// chars. Equal to <c>Source.RawText</c> under the Literate mod and to
        /// <see cref="Typeability.ToDefaultStream"/> of it otherwise. Never re-derived anywhere
        /// else: the stage renders the same cells this is built from.
        /// </summary>
        public string DisplayText { get; }

        public IReadOnlyList<TypingCell> Cells { get; }

        public double StartTime { get; }

        public double EndTime { get; }

        public double SingEndTime { get; }

        /// <summary>
        /// When this line becomes typeable: a constant cue lead before its first typeable cell's
        /// target (<see cref="TypingEngine.CUE_LEAD_MS"/>), never earlier than <see cref="StartTime"/>
        /// (the shared boundary; the previous line cannot seal before it). Independent of the
        /// boundary otherwise: a line whose vocals start late in its window activates late, and
        /// the gap in between is a dead zone where no line is active.
        /// </summary>
        public double ActivationTime { get; }

        /// <summary>
        /// When this line's vocals actually begin: the first typeable cell's target time, falling
        /// back to <see cref="StartTime"/> for a line with no typeable cells. Together with the
        /// previous line's <see cref="SingEndTime"/> this measures the *perceived* instrumental
        /// stretch between two lines (what a player hears as "no lyrics").
        /// </summary>
        public double FirstVocalTime { get; }

        /// <summary>
        /// Extra typeable time past <see cref="EndTime"/> before the engine may force-seal an
        /// incomplete line. Positive when source vocals overrun the boundary (overlapping lines)
        /// or when the last cell's target sits on the boundary itself.
        /// </summary>
        public double SealGraceMs { get; }

        public int TypeableCount { get; }

        /// <summary>
        /// Piecewise-linear anchor points for <see cref="SungPositionAt"/>:
        /// (StartTime, 0), each typeable cell's (TargetTime, displayIndex), (SingEndTime, Cells.Count).
        /// Times are clamped monotonic non-decreasing at construction.
        /// </summary>
        private readonly List<(double time, double index)> sungPoints;

        private TypingLine(LyricLine source, IReadOnlyList<TypingCell> cells, double sealGraceMs)
        {
            Source = source;

            var display = new System.Text.StringBuilder(cells.Count);

            foreach (var cell in cells)
                display.Append(cell.Expected);

            DisplayText = display.ToString();
            Cells = cells;
            StartTime = source.StartTime;
            EndTime = source.EndTime;
            SingEndTime = source.SingEndTime;
            SealGraceMs = sealGraceMs;

            int typeable = 0;

            foreach (var c in cells)
            {
                if (c.IsTypeable)
                    typeable++;
            }

            TypeableCount = typeable;

            double? firstTypeableTarget = null;

            foreach (var c in cells)
            {
                if (c.IsTypeable)
                {
                    firstTypeableTarget = c.TargetTime;
                    break;
                }
            }

            ActivationTime = firstTypeableTarget is double first
                ? Math.Max(StartTime, first - TypingEngine.CUE_LEAD_MS)
                : StartTime;

            FirstVocalTime = firstTypeableTarget ?? StartTime;

            // Pre-build the sung-position polyline, clamping times monotonic.
            sungPoints = new List<(double, double)>(typeable + 2) { (StartTime, 0) };
            double lastTime = StartTime;

            for (int i = 0; i < cells.Count; i++)
            {
                if (!cells[i].IsTypeable)
                    continue;

                double t = Math.Max(cells[i].TargetTime, lastTime);
                sungPoints.Add((t, i));
                lastTime = t;
            }

            sungPoints.Add((Math.Max(SingEndTime, lastTime), cells.Count));
        }

        /// <summary>
        /// Fractional display-cell index in [0, Cells.Count]: piecewise-linear through
        /// (StartTime, 0) .. (TargetTime_j, displayIndex_j) .. (SingEndTime, Cells.Count),
        /// clamped outside, zero-length time segments skipped (position jumps).
        /// </summary>
        public double SungPositionAt(double time)
        {
            if (time <= sungPoints[0].time)
                return sungPoints[0].index;

            if (time >= sungPoints[^1].time)
                return sungPoints[^1].index;

            // Left anchor: the LAST point with point.time <= time (this skips zero-length segments;
            // among points sharing one time we anchor at the greatest index).
            int left = 0;

            for (int i = 1; i < sungPoints.Count; i++)
            {
                if (sungPoints[i].time <= time)
                    left = i;
                else
                    break;
            }

            var (t0, v0) = sungPoints[left];
            var (t1, v1) = sungPoints[left + 1];

            if (t1 <= t0)
                return v1; // degenerate tail segment

            return v0 + (v1 - v0) * (time - t0) / (t1 - t0);
        }

        /// <summary>
        /// Flattens an authored line into the cell list gameplay judges and the stage renders.
        ///
        /// <para><paramref name="literate"/> selects WHICH STREAM the cells carry. Off (default):
        /// the cells are <see cref="Typeability.ToDefaultStream"/> of the authored text, so
        /// capitals fold to lower case, a hyphen becomes a typed space and every other supported
        /// mark is gone; that is what the player types AND what the display shows, because both
        /// read these same cells. On: one cell per authored char, punctuation and capitals
        /// included, every one of them typeable.</para>
        ///
        /// <para>Letter timings are IDENTICAL in both modes: the per-word char spread below counts
        /// only <see cref="Typeability.IsCell"/> chars (never punctuation), so turning the mod on
        /// adds cells without moving any of the existing ones.</para>
        /// </summary>
        public static TypingLine FromLyricLine(LyricLine line, TimingGranularity granularity = TimingGranularity.Line, bool literate = false)
        {
            string text = line.RawText;
            var units = line.Units;

            int n = text.Length;
            char[] expected = new char[n];
            bool[] isTypeable = new bool[n];
            double?[] targets = new double?[n];
            var judgeGrans = new TimingGranularity[n];

            for (int i = 0; i < n; i++)
                judgeGrans[i] = granularity;

            // First pass: walk the raw text token by token (spaces delimit tokens; token m maps to Units[m]).
            string[] tokens = text.Split(' ');
            int pos = 0;

            for (int m = 0; m < tokens.Length; m++)
            {
                string token = tokens[m];

                // Guard against malformed data where token count != unit count.
                TimedUnit? unit = units.Count > 0 ? units[Math.Min(m, units.Count - 1)] : null;

                double unitStart = unit?.StartTime ?? line.StartTime;
                double unitEnd = unit?.EndTime ?? line.SingEndTime;

                // Unreliable timing gets the widest windows: estimated lines and
                // low-confidence words are judged at the Line tier.
                TimingGranularity judgeGran = line.Estimated || (unit?.Confidence ?? 1) < SyncWindows.LOW_CONFIDENCE_SCORE
                    ? TimingGranularity.Line
                    : granularity;

                // k = number of typeable cells in this token (freestyle slots included: the player
                // presses a key for them, so they take a share of the word's time like any letter).
                int k = 0;

                foreach (char ch in token)
                {
                    if (Typeability.IsCell(ch))
                        k++;
                }

                // Syllable subdivisions warp the char-to-time mapping WITHIN the word: instead of
                // one flat ramp across [unitStart, unitEnd], the boundaries split it into segments
                // and the k chars are spread evenly across the segments in index-space, so the caret
                // reaches each boundary time at that boundary's proportional char and moves linearly
                // (but at a per-segment speed) between them. Empty boundaries => the flat ramp.
                var boundaries = unit?.SyllableBoundaries ?? System.Array.Empty<double>();

                int j = 0;

                foreach (char ch in token)
                {
                    expected[pos] = ch;
                    judgeGrans[pos] = judgeGran;

                    if (Typeability.IsCell(ch))
                    {
                        isTypeable[pos] = true;
                        // Typeable char j of k in unit u: first char AT unit start, piecewise across
                        // syllable boundaries (degenerates to u.Start + j*(u.End-u.Start)/k when undivided).
                        targets[pos] = syllableCharTarget(unitStart, unitEnd, boundaries, k, j);
                        j++;
                    }
                    // else: punctuation, resolved in the second pass.

                    pos++;
                }

                if (m < tokens.Length - 1)
                {
                    // Inter-word space cell: preceding unit's EndTime.
                    expected[pos] = ' ';
                    isTypeable[pos] = true;
                    targets[pos] = unitEnd;
                    judgeGrans[pos] = judgeGran;
                    pos++;
                }
            }

            // Second pass: time the chars pass one left untimed. After Typeability.Normalize the
            // only such chars are supported PUNCTUATION (letters, digits, spaces and freestyle
            // slots were all timed above, punctuation is excluded from the per-word char spread on
            // purpose so adding a mark never moves a letter).
            //
            // A run of marks between two timed chars is spread EVENLY across the gap: mark m of a
            // run of len takes prev + (m+1)*(next-prev)/(len+1). Under the Literate mod that gives
            // every mark its own slot in the sweep instead of colliding with the letter beside it,
            // and it is the same "distribute evenly across the span you have" rule the per-word
            // char spread itself uses. A run with nothing after it (trailing punctuation, the '.'
            // of "sat.") attaches to the PRECEDING char's target; one with nothing before it (a
            // line opening on a quote) attaches to the FOLLOWING char's.
            for (int i = 0; i < n; i++)
            {
                if (targets[i].HasValue)
                    continue;

                int end = i;

                while (end + 1 < n && !targets[end + 1].HasValue)
                    end++;

                double? before = i > 0 ? targets[i - 1] : null;
                double? after = end + 1 < n ? targets[end + 1] : null;
                int len = end - i + 1;

                for (int m = 0; m < len; m++)
                {
                    targets[i + m] = before is double p
                        ? (after is double q ? p + (m + 1) * (q - p) / (len + 1) : p)
                        : after ?? line.StartTime;
                }

                i = end;
            }

            // Guard: targets non-decreasing (clamp to previous if data is inverted).
            for (int i = 1; i < n; i++)
            {
                if (targets[i]!.Value < targets[i - 1]!.Value)
                    targets[i] = targets[i - 1];
            }

            TypingCell[] cells;

            if (literate)
            {
                // One cell per authored char, all of them typed. A supported mark is a first-class
                // typeable cell here (and therefore COUNTABLE too: it costs a real keypress, so it
                // spends the Flashlight/Fletcher character budget like any other non-space char).
                cells = new TypingCell[n];

                for (int i = 0; i < n; i++)
                    cells[i] = new TypingCell(expected[i], isTypeable[i] || Typeability.IsPunctuation(expected[i]), targets[i]!.Value, judgeGrans[i]);
            }
            else
            {
                // The default stream. Each surviving char keeps the timing and judge tier of the
                // authored char it came from, so a hyphen-turned-space lands on the interpolated
                // slot the hyphen held between the two letters it separated.
                var sb = new System.Text.StringBuilder(n);
                var sources = new List<int>(n);
                Typeability.ProjectDefault(text, sb, sources);

                cells = new TypingCell[sources.Count];

                for (int i = 0; i < sources.Count; i++)
                {
                    int src = sources[i];
                    cells[i] = new TypingCell(sb[i], Typeability.IsCell(sb[i]), targets[src]!.Value, judgeGrans[src]);
                }
            }

            // A last typeable cell whose target sits on the seal boundary loses the target-vs-seal
            // race every frame; grant a minimum finish window on top of any data-driven grace.
            double sealGrace = line.SealGraceMs;

            for (int i = cells.Length - 1; i >= 0; i--)
            {
                if (!cells[i].IsTypeable)
                    continue;

                if (cells[i].TargetTime >= line.EndTime - boundary_epsilon_ms)
                    sealGrace = Math.Max(sealGrace, min_boundary_grace_ms);

                break;
            }

            return new TypingLine(line, cells, Math.Min(sealGrace, max_seal_grace_ms));
        }

        /// <summary>
        /// Target time of typeable char <paramref name="j"/> (0-based, of <paramref name="k"/> in the
        /// word) under piecewise-linear syllable timing. The word spans [unitStart, unitEnd]; each
        /// entry of <paramref name="boundaries"/> (absolute ms, strictly inside, ascending) splits it
        /// into one more segment. The k chars are distributed evenly by index across the segments, so
        /// segment s covers char-index range [s*k/S, (s+1)*k/S] mapped linearly onto its time range,
        /// where S = segment count. With no boundaries this is exactly unitStart + j*(unitEnd-unitStart)/k;
        /// char j = 0 always lands on unitStart. Monotonic non-decreasing in j because boundaries are sorted.
        /// </summary>
        internal static double syllableCharTarget(double unitStart, double unitEnd, IReadOnlyList<double> boundaries, int k, int j)
        {
            if (k <= 0)
                return unitStart;

            if (boundaries.Count == 0)
                return unitStart + (double)j * (unitEnd - unitStart) / k;

            int segments = boundaries.Count + 1;

            // Which segment holds char index j (floor of j scaled into segment-space), clamped to the last.
            int s = (int)Math.Floor((double)j * segments / k);

            if (s >= segments)
                s = segments - 1;

            double segIndexLo = (double)s * k / segments;
            double segIndexHi = (double)(s + 1) * k / segments;
            double timeLo = s == 0 ? unitStart : boundaries[s - 1];
            double timeHi = s == segments - 1 ? unitEnd : boundaries[s];

            if (segIndexHi <= segIndexLo)
                return timeLo;

            return timeLo + (j - segIndexLo) / (segIndexHi - segIndexLo) * (timeHi - timeLo);
        }

        private const double boundary_epsilon_ms = 30;
        private const double min_boundary_grace_ms = 250;
        private const double max_seal_grace_ms = 700;
    }
}
