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

        /// <summary>Signed lead/lag of the awarded correct keypress, in MILLISECONDS.</summary>
        public double? JudgedDelta { get; internal set; }

        /// <summary>
        /// The awarded correct keypress's offset in the measure the play is JUDGED in (see
        /// <see cref="SyncMeasure"/>): a fractional CHARACTER DISTANCE by default (negative = the
        /// press was ahead of the playhead), and equal to <see cref="JudgedDelta"/> under
        /// <see cref="SyncMeasure.Milliseconds"/>.
        ///
        /// <para>This, and not <see cref="JudgedDelta"/>, is what
        /// <see cref="SyncWindows.Classify"/> and <see cref="SyncWindows.SyncQuality"/> read, so it
        /// is what the sync tint and the sync percent are computed from. The two are kept apart
        /// because they answer different questions: the delta says WHEN the key was pressed, the
        /// offset says how far off the playhead that was in the map's own pace.</para>
        /// </summary>
        public double? JudgedOffset { get; internal set; }

        /// <summary>
        /// <see cref="SyncWindows.SyncQuality"/> of the awarded correct keypress, in [0, 1], banked
        /// at the moment it was judged. It is BANKED rather than recomputed because the windows it
        /// comes from depend on the play's <see cref="SyncMeasure"/>, and the one thing that knows
        /// the measure is the engine: the stage reads cells pull-based and holds no engine
        /// reference, so recomputing it out there would silently judge a millisecond offset against
        /// character windows the day a mod selects the other measure. Banking also makes the sync
        /// percent a sum rather than a whole-map reclassification every frame.
        /// </summary>
        public double? JudgedSyncQuality { get; internal set; }

        /// <summary>
        /// Delta of the FIRST correct judgement, in milliseconds: set once, never cleared (survives
        /// backspace). Its presence makes later correct retypes scoring-inert, so backspace-retype
        /// cannot farm score/combo/accuracy.
        /// </summary>
        internal double? FirstCorrectDelta { get; set; }

        /// <summary>
        /// <see cref="JudgedOffset"/> of the FIRST correct judgement, kept alongside
        /// <see cref="FirstCorrectDelta"/> and for the same reason: a scoring-inert retype replays
        /// the judgement the cell already earned rather than earning a new one, and under the
        /// character measure the offset is what that judgement was derived from.
        /// </summary>
        internal double? FirstCorrectOffset { get; set; }

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

        /// <summary>
        /// Target times of the line's TYPEABLE cells, in display order and therefore ascending
        /// (construction clamps every target non-decreasing). This is the axis character DISTANCE is
        /// measured on: index k is "the k'th character of this line".
        /// </summary>
        private readonly double[] judgeTargets;

        /// <summary>
        /// Where each DISPLAY cell sits on that axis. A typeable cell sits exactly on its own
        /// integer index; a non-typeable one (auto-skipped punctuation) is interpolated onto the
        /// axis from its target, so a caller never has to special-case it. Nothing judges a
        /// non-typeable cell (the caret hops it), so which end of a tie it takes cannot reach a
        /// score.
        /// </summary>
        private readonly double[] cellPositions;

        /// <summary>
        /// Milliseconds per character used to EXTRAPOLATE beyond the ends of
        /// <see cref="judgeTargets"/>: this line's mean typeable spacing, with the fallbacks
        /// described on <see cref="computeExtrapolationSpacing"/>.
        /// </summary>
        private readonly double extrapolationSpacingMs;

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

            // The character-distance axis (backlog 133). Typeable cells only: they are the ones the
            // caret stops on and the ones a keypress can ever be judged against, and SPACES are
            // among them on purpose. A word gap is a cell with a target that the playhead crosses,
            // so leaving it out would put a hole in the axis and make the interpolation jump. This
            // is deliberately NOT the countable stream (TypingCell.IsCountable, which excludes
            // spaces): that one measures a keypress BUDGET for Flashlight and Fletcher, a different
            // question with a different right answer.
            judgeTargets = new double[typeable];
            int nextTarget = 0;

            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].IsTypeable)
                    judgeTargets[nextTarget++] = cells[i].TargetTime;
            }

            extrapolationSpacingMs = computeExtrapolationSpacing();

            cellPositions = new double[cells.Count];
            int rank = 0;

            for (int i = 0; i < cells.Count; i++)
                cellPositions[i] = cells[i].IsTypeable ? rank++ : playheadSpan(cells[i].TargetTime).last;
        }

        /// <summary>
        /// The character spacing to extrapolate at beyond the ends of the axis, in milliseconds per
        /// character. The line's MEAN typeable spacing, which is its own pace and is immune to a
        /// single degenerate gap in a way the first/last interval would not be. Two fallbacks, for
        /// data that offers no spacing at all: a line with one typeable cell, or one whose targets
        /// all sit on the same millisecond, falls back to its sung span over its cell count, and a
        /// line with no span either falls back to <see cref="FALLBACK_CHAR_SPACING_MS"/>. The result
        /// is always strictly positive, so no caller can divide by zero.
        /// </summary>
        private double computeExtrapolationSpacing()
        {
            int m = judgeTargets.Length;

            if (m >= 2)
            {
                double mean = (judgeTargets[m - 1] - judgeTargets[0]) / (m - 1);

                if (mean > 0)
                    return mean;
            }

            if (m >= 1)
            {
                double sung = (Math.Max(SingEndTime, judgeTargets[m - 1]) - judgeTargets[0]) / m;

                if (sung > 0)
                    return sung;
            }

            return FALLBACK_CHAR_SPACING_MS;
        }

        /// <summary>
        /// How many characters a keypress at <paramref name="time"/> on the cell at
        /// <paramref name="cellIndex"/> is from the character the playhead is on. NEGATIVE means the
        /// press is AHEAD of the playhead (the player is rushing), positive that it is behind
        /// (dragging), matching the sign of the millisecond delta it replaces. Fractional: a press
        /// halfway between two characters' targets is half a character out. This is the whole of
        /// what backlog 133 judges on.
        ///
        /// <para>The playhead is a SPAN of characters, not a point (see
        /// <see cref="playheadSpan"/>), and a cell inside that span is exactly 0 characters out. For
        /// every ordinary press that is the same thing as subtracting one position from another,
        /// because the span is a single point; it differs only where several characters share one
        /// target time, which is the ordinary case at a word boundary (a word gap takes its unit's
        /// end time and the next word's first letter takes the next unit's start time, and for
        /// contiguous words those are the same millisecond). Both of those characters are equally
        /// "the one the playhead is on", so a press dead on that time has to read as 0 for both;
        /// picking either end of the run instead would charge a rhythm-perfect player a whole
        /// character for the other one.</para>
        /// </summary>
        public double CharacterDistanceAt(double time, int cellIndex)
        {
            double cell = CellPosition(cellIndex);
            (double first, double last) = playheadSpan(time);

            if (cell < first)
                return first - cell; // the playhead has gone past this character: the press is behind

            if (cell > last)
                return last - cell;  // the playhead has not reached it yet: the press is ahead

            return 0;                // the playhead is ON this character
        }

        /// <summary>The cell's own position on the character axis (see <see cref="cellPositions"/>).</summary>
        public double CellPosition(int cellIndex)
            => cellPositions.Length == 0 ? 0 : cellPositions[Math.Clamp(cellIndex, 0, cellPositions.Length - 1)];

        /// <summary>
        /// Where the playhead is on this line's character axis at <paramref name="time"/>, as the
        /// INCLUSIVE RANGE of fractional cell positions it covers. The two ends are equal for almost
        /// every time; they separate only when the time lands exactly on a run of characters that
        /// share a target, and then the range is that whole run.
        ///
        /// <para>INSIDE the line, between two targets, it is the exact piecewise-linear
        /// interpolation between them, which is the inverse of the per-character target
        /// interpolation <see cref="FromLyricLine"/> already does. Where spacing is locally uniform
        /// it reduces exactly to the millisecond delta divided by that spacing, which is the point of
        /// the whole measure.</para>
        ///
        /// <para>OUTSIDE it, at the line's first and last characters, one bracket is missing, and the
        /// position is EXTRAPOLATED linearly at <see cref="extrapolationSpacingMs"/> rather than
        /// clamped. Clamping is what <see cref="SungPositionAt"/> does, correctly, because a caret
        /// sweep must not leave its line; doing it here would make every early press on a line's
        /// first character a distance of exactly 0, i.e. a Perfect however early it was, which is the
        /// one answer that must not come out. Extrapolating instead reads a press one second before a
        /// 100 ms/character line opens as ten characters early, a miss, exactly as the old
        /// millisecond windows read it.</para>
        ///
        /// <para>The axis is PER LINE, and that is the reason the ends need extrapolating at all. A
        /// map-wide axis would bracket a press made during the cue lead between the PREVIOUS line's
        /// last character and this line's first, so a ten-second instrumental gap would compress into
        /// one character of distance and a press two seconds early would read as a fifth of a
        /// character out, i.e. a Perfect. Per line, the gaps between lines are simply not on the
        /// axis.</para>
        /// </summary>
        private (double first, double last) playheadSpan(double time)
        {
            int m = judgeTargets.Length;

            // A line with nothing typeable has no characters to be distant from, and no keypress can
            // reach one either (the caret auto-skips straight past it).
            if (m == 0)
                return (0, 0);

            int last = lastAtOrBefore(time);  // -1 when the time precedes every target
            int first = firstAtOrAfter(time); // m  when the time follows every target

            // The time lands exactly on one or more targets: the playhead is on all of them.
            if (first <= last)
                return (first, last);

            double position;

            if (last < 0)
                position = (time - judgeTargets[0]) / extrapolationSpacingMs;
            else if (first >= m)
                position = (m - 1) + (time - judgeTargets[m - 1]) / extrapolationSpacingMs;
            else
                // Strictly between two targets, so first == last + 1 and the bracket has real width:
                // the divisor cannot be zero however many characters share a millisecond elsewhere.
                position = last + (time - judgeTargets[last]) / (judgeTargets[first] - judgeTargets[last]);

            return (position, position);
        }

        /// <summary>The last index of <see cref="judgeTargets"/> at or before <paramref name="time"/>, or -1.</summary>
        private int lastAtOrBefore(double time)
        {
            int found = -1;
            int lo = 0;
            int hi = judgeTargets.Length - 1;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;

                if (judgeTargets[mid] <= time)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                    hi = mid - 1;
            }

            return found;
        }

        /// <summary>The first index of <see cref="judgeTargets"/> at or after <paramref name="time"/>, or the length.</summary>
        private int firstAtOrAfter(double time)
        {
            int found = judgeTargets.Length;
            int lo = 0;
            int hi = judgeTargets.Length - 1;

            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;

                if (judgeTargets[mid] >= time)
                {
                    found = mid;
                    hi = mid - 1;
                }
                else
                    lo = mid + 1;
            }

            return found;
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

        /// <summary>
        /// The character spacing a line falls back to when its own data offers none at all (see
        /// <see cref="computeExtrapolationSpacing"/>): one typeable cell, or every target on the
        /// same millisecond, AND no sung span to divide either. 200 ms per character is 60 WPM, a
        /// plausible middle of the range this game is typed at, so the windows such a line hands out
        /// are neither free nor impossible. It is a floor for degenerate data, never a tuning knob:
        /// no real aligned line reaches it.
        /// </summary>
        public const double FALLBACK_CHAR_SPACING_MS = 200;

        private const double boundary_epsilon_ms = 30;
        private const double min_boundary_grace_ms = 250;
        private const double max_seal_grace_ms = 700;
    }
}
