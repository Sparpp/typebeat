// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, PACE AND RESULTS: the active-time WPM clock and its lazy arm, the rolling WPM
// ring, the live accuracy and sync readouts, and the results summary built from them.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>How many of the most recent correct keypresses <see cref="LiveRollingWpm"/> averages over.</summary>
        private const int rolling_wpm_window = 30;

        /// <summary>correctKeypresses / allCharKeypresses; 1.0 before any keypress.</summary>
        public double LiveAccuracy => totalKeypresses == 0 ? 1.0 : correctKeypresses / (double)totalKeypresses;

        /// <summary>
        /// Gross WPM over active time only; 0 before any active time. Active time is REAL elapsed
        /// time (see <see cref="activeRealTimeMs"/>), not beatmap time, so the readout is the
        /// player's actual typing speed under any speed-adjusting mod rather than 1/rate of it.
        /// Japanese internal input counts accepted keys, including romaji prefixes, rather than
        /// the fewer original-script characters their conversions produce.
        /// </summary>
        public double LiveWpm
        {
            get
            {
                if (activeRealTimeMs <= 0)
                    return 0;

                return (wpmInputCount() / 5.0) / (activeRealTimeMs / 60000.0);
            }
        }

        /// <summary>
        /// Gross WPM over the last <see cref="rolling_wpm_window"/> correct keypresses: the HUD's live
        /// readout, so the number tracks how fast the player is typing RIGHT NOW instead of averaging the
        /// whole run flat. Display only; <see cref="LiveWpm"/> and <see cref="ResultsSummary.Wpm"/> keep
        /// the whole-run figure.
        /// Falls back to <see cref="LiveWpm"/> until the window holds at least two presses spread over a
        /// non-zero span, so the readout is meaningful from the first seconds instead of flapping.
        /// The clock is active time, exactly like <see cref="LiveWpm"/>: count-ins, instrumental gaps and
        /// post-line-completion waits do not decay the value, they simply do not pass. Being stamped in
        /// that same REAL-time currency (see <see cref="activeRealTimeMs"/>), this inherits the
        /// speed-adjusting-mod correction for free and must never be scaled by the rate a second time.
        /// </summary>
        public double LiveRollingWpm
        {
            get
            {
                if (rollingCount < 2)
                    return LiveWpm;

                // Once the ring is full, the next slot to write is also the oldest entry.
                double oldest = rollingSamples[rollingCount < rolling_wpm_window ? 0 : rollingNext];
                double newest = rollingSamples[(rollingNext + rolling_wpm_window - 1) % rolling_wpm_window];
                double spanMs = newest - oldest;

                // Every press in the window landed within a single frame, so active time never advanced
                // between them: there is no span to divide by, defer to the whole-run figure.
                if (spanMs <= 0)
                    return LiveWpm;

                // n presses bound n-1 inter-key gaps, so the span covers (n-1) chars' worth of typing,
                // not n. Using n would inflate the readout by n/(n-1) (3.4% at a 30-press window, and
                // badly more while the window is still filling).
                return ((rollingCount - 1) / 5.0) / (spanMs / 60000.0);
            }
        }

        /// <summary>
        /// Mean sync quality (x100) over TIMED cells resolved so far (judged correct + sealed); 100
        /// before anything resolves. SPACE cells are excluded from both halves of the mean since
        /// backlog 148: an untimed space is judged on a zeroed delta, so counting it would hand back
        /// a full 1.0 quality it never earned and lift this readout for free. Out of the numerator
        /// AND the denominator, so a space neither helps nor hurts, which is the same treatment the
        /// sync timeline gives it (see <see cref="ProcessKey"/>).
        ///
        /// <para>DISPLAY ONLY, and only when asked for: the HUD reads this behind
        /// <c>TypeBeatRulesetSetting.ShowSyncMetric</c> (off by default since backlog 251) and
        /// nothing else reads it at all. It is still computed unconditionally, which is what keeps
        /// the toggle a pure display switch.</para>
        /// </summary>
        public double LiveSyncPercent
        {
            get
            {
                double sum = 0;
                int resolved = 0;

                for (int i = 0; i < lines.Count; i++)
                {
                    foreach (var cell in lines[i].Cells)
                    {
                        if (!isTimed(cell))
                            continue;

                        if (cell.State == CellState.Correct && cell.JudgedDelta is double d)
                        {
                            sum += WindowsFor(cell).SyncQuality(d);
                            resolved++;
                        }
                        else if (lineSealed[i])
                        {
                            // Missed / still-Wrong at seal: q = 0.
                            resolved++;
                        }
                    }
                }

                return resolved == 0 ? 100 : 100 * sum / resolved;
            }
        }

        /// <summary>Ring buffer of the ACTIVE-REAL-TIME stamps of the last correct keypresses (see <see cref="LiveRollingWpm"/>).</summary>
        private readonly double[] rollingSamples = new double[rolling_wpm_window];

        /// <summary>
        /// The WPM clock: REAL (wall-clock) milliseconds spent with a line active, incomplete and the
        /// song actually singing it. Deliberately NOT beatmap milliseconds: <see cref="Update"/> is fed
        /// beatmap times, so each segment is divided by the clock rate that applied over it. Under Half
        /// Time (0.75x) 2000 ms of beatmap time is 2666.67 ms the player really had, and under Double
        /// Time (1.5x) it is 1333.33 ms. Nothing in judgement reads this; it only feeds
        /// <see cref="LiveWpm"/>, <see cref="LiveRollingWpm"/> and <see cref="ResultsSummary.Wpm"/>.
        /// </summary>
        private double activeRealTimeMs;

        /// <summary>
        /// THE LAZY CLOCK ARM (backlog 222): the line the WPM clock has been armed on AHEAD OF ITS
        /// CUE, and the instant it was armed at, which is the time of the first press the player put
        /// on that line while the song had not reached it yet. -1 / 0 when nothing is armed, which is
        /// every ordinary in-sync frame. See <see cref="clockRunsFrom"/> for what it buys.
        ///
        /// <para>Read only through <see cref="clockRunsFrom"/>, which validates it against the CURRENT
        /// <see cref="activeLineIndex"/> rather than clearing it at each of the six sites that can move
        /// the caret. An arm left behind on a line the caret has left is simply not this line's arm,
        /// and a line is only ever entered once (the caret advances, or goes to -1 and comes back on a
        /// LATER line, never on an earlier one), so a stale arm can never be mistaken for a live
        /// one.</para>
        ///
        /// <para>A pure function of (keypress times, beatmap): no wall clock, no frame cadence, and
        /// cleared by <see cref="reset"/> like every other accumulator, which is what keeps the WPM
        /// digest in <c>ReplayRewindTest</c> reproducible across a seek.</para>
        /// </summary>
        private int wpmClockArmedLine = -1;

        private double wpmClockArmedAt;

        private int rollingCount; // entries held in rollingSamples, capped at rolling_wpm_window

        private int rollingNext;  // next slot to write; also the oldest entry once the ring is full

        /// <summary>
        /// The instant from which the WPM/sync active-time clock runs across the frame that STARTED at
        /// <paramref name="previousTime"/>, or null when it does not run over that frame at all.
        /// Normally that instant is <paramref name="previousTime"/> itself, i.e. the whole frame
        /// counts; the one case that returns something later is the lazy arm below.
        ///
        /// <para>The whole frame, always, with a pinned caret. Under <see cref="FletcherEnabled"/> the
        /// caret can be sitting on a line the song has not reached yet, and a clock that ran through a
        /// 20-second instrumental would read the wait as typing time; so being there is not by itself
        /// enough, and the clock runs from that line's <see cref="TypingLine.ActivationTime"/>, which
        /// is exactly when the line would have gone active while pinned.</para>
        ///
        /// <para>THE TWO PARKED STATES ARE NOT THE SAME FACT (backlog 222 correcting backlog 218,
        /// whose note here said they were, and that claim is what hid the defect below). A caret the
        /// rush bound left sitting past the last cell of its OWN line still needs nothing from this
        /// method: the caller accrues only while the active line is INCOMPLETE, that caret's line is
        /// complete by definition, and there is genuinely nothing the player can type. But a caret
        /// that <see cref="rollForwardIfFinishedEarly"/> or <see cref="snapForwardOnLineStart"/> has
        /// moved on to the NEXT line sits there from <see cref="entryOpensAt"/>, which is
        /// <see cref="FLETCHER_DRAG_GRACE_MS"/> BEFORE that line's activation, and
        /// <see cref="ProcessKey"/> has no time gate of its own: the player really can type there, up
        /// to 1500 ms per line (unboundedly, before 218 bounded the roll). Every character they land
        /// is counted by <see cref="countCorrectCells"/> for the rest of the run, so counting them
        /// against a stopped clock walked <see cref="LiveWpm"/> and <see cref="LiveRollingWpm"/>
        /// upward for free.</para>
        ///
        /// <para>THE LAZY ARM. So the clock arms on the FIRST press the player puts on such a line
        /// (<see cref="armWpmClockAheadOfTheCue"/>) and runs from that press's own time onward. Not
        /// from <see cref="entryOpensAt"/>: that would hand the whole head start back as typing time
        /// for a player who is sitting there NOT typing, which is the dilution the ActivationTime gate
        /// exists to prevent and which <c>ActiveTimeDoesNotRunWhileParkedAheadOfTheCue</c> pins. And
        /// nothing is back-dated: the arming press is credited no elapsed time at all, exactly like a
        /// press made at ActivationTime + 0 on the ordinary path. Once the playhead reaches the line,
        /// the ActivationTime branch above answers first and the arm is never consulted again.</para>
        /// </summary>
        private double? clockRunsFrom(double previousTime)
        {
            if (!FletcherEnabled || activeLineIndex == -1)
                return previousTime;

            if (previousTime >= lines[activeLineIndex].ActivationTime)
                return previousTime;

            // Ahead of the cue: the clock runs only from an arm, and only from an arm belonging to
            // the line the caret is on now (see wpmClockArmedLine for why that check is the reset).
            if (wpmClockArmedLine == activeLineIndex)
                return Math.Max(previousTime, wpmClockArmedAt);

            return null;
        }

        /// <summary>
        /// Arm the WPM clock on the active line when the press at <paramref name="time"/> is the first
        /// one the player has put on it AHEAD OF ITS CUE (see <see cref="clockRunsFrom"/>). No-op on
        /// every ordinary press, i.e. one made at or after the line's
        /// <see cref="TypingLine.ActivationTime"/>, where the clock is already running.
        ///
        /// <para>Called from <see cref="ProcessKey"/> once the press is known not to be inert, so
        /// every press that can resolve or spoil a cell arms the clock, a Gatekeeper-rejected wrong
        /// key included: what the arm records is that the player is TYPING here, not what the
        /// keystroke turned out to be worth. A press the caret-parked guard refuses outright never
        /// reaches it, which is right, since that press does nothing at all.</para>
        /// </summary>
        private void armWpmClockAheadOfTheCue(double time)
        {
            if (!FletcherEnabled || activeLineIndex == -1)
                return;

            if (time >= lines[activeLineIndex].ActivationTime)
                return;

            // Only the FIRST press arms. A later one must not push the arm forward, or the time
            // between the two presses (the player typing) would be swallowed.
            if (wpmClockArmedLine == activeLineIndex)
                return;

            wpmClockArmedLine = activeLineIndex;
            wpmClockArmedAt = time;
        }

        /// <summary>
        /// The usable magnitude of a clock rate for the WPM divisor. A rewinding clock reports a
        /// NEGATIVE rate, but rewind is a direction and not a speed, and dt is already clamped >= 0, so
        /// the sign is dropped rather than allowed to run active time backwards. A stopped (0) or
        /// non-finite rate carries no speed information at all, so it falls back to 1x instead of
        /// poisoning the accumulator with an infinity or a NaN that every later readout would inherit.
        /// </summary>
        private static double sanitisedRate(double rate)
        {
            double magnitude = Math.Abs(rate);

            return double.IsFinite(magnitude) && magnitude > 0 ? magnitude : 1;
        }

        public ResultsSummary BuildResults()
        {
            // SyncPercent over every TIMED cell: finally-Correct cells contribute SyncQuality(final
            // correct delta); everything else (Missed / Wrong / unresolved) is 0. SPACE cells are
            // out of both the sum and the divisor since backlog 148, for the reason on
            // LiveSyncPercent: their delta is zeroed by rule, so leaving them in would pay a full
            // quality per word gap for nothing and inflate the readout (a map runs roughly one space
            // in six typeable cells, which used to be enough to carry a 90 to an S back when this
            // figure gated the letter grade; backlog 251 cut that tie, and the exclusion outlives it
            // because a readout nobody is graded on still has to be true).
            double qualitySum = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (isTimed(cell) && cell.State == CellState.Correct && cell.JudgedDelta is double d)
                        qualitySum += WindowsFor(cell).SyncQuality(d);
                }
            }

            double syncPercent = totalTimedCells == 0 ? 100 : 100 * qualitySum / totalTimedCells;

            double wpm = activeRealTimeMs <= 0 ? 0 : (wpmInputCount() / 5.0) / (activeRealTimeMs / 60000.0);

            return new ResultsSummary
            {
                Accuracy = LiveAccuracy,
                Wpm = wpm,
                SyncPercent = syncPercent,
                MaxCombo = maxCombo,
                Counts = new Dictionary<JudgementType, int>(counts),
                SyncTimeline = syncTimeline.ToArray(),
                Artist = Beatmap.Metadata.Artist,
                Title = Beatmap.Metadata.Title,
            };
        }

        private int wpmInputCount() => UsesJapaneseKeystrokeWpm ? japaneseWpmInputs : countCorrectCells();

        /// <summary>Correct cells (including spaces) across all lines: the legacy WPM numerator.</summary>
        private int countCorrectCells()
        {
            int count = 0;

            foreach (var line in lines)
            {
                foreach (var cell in line.Cells)
                {
                    if (cell.State == CellState.Correct)
                        count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Record one correct keypress at the current active REAL time for <see cref="LiveRollingWpm"/>.
        /// Nothing reads this back into judgement, so it can never move a score. <see cref="ProcessBackspace"/>
        /// deliberately does NOT pop: the buffer is what the player typed, not what the cells currently
        /// hold, so backspacing and retyping simply logs another (later) press.
        /// </summary>
        private void pushRollingSample()
        {
            rollingSamples[rollingNext] = activeRealTimeMs;
            rollingNext = (rollingNext + 1) % rolling_wpm_window;

            if (rollingCount < rolling_wpm_window)
                rollingCount++;
        }
    }
}
