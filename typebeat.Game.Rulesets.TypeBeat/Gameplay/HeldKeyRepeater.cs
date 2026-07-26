// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osuTK.Input;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// SONG-PACED HELD-KEY REPEAT. Holding a character key down re-fires that character at the
    /// cadence the SONG sings the line at, which is exactly the cadence
    /// <see cref="Replays.TypeBeatAutoGenerator"/> plays at: one press per upcoming typeable cell,
    /// scheduled on that cell's <see cref="TypingCell.TargetTime"/> (carrying the player's own drift,
    /// see PACING below). Runs of the same letter ("aaaaaaaa") can therefore be sustained instead of
    /// hammered, in time with the vocal.
    ///
    /// <para>This is an INPUT-LAYER device only. Every repeat is synthesized as an ordinary
    /// <see cref="TypingEngine.ProcessKey"/> call preceded by <see cref="TypingEngine.Update"/> at
    /// the same timestamp, which is byte-for-byte the sequence the live key handler makes, and every
    /// effective repeat is handed to the replay recorder like a real keystroke. The judged model
    /// (and its JS mirror) is untouched, and a replay of a run with held repeats plays back
    /// bit-exactly through the ordinary frame feeder.</para>
    ///
    /// <para>NO BOUNDARY DETECTION. The schedule is made of cell TIMES; nothing checks whether the
    /// cell a repeat will land on actually expects the held character. Holding 'a' through
    /// "aaaaaaaaaah" fires an 'a' at the 'h' cell's target time and takes the ordinary punishment
    /// (rejection plus combo break in strict play, a red Wrong fill under allow-wrong-input).</para>
    ///
    /// <para>PACING IS THE SONG'S, NOT THE PLAYER'S. Repeat times are the song's cell targets carrying
    /// the player's own DRIFT (the lag of the press that armed the hold, see
    /// <see cref="BeginHold"/>): the cadence between repeats is exactly the song's, and a player who
    /// was already lagging keeps lagging by the same amount, their presses landing on the cells they
    /// are behind on and judged honestly late. The repeat never catches the player up and never
    /// overtakes them: it cannot fire at or before the press that armed it.</para>
    ///
    /// <para>NO ENGAGE DELAY. There used to be an OS-autorepeat-style pause before the first repeat
    /// could fire (250ms, the shortest Windows offers). Playtesting (backlog task 39) called that
    /// pause out as feeling wrong for a rhythm game: the hold should flow straight into the song's
    /// cadence the instant the key goes down, however soon the next cell target lands. The schedule
    /// now fires every upcoming cell at its own target time starting immediately after the initial
    /// press, with no minimum hold duration and no clamping.</para>
    ///
    /// <para>TRADEOFF: releasing the key is now the ONLY thing that stops a repeat from firing.
    /// Ordinary typing dwells on a key for roughly 60-100ms, so on a densely timed line (cell targets
    /// closer together than that dwell) a normally-typed key that is still physically down when the
    /// next cell's target arrives WILL fire a repeat, double-hitting that cell for a player who does
    /// not release crisply. This is accepted behavior: the song's own pacing is the intended feel,
    /// and the old engage delay traded that feel away to buy safety this design no longer wants. It
    /// is bounded by the SONG'S cadence and nothing else, so it bites a lagging player exactly as
    /// hard as a player in time, never harder.</para>
    /// </summary>
    public sealed class HeldKeyRepeater
    {
        /// <summary>
        /// Largest clock advance between two pumps that a hold survives. A pause/resume, a skip
        /// seek or a long stall would otherwise leave a backlog of due repeats and dump them into
        /// the engine in one burst; the hold is simply dropped instead and the player re-presses.
        /// </summary>
        public const double MAX_ADVANCE_MS = 250;

        private readonly TypingEngine engine;
        private readonly Action<char, double>? recordInput;

        /// <summary>Absolute times the pending repeats fire at, ascending. Built once per hold.</summary>
        private readonly List<double> schedule = new List<double>();

        private int scheduleIndex;

        private bool holding;
        private Key heldKey;
        private char heldChar;
        private int heldLineIndex;
        private double lastPumpTime;

        /// <param name="engine">The judged model. Repeats go through its ordinary public entry points.</param>
        /// <param name="recordInput">Replay recording sink for effective repeats; null when not recording.</param>
        public HeldKeyRepeater(TypingEngine engine, Action<char, double>? recordInput = null)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.recordInput = recordInput;
        }

        /// <summary>Whether a character key is currently held with repeats still to come.</summary>
        public bool IsHolding => holding;

        /// <summary>
        /// The character the repeats fire, captured at the initial press: post-layout and
        /// post-Shift, so under the Literate mod a held Shift+A repeats 'A'. Releasing or pressing
        /// Shift MID-HOLD does not change it; the hold reproduces the keystroke that started it.
        /// </summary>
        public char HeldChar => heldChar;

        /// <summary>Repeats still scheduled for the current hold; 0 when not holding.</summary>
        public int PendingRepeats => holding ? schedule.Count - scheduleIndex : 0;

        /// <summary>
        /// Start (or replace) a hold from an initial, already-judged keystroke. Call AFTER the
        /// initial press has been given to the engine, so the caret is where the repeats continue
        /// from. <paramref name="time"/> is the same integral millisecond stamp the keystroke was
        /// judged and recorded at.
        /// </summary>
        public void BeginHold(Key key, char character, double time)
        {
            // A new key always ends the previous hold: rolling from one letter to the next while
            // the first is still physically down must not leave two repeaters running.
            Cancel();

            if (engine.IsFinished || !engine.LineIsActive || engine.IsLineComplete)
                return;

            int lineIndex = engine.ActiveLineIndex;
            var cells = engine.Lines[lineIndex].Cells;

            // The player's own DRIFT, carried by every repeat this hold fires: how far the arming
            // press landed after the target of the cell it was judged against. Shifting the whole
            // schedule by it is what keeps the repeats at the SONG'S cadence measured from where the
            // player actually is. Firing them at raw absolute targets instead (dropping the ones the
            // song had already sung past) decoupled the schedule from the caret: a player a few cells
            // behind got their first repeat at the target of a cell they had not reached, which can
            // fall a millisecond after their press, so an ordinary keystroke's dwell squeezed a
            // duplicate press into a cell expecting a different char, and a correct keystroke was
            // answered with a rejected wrong key. Nothing is caught up here: the lag is carried
            // forward unchanged, the repeats still land on the cells the player is behind on, and
            // they are still judged honestly late.
            double lag = Math.Max(0, time - driftAnchorTarget(cells, engine.CaretIndex));

            for (int i = engine.CaretIndex; i < cells.Count; i++)
            {
                if (!cells[i].IsTypeable)
                    continue;

                // Integral like every other judged keystroke: lossless through .osr encoding, and
                // identical whatever frame rate the repeat happens to be noticed on.
                double target = Math.Round(cells[i].TargetTime + lag);

                // A repeat may never fire at or before the press that armed it: the first one is a
                // whole song cadence away, and only the cells whose own targets coincide with the
                // anchor's (degenerate timing) can land on the wrong side of that.
                if (target <= time)
                    continue;

                // No engage delay, no clamp beyond that: the cell fires at its own drifted target,
                // however soon that is. See the class doc's TRADEOFF note.
                schedule.Add(target);
            }

            if (schedule.Count == 0)
                return;

            holding = true;
            heldKey = key;
            heldChar = character;
            heldLineIndex = lineIndex;
            lastPumpTime = time;
        }

        /// <summary>
        /// The target a hold's drift is measured against: the cell the arming press consumed (the
        /// last typeable cell behind the caret), or the caret's own cell when the press was rejected
        /// and nothing was consumed. Lines with no typeable cell schedule nothing, so the value
        /// returned for them is never used.
        /// </summary>
        private static double driftAnchorTarget(IReadOnlyList<TypingCell> cells, int caretIndex)
        {
            for (int i = Math.Min(caretIndex, cells.Count) - 1; i >= 0; i--)
            {
                if (cells[i].IsTypeable)
                    return cells[i].TargetTime;
            }

            for (int i = Math.Max(caretIndex, 0); i < cells.Count; i++)
            {
                if (cells[i].IsTypeable)
                    return cells[i].TargetTime;
            }

            return 0;
        }

        /// <summary>Key-up. Ends the hold only if it is the held key that was released.</summary>
        public void Release(Key key)
        {
            if (holding && heldKey == key)
                Cancel();
        }

        /// <summary>Drop the hold outright (focus loss, pause, backspace, replay playback).</summary>
        public void Cancel()
        {
            holding = false;
            heldKey = Key.Unknown;
            heldChar = default;
            heldLineIndex = -1;
            schedule.Clear();
            scheduleIndex = 0;
        }

        /// <summary>
        /// Fire every repeat now due, in order. Must be called once per frame BEFORE the frame's own
        /// <see cref="TypingEngine.Update"/> tick, so the engine is never advanced past a repeat's
        /// timestamp and then handed that earlier timestamp (which would re-accrue active time).
        /// Returns how many repeats actually mutated engine state.
        /// </summary>
        public int Pump(double now)
        {
            if (!holding)
                return 0;

            // A clock discontinuity: never dump the backlog it would have produced.
            if (now - lastPumpTime > MAX_ADVANCE_MS)
            {
                Cancel();
                return 0;
            }

            // A backwards seek needs no special handling: the cursor never rewinds, so the schedule
            // simply waits for the clock to come back round.
            lastPumpTime = Math.Max(lastPumpTime, now);

            if (!stillEligible())
            {
                Cancel();
                return 0;
            }

            int fired = 0;

            while (scheduleIndex < schedule.Count && schedule[scheduleIndex] <= now)
            {
                double time = schedule[scheduleIndex++];

                // EXACTLY the live key handler's sequence, so a repeat is indistinguishable from a
                // real keystroke to the engine and to the recorder.
                engine.Update(time);

                if (engine.ProcessKey(heldChar, time))
                {
                    recordInput?.Invoke(heldChar, time);
                    fired++;
                }

                // Finishing the line (or a Fletcher roll-forward on to the next one) ends the hold:
                // a held key must not spill into a line the player has not chosen to start, and the
                // key handler already lets keys fall through once a line is complete.
                if (!stillEligible())
                {
                    Cancel();
                    break;
                }
            }

            if (holding && scheduleIndex >= schedule.Count)
                Cancel();

            return fired;
        }

        /// <summary>The hold is scoped to the line it began on, and to that line being unfinished.</summary>
        private bool stillEligible()
            => !engine.IsFinished && engine.ActiveLineIndex == heldLineIndex && !engine.IsLineComplete;
    }
}
