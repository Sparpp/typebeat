// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.UI
{
    /// <summary>
    /// One line's slice of the flashlight window: the inclusive range of that line's own COUNTABLE
    /// slots (typeable, non-space) that are lit, plus whether the outermost lit char on each side
    /// should soften because darkness lies beyond it in the stream. <see cref="Lo"/> &gt;
    /// <see cref="Hi"/> means the line is fully hidden. A soft flag is only set on the side that
    /// carries the WHOLE window's outer edge; an internal line-to-line boundary (the window
    /// continues into the adjacent line) stays hard so the two lines join seamlessly.
    /// </summary>
    public readonly struct LineWindow : IEquatable<LineWindow>
    {
        public readonly int Lo;
        public readonly int Hi;
        public readonly bool SoftLeft;
        public readonly bool SoftRight;

        public LineWindow(int lo, int hi, bool softLeft, bool softRight)
        {
            Lo = lo;
            Hi = hi;
            SoftLeft = softLeft;
            SoftRight = softRight;
        }

        /// <summary>No countable char of the line is lit.</summary>
        public static LineWindow Hidden => new LineWindow(0, -1, false, false);

        public bool IsHidden => Lo > Hi;

        public bool Equals(LineWindow other) =>
            Lo == other.Lo && Hi == other.Hi && SoftLeft == other.SoftLeft && SoftRight == other.SoftRight;

        public override bool Equals(object? obj) => obj is LineWindow o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(Lo, Hi, SoftLeft, SoftRight);
    }

    /// <summary>
    /// The Flashlight mod's per-character window math, pure so it is unit-testable without
    /// drawables. The stage slices the stream window across the lyric stack with
    /// <see cref="ComputeStreamWindows"/>, and each <see cref="LyricLineDisplay"/> turns its own
    /// <see cref="LineWindow"/> slice into per-cell alphas with
    /// <see cref="ComputeWindowAlphas(IReadOnlyList{TypingCell}, LineWindow, float)"/>. Purely visual:
    /// judgement is untouched.
    /// </summary>
    public static class FlashlightWindow
    {
        /// <summary>
        /// Per-cell visibility multipliers for a line given its <see cref="LineWindow"/> slice of the
        /// stream window: 1 for a lit char, <paramref name="softAlpha"/> for the outermost lit char on
        /// a side the window flagged soft (darkness beyond it in the stream), 0 for a hidden char. A
        /// space/punctuation cell strictly between two lit countable chars stays lit (it spends no
        /// budget); one at or past a lit edge is hidden. Pure (no drawable state) so it is unit-testable.
        /// </summary>
        public static float[] ComputeWindowAlphas(IReadOnlyList<TypingCell> cells, LineWindow window, float softAlpha)
        {
            int n = cells.Count;
            var result = new float[n];

            if (n == 0 || window.IsHidden)
                return result;

            // pref[i] = countable cells strictly before i; pref[n] = total countable in the line.
            int[] pref = new int[n + 1];
            for (int i = 0; i < n; i++)
                pref[i + 1] = pref[i] + (IsCountable(cells[i]) ? 1 : 0);

            int lo = window.Lo;
            int hi = window.Hi;

            for (int i = 0; i < n; i++)
            {
                if (IsCountable(cells[i]))
                {
                    int slot = pref[i];

                    if (slot < lo || slot > hi)
                        continue; // hidden

                    // Soften the outermost lit char only on a side the stream marked soft (a hidden
                    // char lies beyond it). A real line start/end or a boundary the window crosses
                    // into the next line stays a hard, full-alpha edge.
                    bool fadesLeft = slot == lo && window.SoftLeft;
                    bool fadesRight = slot == hi && window.SoftRight;
                    result[i] = fadesLeft || fadesRight ? softAlpha : 1f;
                }
                else
                {
                    // Space / punctuation: lit only when it sits strictly between two lit countable
                    // chars, so leading/trailing marks and the space just past the edge stay hidden.
                    int leftSlot = pref[i] - 1;
                    int rightSlot = pref[i];
                    bool leftLit = leftSlot >= lo && leftSlot <= hi;
                    bool rightLit = rightSlot >= lo && rightSlot <= hi;
                    result[i] = leftLit && rightLit ? 1f : 0f;
                }
            }

            return result;
        }

        /// <summary>
        /// Single-line convenience overload: treat <paramref name="cells"/> as the whole stream and
        /// centre a <paramref name="radius"/>-countable window on the caret. The stream-level path
        /// (<see cref="ComputeStreamWindows"/>) is what gameplay uses; this stays for isolated-line
        /// unit tests and any caller with one line in hand.
        /// </summary>
        public static float[] ComputeWindowAlphas(IReadOnlyList<TypingCell> cells, int caretCellIndex, int radius, float softAlpha)
            => ComputeWindowAlphas(cells, SingleLineWindow(cells, caretCellIndex, radius), softAlpha);

        /// <summary>
        /// Split a stream window across ordered lines. Given each line's countable-char count and the
        /// caret's stream slot (countable chars before the caret across the whole stack), returns the
        /// <see cref="LineWindow"/> slice for each line: <paramref name="radius"/> countable chars reach
        /// each side of the caret through the concatenated stream, so the budget spills from a line's
        /// tail into the next line's head (and symmetrically the other way). Only the outer edges of the
        /// whole window soften; boundaries the window crosses stay hard.
        ///
        /// <paramref name="maxRightSlot"/> caps the rightmost lit stream slot (inclusive). The stage
        /// passes the active line's last countable slot while the player is still typing it, so the
        /// forward budget cannot reach into the next line mid-line; once the line is complete (or during
        /// a cue-in, where there is no active line) it passes <see cref="int.MaxValue"/> and the budget
        /// spills forward as an early-finish reward. A clamped right edge is HARD (the clamped char is
        /// the last one you must still type, so it stays full alpha and darkness begins right after).
        /// The left/backward budget is never capped. Pure and unit-testable.
        /// </summary>
        public static LineWindow[] ComputeStreamWindows(IReadOnlyList<int> lineCountableCounts, int caretStreamSlot, int radius, int maxRightSlot = int.MaxValue)
        {
            int m = lineCountableCounts.Count;
            var result = new LineWindow[m];

            if (m == 0)
                return result;

            int total = 0;
            for (int k = 0; k < m; k++)
                total += lineCountableCounts[k];

            int segBase = 0;
            for (int k = 0; k < m; k++)
            {
                result[k] = windowForSegment(segBase, lineCountableCounts[k], total, caretStreamSlot, radius, maxRightSlot);
                segBase += lineCountableCounts[k];
            }

            return result;
        }

        /// <summary>Slice the global lit range [caret-radius, caret+radius-1] (clamped to the stream,
        /// and on the right to <paramref name="maxRightSlot"/>) down to the segment
        /// [segBase, segBase+segCount-1], reporting which side, if any, carries the window's soft outer
        /// edge.</summary>
        private static LineWindow windowForSegment(int segBase, int segCount, int totalCountable, int caretStreamSlot, int radius, int maxRightSlot)
        {
            if (segCount <= 0 || radius <= 0 || totalCountable <= 0)
                return LineWindow.Hidden;

            int caret = Math.Clamp(caretStreamSlot, 0, totalCountable);
            int gLo = Math.Max(0, caret - radius);          // leftmost lit stream slot (inclusive)
            int gHi = Math.Min(totalCountable - 1, caret + radius - 1); // rightmost lit stream slot

            // Cap the forward reach: while the active line is still being typed the stage caps this at
            // that line's last countable slot so nothing lights in the next line. A cap is a deliberate
            // wall, not a stream end, so the capped edge stays HARD (its char is fully lit).
            bool clampedRight = false;
            if (gHi > maxRightSlot)
            {
                gHi = maxRightSlot;
                clampedRight = true;
            }

            if (gLo > gHi)
                return LineWindow.Hidden;

            // Soft only where a hidden countable char actually lies beyond the window in the stream;
            // a clamp to slot 0 / the last slot is a hard stream end, and a right-cap is a hard wall.
            bool leftSoft = gLo > 0;
            bool rightSoft = !clampedRight && gHi < totalCountable - 1;

            int segHi = segBase + segCount - 1;
            int litLo = Math.Max(gLo, segBase);
            int litHi = Math.Min(gHi, segHi);

            if (litLo > litHi)
                return LineWindow.Hidden;

            // The soft edge belongs to whichever segment carries the window's outermost lit slot; a
            // segment whose lit run merely abuts the next line's run keeps a hard join.
            bool holdsGlobalLeft = litLo == gLo;
            bool holdsGlobalRight = litHi == gHi;

            return new LineWindow(litLo - segBase, litHi - segBase,
                holdsGlobalLeft && leftSoft,
                holdsGlobalRight && rightSoft);
        }

        private static LineWindow SingleLineWindow(IReadOnlyList<TypingCell> cells, int caretCellIndex, int radius)
        {
            int n = cells.Count;
            int caret = Math.Clamp(caretCellIndex, 0, n);
            int total = 0;
            int caretBudget = 0; // countable chars strictly left of the caret head

            for (int i = 0; i < n; i++)
            {
                if (!IsCountable(cells[i]))
                    continue;

                if (i < caret)
                    caretBudget++;

                total++;
            }

            return windowForSegment(0, total, total, caretBudget, radius, int.MaxValue);
        }

        /// <summary>A COUNTABLE cell: typeable and not a space. Spaces and punctuation do not spend the
        /// flashlight budget; the stage uses this to size the stream too, so it is shared here.
        /// Delegates to <see cref="TypingCell.IsCountable"/>, the single definition the engine's
        /// Fletcher rush cap measures character distance with.</summary>
        public static bool IsCountable(TypingCell cell) => cell.IsCountable;
    }
}
