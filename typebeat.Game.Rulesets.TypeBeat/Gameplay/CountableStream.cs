// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// THE COUNTABLE STREAM (Fletcher): the whole map read as one run of COUNTABLE cells (typeable
    /// and not a space), which is the currency the rush cap measures in. <see cref="targets"/> holds
    /// every countable cell's target time, sorted ascending, so the playhead's position is a binary
    /// search; <see cref="lineBase"/> and <see cref="linePrefix"/> say where line k, cell i sits in
    /// the stream, so the caret's position is a lookup. The base and prefix are fixed at construction
    /// (which cells count never changes); the targets are re-read by <see cref="RebuildTargets"/>
    /// whenever the cells' target times are swapped.
    /// </summary>
    internal sealed class CountableStream
    {
        private readonly IReadOnlyList<TypingLine> lines;

        private double[] targets = Array.Empty<double>();

        private readonly int[] lineBase;

        private readonly int[][] linePrefix;

        public CountableStream(IReadOnlyList<TypingLine> lines)
        {
            this.lines = lines;

            lineBase = new int[lines.Count];
            linePrefix = new int[lines.Count][];
            int position = 0;

            for (int k = 0; k < lines.Count; k++)
            {
                var cells = lines[k].Cells;
                lineBase[k] = position;
                var prefix = new int[cells.Count + 1];

                for (int i = 0; i < cells.Count; i++)
                {
                    prefix[i + 1] = prefix[i];

                    if (!cells[i].IsCountable)
                        continue;

                    prefix[i + 1]++;
                    position++;
                }

                linePrefix[k] = prefix;
            }

            RebuildTargets();
        }

        /// <summary>
        /// Re-read every countable cell's CURRENT target time into <see cref="targets"/>. Called at
        /// construction and again whenever <see cref="TypingEngine.AlignSubdivisionTargets"/> swaps the
        /// cells between the legacy and the aligned targets, so the playhead the rush cap measures
        /// against is always the one this run is judged on.
        /// </summary>
        public void RebuildTargets()
        {
            var sorted = new List<double>();

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (cell.IsCountable)
                        sorted.Add(cell.TargetTime);
                }
            }

            // Overlapping lines can interleave their targets across a boundary, so sort rather than
            // assume the per-line order carries: the playhead lookup only needs "how many countable
            // targets are at or before this time".
            sorted.Sort();
            targets = sorted.ToArray();
        }

        /// <summary>
        /// How many countable cells of the whole map have a target at or before <paramref name="time"/>
        /// (see <see cref="TypingEngine.PlayheadCountablePosition"/>).
        /// </summary>
        public int PlayheadPosition(double time)
        {
            int lo = 0;
            int hi = targets.Length;

            while (lo < hi)
            {
                int mid = (lo + hi) / 2;

                if (targets[mid] <= time)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        /// <summary>
        /// Where cell <paramref name="cellIndex"/> of line <paramref name="lineIndex"/> sits in the
        /// stream: the countable cells of every line before it, plus the countable cells of this line
        /// before the cell, the index clamped to the line.
        /// </summary>
        public int PositionAt(int lineIndex, int cellIndex)
        {
            var prefix = linePrefix[lineIndex];

            return lineBase[lineIndex] + prefix[Math.Clamp(cellIndex, 0, prefix.Length - 1)];
        }
    }
}
