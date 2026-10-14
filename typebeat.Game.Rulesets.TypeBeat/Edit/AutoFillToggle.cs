// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The editor's "auto-fill" button. In order: a line box highlight marks the words it touches (or,
    /// inside one word, picks those letters); else the word selection is marked; else the auto-fill
    /// typing mode toggles. Marking clears the targets when they are all marked already.
    /// The highlight edits its own line, which need not be the active one.
    /// </summary>
    public static class AutoFillToggle
    {
        /// <summary>The (first, last) words of <paramref name="text"/> the highlight touches, or null.</summary>
        public static (int First, int Last)? WordsInRange(string text, int start, int length)
        {
            int? first = null, last = null;
            int word = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == ' ')
                {
                    word++;
                    continue;
                }

                if (i >= start && i < start + length)
                {
                    first ??= word;
                    last = word;
                }
            }

            return first == null ? null : (first.Value, last!.Value);
        }

        /// <summary>
        /// When the highlight covers some but not all characters of exactly one word, that word and the
        /// highlighted character indices into its stored text (pipes and tick marks skipped); otherwise null.
        /// </summary>
        public static (int Word, int[] Letters)? LettersInWord(string boxText, int start, int length, bool[]? tickStars = null)
        {
            int word = 0, c = 0;
            int? pickedWord = null;
            int wordLength = 0;
            var letters = new List<int>();

            for (int i = 0; i <= boxText.Length; i++)
            {
                if (i == boxText.Length || boxText[i] == ' ')
                {
                    if (word == pickedWord)
                        wordLength = c;

                    word++;
                    c = 0;
                    continue;
                }

                if (boxText[i] == Typeability.SPLIT_MARKER || (tickStars != null && i < tickStars.Length && tickStars[i]))
                    continue;

                if (i >= start && i < start + length)
                {
                    if (pickedWord != null && pickedWord != word)
                        return null;

                    pickedWord = word;
                    letters.Add(c);
                }

                c++;
            }

            if (pickedWord == null || letters.Count >= wordLength)
                return null;

            return (pickedWord.Value, letters.ToArray());
        }

        /// <summary><paramref name="targets"/> removed from <paramref name="current"/> when all are in it, else added. Ascending.</summary>
        public static int[] TogglePicks(IReadOnlyList<int> current, IReadOnlyList<int> targets)
        {
            var result = targets.All(current.Contains) ? current.Except(targets) : current.Union(targets);
            return result.Distinct().OrderBy(i => i).ToArray();
        }

        /// <summary>Applies the button. <paramref name="line"/> is the active line (null when none); refused with nothing to act on.</summary>
        public static void Apply(EditorBeatmap editorBeatmap, LyricEditState state, TypeBeatHitObject? line)
        {
            var highlighted = UsableHighlight(editorBeatmap, state);
            state.TextWordSelection = null;

            if (highlighted != null)
            {
                var highlightedLine = highlighted.Line;

                if (LettersInWord(highlighted.BoxText, highlighted.StartOffset, highlighted.Length, highlighted.TickStars) is (int word, int[] letters)
                    && word >= highlighted.FirstWord && word <= highlighted.LastWord)
                {
                    string text = highlightedLine.Line.Units[word].Text;
                    int[] picks = letters.Where(i => i < text.Length && TypedLetters.IsPickable(text[i])).ToArray();

                    // Only punctuation highlighted.
                    if (picks.Length == 0)
                    {
                        state.Refuse();
                        return;
                    }

                    // Every letter of the word ("go" of "go,") toggles the word instead.
                    if (picks.Length < text.Count(TypedLetters.IsPickable))
                    {
                        applyLetterPick(editorBeatmap, state, highlightedLine, word, picks);
                        return;
                    }
                }

                int[] highlightedWords = Enumerable.Range(highlighted.FirstWord, highlighted.LastWord - highlighted.FirstWord + 1).ToArray();
                bool allMarked = highlightedWords.All(i => highlightedLine.Line.Units[i].Objects.AutoFill);
                TypeBeatEditorOperations.SetAutoFill(editorBeatmap, highlightedLine, highlightedWords, !allMarked);
                return;
            }

            if (line == null)
            {
                state.Refuse();
                return;
            }

            int[] targets = state.SelectedUnitsInOrder(TypeBeatEditorOperations.WordCount(line.Line));

            if (targets.Length == 0)
            {
                state.AutoFillTyping.Toggle();
                return;
            }

            bool allOn = targets.All(i => i < line.Line.Units.Count && line.Line.Units[i].Objects.AutoFill);
            TypeBeatEditorOperations.SetAutoFill(editorBeatmap, line, targets, !allOn);
        }

        /// <summary>Adds or removes <paramref name="targets"/> as the word's typed letters; refused when nothing changes.</summary>
        private static void applyLetterPick(EditorBeatmap editorBeatmap, LyricEditState state, TypeBeatHitObject line, int word, int[] targets)
        {
            var unit = line.Line.Units[word];
            var current = unit.Objects.AutoFill ? unit.Objects.TypedLetters : Array.Empty<int>();

            if (!TypeBeatEditorOperations.SetTypedLetters(editorBeatmap, line, word, TogglePicks(current, targets)))
                state.Refuse();
        }

        /// <summary>The stored highlight while its line is in the map and its words are spelled as highlighted, else null. Allocation-free.</summary>
        public static TextWordSelection? UsableHighlight(EditorBeatmap editorBeatmap, LyricEditState state)
        {
            if (state.TextWordSelection is not TextWordSelection selection || !editorBeatmap.HitObjects.Contains(selection.Line))
                return null;

            var units = selection.Line.Line.Units;

            if (selection.LastWord >= units.Count)
                return null;

            for (int i = selection.FirstWord; i <= selection.LastWord; i++)
            {
                if (units[i].Text != selection.Words[i - selection.FirstWord])
                    return null;
            }

            return selection;
        }
    }
}
