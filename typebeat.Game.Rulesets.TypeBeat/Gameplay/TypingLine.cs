// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/TypingLine.cs (regression-anchored).
// Pure C# — no osu.Framework dependencies. All times are double milliseconds.

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
        /// <summary>Display char as authored (normalized).</summary>
        public char Expected { get; }

        public bool IsTypeable { get; }

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
        /// Delta of the FIRST correct judgement — set once, never cleared (survives backspace).
        /// Its presence makes later correct retypes scoring-inert, so backspace-retype cannot
        /// farm score/combo/accuracy.
        /// </summary>
        internal double? FirstCorrectDelta { get; set; }

        internal TypingCell(char expected, bool isTypeable, double targetTime, TimingGranularity judgeGranularity)
        {
            Expected = expected;
            IsTypeable = isTypeable;
            TargetTime = targetTime;
            JudgeGranularity = judgeGranularity;
            State = CellState.Untyped;
        }
    }

    public sealed class TypingLine
    {
        public LyricLine Source { get; }

        /// <summary>== Source.RawText.</summary>
        public string DisplayText { get; }

        public IReadOnlyList<TypingCell> Cells { get; }

        public double StartTime { get; }

        public double EndTime { get; }

        public double SingEndTime { get; }

        /// <summary>
        /// When this line becomes typeable: a constant cue lead before its first typeable cell's
        /// target (<see cref="TypingEngine.CUE_LEAD_MS"/>), never earlier than <see cref="StartTime"/>
        /// (the shared boundary — the previous line cannot seal before it). Independent of the
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
            DisplayText = source.RawText;
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

            // Left anchor: the LAST point with point.time <= time (this skips zero-length segments —
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

        public static TypingLine FromLyricLine(LyricLine line, TimingGranularity granularity = TimingGranularity.Line)
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

                // k = number of typeable chars in this token.
                int k = 0;

                foreach (char ch in token)
                {
                    if (Typeability.IsTypeable(ch))
                        k++;
                }

                int j = 0;

                foreach (char ch in token)
                {
                    expected[pos] = ch;
                    judgeGrans[pos] = judgeGran;

                    if (Typeability.IsTypeable(ch))
                    {
                        isTypeable[pos] = true;
                        // Typeable char j of k in unit u: u.Start + j * (u.End - u.Start) / k — first char AT unit start.
                        targets[pos] = unitStart + j * (unitEnd - unitStart) / k;
                        j++;
                    }
                    // else: punctuation — resolved in the second pass.

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

            // Second pass (a): non-typeable cells copy the NEXT typeable cell's TargetTime.
            double? next = null;

            for (int i = n - 1; i >= 0; i--)
            {
                if (targets[i].HasValue)
                    next = targets[i];
                else if (next.HasValue)
                    targets[i] = next;
            }

            // Second pass (b): trailing punctuation (no next typeable) copies the PREVIOUS target.
            double? prev = null;

            for (int i = 0; i < n; i++)
            {
                if (targets[i].HasValue)
                    prev = targets[i];
                else
                    targets[i] = prev ?? line.StartTime;
            }

            // Guard: targets non-decreasing (clamp to previous if data is inverted).
            for (int i = 1; i < n; i++)
            {
                if (targets[i]!.Value < targets[i - 1]!.Value)
                    targets[i] = targets[i - 1];
            }

            var cells = new TypingCell[n];

            for (int i = 0; i < n; i++)
                cells[i] = new TypingCell(expected[i], isTypeable[i], targets[i]!.Value, judgeGrans[i]);

            // A last typeable cell whose target sits on the seal boundary loses the target-vs-seal
            // race every frame — grant a minimum finish window on top of any data-driven grace.
            double sealGrace = line.SealGraceMs;

            for (int i = n - 1; i >= 0; i--)
            {
                if (!isTypeable[i])
                    continue;

                if (targets[i]!.Value >= line.EndTime - boundary_epsilon_ms)
                    sealGrace = Math.Max(sealGrace, min_boundary_grace_ms);

                break;
            }

            return new TypingLine(line, cells, Math.Min(sealGrace, max_seal_grace_ms));
        }

        private const double boundary_epsilon_ms = 30;
        private const double min_boundary_grace_ms = 250;
        private const double max_seal_grace_ms = 700;
    }
}
