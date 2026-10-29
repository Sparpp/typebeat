// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The editor's "hold" button: toggles a hold on the character after the line box's stored caret (on
    /// that caret's line), else on the playhead's character in the selected word or the word under the
    /// playhead. A press that changes nothing is refused.
    /// </summary>
    public static class HoldToggle
    {
        /// <summary>
        /// The (word, char) after the caret at <paramref name="index"/> in the box's pipe text, skipping pipes
        /// and tick marks. Null on a space or past the end.
        /// </summary>
        public static (int Word, int Char)? CharAt(string pipeText, int index, bool[]? tickStars = null)
        {
            if (index < 0)
                return null;

            int word = 0, c = 0;

            for (int i = 0; i < pipeText.Length; i++)
            {
                char ch = pipeText[i];

                if (ch == Typeability.SPLIT_MARKER || (tickStars != null && i < tickStars.Length && tickStars[i]))
                    continue;

                if (i >= index)
                    return ch == ' ' ? null : (word, c);

                if (ch == ' ')
                {
                    word++;
                    c = 0;
                }
                else
                    c++;
            }

            return null;
        }

        /// <summary>Applies the button. <paramref name="line"/> is the active line (null when none).</summary>
        public static void Apply(EditorBeatmap editorBeatmap, LyricEditState state, TypeBeatHitObject? line, double playhead)
        {
            if (UsableCaret(editorBeatmap, state) is TextCaretPosition caret)
            {
                if (!TypeBeatEditorOperations.ToggleHold(editorBeatmap, caret.Line, caret.Word, caret.Char, playhead))
                    state.Refuse();

                return;
            }

            if (line == null)
            {
                state.Refuse();
                return;
            }

            var units = line.Line.Units;
            int word = state.SelectedUnitIndex.Value;

            if (word < 0 || word >= units.Count)
                word = wordAt(line, playhead);

            if (word < 0 || !TypeBeatEditorOperations.ToggleHold(editorBeatmap, line, word, null, playhead))
                state.Refuse();
        }

        /// <summary>The stored caret while its line is in the map and its word is spelled as it was, else null. Allocation-free.</summary>
        public static TextCaretPosition? UsableCaret(EditorBeatmap editorBeatmap, LyricEditState state)
        {
            if (state.TextCaret is not TextCaretPosition caret || !editorBeatmap.HitObjects.Contains(caret.Line))
                return null;

            var units = caret.Line.Line.Units;
            return caret.Word < units.Count && units[caret.Word].Text == caret.WordText ? caret : null;
        }

        /// <summary>The word whose span holds <paramref name="time"/>, or -1.</summary>
        private static int wordAt(TypeBeatHitObject line, double time)
        {
            var units = line.Line.Units;

            for (int i = 0; i < units.Count; i++)
            {
                if (time >= units[i].StartTime && time <= units[i].EndTime)
                    return i;
            }

            return -1;
        }
    }
}
