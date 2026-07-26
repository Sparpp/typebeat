// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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

        /// <summary>
        /// The range anchor: the line a plain or Ctrl+click last landed on. Shift+click always
        /// ranges FROM here, so repeated shift+clicks grow and shrink one run around a fixed
        /// anchor instead of walking it forward one click at a time (standard list semantics).
        /// </summary>
        private TypeBeatHitObject? rangeAnchor;

        /// <summary>The line Shift+click ranges from; null until something has been clicked.</summary>
        public TypeBeatHitObject? RangeAnchor => rangeAnchor;

        /// <summary>Plain click: single selection, replacing any multi-selection, and a fresh anchor.</summary>
        public void SelectLine(TypeBeatHitObject line)
        {
            MultiSelectedLines.Clear();
            rangeAnchor = line;
            SelectedLine.Value = line;
        }

        /// <summary>
        /// Ctrl+click: toggles a line's membership. The first toggle seeds the set with the
        /// current primary selection (so ctrl+click ADDS to what is visibly selected); removing
        /// the primary promotes another member; an emptied set returns to playhead follow.
        /// The clicked line becomes the new range anchor either way, so a following Shift+click
        /// ranges from where the user last pointed.
        /// </summary>
        public void ToggleLine(TypeBeatHitObject line)
        {
            if (MultiSelectedLines.Count == 0 && SelectedLine.Value is TypeBeatHitObject primary && primary != line)
                MultiSelectedLines.Add(primary);

            rangeAnchor = line;

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
        /// ANCHOR (<see cref="RangeAnchor"/>, the last plain/Ctrl-clicked line) and
        /// <paramref name="line"/>, inclusive. The anchor deliberately does not move, so
        /// shift-clicking again re-ranges from the same place; the clicked line still becomes the
        /// primary (what the detail panel edits).
        /// </summary>
        public void SelectLineRange(IReadOnlyList<TypeBeatHitObject> ordered, TypeBeatHitObject line)
        {
            var anchor = rangeAnchor ?? SelectedLine.Value ?? line;

            int a = indexOf(ordered, anchor);
            int b = indexOf(ordered, line);

            if (a < 0 || b < 0)
            {
                SelectLine(line);
                return;
            }

            MultiSelectedLines.Clear();

            for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                MultiSelectedLines.Add(ordered[i]);

            rangeAnchor = anchor;
            SelectedLine.Value = line;
        }

        /// <summary>Drops the multi-selection and its anchor (the primary selection is the callers' concern).</summary>
        public void ClearMultiLineSelection()
        {
            MultiSelectedLines.Clear();
            rangeAnchor = null;
        }

        /// <summary>
        /// The lines a SECTION-level operation should act on, in typing order: the multi-selection
        /// when there is one, else the single primary selection, else empty (the caller decides
        /// what "nothing selected" means for it).
        /// </summary>
        public List<TypeBeatHitObject> SelectedLinesInOrder(IReadOnlyList<TypeBeatHitObject> ordered)
        {
            if (MultiSelectedLines.Count > 0)
                return ordered.Where(MultiSelectedLines.Contains).ToList();

            if (SelectedLine.Value is TypeBeatHitObject single && ordered.Contains(single))
                return new List<TypeBeatHitObject> { single };

            return new List<TypeBeatHitObject>();
        }

        /// <summary>
        /// Re-points the multi-selection and the range anchor at live hit objects after an
        /// undo/redo (which replaces every instance): stale entries are matched by
        /// <see cref="TypeBeatHitObject.LineIndex"/>, vanished lines are dropped.
        /// </summary>
        public void RebindMultiSelection(IReadOnlyList<TypeBeatHitObject> ordered, Func<TypeBeatHitObject, bool> stillAlive)
        {
            if (rangeAnchor != null && !stillAlive(rangeAnchor))
                rangeAnchor = ordered.FirstOrDefault(o => o.LineIndex == rangeAnchor.LineIndex);

            if (MultiSelectedLines.Count == 0 || MultiSelectedLines.All(stillAlive))
                return;

            var rebound = MultiSelectedLines
                          .Select(o => stillAlive(o) ? o : ordered.FirstOrDefault(n => n.LineIndex == o.LineIndex))
                          .Where(o => o != null)
                          .Select(o => o!)
                          .ToList();

            MultiSelectedLines.Clear();

            foreach (var o in rebound)
                MultiSelectedLines.Add(o);
        }

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

        /// <summary>True while a drag or text edit is live; playhead-follow is frozen.</summary>
        public bool InteractionPinned => interactionLocks > 0;

        public void BeginInteraction() => interactionLocks++;

        public void EndInteraction() => interactionLocks = System.Math.Max(0, interactionLocks - 1);

        /// <summary>When set, playback auto-pauses at this time (A/B line replay).</summary>
        public double? ReplayStopTime;
    }
}
