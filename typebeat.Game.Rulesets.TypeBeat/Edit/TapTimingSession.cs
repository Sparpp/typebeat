// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// A live tap-timing recording. Record-then-commit: this holds the QUEUE of word slots being
    /// timed, a snapshot of the lines it was started against, and a plain <see cref="List{T}"/> of
    /// song times. Nothing here touches the beatmap; entering and leaving tap mode without
    /// committing leaves zero trace (and no undo entry), and the whole pass lands in one shot via
    /// <see cref="TapTimingBuilder.Build"/>.
    ///
    /// <para>Because the taps are just a list, transport control is trivial: pausing holds, resuming
    /// carries on, and seeking backwards drops every tap at or after the seek point
    /// (<see cref="TruncateFrom"/>), which rewinds the queue by exactly the same amount.</para>
    /// </summary>
    public sealed class TapTimingSession
    {
        /// <summary>Taps closer together than this are treated as a double-fire and ignored.</summary>
        public const double MIN_TAP_GAP_MS = TypeBeatEditorOperations.MIN_SPAN_MS;

        /// <summary>The lines the session was started against (the commit is built from these).</summary>
        public IReadOnlyList<LyricLine> Lines { get; }

        /// <summary>The word slots being timed, contiguous in sheet order.</summary>
        public IReadOnlyList<TapTarget> Queue { get; }

        private readonly List<double> taps = new List<double>();

        /// <summary>The recorded song times, ascending. One per timed word, in queue order.</summary>
        public IReadOnlyList<double> Taps => taps;

        public TapTimingSession(IReadOnlyList<LyricLine> lines, IReadOnlyList<TapTarget> queue)
        {
            Lines = lines;
            Queue = queue;
        }

        /// <summary>How many words have been timed so far.</summary>
        public int TappedCount => taps.Count;

        /// <summary>Whether every queued word has a tap (the pass is finished, nothing left to time).</summary>
        public bool QueueComplete => taps.Count >= Queue.Count;

        /// <summary>The most recent tap, or null before the first one.</summary>
        public double? LastTapTime => taps.Count > 0 ? taps[^1] : null;

        /// <summary>The word slot the next tap will time, or null when the queue is exhausted.</summary>
        public TapTarget? NextTarget => QueueComplete ? null : Queue[taps.Count];

        /// <summary>The text of the queued word at <paramref name="index"/>, or empty when out of range.</summary>
        public string WordAt(int index)
        {
            if (index < 0 || index >= Queue.Count)
                return string.Empty;

            var target = Queue[index];

            if (target.LineIndex < 0 || target.LineIndex >= Lines.Count)
                return string.Empty;

            var units = Lines[target.LineIndex].Units;
            return target.UnitIndex >= 0 && target.UnitIndex < units.Count ? units[target.UnitIndex].Text : string.Empty;
        }

        /// <summary>Whether the queued word at <paramref name="index"/> opens a new lyric line.</summary>
        public bool StartsLine(int index)
            => index >= 0 && index < Queue.Count && (index == 0 || Queue[index - 1].LineIndex != Queue[index].LineIndex);

        /// <summary>
        /// Records a tap at <paramref name="songTime"/>. Refused (returns false) once the queue is
        /// exhausted, or when the tap lands within <see cref="MIN_TAP_GAP_MS"/> of the previous one
        /// (a key repeat or a double fire, never two real words).
        /// </summary>
        public bool Tap(double songTime)
        {
            if (QueueComplete)
                return false;

            double time = Math.Max(0, songTime);

            if (taps.Count > 0 && time < taps[^1] + MIN_TAP_GAP_MS)
                return false;

            taps.Add(time);
            return true;
        }

        /// <summary>Drops the most recent tap (the mapper fumbled one word). False when there is none.</summary>
        public bool UndoLastTap()
        {
            if (taps.Count == 0)
                return false;

            taps.RemoveAt(taps.Count - 1);
            return true;
        }

        /// <summary>
        /// Drops every tap at or after <paramref name="songTime"/>: what a backward seek means.
        /// Returns whether anything was dropped.
        /// </summary>
        public bool TruncateFrom(double songTime)
        {
            int keep = taps.Count;

            while (keep > 0 && taps[keep - 1] >= songTime)
                keep--;

            if (keep == taps.Count)
                return false;

            taps.RemoveRange(keep, taps.Count - keep);
            return true;
        }

        /// <summary>The sheet this session commits to, built in one shot from the recorded taps.</summary>
        public IReadOnlyList<LyricLine> BuildCommit() => TapTimingBuilder.Build(Lines, Queue, taps);
    }
}
