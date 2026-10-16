// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, BACKSPACE AND THE ERASE GESTURES: the single backspace, the step back into a line
// the caret was moved off, and the pure queries the word-erase and retype-selection gestures are
// composed from.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>
        /// HAND BACK INTO A LINE the player was moved off: the undo for a mid-line Enter that gave the
        /// rest of the line up (see <see cref="ProcessEnter"/>), and just as much the way back to a
        /// line the SONG has handed them on from (see <see cref="ProcessBackspace"/>, which asks for
        /// this on either caret). The caret lands at the LAST CHARACTER THEY ACTUALLY TYPED - just
        /// after the last cell they put something into - and everything that was given up is handed
        /// back LIVE rather than left behind the caret to be missed at the seal.
        ///
        /// <para>THE END OF THE LINE IS THE WRONG PLACE FOR IT, and that is the whole reason this is
        /// not the plain <c>caretIndex = Cells.Count</c> it used to be: parked past the last cell the
        /// line reads COMPLETE (<see cref="IsLineComplete"/>), keypresses there are inert, the
        /// characters the player came back for are unreachable, and the misses the return was meant
        /// to erase are the very ones it guarantees. On the frontier they are all in front of the
        /// caret again, where typing them is what erases them - the same "come back and type it"
        /// account the word skip's reclaim keeps (backlog 167).</para>
        ///
        /// <para>WHAT THE WALK BACK STOPS ON: a cell the player TYPED (correct or wrong) and a cell
        /// already resolved as <see cref="CellState.Missed"/> both end it, so the caret lands after
        /// the last thing the line has an answer for. A Missed cell can only sit in that tail on a
        /// stored run re-derived under the pre-167 <see cref="WordSkipRule.ImmediateMiss"/> era,
        /// where the skip spent the cell then and there; those are left exactly as they are, because
        /// re-opening one would move a judgement that run is pinned to.</para>
        ///
        /// <para>The line's own abandonment goes with the skip (<see cref="lineAbandoned"/>): the
        /// flag exists to hold the line open, past its deadline, for the misses the player walked
        /// away from, and there is nothing left to hold it open for once they have walked back. An
        /// ABANDONED word caught in the tail is re-opened the way <see cref="ProcessBackspace"/>'s own
        /// walk re-opens one, refund included (<see cref="AbandonReclaimed"/>); its combo still comes
        /// back at the retype, which is what the claim left in place is for.</para>
        /// </summary>
        private void stepBackIntoLine(int index)
        {
            var cells = lines[index].Cells;

            int frontier = 0;

            for (int i = cells.Count - 1; i >= 0; i--)
            {
                if (cells[i].State == CellState.Correct || cells[i].State == CellState.Wrong || cells[i].State == CellState.Missed)
                {
                    frontier = i + 1;
                    break;
                }
            }

            List<int>? reclaimed = null;

            for (int i = frontier; i < cells.Count; i++)
            {
                if (cells[i].State == CellState.Abandoned)
                    (reclaimed ??= new List<int>()).Add(i);

                if (cells[i].State == CellState.Abandoned || cells[i].State == CellState.AutoSkipped)
                    cells[i].State = CellState.Untyped;
            }

            lineAbandoned[index] = false;

            activeLineIndex = index;
            caretIndex = frontier;
            autoSkipForward();

            if (reclaimed != null)
                raise(AbandonReclaimed, new AbandonedCells(index, reclaimed));
        }

        /// <summary>
        /// True when the caret sits on a PARKED typo: the one cell state a backspace clears IN PLACE
        /// (see <see cref="ProcessBackspace"/>), reporting a mutation without moving the caret. Only
        /// the StrictSpaces + <see cref="SpaceSkipsWord"/> arm ever leaves the caret there.
        ///
        /// <para>The playfield's erase runs read this so an in-place clear is not mistaken for the end
        /// of a selection. A retype selection (<see cref="RetypeSelectionAnchor"/>) that ends on a
        /// spoiled word gap clears that gap first; stopping the run there would leave the word the
        /// selection was opened for still standing, and the next letter would land on the just-cleared
        /// gap as a fresh typo - the "select back to my mistake, type, and nothing I typed took" case
        /// a gap holding more than one character produces.</para>
        /// </summary>
        public bool CaretOnParkedTypo
        {
            get
            {
                if (isFinished || activeLineIndex == -1)
                    return false;

                var cells = lines[activeLineIndex].Cells;

                return caretIndex < cells.Count && cells[caretIndex].State == CellState.Wrong;
            }
        }

        /// <summary>
        /// Erase the most recent typed cell within the active line, stepping back transparently
        /// over AutoSkipped punctuation (which is un-skipped so retyping re-marks it) and, since
        /// backlog 167, over the ABANDONED cells of a skipped word (which go back to
        /// <see cref="CellState.Untyped"/> for the same reason: retyping them re-earns them).
        /// Returns false if nothing to erase. The erased keypress stays in the accuracy counts.
        ///
        /// <para>Under <see cref="InputEra2"/> a word skip and the space it typed on the following gap
        /// are undone together (<see cref="tryUndoWordSkip"/>). The abandoned cells are re-opened and
        /// the caret returns to the first one, leaving every correctly typed character in the word
        /// intact. The same rule applies at the end of a line, where a skipped final word has no
        /// following gap.</para>
        ///
        /// <para>In the era before it (every stored run without the flag) both step-overs are
        /// transparent because neither cell holds anything the player put there, so neither is an
        /// erase: one press takes the typed gap, and the next re-enters the skipped word and lands
        /// on the last character actually typed, erasing it, however many characters were given
        /// up.</para>
        ///
        /// <para>The one case that does not erase BEHIND the caret is a typo the caret is parked ON,
        /// which only <see cref="StrictSpaces"/> can produce (backlog 184): that cell is cleared in
        /// place and the caret does not move, because the gap it sits on is still owed its space.</para>
        ///
        /// <para>UNCHANGED by <see cref="AnyOrderWithinWord"/> (backlog 231), deliberately. "The most
        /// recently typed cell" and "the nearest typed cell behind the caret" are the same cell
        /// without that mod and can differ under it, and this stays the NEAREST ONE BEHIND: it is the
        /// character the player is looking at, the caret is still the leftmost cell nobody has typed,
        /// and erasing a cell somewhere off to the right because it happened to be typed last would
        /// be the one place in the game where backspace did not take back what is in front of it. It
        /// is also rarely felt, because every cell behind the frontier caret is Correct or Wrong
        /// whichever order they were typed in.</para>
        /// </summary>
        public bool ProcessBackspace()
        {
            if (tryEraseAPendingReading(out bool consumed))
                return consumed;

            if (tryStepBackUpALine(out consumed))
                return consumed;

            var cells = lines[activeLineIndex].Cells;

            if (tryClearAParkedTypo(cells))
                return true;

            // THE SECOND INPUT ERA's undo of a word skip. Gated so every stored run re-derives on the
            // transparent walk below, which is what its recorded BACKSPACE frames were played on.
            if (InputEra2 && tryUndoWordSkip(cells))
                return true;

            return eraseBehindTheCaret(cells);
        }

        /// <summary>
        /// The first steps of <see cref="ProcessBackspace"/>: a pending romaji reading loses its last letter,
        /// and with no live line the press is inert. Handled when either decided the press.
        /// </summary>
        private bool tryEraseAPendingReading(out bool consumed)
        {
            japaneseCompletionLine = japaneseCompletionCaret = -1;
            if (JapaneseRomajiInput && JapanesePending.Length > 0)
            {
                japanesePending = japanesePending[..^1];
                consumed = true;
                return true;
            }
            clearJapanesePending();
            consumed = false;

            return isFinished || activeLineIndex == -1;
        }

        /// <summary>
        /// The head of a line the player arrived on: step back up to the line they came from while it is
        /// still theirs. Handled whenever the caret is at such a head.
        /// </summary>
        private bool tryStepBackUpALine(out bool consumed)
        {
            // THE HEAD OF A LINE THE PLAYER ARRIVED ON: a backspace there steps BACK UP to the line it
            // came from. That is the undo for a newline pressed EARLY (see
            // <see cref="rollForwardManually"/>), and equally the way back to a line the SONG has just
            // handed them on from, so a typo noticed a beat too late is still the player's to fix. It
            // works for exactly as long as that line is still theirs, whoever moved them: until the
            // engine TAKES it, which is the seal (<see cref="sealPermitted"/>, the instant the push
            // warning's red bar has counted down to) and not the vocals running out or the next cue
            // arriving. Once the line is sealed the press is inert, just as it is on any other line's
            // head, so the step back closes exactly when being forced on with the setting off would
            // have closed it.
            //
            // AND IT LANDS ON WHAT THE PLAYER LAST TYPED rather than at the end of the line (see
            // stepBackIntoLine): the line it comes back to is one the player may have given the rest
            // of up to a mid-line Enter, and the characters that press gave up have to be in front of
            // the caret again for coming back to mean anything.
            if (caretIndex == 0 && activeLineIndex > 0 && FletcherEnabled)
            {
                int previous = activeLineIndex - 1;

                if (previous >= nextSealIndex)
                {
                    stepBackIntoLine(previous);
                    raise(LineActivated, activeLineIndex);
                    consumed = true;
                    return true;
                }

                consumed = false;
                return true;
            }

            consumed = false;
            return false;
        }

        /// <summary>
        /// A typo the caret is PARKED ON is cleared where it sits.
        /// </summary>
        private bool tryClearAParkedTypo(IReadOnlyList<TypingCell> cells)
        {
            // A typo the caret is PARKED ON (backlog 184, see StrictSpaces): cleared where it sits,
            // not erased from behind. The gap is still owed its space, so the caret has no business
            // retreating into the perfectly good word in front of it, and the character the player
            // wants back is the one they are looking at. One press, one cell, caret unmoved.
            //
            // Keyed on the STATE rather than on the era flags, and the two are the same thing here:
            // only the StrictSpaces + SpaceSkipsWord arm ever leaves the caret sitting on a Wrong
            // cell, because everywhere else resolving a cell is how the caret got past it. A replay
            // re-deriving with flags bit 4 clear therefore cannot reach this branch.
            if (caretIndex < cells.Count && cells[caretIndex].State == CellState.Wrong)
            {
                var parked = cells[caretIndex];

                parked.State = CellState.Untyped;
                parked.TypedChar = null;
                parked.JudgedDelta = null;

                raise(TypoErased);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The ordinary erase: the nearest cell behind the caret holding something the player typed, stepping
        /// back over auto-skipped and abandoned cells. Returns false when there is nothing to erase.
        /// </summary>
        private bool eraseBehindTheCaret(IReadOnlyList<TypingCell> cells)
        {
            // Find the nearest cell behind the caret holding something the player typed, stepping
            // over the two transparent states (scan first, mutate after).
            int target = caretIndex - 1;

            while (target >= 0 && (cells[target].State == CellState.AutoSkipped || cells[target].State == CellState.Abandoned))
                target--;

            // Which of the stepped-over cells are abandoned ones being RECLAIMED, still without
            // mutating: the answer decides whether a press with nothing typed behind it did anything
            // at all.
            List<int>? reclaimed = null;

            for (int i = target + 1; i < caretIndex; i++)
            {
                if (cells[i].State == CellState.Abandoned)
                    (reclaimed ??= new List<int>()).Add(i);
            }

            if (target < 0 && reclaimed == null)
                return false;

            // Un-skip the punctuation and re-open the abandoned cells we stepped back over.
            for (int i = target + 1; i < caretIndex; i++)
            {
                if (cells[i].State == CellState.AutoSkipped || cells[i].State == CellState.Abandoned)
                    cells[i].State = CellState.Untyped;
            }

            if (target < 0)
            {
                // Nothing typed is left behind the caret. Ordinarily that is "nothing to erase", but
                // a word skipped at the very start of a line leaves phantom cells and no keypress
                // before them, and refusing here would make that one word the only unreclaimable one
                // on the map. The reclaim IS the state change, so the press did something: put the
                // caret back at the head of the word it just re-opened. (Non-null by the guard
                // above: with nothing typed behind the caret, a reclaim is the only thing left.)
                caretIndex = 0;
                autoSkipForward();
                raise(AbandonReclaimed, new AbandonedCells(activeLineIndex, reclaimed!));
                return true;
            }

            var cell = cells[target];

            // Read BEFORE the cell is cleared: a wrong character is being taken back, which is the
            // one erase anything downstream has to hear about (backlog 166, see TypoErased).
            bool erasedTypo = cell.State == CellState.Wrong;

            cell.State = CellState.Untyped;
            cell.TypedChar = null;
            cell.JudgedDelta = null;

            caretIndex = target;

            // Announced with the engine already settled, like every other event here, and silent
            // during a rebuild for the same reason every other one is (backlog 165): a backwards
            // seek re-walks the whole prefix, so a raw invoke here would refund a drain per
            // backspace in the run while the matching drains, riding on CharJudged, stayed silent.
            if (reclaimed != null)
                raise(AbandonReclaimed, new AbandonedCells(activeLineIndex, reclaimed));

            if (erasedTypo)
                raise(TypoErased);

            return true;
        }

        /// <summary>
        /// Where a CTRL+BACKSPACE (backlog 182, the typing-site "erase the previous word" gesture)
        /// should leave the caret: a PURE QUERY, mutating nothing. The caller composes the gesture
        /// out of ordinary <see cref="ProcessBackspace"/> calls
        /// (<c>while (CaretIndex &gt; target &amp;&amp; ProcessBackspace()) record();</c>), which is
        /// what keeps the whole gesture inside the existing replay vocabulary: a stored run holds the
        /// same run of backspace frames the live engine consumed, and nothing here has to be
        /// re-derived by a replay at all.
        ///
        /// <para>The rule is the one every typing site implements. Walk back over the word GAPS
        /// immediately behind the caret, then over the word behind them, and stop at that word's
        /// first cell. So a caret sitting mid-word erases back to the start of the word it is inside,
        /// and a caret sitting at the head of a word (the gap immediately behind it) erases that gap
        /// AND the whole word before it. At the head of the line the answer is
        /// <see cref="CaretIndex"/> itself, which makes the composed gesture a no-op: it never calls
        /// the engine and therefore never records anything.</para>
        ///
        /// <para>The target is a FLOOR, not a promise: a single <see cref="ProcessBackspace"/> steps
        /// transparently back over auto-skipped cells, and in the era before <see cref="InputEra2"/>
        /// over abandoned ones too, so one press over a word that was entirely given up to a word skip
        /// could land the caret further back than this, exactly as a plain backspace there would.
        /// Under <see cref="InputEra2"/> undoing a word skip stops at the first abandoned cell,
        /// preserving the typed cells before it.</para>
        ///
        /// <para>Answers <see cref="CaretIndex"/> unchanged when no line is active or the run has
        /// finished, so the caller needs no second guard.</para>
        /// </summary>
        public int WordBackspaceTarget
        {
            get
            {
                if (isFinished || activeLineIndex == -1)
                    return caretIndex;

                var cells = lines[activeLineIndex].Cells;
                int target = Math.Min(caretIndex, cells.Count);

                // The gaps directly behind the caret (normally one; a map never authors two in a
                // row, and the loop costs nothing for being written to survive one that did).
                while (target > 0 && isWordGap(cells[target - 1]))
                    target--;

                // Then the word they follow, back to the gap that opens it or to the line's head.
                while (target > 0 && !isWordGap(cells[target - 1]))
                    target--;

                return target;
            }
        }

        /// <summary>
        /// Where a CTRL+A (backlog 182, "select back to the mistake I have to retype") should put the
        /// start of its selection: the first cell of the run holding the EARLIEST unfixed MISTAKE
        /// behind the caret, or -1 when there is no mistake behind the caret at all (the gesture is
        /// then a no-op). The selection itself is the half-open range [this, <see cref="CaretIndex"/>),
        /// and it is pure UI state: nothing in the engine knows it exists. Consuming it is composed,
        /// like the gesture above, out of ordinary <see cref="ProcessBackspace"/> calls back to this
        /// index plus at most one <see cref="ProcessKey"/>, so a replay stores exactly the engine
        /// calls that were made.
        ///
        /// <para>A MISTAKE is a cell in one of two states, and they are the two ways a cell behind the
        /// caret can still be owed something. <see cref="CellState.Wrong"/> is a wrong character typed
        /// through and not yet backspaced away. <see cref="CellState.Abandoned"/> is a character a
        /// word skip gave up (backlog 167, see <see cref="SpaceSkipsWord"/>), which since backlog 244
        /// anchors a selection exactly as a typo does: an abandoned cell is precisely a cell the
        /// player still has to type, a backspace re-opens it (in either era), and re-typing the cell REDEEMS the
        /// combo claim the skip took against it (backlog 140's machinery, and backlog 243's fix to
        /// who owns that claim). Without this the one mistake the game hands a player in a single
        /// keystroke was the one mistake the one-keystroke correction refused to reach.</para>
        ///
        /// <para>An abandoned cell is never itself a word GAP: <see cref="skipCurrentWord"/> scans
        /// strictly between the gaps either side of the caret, so the gap-anchoring case below is
        /// reachable only by a typo. The two states share the scan anyway, because "the first cell I
        /// must retype to fix this" is one rule and stating it twice is how the two drift apart.</para>
        ///
        /// <para>The scan takes the EARLIEST mistake of either kind on the line, so the selection
        /// covers every unfixed one behind the caret rather than only the most recent (backlog 184).
        /// An earlier ABANDONED cell therefore wins over a later typo just as an earlier typo wins
        /// over a later abandoned one: the two are one ordered list, not two ranked kinds.
        /// The gesture is "fix my mistakes", and it is one keystroke: offering the shortest retype
        /// would leave a player with two spoiled words pressing it, retyping, pressing it again, and
        /// having no way to see from the caret how many rounds are left. Retyping the cells in between
        /// costs nothing, since a correct cell re-typed is scoring-inert.</para>
        ///
        /// <para>WHICH run the mistake's cell opens has two cases, and they are the same rule stated
        /// twice: the selection starts at the first cell the player must retype to fix it. For an
        /// ordinary lyric character (a typo or an abandoned cell alike) that is its WORD's first cell
        /// (walk back to the gap before it), which for a skipped word is its head: the mass backspace
        /// the caller composes reclaims the abandoned tail on its way past, exactly as a plain
        /// backspace there does. For a WORD GAP holding a typo (possible since backlog 181, see
        /// <see cref="WrongInputOnWordGaps"/>) the answer depends on the era.</para>
        ///
        /// <para>Under <see cref="InputEra2"/> a gap typo selects from the beginning of the word
        /// preceding that gap, so the retype includes its space as well; a word given up whole anchors
        /// on its own head, because that era's backspace stops there; and leading punctuation the word
        /// auto-skips is stepped over. In the era before it the gap itself is the anchor (it belongs to
        /// no word, so walking back from it would swallow the perfectly good word in front of it), and
        /// a word given up whole widens the anchor back onto the gap in front of it (backlog 260, see
        /// the walk in <see cref="legacyRetypeSelectionAnchor"/>). The anchor only decides which
        /// BACKSPACE frames the live gesture records, never how a recorded one plays back; it follows
        /// the era so it always agrees with the backspace it composes.</para>
        ///
        /// <para>The answer is never equal to <see cref="CaretIndex"/> when it is non-negative: the
        /// scan is over [0, <see cref="CaretIndex"/>), so a selection always covers at least one cell.
        /// The one typo that can sit AT the caret, the gap a <see cref="StrictSpaces"/> park is
        /// holding, is deliberately outside that range: it needs no selection, being one backspace
        /// away under the same rule that parked it. An abandoned cell can never sit at or ahead of
        /// the caret at all, since the skip that made it left the caret past the whole word.</para>
        /// </summary>
        public int RetypeSelectionAnchor
        {
            get
            {
                if (isFinished || activeLineIndex == -1)
                    return -1;

                var cells = lines[activeLineIndex].Cells;
                int limit = Math.Min(caretIndex, cells.Count);
                int mistake = -1;

                for (int i = 0; i < limit; i++)
                {
                    // The two unfixed states, taken in one pass so the earliest of EITHER kind wins
                    // (backlog 244 added the abandoned one).
                    if (cells[i].State == CellState.Wrong || cells[i].State == CellState.Abandoned)
                    {
                        mistake = i;
                        break;
                    }
                }

                if (mistake < 0)
                    return -1;

                if (!InputEra2)
                    return legacyRetypeSelectionAnchor(cells, mistake);

                int anchor = mistake;

                // A typo on a space belongs to the word immediately before it for retyping.
                // Step over adjacent gaps first so even unusual repeated spaces find that word.
                if (isWordGap(cells[mistake]))
                {
                    while (anchor > 0 && isWordGap(cells[anchor - 1]))
                        anchor--;
                }

                while (anchor > 0 && !isWordGap(cells[anchor - 1]))
                    anchor--;

                // A word can begin with punctuation that typing auto-skips. The first typeable
                // cell is the earliest place a backspace can stop without crossing the prior gap.
                while (anchor < mistake && !cells[anchor].IsTypeable)
                    anchor++;

                return anchor;
            }
        }

        /// <summary>
        /// <see cref="RetypeSelectionAnchor"/> in the era before <see cref="InputEra2"/>, kept whole so
        /// an engine without the era composes the selection its own backspace can land on.
        /// </summary>
        private int legacyRetypeSelectionAnchor(IReadOnlyList<TypingCell> cells, int mistake)
        {
            if (isWordGap(cells[mistake]))
                return mistake;

            int anchor = mistake;

            while (anchor > 0 && !isWordGap(cells[anchor - 1]))
                anchor--;

            // ...and then back to a cell the mass backspace can actually LAND on (backlog 260).
            // The collapse is a run of ordinary ProcessBackspace calls, and one of those steps
            // TRANSPARENTLY over abandoned and auto-skipped cells to erase the nearest cell the
            // player typed: it cannot stop on a cell nobody typed. So when the whole word was
            // given up (a space struck at its head), the word's own first cell is not a stopping
            // place, the run carries on to the gap in front of it, and a selection anchored on the
            // word head was one cell short of where its own collapse ends up. The caret then sat
            // BEHIND the anchor on a gap that had already been judged, and the next letter of the
            // retype landed on it as a fresh typo: one keystroke of correction manufacturing a
            // mistake of its own.
            //
            // Widening the SELECTION rather than bounding the backspace is what kept this inside
            // the input layer with no era of its own. A bounded erase would make the live run
            // stop somewhere its own recorded BACKSPACE frames cannot, since playback feeds them
            // through the plain call, and the replay would then diverge from the run it stores.
            // (InputEra2 did bound it, on an era of its own, which is why that era does not widen.)
            // The extra cell costs the player one keystroke and nothing else: a gap that was
            // already judged retypes inert (see FirstCorrectDelta), so no count, no score and no
            // combo moves, and the highlight shows exactly the run the collapse will clear.
            //
            // The same walk the backspace makes, so the two cannot disagree: over the transparent
            // states only, stopping at the line's head. A word gap is never in either state
            // (skipCurrentWord scans strictly between the gaps), so this steps back at most out
            // of the abandoned word and onto the gap before it.
            while (anchor > 0 && (cells[anchor].State == CellState.Abandoned || cells[anchor].State == CellState.AutoSkipped))
                anchor--;

            return anchor;
        }
    }
}
