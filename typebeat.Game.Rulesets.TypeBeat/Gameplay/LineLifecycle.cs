// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// LINE ENTRY AND SEALING: when a line may seal, when a line is held open past its deadline, and
    /// when a finished caret may move on to the next one. Pure decisions over the map's lines, the
    /// caret, the time and the engine's caret switches; <see cref="TypingEngine"/> owns the state and
    /// performs every move these decide, so the rules can be pinned directly
    /// (<c>LineLifecycleTest</c>) rather than only through played runs.
    /// </summary>
    internal static class LineLifecycle
    {
        /// <summary>
        /// The caret switches line entry and sealing read: the engine's
        /// <see cref="TypingEngine.FletcherEnabled"/>, <see cref="TypingEngine.BoundedRush"/>,
        /// <see cref="TypingEngine.FlexibleLineSnap"/>, <see cref="TypingEngine.ManualNewlines"/> and
        /// <see cref="TypingEngine.EarlyFinish"/>.
        /// </summary>
        public readonly record struct Rules(bool Fletcher, bool BoundedRush, bool FlexibleLineSnap, bool ManualNewlines, bool EarlyFinish);

        /// <summary>Where the caret stands: the active line (-1 for none) and the cell on it.</summary>
        public readonly record struct Caret(int Line, int Cell);

        /// <summary>
        /// DRAG FREEDOM (see <see cref="TypingEngine.FletcherEnabled"/>): a line the player is still typing must not
        /// be force-sealed out from under them at its normal deadline. The seal is deferred while the
        /// caret is on the line, up to <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> past its hard deadline;
        /// past that the line seals as usual (untyped cells become misses, one combo break) and the
        /// caret is moved on. Always true with a pinned caret, and true under a flexible one for any
        /// line the player is not currently on, so a finished-early line still seals exactly on its
        /// own deadline - unless <see cref="TypingEngine.ManualNewlines"/> is holding it (see
        /// <see cref="ManualNewlineHoldsLineOpen"/>).
        ///
        /// <para>Its mirror is <see cref="EntryPermitted"/> (backlog 218): this one is how far past a
        /// line's natural END a dragging player may still be on it, that one is how far before a
        /// line's natural START a rushing player may already be on it, and both distances are the
        /// one <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/>.</para>
        ///
        /// <para>A line the player ABANDONED with a line skip keeps the grace after the caret has
        /// left it (see <c>lineAbandoned</c>): the skip is caret movement only, so the line it
        /// walked out of has to reach its misses and its one combo break at the very instant it would
        /// have with the player still sitting there doing nothing.</para>
        /// </summary>
        public static bool SealPermitted(Rules rules, IReadOnlyList<TypingLine> lines, int index, double time, Caret caret, bool abandoned, Func<TypingLine, double> effectiveSealTime)
        {
            // MANUAL NEWLINES: a line the player has TYPED OUT and not closed is held to the drag
            // cutoff rather than left to seal on its own deadline. That deadline is the next line's
            // first word in any ordinary map, so sealing there is exactly the pull the setting exists
            // to prevent; the cutoff is the instant the push warning's red bar completes, and the
            // same one a caret still dragging on the line is force-sealed at with the setting off.
            // Held together, the seal, the seal loop's hand-over of the caret and the closed step
            // back (see ProcessBackspace) all land on that one instant.
            if (ManualNewlineHoldsLineOpen(rules, lines, index, caret))
                return time >= effectiveSealTime(lines[index]) + TypingEngine.FLETCHER_DRAG_GRACE_MS;

            if (!rules.Fletcher || (caret.Line != index && !abandoned))
                return true;

            var line = lines[index];

            // Nothing left untyped means there is no drag to protect: the line seals on its normal
            // deadline, exactly as it would without the mod. (This is also what lets the FINAL line,
            // which has no next line to roll on to, finish the run on time once it is fully typed.)
            if (!HasUntypedTypeable(line))
                return true;

            return time >= effectiveSealTime(line) + TypingEngine.FLETCHER_DRAG_GRACE_MS;
        }

        /// <summary>
        /// Whether <see cref="TypingEngine.ManualNewlines"/> is holding <paramref name="index"/> open: a FINISHED
        /// line the player is still standing on, or one immediately behind a caret that has been
        /// handed to the next line's head and could still step back up to it.
        ///
        /// <para>Those two are the states the delay is observable in, and they are the same one: the
        /// caret is one keystroke from the line and the line is still the player's to give up. The
        /// hold ends at the drag cutoff (<see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> past the line's own
        /// deadline), which is the instant the push warning has been counting down to, so a player
        /// who never presses is handed on exactly when they would have been forced on with the
        /// setting off - not at the line's own deadline, which is the next line's first word.</para>
        ///
        /// <para>A line that still owes a character is NOT held here: the drag rule above already
        /// holds the caret's own line, and holding a line the player merely left behind untyped would
        /// put its misses later than the song's own punishment. The LAST line is not held either: it
        /// has no next line to be handed on to, so its seal is what ends the run and must stay on its
        /// own deadline.</para>
        /// </summary>
        public static bool ManualNewlineHoldsLineOpen(Rules rules, IReadOnlyList<TypingLine> lines, int index, Caret caret)
        {
            if (!rules.ManualNewlines || !rules.Fletcher)
                return false;

            if (index + 1 >= lines.Count || HasUntypedTypeable(lines[index]))
                return false;

            return caret.Line == index || (caret.Line == index + 1 && caret.Cell == 0);
        }

        /// <summary>
        /// THE RUSH BOUND (see <see cref="TypingEngine.BoundedRush"/>), and the exact mirror of
        /// <see cref="SealPermitted"/>: may a FINISHED caret move on to <paramref name="line"/>
        /// at <paramref name="time"/> yet? Drag holds a line open up to
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> past its natural end
        /// (<see cref="TypingLine.EndTime"/> + <see cref="TypingLine.SealGraceMs"/>); rush enters a
        /// line up to the same <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> before its natural start
        /// (<see cref="TypingLine.ActivationTime"/>). Always true under the unbounded era, which is
        /// what every run stored before backlog 218 was played under.
        ///
        /// <para>Asked ONLY of a caret moving itself: the keypress roll
        /// (<c>rollForwardIfFinishedEarly</c>) and the time-driven one
        /// (<c>snapForwardOnLineStart</c>). The hand-overs the SEAL LOOP performs, the
        /// ordinary one and the drag cutoff's, do not consult it and must not: the song has moved
        /// off the old line there, so entry is late rather than early, and refusing it would leave
        /// the player in a dead zone the flexible caret does not otherwise have. On a decoder-built
        /// map that is never even a near thing, because a line's activation is clamped to its own
        /// StartTime, which IS the previous line's EndTime, so this bound opens at most
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> before the previous line could seal at all.</para>
        /// </summary>
        public static bool EntryPermitted(TypingLine line, double time, bool boundedRush) => !boundedRush || time >= EntryOpensAt(line, boundedRush);

        /// <summary>
        /// Whether the caret is on a line it may not type on yet, at <paramref name="time"/> (see
        /// <see cref="TypingEngine.AwaitingEntry"/>): the manual-newline era only, and only for a live line.
        /// </summary>
        public static bool AwaitingEntry(Rules rules, IReadOnlyList<TypingLine> lines, int activeLine, double time)
            => rules.ManualNewlines && rules.Fletcher && activeLine >= 0 && !EntryPermitted(lines[activeLine], time, rules.BoundedRush);

        /// <summary>
        /// The earliest instant the caret may be on <paramref name="line"/> by RUSHING onto it:
        /// the line's own <see cref="TypingLine.ActivationTime"/> under the unbounded era, and
        /// <see cref="TypingEngine.FLETCHER_DRAG_GRACE_MS"/> before it under <see cref="TypingEngine.BoundedRush"/>, which is
        /// the head start a player earns for having finished the line before it. Read by both arms
        /// that move a finished caret: <see cref="EntryPermitted"/> (the keypress roll) and
        /// <c>snapForwardOnLineStart</c> (the time-driven one).
        /// </summary>
        public static double EntryOpensAt(TypingLine line, bool boundedRush) => line.ActivationTime - (boundedRush ? TypingEngine.FLETCHER_DRAG_GRACE_MS : 0);

        /// <summary>
        /// Whether the time-driven hand-over of a finished caret (the flexible-lines snap and the
        /// deferred rush roll, <c>TypingEngine.snapForwardOnLineStart</c>) may run at all: only for an
        /// unpinned caret under either switch, never once the run has finished, and never under
        /// <see cref="TypingEngine.ManualNewlines"/>, where the caret is the player's to hand over.
        /// </summary>
        public static bool TimeMayMoveAFinishedCaret(Rules rules, bool isFinished)
        {
            // BoundedRush belongs in this gate as well as FlexibleLineSnap: this is the only arm that
            // can move a caret the bound parked, and a live stack always sets both anyway.
            if (!rules.Fletcher || (!rules.FlexibleLineSnap && !rules.BoundedRush) || isFinished)
                return false;

            return !rules.ManualNewlines;
        }

        /// <summary>
        /// A line may seal once its EndTime has passed AND either its grace window has elapsed
        /// or nothing typeable is left untyped (early seal so the next line isn't delayed). This is
        /// only the DEADLINE half: whether the seal may actually run is
        /// <see cref="SealPermitted"/>'s, and a line is held past this point both by drag protection
        /// and by a manual caret that has finished it (<see cref="ManualNewlineHoldsLineOpen"/>).
        /// </summary>
        public static bool CanSeal(TypingLine line, double time, Func<TypingLine, double> effectiveSealTime)
        {
            if (time < line.EndTime)
                return false;

            if (time >= effectiveSealTime(line))
                return true;

            return !HasUntypedTypeable(line);
        }

        /// <summary>
        /// EARLY FINISH (see <see cref="TypingEngine.EarlyFinish"/>): the map's FINAL line has every typeable cell
        /// typed correctly, so its seal does not wait for the line's end. Only the final line: an
        /// earlier one seals on its own deadline so the song's pacing of the stack is untouched, and
        /// the final line's seal is what ends the run. A wrong cell (still fixable) or a line with
        /// nothing typeable in it does not qualify, so both keep the ordinary deadline.
        /// </summary>
        public static bool FinishedEarly(IReadOnlyList<TypingLine> lines, int index, bool earlyFinish)
        {
            if (!earlyFinish || index != lines.Count - 1)
                return false;

            bool anyTypeable = false;

            foreach (var cell in lines[index].Cells)
            {
                if (!cell.IsTypeable)
                    continue;

                if (cell.State != CellState.Correct)
                    return false;

                anyTypeable = true;
            }

            return anyTypeable;
        }

        /// <summary>
        /// Whether the line still holds a typeable cell nobody has put anything into. An ABANDONED
        /// cell is one of those (backlog 167): the player owes that character exactly as much as one
        /// they simply have not reached, and it is re-typeable until the line seals, so the early
        /// seal ("nothing left to type, do not hold the next line up") must not fire on a line the
        /// player can still come back into. That keeps the reclaim window running to the line's own
        /// deadline, which is the window the grace exists to grant.
        /// </summary>
        public static bool HasUntypedTypeable(TypingLine line)
        {
            foreach (var cell in line.Cells)
            {
                if (cell.IsTypeable && (cell.State == CellState.Untyped || cell.State == CellState.Abandoned))
                    return true;
            }

            return false;
        }
    }
}
