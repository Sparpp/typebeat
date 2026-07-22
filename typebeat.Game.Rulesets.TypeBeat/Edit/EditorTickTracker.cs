// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Frame-to-frame crossing detector for the compose screen's audible note ticks. Given the
    /// playhead time each running frame, it reports which fixed tick times (word-unit starts) the
    /// playhead swept across since the previous frame — the half-open interval (prev, now].
    ///
    /// It is deliberately state-light and side-effect free so the compose screen owns all the audio:
    /// the screen calls <see cref="Advance"/> only while the editor clock is running and plays a
    /// sample per returned time, and calls <see cref="Reset"/> whenever playback is not advancing
    /// normally (paused, stopped, or right after a manual seek) so the gap that opens up is never
    /// reported as a burst of crossings.
    ///
    /// Non-forward frames (rewind / scrub-back) and implausibly large forward jumps (a seek while
    /// playing) are self-suppressing: they yield no ticks and simply re-anchor the tracker, so the
    /// playhead never "machine-guns" every unit it teleported past.
    /// </summary>
    public sealed class EditorTickTracker
    {
        /// <summary>
        /// Largest forward gap (ms) between two consecutive running frames still treated as ordinary
        /// playback. The editor plays at &lt;= 1x, so even a badly hitched frame stays well under this;
        /// anything larger is assumed to be a seek jump and produces no ticks for that frame.
        /// </summary>
        public const double MAX_FRAME_DELTA_MS = 250;

        private readonly double maxFrameDelta;

        /// <summary>Playhead time at the previous running frame, or null when re-anchoring is pending.</summary>
        private double? lastTime;

        public EditorTickTracker(double maxFrameDelta = MAX_FRAME_DELTA_MS)
        {
            this.maxFrameDelta = maxFrameDelta;
        }

        /// <summary>
        /// Forget the previous frame time. The next <see cref="Advance"/> call reports no crossings and
        /// just re-anchors, so a pause gap or a seek target is never swept. Call whenever the clock is
        /// not running (paused/stopped) or immediately after a programmatic seek.
        /// </summary>
        public void Reset() => lastTime = null;

        /// <summary>
        /// Advance to <paramref name="currentTime"/> and return the <paramref name="tickTimes"/> lying in
        /// (previousFrameTime, currentTime], ascending. Returns empty on the first frame after a reset, on
        /// a non-forward frame (rewind/scrub-back), and on an implausibly large forward jump; in every case
        /// the tracker re-anchors to <paramref name="currentTime"/> for the next frame.
        /// </summary>
        /// <param name="currentTime">The editor clock's current playhead time (ms).</param>
        /// <param name="tickTimes">Candidate tick times (ms); order need not be sorted. Read once, not retained.</param>
        public IReadOnlyList<double> Advance(double currentTime, IEnumerable<double> tickTimes)
        {
            double? prev = lastTime;
            lastTime = currentTime;

            // First frame after a reset: nothing to compare against, just anchor.
            if (prev is not double previous)
                return Array.Empty<double>();

            double delta = currentTime - previous;

            // Not moving forward (paused frame slipped through, rewind, scrub-back) or a seek-sized
            // jump: swallow the interval so we never burst-tick across it.
            if (delta <= 0 || delta > maxFrameDelta)
                return Array.Empty<double>();

            List<double>? crossed = null;

            foreach (double t in tickTimes)
            {
                if (t > previous && t <= currentTime)
                    (crossed ??= new List<double>()).Add(t);
            }

            if (crossed == null)
                return Array.Empty<double>();

            crossed.Sort();
            return crossed;
        }
    }
}
