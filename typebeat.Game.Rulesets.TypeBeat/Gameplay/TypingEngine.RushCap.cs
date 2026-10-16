// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// TypingEngine, THE COUNTABLE STREAM AND THE RUSH CAP: the map read as one run of countable
// characters, where the playhead and the caret sit in it, and the Fletcher cap measured on it.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public sealed partial class TypingEngine
    {
        /// <summary>
        /// THE RUSH CAP: how many COUNTABLE characters (typeable and not a space, the same currency
        /// the Flashlight window measures in) the unpinned caret may sit ahead of the playhead before
        /// a keypress is penalised (see <see cref="FletcherEnabled"/>). Since backlog 347 the penalty
        /// is the press's JUDGEMENT (it is awarded Meh and still credits combo, see
        /// <see cref="RushCapCostsAccuracy"/>), and the cap loosened from five to six with it.
        ///
        /// <para>The live value. A run stored before 347 is measured against
        /// <see cref="LEGACY_FLETCHER_MAX_CHARS_AHEAD"/> instead, and <see cref="RushCap"/> is the one
        /// place that picks between them, off the same era flag that picks the penalty.</para>
        /// </summary>
        public const int FLETCHER_MAX_CHARS_AHEAD = 6;

        /// <summary>
        /// The rush cap every run stored before backlog 347 was played against: five countable
        /// characters, past which a press landed and scored on the timing ladder but broke the combo.
        /// Kept as the era constant a replay whose second CONFIG word is absent re-derives under.
        /// </summary>
        public const int LEGACY_FLETCHER_MAX_CHARS_AHEAD = 5;

        /// <summary>
        /// The rush cap in force for this run: <see cref="FLETCHER_MAX_CHARS_AHEAD"/> under
        /// <see cref="RushCapCostsAccuracy"/>, <see cref="LEGACY_FLETCHER_MAX_CHARS_AHEAD"/> in the
        /// era before it.
        /// </summary>
        public int RushCap => RushCapCostsAccuracy ? FLETCHER_MAX_CHARS_AHEAD : LEGACY_FLETCHER_MAX_CHARS_AHEAD;

        /// <summary>The map read as one run of countable characters (see <see cref="CountableStream"/>).</summary>
        private readonly CountableStream countable;

        /// <summary>
        /// Re-read every countable cell's CURRENT target time (see <see cref="CountableStream.RebuildTargets"/>),
        /// whenever <see cref="AlignSubdivisionTargets"/> or <see cref="JapaneseWordTiming"/> swaps the
        /// cells' target times.
        /// </summary>
        private void rebuildCountableTargets() => countable.RebuildTargets();

        /// <summary>
        /// How many COUNTABLE characters the song has reached by <paramref name="time"/>: the count
        /// of countable cells across the whole map whose target time is at or before it. The
        /// playhead's position in the countable stream, and the reference the Fletcher rush cap is
        /// measured against. Monotonic in time and a pure function of the beatmap, so a replay
        /// reproduces it exactly.
        /// </summary>
        public int PlayheadCountablePosition(double time) => countable.PlayheadPosition(time);

        /// <summary>
        /// The player caret's position in the same countable stream: every countable cell in the
        /// lines before the active one, plus the countable cells behind the caret within it. 0 when
        /// no line is active.
        /// </summary>
        public int CaretCountablePosition => countablePositionAt(caretIndex);

        /// <summary>
        /// <see cref="CaretCountablePosition"/> for an arbitrary caret index on the active line: the
        /// countable cells of every line before it, plus the countable cells of this line behind
        /// <paramref name="index"/>. Split out for backlog 260, where the rush cap has to measure a
        /// skipping space against the caret as it stood BEFORE the skip moved it (see
        /// <see cref="LosslessSkipReclaim"/>); the property is exactly this at the live caret.
        /// </summary>
        private int countablePositionAt(int index)
            => activeLineIndex == -1 ? 0 : countable.PositionAt(activeLineIndex, index);

        /// <summary>
        /// Signed countable-character drift of the caret against the playhead at
        /// <paramref name="time"/>: positive = rushing ahead, negative = dragging behind. The
        /// quantity the Fletcher rush cap bounds (and the honest read-out of what the mod is about).
        /// </summary>
        public int CharsAheadOfPlayhead(double time) => CaretCountablePosition - PlayheadCountablePosition(time);

        /// <summary>
        /// Fletcher rush cap: would accepting <paramref name="cell"/> at <paramref name="time"/> leave
        /// the caret more than <see cref="RushCap"/> countable chars past the playhead? Measured on
        /// the caret position AFTER the press, so with the live cap of 6 the sixth char ahead is
        /// still fine and the seventh is not (the fifth and the sixth under the pre-347 cap of 5).
        /// A non-countable cell (a space) spends no
        /// budget, so pressing it can never push the caret over the line by itself.
        ///
        /// <para><paramref name="caretIndexForCap"/> is normally the live caret, and is the caret as
        /// it stood BEFORE a word skip for the one press that can be judged past a caret it moved
        /// itself (backlog 260, see <see cref="LosslessSkipReclaim"/>). Without that the abandoned
        /// tail was spent out of the player's budget by the very press that gave it up, which is the
        /// one way the sentence above about a space could be false.</para>
        /// </summary>
        private bool rushesPastCap(TypingCell cell, double time, int caretIndexForCap)
        {
            int after = countablePositionAt(caretIndexForCap) + (cell.IsCountable ? 1 : 0);

            return after - PlayheadCountablePosition(time) > RushCap;
        }
    }
}
