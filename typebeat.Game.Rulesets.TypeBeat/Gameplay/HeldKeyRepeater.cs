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
    /// scheduled on that cell's <see cref="TypingCell.TargetTime"/>. Runs of the same letter
    /// ("aaaaaaaa") can therefore be sustained instead of hammered, in time with the vocal.
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
    /// <para>PACING IS THE SONG'S, NOT THE PLAYER'S. Repeat times are absolute cell targets, so a
    /// player who was already lagging keeps lagging (their presses land on cells they are behind on)
    /// and a player who rushed ahead keeps rushing. The repeat never catches the player up: cells the
    /// song had already sung past when the hold began are not scheduled at all.</para>
    /// </summary>
    public sealed class HeldKeyRepeater
    {
        /// <summary>
        /// How long a character key must stay down before the first repeat may fire. Normal typing
        /// dwells on a key for roughly 60-100ms (well under 200ms even for slow typists), and this
        /// matches the SHORTEST auto-repeat delay Windows offers, so a discrete keystroke can never
        /// turn into a repeat no matter how densely the line is timed. Repeats whose cell target
        /// falls inside this window are not dropped, they are clamped to the end of it (see
        /// <see cref="BeginHold"/>), so engaging a hold costs at most this much lateness on the one
        /// or two characters it covers instead of missing them outright.
        /// </summary>
        public const double ENGAGE_DELAY_MS = 250;

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

            double engageTime = time + ENGAGE_DELAY_MS;

            for (int i = engine.CaretIndex; i < cells.Count; i++)
            {
                if (!cells[i].IsTypeable)
                    continue;

                // Integral like every other judged keystroke: lossless through .osr encoding, and
                // identical whatever frame rate the repeat happens to be noticed on.
                double target = Math.Round(cells[i].TargetTime);

                // Cells the song had already sung past when the hold began belong to the player's
                // own drift; a hold reproduces what autoplay would do FROM HERE ON, it never types
                // backlog. This is what keeps the repeat rate the song's rate rather than a
                // catch-up burst for anyone who is behind.
                if (target < time)
                    continue;

                schedule.Add(Math.Max(target, engageTime));
            }

            if (schedule.Count == 0)
                return;

            holding = true;
            heldKey = key;
            heldChar = character;
            heldLineIndex = lineIndex;
            lastPumpTime = time;
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
