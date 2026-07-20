// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Shared state of the lyric compose screen, cached into its subtree.
    /// The ACTIVE line is what the detail panel edits: the user's explicit selection when there
    /// is one, otherwise the line under the playhead (karaoke-style follow). While an
    /// interaction (drag / text focus) is live the active line is pinned so playback can't
    /// yank the surface out from under the user's pointer.
    /// </summary>
    public partial class LyricEditState
    {
        /// <summary>The explicit user selection; null = follow the playhead.</summary>
        public readonly Bindable<TypeBeatHitObject?> SelectedLine = new Bindable<TypeBeatHitObject?>();

        /// <summary>
        /// All multi-selected lines (Ctrl/Shift+click in the line list). Contains
        /// <see cref="SelectedLine"/> whenever a multi-selection exists; empty when only the
        /// single primary selection (or playhead follow) is in effect. Polled by the line list.
        /// </summary>
        public readonly HashSet<TypeBeatHitObject> MultiSelectedLines = new HashSet<TypeBeatHitObject>();

        /// <summary>Plain click: single selection, replacing any multi-selection.</summary>
        public void SelectLine(TypeBeatHitObject line)
        {
            MultiSelectedLines.Clear();
            SelectedLine.Value = line;
        }

        /// <summary>
        /// Ctrl+click: toggles a line's membership. The first toggle seeds the set with the
        /// current primary selection (so ctrl+click ADDS to what is visibly selected); removing
        /// the primary promotes another member; an emptied set returns to playhead follow.
        /// </summary>
        public void ToggleLine(TypeBeatHitObject line)
        {
            if (MultiSelectedLines.Count == 0 && SelectedLine.Value is TypeBeatHitObject primary && primary != line)
                MultiSelectedLines.Add(primary);

            if (!MultiSelectedLines.Add(line))
                MultiSelectedLines.Remove(line);
            else
            {
                SelectedLine.Value = line;
                return;
            }

            if (SelectedLine.Value == line)
                SelectedLine.Value = MultiSelectedLines.OrderBy(o => o.LineIndex).LastOrDefault();
        }

        /// <summary>
        /// Shift+click: selects the contiguous run of <paramref name="ordered"/> between the
        /// current primary (anchor) and <paramref name="line"/>, inclusive.
        /// </summary>
        public void SelectLineRange(IReadOnlyList<TypeBeatHitObject> ordered, TypeBeatHitObject line)
        {
            var anchor = SelectedLine.Value ?? line;

            int a = indexOf(ordered, anchor);
            int b = indexOf(ordered, line);

            if (a < 0 || b < 0)
            {
                SelectLine(line);
                return;
            }

            MultiSelectedLines.Clear();

            for (int i = System.Math.Min(a, b); i <= System.Math.Max(a, b); i++)
                MultiSelectedLines.Add(ordered[i]);

            SelectedLine.Value = line;
        }

        /// <summary>Drops the multi-selection (the primary selection is the callers' concern).</summary>
        public void ClearMultiLineSelection() => MultiSelectedLines.Clear();

        private static int indexOf(IReadOnlyList<TypeBeatHitObject> list, TypeBeatHitObject item)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == item)
                    return i;
            }

            return -1;
        }

        /// <summary>What the detail surface shows (selection ?? playhead line). Maintained by the screen.</summary>
        public readonly Bindable<TypeBeatHitObject?> ActiveLine = new Bindable<TypeBeatHitObject?>();

        /// <summary>
        /// The PRIMARY focused word unit within the active line (-1 = none). Used by single-target
        /// actions (tap-stamp T, split S) and as the anchor for range selection. Always a member of
        /// <see cref="SelectedUnitIndices"/> when >= 0.
        /// </summary>
        public readonly BindableInt SelectedUnitIndex = new BindableInt(-1);

        /// <summary>All selected word units within the active line (multi-select). Polled by the word strip.</summary>
        public readonly HashSet<int> SelectedUnitIndices = new HashSet<int>();

        /// <summary>Selects a single unit, replacing any multi-selection.</summary>
        public void SelectUnit(int index)
        {
            SelectedUnitIndices.Clear();

            if (index >= 0)
                SelectedUnitIndices.Add(index);

            SelectedUnitIndex.Value = index;
        }

        /// <summary>Toggles a unit's membership in the selection (Ctrl+click); updates the anchor.</summary>
        public void ToggleUnit(int index)
        {
            if (index < 0)
                return;

            if (!SelectedUnitIndices.Add(index))
                SelectedUnitIndices.Remove(index);

            SelectedUnitIndex.Value = SelectedUnitIndices.Contains(index)
                ? index
                : SelectedUnitIndices.Count > 0 ? SelectedUnitIndices.Max() : -1;
        }

        /// <summary>Selects the contiguous run between the anchor and <paramref name="index"/> (Shift+click).</summary>
        public void SelectUnitRange(int anchor, int index)
        {
            SelectedUnitIndices.Clear();

            int lo = System.Math.Min(anchor, index);
            int hi = System.Math.Max(anchor, index);

            for (int i = lo; i <= hi; i++)
            {
                if (i >= 0)
                    SelectedUnitIndices.Add(i);
            }

            SelectedUnitIndex.Value = index;
        }

        /// <summary>Clears the word-unit selection (e.g. when the active line changes).</summary>
        public void ClearUnitSelection()
        {
            SelectedUnitIndices.Clear();
            SelectedUnitIndex.Value = -1;
        }

        private int interactionLocks;

        /// <summary>True while a drag or text edit is live — playhead-follow is frozen.</summary>
        public bool InteractionPinned => interactionLocks > 0;

        public void BeginInteraction() => interactionLocks++;

        public void EndInteraction() => interactionLocks = System.Math.Max(0, interactionLocks - 1);

        /// <summary>When set, playback auto-pauses at this time (A/B line replay).</summary>
        public double? ReplayStopTime;
    }
}
