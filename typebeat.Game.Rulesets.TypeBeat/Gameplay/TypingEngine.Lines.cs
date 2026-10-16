// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, LINE ENTRY, SEALING AND HAND-OVER: when a line opens, when it may seal, how a
// finished caret moves on (by time, by keypress, or by the player's own newline) and the line skip.

using System;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>
        /// Whether the PLAYHEAD is inside a typeable line window: the plain time rule
        /// (ActivationTime &lt;= now &lt; EndTime + SealGraceMs on the first unsealed line), read
        /// independently of where the player's caret has got to. Equal to <see cref="LineIsActive"/>
        /// without <see cref="FletcherEnabled"/>; under Fletcher the two diverge, because the caret
        /// can be parked on a line the song has not reached (rush) or still finishing one the song
        /// has left (drag). The key handler uses it so Space still reaches the skip overlay during a
        /// real instrumental gap.
        /// </summary>
        public bool SongWindowOpen
        {
            get
            {
                if (isFinished || nextSealIndex >= lines.Count || lastUpdateTime is not double time)
                    return false;

                var line = lines[nextSealIndex];

                return time >= line.ActivationTime && time < EffectiveSealTime(line);
            }
        }

        /// <summary>
        /// Whether the map's FIRST line is inside the head start <see cref="FIRST_LINE_LEAD_MS"/> gives
        /// it, at <paramref name="time"/>: the window in which a press may open a line the clock has not
        /// activated yet (see <see cref="ProcessKey"/>).
        ///
        /// <para>The line's own <see cref="TypingLine.ActivationTime"/> is NOT moved by any of this,
        /// because the WPM clock is armed from it and a stored run's WPM must re-derive exactly as it
        /// was played.</para>
        ///
        /// <para>Always false unless <see cref="FirstLineLeadIn"/> is set, which is the era this head
        /// start belongs to.</para>
        /// </summary>
        public bool FirstLineTypingOpensAt(double time)
            => FirstLineLeadIn && !isFinished && activeLineIndex == -1 && firstLineTypingWindowOpen(time);

        private bool firstLineTypingWindowOpen(double time)
            => nextSealIndex == 0
               && lines.Count > 0
               && time >= Math.Min(lines[0].ActivationTime, lines[0].FirstVocalTime - FIRST_LINE_LEAD_MS)
               && time < EffectiveSealTime(lines[0]);

        /// <summary>
        /// Whether the SONG is asking for characters on the very line the player's caret is on:
        /// the playhead is inside a typeable window AND that window belongs to the caret's line.
        /// Equal to <see cref="SongWindowOpen"/> with a pinned caret, where the two are always the
        /// same line, and the pair the key handler needs once the caret is unpinned.
        ///
        /// <para><see cref="SongWindowOpen"/> alone is not that question, and the difference is the
        /// whole of a real map's instrumental gap: a decoder-built line's window runs to the NEXT
        /// line's start (contiguous, no holes), so through a twelve-second instrumental the playhead
        /// is still inside line N's window and <see cref="SongWindowOpen"/> stays true, while the
        /// player who finished line N is parked at the head of line N+1 with nothing being asked of
        /// them. That is exactly when Space has to reach the mid-song skip overlay instead of being
        /// eaten as a keystroke (see <c>TypeBeatPlayfield</c>'s key handler).</para>
        /// </summary>
        public bool SongIsOnTheCaretsLine => activeLineIndex != -1 && activeLineIndex == nextSealIndex && SongWindowOpen;

        /// <summary>
        /// True while the player has put nothing into the active line yet (no cell behind the caret
        /// is Correct or Wrong and no romaji is pending; leading auto-skipped punctuation does not
        /// count as progress). Used
        /// by the key handler under <see cref="FletcherEnabled"/> to tell "parked on a line I have
        /// not started" from "typing it".
        /// </summary>
        public bool ActiveLineUntouched
        {
            get
            {
                if (activeLineIndex == -1 || JapanesePending.Length > 0)
                    return false;

                var cells = lines[activeLineIndex].Cells;
                int end = Math.Min(caretIndex, cells.Count);

                for (int i = 0; i < end; i++)
                {
                    if (cells[i].State == CellState.Correct || cells[i].State == CellState.Wrong)
                        return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Whether the caret is WAITING ON A LINE IT MAY NOT TYPE YET: the player has handed
        /// themselves on to the next line (see <see cref="ManualNewlines"/>), or the song has, and
        /// that line's entry window (<see cref="entryPermitted"/>) has not opened. The line's
        /// characters render greyed while this is true, keypresses are swallowed by
        /// <see cref="ProcessKey"/> and <see cref="ProcessEnter"/>, and a
        /// <see cref="ProcessBackspace"/> from the head of the line steps back up to the one it came
        /// from while that line can still be typed.
        ///
        /// <para>This is what makes an early newline a WAIT rather than a mistake: the setting no
        /// longer refuses a press made before the next line's window (see
        /// <see cref="rollForwardManually"/>), so the refusal had to move from the hand-over to the
        /// typing. False in every other era, and false while no line is active, so the pinned and
        /// rush arms keep exactly the input they had.</para>
        /// </summary>
        public bool AwaitingEntry => awaitingEntry(lastUpdateTime ?? double.NegativeInfinity);

        /// <summary>
        /// Walks the caret off every character of the active line that was already DUE before
        /// <see cref="PlayStartTime"/>: this play began among them, so they are not the player's to
        /// type. Nothing is judged here - the cells are left exactly as they are, and the seam that
        /// resolves them (the line's seal) grants them for the same reason this walk does, from the same
        /// time. The walk stops at the first character still to come, which is where the mapper pointed
        /// the playhead and therefore where their play starts.
        /// </summary>
        private void alignCaretToThePlayStart()
        {
            if (activeLineIndex == -1 || PlayStartTime is not double start)
                return;

            var cells = lines[activeLineIndex].Cells;

            while (caretIndex < cells.Count && cells[caretIndex].IsTypeable && cells[caretIndex].TargetTime < start)
                caretIndex++;

            // Same landing as every other caret move: a caret that came to rest on nothing typeable is
            // walked on, so the player's first key lands on a real character.
            autoSkipForward();
        }

        /// <summary>
        /// FLEXIBLE-LINES SNAP (backlog 208, see <see cref="FlexibleLineSnap"/>): while the caret
        /// sits PAST THE LAST CHARACTER of its line, the next line STARTING takes it, which is what
        /// keeps the flexible default feeling like the pinned game it replaced (finish your line and
        /// the song moves you on). A line the player has not finished is never touched: dragging
        /// behind is precisely the freedom the flexible caret grants, and
        /// <see cref="sealPermitted"/> makes the same distinction for the same reason ("nothing left
        /// untyped means there is no drag to protect").
        ///
        /// <para>"Finished" is <see cref="IsLineComplete"/>, i.e. the caret has walked off the end
        /// of the cell list. That is exact rather than approximate: every caret advance runs
        /// <see cref="autoSkipForward"/>, so a caret at <c>Cells.Count</c> is a caret with no
        /// typeable cell left in front of it, and it is the same predicate the keypress-driven
        /// <see cref="rollForwardIfFinishedEarly"/> gates on. Cells left BEHIND the caret wrong or
        /// abandoned do not hold the line: the player is done with them, and the seal resolves them
        /// exactly as it always did.</para>
        ///
        /// <para>A LOOP rather than a single step, because the line it lands on can be finished the
        /// instant it is reached (a line whose cells are all non-typeable is complete at caret 0),
        /// and the roll-forward this backs up does not recurse. Returns whether the caret moved, so
        /// the caller announces one <c>LineActivated</c> however many lines were crossed.</para>
        ///
        /// <para>Since backlog 218 this is also the DEFERRED ROLL arm (see <see cref="BoundedRush"/>),
        /// which is why the two are one method rather than two: both move a FINISHED caret onto the
        /// next line on a TIME condition, they would fire on the same frame, and a second arm could
        /// only ever raise a second <c>LineActivated</c> for a caret the first one had already moved.
        /// The instant they fire at is <see cref="entryOpensAt"/>, the line's activation under the
        /// unbounded era and <see cref="FLETCHER_DRAG_GRACE_MS"/> before it under the bounded one, so
        /// the head start the bound grants a rushing player still exists: refusing the keypress roll
        /// and then waiting for the full activation would take with one hand what the mirror gives
        /// with the other.</para>
        /// </summary>
        private bool snapForwardOnLineStart(double time)
        {
            // The gate: an unpinned caret under either switch, a run still going, and not under
            // ManualNewlines (see LineLifecycle.TimeMayMoveAFinishedCaret).
            //
            // MANUAL NEWLINES: a finished caret is the PLAYER's to hand over, so this arm does
            // nothing for them - and NEITHER does anything earlier. What hands them on is the seal
            // itself, the instant the engine takes the line away (<see cref="sealPermitted"/>), and a
            // FINISHED line is held to the one instant that matters rather than left to seal on its
            // deadline (see <see cref="manualNewlineHoldsLineOpen"/>): the moment the PUSH WARNING's
            // red bar has been counting down to. "The time you would be forced on with the setting
            // off" is the line being taken, not the vocals running out, the next line's cue arriving,
            // or the line's own deadline - which is the next line's first word in any ordinary map.
            // The seal loop's own hand-over does it, so a manual caret waits through all of those and
            // moves exactly when a pinned one would.
            if (!LineLifecycle.TimeMayMoveAFinishedCaret(lifecycleRules, isFinished))
                return false;

            bool snapped = false;

            while (IsLineComplete
                   && activeLineIndex + 1 < lines.Count
                   && time >= entryOpensAt(activeLineIndex + 1))
            {
                activeLineIndex++;
                caretIndex = 0;
                autoSkipForward();
                snapped = true;
            }

            return snapped;
        }

        /// <summary>DRAG FREEDOM: may line <paramref name="index"/> seal at <paramref name="time"/> (see <see cref="LineLifecycle.SealPermitted"/>)?</summary>
        private bool sealPermitted(int index, double time)
            => LineLifecycle.SealPermitted(lifecycleRules, lines, index, time, lifecycleCaret, lineAbandoned[index], effectiveSealTimeOf);

        /// <summary>Whether <see cref="ManualNewlines"/> is holding <paramref name="index"/> open (see <see cref="LineLifecycle.ManualNewlineHoldsLineOpen"/>).</summary>
        private bool manualNewlineHoldsLineOpen(int index) => LineLifecycle.ManualNewlineHoldsLineOpen(lifecycleRules, lines, index, lifecycleCaret);

        /// <summary>THE RUSH BOUND: may a finished caret move on to line <paramref name="index"/> yet (see <see cref="LineLifecycle.EntryPermitted"/>)?</summary>
        private bool entryPermitted(int index, double time) => LineLifecycle.EntryPermitted(lines[index], time, BoundedRush);

        /// <summary>Whether the caret is on a line it may not type on yet (see <see cref="LineLifecycle.AwaitingEntry"/>).</summary>
        private bool awaitingEntry(double time) => LineLifecycle.AwaitingEntry(lifecycleRules, lines, activeLineIndex, time);

        /// <summary>The earliest instant the caret may rush onto line <paramref name="index"/> (see <see cref="LineLifecycle.EntryOpensAt"/>).</summary>
        private double entryOpensAt(int index) => LineLifecycle.EntryOpensAt(lines[index], BoundedRush);

        /// <summary>The DEADLINE half of a seal (see <see cref="LineLifecycle.CanSeal"/>).</summary>
        private bool canSeal(TypingLine line, double time) => LineLifecycle.CanSeal(line, time, effectiveSealTimeOf);

        /// <summary>EARLY FINISH: the final line is typed out (see <see cref="LineLifecycle.FinishedEarly"/>).</summary>
        private bool finishedEarly(int index) => LineLifecycle.FinishedEarly(lines, index, EarlyFinish);

        /// <summary>Whether the line still owes a typeable cell (see <see cref="LineLifecycle.HasUntypedTypeable"/>).</summary>
        private static bool hasUntypedTypeable(TypingLine line) => LineLifecycle.HasUntypedTypeable(line);

        /// <summary>The caret switches <see cref="LineLifecycle"/> decides under, as this engine is set now.</summary>
        private LineLifecycle.Rules lifecycleRules => new LineLifecycle.Rules(FletcherEnabled, BoundedRush, FlexibleLineSnap, ManualNewlines, EarlyFinish);

        /// <summary>Where the caret stands, for <see cref="LineLifecycle"/>.</summary>
        private LineLifecycle.Caret lifecycleCaret => new LineLifecycle.Caret(activeLineIndex, caretIndex);

        private Func<TypingLine, double>? effectiveSealTime;

        /// <summary><see cref="EffectiveSealTime"/> as the delegate <see cref="LineLifecycle"/> reads a deadline through, built once.</summary>
        private Func<TypingLine, double> effectiveSealTimeOf => effectiveSealTime ??= EffectiveSealTime;

        /// <summary>
        /// LINE SKIP (backlog 241): give up the rest of the active line and move on. Deterministic in
        /// (input, time) exactly like <see cref="ProcessKey"/>, and returns whether it did anything,
        /// so the caller records a frame only for an effective press. UNPINNED ONLY
        /// (<see cref="FletcherEnabled"/>): the press is inert with the caret pinned, because the
        /// "move on" half of it is the whole of what it is for and a pinned caret has nowhere to be
        /// moved to - see the guard at the head of <see cref="ProcessEnter"/>.
        ///
        /// <para>IT IS CARET MOVEMENT AND NOTHING ELSE. The caret parks past the line's last cell,
        /// which is the SAME parked state a <see cref="BoundedRush"/>-refused roll leaves behind
        /// (see <see cref="rollForwardIfFinishedEarly"/>), and from there the machinery that already
        /// exists carries the player onward: the roll below hands them the next line at once when its
        /// entry window is open, and <see cref="snapForwardOnLineStart"/> performs the deferred hand
        /// over when it opens otherwise. The cells left behind are NOT judged here. They stay
        /// <see cref="CellState.Untyped"/> and become misses in the seal loop, all at once, with the
        /// line's one combo break, at the line's own deadline: precisely what would have happened to
        /// a player who stopped typing and sat there. That break is still taken AT the seal since
        /// backlog 259, and only its REACH moved: back-dated to the last of those cells, it destroys
        /// what the player had earned by then and leaves what they have built on the line they
        /// skipped to, which is the same "precisely what would have happened" read one step more
        /// carefully. So no judged quantity changes value or timing
        /// against the un-skipped run, which is why the skip carries NO era bit of its own (the
        /// abandoned line's drag grace is held for it by <see cref="lineAbandoned"/>, which is the
        /// one piece of state that claim depends on).</para>
        ///
        /// <para>Deliberately NOT the word skip's shape (<see cref="skipCurrentWord"/>): that one
        /// marks cells <see cref="CellState.Abandoned"/>, takes an immediate combo break and
        /// snapshots a redeemable claim, all of which move the account at press time and would make
        /// this an era change. A line skip gives up more and costs nothing extra for it, because the
        /// player pays the same misses either way, just without having to sit through them.</para>
        ///
        /// <para>NO-OP when there is nothing to skip: no active line, the run finished, or the caret
        /// already past the last cell (a line typed out, or one already skipped). In particular Enter
        /// on a COMPLETE line does NOT perform the roll the next keypress would UNDER THE DEFAULT
        /// hand-over: the two time-driven arms already own that caret, so a second way in could only
        /// duplicate them, and the key handler lets the press fall through to its global binding in
        /// that state rather than swallowing it for nothing. Under <see cref="ManualNewlines"/> there
        /// is no time-driven arm left to duplicate and Enter IS the newline, so on a complete line it
        /// does the same hand-over a space does (see <see cref="rollForwardManually"/>).</para>
        ///
        /// <para>The WPM clock needs nothing here and is deliberately NOT armed: an Enter is not
        /// typing. Accrual stops by itself the moment the caret parks (<see cref="Update"/> accrues
        /// only while the active line is INCOMPLETE, and a parked caret's line reads complete), and
        /// on the line the player lands on <see cref="clockRunsFrom"/> answers exactly as it does
        /// after a refused rush: stopped until the first press there arms it
        /// (<see cref="armWpmClockAheadOfTheCue"/>), because a stale arm belongs to a line index the
        /// caret has left.</para>
        /// </summary>
        public bool ProcessEnter(double time)
        {
            if (JapaneseRomajiInput && UsesJapaneseRomaji && JapanesePending.Length > 0)
            {
                int originalLine = activeLineIndex;
                if (!tryCommitJapanesePending())
                    return false;
                // An IME-style confirmation never abandons the rest of this line, or the next
                // line reached by automatic handoff. A complete manual line can still advance.
                if (activeLineIndex == originalLine && IsLineComplete)
                    processEnter(time);
                return true;
            }
            return processEnter(time);
        }

        private bool processEnter(double time)
        {
            if (isFinished || activeLineIndex == -1)
                return false;

            // PINNED CARET (the Fletcher mod): Enter is not a gameplay key at all, so the press falls
            // through to whatever the client binds it to. The skip below is CARET MOVEMENT, and it is
            // worth something only because the unpinned caret may carry the player on early: with the
            // caret pinned to the song there is nothing to move them to, and the press would give the
            // rest of the line up for NOTHING - the misses land at the seal, the caret stays parked on
            // a line it can no longer type on, and the next line arrives on the song's own time either
            // way. Inert here is the same answer the complete-line arm already gives a pinned caret
            // under the default hand-over (see rollForwardManually), so Enter never does anything at
            // all under this caret.
            if (!FletcherEnabled)
                return false;

            // A line handed to the player before its window opens waits rather than judging: the
            // press is swallowed, exactly as a keypress on it is (see AwaitingEntry).
            if (awaitingEntry(time))
                return false;

            var line = lines[activeLineIndex];

            // Hop auto-skip cells before measuring, exactly as ProcessKey does, so "the caret is at
            // the end" is asked of the same frontier a keypress would see.
            autoSkipForward();

            if (caretIndex >= line.Cells.Count)
            {
                // Nothing left to give up: parked already, or the line is fully typed. That second
                // state is the manual newline's own, so Enter closes it here (and reports the move so
                // the caller records the frame); under the default hand-over it stays inert.
                return rollForwardManually(time);
            }

            // Only a line with something still untyped is ABANDONED. A caret walked to the end over
            // nothing but wrong cells owes no misses, so the seal has no drag to protect and the flag
            // would only hold the line open for cells that are already resolved.
            if (hasUntypedTypeable(line))
                lineAbandoned[activeLineIndex] = true;

            caretIndex = line.Cells.Count;

            // The same call the last character of a line makes, so an Enter inside the next line's
            // entry window rolls on at once and one outside it parks, with no second rule. Under
            // ManualNewlines that roll is the PLAYER'S own, so the skip hands the line over the way
            // the newline key does: one Enter still means "I am done with this line, move me on",
            // and the entry window decides whether it lands now or parks (see rollForwardManually).
            if (!rollForwardManually(time))
                rollForwardIfFinishedEarly(time);

            return true;
        }

        /// <summary>
        /// THE MANUAL NEWLINE (see <see cref="ManualNewlines"/>): the player's own space-on-a-finished-
        /// line or Enter, which is the ONLY thing that hands a parked caret on while the setting is
        /// armed. Returns whether the caret moved, so a caller records a frame only for an effective
        /// press and lets a refused one fall through to whatever it would have done anyway.
        ///
        /// <para>Two conditions, and deliberately NOT the automatic roll's third: the caret must be
        /// FINISHED (<c>caretIndex</c> past the last cell) and there must be a next line. The entry
        /// window (<see cref="entryPermitted"/>, within <see cref="FLETCHER_DRAG_GRACE_MS"/> of the
        /// next cue) does not gate the press itself: the newline always lands, and the line it lands
        /// on sits greyed and untypeable (<see cref="AwaitingEntry"/>) until its window opens, so
        /// "the timing constraints about when you may move on still apply" to the TYPING rather
        /// than to the press. A refused press read as the newline not working. If the player never
        /// presses at all, the seal's drag cutoff takes them as it always did.</para>
        ///
        /// <para>No WPM clock work here, for the reason <see cref="ProcessEnter"/> gives: a newline is
        /// not typing, so the clock on the line being LANDED on arms lazily on its first real press
        /// (<see cref="armWpmClockAheadOfTheCue"/>) exactly as it does after a refused rush.</para>
        /// </summary>
        private bool rollForwardManually(double time)
        {
            if (!ManualNewlines || !FletcherEnabled || isFinished || activeLineIndex == -1)
                return false;

            if (caretIndex < lines[activeLineIndex].Cells.Count)
                return false;

            if (activeLineIndex + 1 >= lines.Count)
                return false;

            // NO WINDOW GATE: the press always lands, and the line it lands on may sit greyed and
            // untypeable until its entry window opens (AwaitingEntry). Refusing the press instead
            // made the player press again at the right moment, which read as the newline not
            // working; the window now refuses the TYPING, which is what it is actually about.
            activeLineIndex++;
            caretIndex = 0;
            autoSkipForward();
            raise(LineActivated, activeLineIndex);
            return true;
        }

        /// <summary>
        /// RUSH FREEDOM (see <see cref="FletcherEnabled"/>): the moment a press finishes a line, the
        /// caret moves straight on to the next one instead of waiting for its activation cue. It is
        /// the KEYPRESS half of moving a finished caret on; the time-driven half, for a caret that
        /// became finished without a press of its own, is <see cref="snapForwardOnLineStart"/>.
        /// The finished line is left UNSEALED
        /// and seals on its own normal deadline (with nothing missed, since it is fully typed), so
        /// nothing about the song's timeline moves; only the player's position does. No-op on the last
        /// line, which keeps the default "line complete, wait for the song" behaviour that lets the
        /// key handler pass Space through to the skip overlay.
        ///
        /// <para>BOUNDED since backlog 218 (see <see cref="BoundedRush"/>): "the moment a press
        /// finishes a line" is now "the moment a press finishes a line, if that next line is within
        /// <see cref="FLETCHER_DRAG_GRACE_MS"/> of starting". Refused, the caret parks past the last
        /// cell and <see cref="snapForwardOnLineStart"/> makes the move for it when the bound opens.
        /// <paramref name="time"/> is the keypress's own time, the same value the press was judged
        /// on, so the bound is a pure function of (char, time) like everything else here and a replay
        /// reproduces it exactly.</para>
        /// </summary>
        private void rollForwardIfFinishedEarly(double time)
        {
            if (!FletcherEnabled || isFinished || activeLineIndex == -1)
                return;

            // MANUAL NEWLINES: the press that finished the line does NOT hand the caret on. The
            // caret parks past the last cell and waits for the player's own newline
            // (rollForwardManually), or for the seal to force it, which is the same parked state a
            // refused rush leaves and therefore the same state every arm downstream already
            // understands.
            if (ManualNewlines)
                return;

            if (caretIndex < lines[activeLineIndex].Cells.Count)
                return;

            if (activeLineIndex + 1 >= lines.Count)
                return;

            if (!entryPermitted(activeLineIndex + 1, time))
                return;

            // Lines seal in order and the player never leaves a line except by finishing it or by a
            // drag cutoff (which advances nextSealIndex with them), so the next line is always unsealed.
            activeLineIndex++;
            caretIndex = 0;
            autoSkipForward();
            raise(LineActivated, activeLineIndex);
        }

        /// <summary>
        /// THE PUSH (backlog 263), read out for display only: the instant the caret's own line will be
        /// force-sealed out from under it and the caret landed on the next line, or null when no such
        /// push is coming. Nothing here decides anything, it only reports the deadline
        /// <see cref="sealPermitted"/> already compares against, so the stage can warn the player
        /// before it arrives.
        ///
        /// <para>Non-null under exactly the three conditions the drag cutoff needs. The caret must be
        /// UNPINNED (<see cref="FletcherEnabled"/>), or the line was never held open for the player in
        /// the first place. The caret's line must also be the next line due to seal, or the seal loop
        /// reaches it with the caret elsewhere and the cutoff's hand-over arm does not run: a line the
        /// player walked out of with a line skip is still held open by <see cref="lineAbandoned"/>,
        /// but nobody is standing on it to be pushed. And the line must still owe a character
        /// (<see cref="hasUntypedTypeable"/>, the same scan the seal asks), because a line with
        /// nothing left untyped seals on its ordinary deadline with no drag to protect and no
        /// punishment to warn about: typing the last cell out cancels the push there and then.</para>
        ///
        /// <para>ONE EXCEPTION, and it warns about a push that really is coming: under
        /// <see cref="ManualNewlines"/> a line the player has typed out but not closed is held to its
        /// own cutoff (<see cref="manualNewlineHoldsLineOpen"/>), so the bar keeps counting down to
        /// the instant that line is taken from them - the setting's whole point being that a press is
        /// what moves the caret on, with the cutoff as the backstop. Everywhere else the three
        /// conditions above stand unchanged.</para>
        ///
        /// <para>The value is <see cref="TypingLine.EndTime"/> + <see cref="TypingLine.SealGraceMs"/> +
        /// <see cref="FLETCHER_DRAG_GRACE_MS"/>, exactly what <see cref="sealPermitted"/> tests, so the
        /// warning can never disagree with the moment it warns about.</para>
        /// </summary>
        public double? DragCutoffAt
        {
            get
            {
                if (isFinished || !FletcherEnabled || activeLineIndex == -1 || activeLineIndex != nextSealIndex)
                    return null;

                var line = lines[activeLineIndex];

                if (!hasUntypedTypeable(line) && !manualNewlineHoldsLineOpen(activeLineIndex))
                    return null;

                return EffectiveSealTime(line) + FLETCHER_DRAG_GRACE_MS;
            }
        }
    }
}
