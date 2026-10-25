// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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

        /// <summary>The selected backing word of the active line (-1 = none). Only meaningful on the backing track.</summary>
        public readonly BindableInt SelectedBackingIndex = new BindableInt(-1);

        /// <summary>A backing word waiting for its line to become active, as <see cref="pendingUnit"/> is for a typed word.</summary>
        private (TypeBeatHitObject line, int index)? pendingBacking;

        /// <summary>Whether the word actions address the backing track.</summary>
        public bool BackingTrackSelected => SelectedTrack.Value == LyricTrack.Backing;

        /// <summary>Picks the backing track with no backing word selected (a click in its empty space).</summary>
        public void SelectBackingTrack()
        {
            ClearUnitSelection();
            SelectedBackingIndex.Value = -1;
            SelectedTrack.Value = LyricTrack.Backing;
        }

        /// <summary>Picks the main track, dropping any backing word selection.</summary>
        public void SelectMainTrack()
        {
            SelectedBackingIndex.Value = -1;
            SelectedTrack.Value = LyricTrack.Main;
        }

        /// <summary>Selects backing word <paramref name="index"/> of the active line, on the backing track.</summary>
        public void SelectBackingWord(int index)
        {
            ClearUnitSelection();
            SelectedBackingIndex.Value = index;
            SelectedTrack.Value = LyricTrack.Backing;
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

        /// <summary>The backing half of <see cref="ResetUnitSelectionFor"/>: the old line's backing word goes, a held one for <paramref name="active"/> is selected.</summary>
        private void resetBackingSelectionFor(TypeBeatHitObject? active)
        {
            SelectedBackingIndex.Value = -1;

            if (pendingBacking is (TypeBeatHitObject line, int index) && line == active && index >= 0 && index < line.Line.BackingVocals.Count)
                SelectBackingWord(index);

            pendingBacking = null;
        }
    }
}
