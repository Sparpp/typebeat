// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Bindables;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>The two tracks of the word strip: the words the player types, and the line's backing vocals (backlog 445).</summary>
    public enum LyricTrack
    {
        Main,
        Backing,
    }

    public partial class LyricEditState
    {
        /// <summary>
        /// Which track of the word strip the WORD actions address (backlog 445). Picked by clicking in a
        /// track (a block, an anchor, or its empty space); the other one is drawn dimmed. Selecting a
        /// typed word always returns to <see cref="LyricTrack.Main"/>.
        /// </summary>
        public readonly Bindable<LyricTrack> SelectedTrack = new Bindable<LyricTrack>(LyricTrack.Main);

        /// <summary>
        /// The PRIMARY selected backing word of the active line (-1 = none): the anchor of a Shift range
        /// and the word a single-target action takes. Always a member of <see cref="SelectedBackingIndices"/>
        /// when >= 0. Only meaningful on the backing track.
        /// </summary>
        public readonly BindableInt SelectedBackingIndex = new BindableInt(-1);

        /// <summary>All selected backing words of the active line (Ctrl/Shift+click), as <see cref="SelectedUnitIndices"/> is for typed words.</summary>
        public readonly HashSet<int> SelectedBackingIndices = new HashSet<int>();

        /// <summary>A backing word waiting for its line to become active, as <see cref="pendingUnit"/> is for a typed word.</summary>
        private (TypeBeatHitObject line, int index)? pendingBacking;

        /// <summary>Whether the word actions address the backing track.</summary>
        public bool BackingTrackSelected => SelectedTrack.Value == LyricTrack.Backing;

        /// <summary>Picks the backing track with no backing word selected (a click in its empty space).</summary>
        public void SelectBackingTrack()
        {
            ClearUnitSelection();
            clearBackingSelection();
            SelectedTrack.Value = LyricTrack.Backing;
        }

        /// <summary>Picks the main track, dropping any backing word selection.</summary>
        public void SelectMainTrack()
        {
            clearBackingSelection();
            SelectedTrack.Value = LyricTrack.Main;
        }

        /// <summary>Selects backing word <paramref name="index"/> of the active line alone, on the backing track.</summary>
        public void SelectBackingWord(int index)
        {
            ClearUnitSelection();
            clearBackingSelection();

            if (index >= 0)
                SelectedBackingIndices.Add(index);

            SelectedBackingIndex.Value = index;
            SelectedTrack.Value = LyricTrack.Backing;
        }

        /// <summary>Toggles a backing word in or out of the selection (Ctrl+click), as <see cref="ToggleUnit"/> does a typed word.</summary>
        public void ToggleBackingWord(int index)
        {
            if (index < 0)
                return;

            if (!BackingTrackSelected)
                SelectBackingTrack();

            if (!SelectedBackingIndices.Add(index))
                SelectedBackingIndices.Remove(index);

            SelectedBackingIndex.Value = SelectedBackingIndices.Contains(index)
                ? index
                : SelectedBackingIndices.Count > 0 ? SelectedBackingIndices.Max() : -1;
        }

        /// <summary>Selects the backing words between <paramref name="anchor"/> and <paramref name="index"/> (Shift+click).</summary>
        public void SelectBackingRange(int anchor, int index)
        {
            ClearUnitSelection();
            SelectedBackingIndices.Clear();

            for (int i = Math.Max(0, Math.Min(anchor, index)); i <= Math.Max(anchor, index); i++)
                SelectedBackingIndices.Add(i);

            SelectedBackingIndex.Value = index;
            SelectedTrack.Value = LyricTrack.Backing;
        }

        /// <summary>Selects every backing word of the active line (Select All on the backing track).</summary>
        public void SelectAllBackingWords(int count)
        {
            SelectBackingTrack();

            for (int i = 0; i < count; i++)
                SelectedBackingIndices.Add(i);

            SelectedBackingIndex.Value = count > 0 ? count - 1 : -1;
        }

        /// <summary>
        /// The backing words a backing action addresses: the selection, else the primary word, else
        /// none. Ascending, and never past <paramref name="count"/>, the active line's backing word count.
        /// </summary>
        public int[] SelectedBackingInOrder(int count)
        {
            IEnumerable<int> selected = SelectedBackingIndices.Count > 0
                ? SelectedBackingIndices
                : SelectedBackingIndex.Value >= 0
                    ? new[] { SelectedBackingIndex.Value }
                    : Array.Empty<int>();

            return selected.Where(i => i >= 0 && i < count).OrderBy(i => i).ToArray();
        }

        /// <summary>
        /// A click on a backing word of ANOTHER line: selects that line, and that word once the line
        /// has become active (see <see cref="SelectLineAtWord"/>, whose reason this shares).
        /// </summary>
        public void SelectLineAtBackingWord(TypeBeatHitObject line, int index)
        {
            pendingBacking = (line, index);
            SelectedTrack.Value = LyricTrack.Backing;
            SelectedLine.Value = line;
        }

        private void clearBackingSelection()
        {
            SelectedBackingIndices.Clear();
            SelectedBackingIndex.Value = -1;
        }

        /// <summary>The backing half of <see cref="ResetUnitSelectionFor"/>: the old line's backing words go, a held one for <paramref name="active"/> is selected.</summary>
        private void resetBackingSelectionFor(TypeBeatHitObject? active)
        {
            clearBackingSelection();

            if (pendingBacking is (TypeBeatHitObject line, int index) && line == active && index >= 0 && index < line.Line.BackingVocals.Count)
                SelectBackingWord(index);

            pendingBacking = null;
        }
    }
}
