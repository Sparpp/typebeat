// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// The BACKING VOCAL edits (backlog 445). A line's backing words live inside its window, in time
    /// order, and every edit here keeps them there: a word is never moved before its line's start or
    /// past its end, and never past the START of a neighbouring backing word (so the order the words
    /// are listed in, which is the order their default anchors spread them across the line, cannot
    /// change under a drag). Each edit is one undo step, or part of the caller's drag transaction.
    /// </summary>
    public static partial class TypeBeatEditorOperations
    {
        /// <summary>
        /// Sets a line's backing vocals from TEXT: one word per whitespace-separated token. The first
        /// words keep the timing and placement of the words they replace, in order; words added beyond
        /// the old count are spread after the last kept one across the rest of the line's sung span (the
        /// whole sung span for a line that had none), and words removed from the end are dropped. Empty
        /// text removes them all. Returns whether anything changed.
        /// </summary>
        public static bool SetBackingVocalText(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, string? text)
        {
            var line = hitObject.Line;
            string[] tokens = BackingVocals.Words(text);
            var old = line.BackingVocals;

            var words = new List<BackingVocal>(tokens.Length);

            for (int i = 0; i < Math.Min(tokens.Length, old.Count); i++)
                words.Add(old[i] with { Text = tokens[i] });

            if (tokens.Length > old.Count)
            {
                var added = tokens.Skip(old.Count).ToArray();
                double from = words.Count > 0 ? Math.Max(words[^1].EndTime, line.StartTime) : line.StartTime;
                double to = Math.Max(line.SingEndTime, from);

                // No room left in the sung span: use the rest of the window, and failing that stack the
                // new words at its end with the minimum span each, for the mapper to drag apart.
                if (to - from < BackingVocals.MIN_SPAN_MS * added.Length)
                    to = Math.Max(line.EndTime, from);

                if (to - from < BackingVocals.MIN_SPAN_MS * added.Length)
                    from = Math.Max(line.StartTime, to - BackingVocals.MIN_SPAN_MS * added.Length);

                words.AddRange(BackingVocals.Spread(added, from, to));
            }

            var fitted = BackingVocals.Fit(words, line.StartTime, line.EndTime);

            if (fitted.SequenceEqual(old))
                return false;

            editorBeatmap.BeginChange();
            setLine(editorBeatmap, hitObject, withBacking(line, fitted));
            editorBeatmap.EndChange();
            return true;
        }

        /// <summary>
        /// Re-times backing word <paramref name="index"/> to [<paramref name="newStart"/>, <paramref name="newEnd"/>]:
        /// the start is kept inside the line and between the starts of the words on either side, the end
        /// inside the line and at least <see cref="BackingVocals.MIN_SPAN_MS"/> after the start.
        /// </summary>
        public static void SetBackingVocalTiming(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index, double newStart, double newEnd)
        {
            var line = hitObject.Line;

            if (index < 0 || index >= line.BackingVocals.Count)
                return;

            var (floor, ceiling) = backingStartRange(line, index);
            double start = Math.Clamp(newStart, floor, ceiling);
            double end = Math.Clamp(newEnd, Math.Min(start + BackingVocals.MIN_SPAN_MS, line.EndTime), line.EndTime);

            replaceBacking(editorBeatmap, hitObject, index, line.BackingVocals[index] with { StartTime = start, EndTime = Math.Max(start, end) });
        }

        /// <summary>
        /// Moves backing word <paramref name="index"/> rigidly so it starts at <paramref name="newStart"/>,
        /// keeping its length where the line has room for it (see <see cref="SetBackingVocalTiming"/> for the walls).
        /// </summary>
        public static void MoveBackingVocal(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index, double newStart)
        {
            var line = hitObject.Line;

            if (index < 0 || index >= line.BackingVocals.Count)
                return;

            var word = line.BackingVocals[index];
            double length = Math.Max(BackingVocals.MIN_SPAN_MS, word.EndTime - word.StartTime);
            var (floor, ceiling) = backingStartRange(line, index);
            double start = Math.Clamp(Math.Min(newStart, line.EndTime - length), floor, ceiling);

            replaceBacking(editorBeatmap, hitObject, index, word with { StartTime = start, EndTime = Math.Min(line.EndTime, start + length) });
        }

        /// <summary>
        /// Places backing word <paramref name="index"/>'s pop-up point across the line: a fraction of the
        /// line's width (0 its left edge, 1 its right edge), or null to put it back on its default place.
        /// </summary>
        public static void SetBackingVocalAnchor(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index, double? anchor)
        {
            var line = hitObject.Line;

            if (index < 0 || index >= line.BackingVocals.Count)
                return;

            replaceBacking(editorBeatmap, hitObject, index, line.BackingVocals[index] with { Anchor = anchor is double a ? BackingVocals.RoundAnchor(a) : null });
        }

        /// <summary>Removes backing word <paramref name="index"/>.</summary>
        public static void RemoveBackingVocal(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index)
        {
            var line = hitObject.Line;

            if (index < 0 || index >= line.BackingVocals.Count)
                return;

            editorBeatmap.BeginChange();
            setLine(editorBeatmap, hitObject, withBacking(line, line.BackingVocals.Where((_, i) => i != index).ToArray()));
            editorBeatmap.EndChange();
        }

        /// <summary>The backing vocals of a line as the text the editor's backing field shows: the words joined by single spaces.</summary>
        public static string BackingVocalText(LyricLine line) => string.Join(' ', line.BackingVocals.Select(w => w.Text));

        /// <summary>Where backing word <paramref name="index"/> may start: inside the line, and between its neighbours' starts.</summary>
        private static (double Floor, double Ceiling) backingStartRange(LyricLine line, int index)
        {
            var words = line.BackingVocals;
            double floor = index > 0 ? Math.Max(line.StartTime, words[index - 1].StartTime) : line.StartTime;
            double ceiling = index + 1 < words.Count ? Math.Min(line.EndTime, words[index + 1].StartTime) : line.EndTime;
            return (floor, Math.Max(floor, ceiling));
        }

        private static void replaceBacking(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index, BackingVocal word)
        {
            var line = hitObject.Line;

            if (line.BackingVocals[index] == word)
                return;

            var words = line.BackingVocals.ToArray();
            words[index] = word;

            editorBeatmap.BeginChange();
            setLine(editorBeatmap, hitObject, withBacking(line, words));
            editorBeatmap.EndChange();
        }

        private static LyricLine withBacking(LyricLine line, IReadOnlyList<BackingVocal> words) => BackingVocals.With(line, words);
    }
}
