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

        /// <summary>How long a backing word added by "add word" is, where its line has the room.</summary>
        public const double NEW_BACKING_WORD_MS = 400;

        /// <summary>
        /// "add word" on the backing track: a new backing word (<see cref="NEW_WORD_TEXT"/>, renamed in
        /// the line's text like any new word) starting at <paramref name="time"/> when that falls inside
        /// the line, else right after the line's last backing word (at its start for the first one),
        /// <see cref="NEW_BACKING_WORD_MS"/> long where the line has the room. Returns the new word's
        /// index in the line's (start-ordered) backing words, or -1 when nothing was added.
        /// </summary>
        public static int AddBackingVocal(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, double time, string text = NEW_WORD_TEXT)
        {
            var line = hitObject.Line;
            string clean = BackingVocals.CleanText(text);

            if (clean.Length == 0 || clean.Contains(' ') || line.EndTime <= line.StartTime)
                return -1;

            var words = line.BackingVocals;
            double start = time >= line.StartTime && time < line.EndTime
                ? time
                : words.Count > 0 ? words[^1].EndTime : line.StartTime;

            start = Math.Clamp(start, line.StartTime, Math.Max(line.StartTime, line.EndTime - BackingVocals.MIN_SPAN_MS));
            double end = Math.Min(line.EndTime, start + NEW_BACKING_WORD_MS);

            // Placed AFTER any word that starts at the same moment (Fit's ordering is stable), so it
            // is the last of them, and found there again by its start.
            var fitted = BackingVocals.Fit(words.Append(new BackingVocal(clean, start, end)).ToArray(), line.StartTime, line.EndTime);
            int index = -1;

            for (int i = 0; i < fitted.Count; i++)
            {
                if (fitted[i].StartTime == start && fitted[i].Text == clean)
                    index = i;
            }

            editorBeatmap.BeginChange();
            setLine(editorBeatmap, hitObject, withBacking(line, fitted));
            editorBeatmap.EndChange();
            return index;
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

        /// <summary>
        /// How the line list shows a line's backing vocals: in round brackets after its text
        /// (" (yeah yeah)"), or nothing at all for a line without any.
        /// </summary>
        public static string BackingSuffix(LyricLine line) => line.BackingVocals.Count == 0 ? string.Empty : $" ({BackingVocalText(line)})";

        /// <summary>
        /// Splits a line row's edited text back into the line's own text and its backing vocals: a
        /// trailing round-bracketed group is the backing text (an import's convention, so typing
        /// "(oh oh)" after a line gives it backing vocals). Backing is null when the edit says nothing
        /// about them, and empty when it removed them (the line had some and no group is left).
        ///
        /// <para>A bracket is also a literal lyric mark (backlog 255), so a line whose own text ends in
        /// one and has no backing vocals keeps that group as its text; and a group with nothing in
        /// front of it is the line itself, not a backing part.</para>
        /// </summary>
        /// <param name="text">The row's committed text.</param>
        /// <param name="lineText">The line's own text as the row shows it (its pipe form), without the backing suffix.</param>
        /// <param name="lineHasBacking">Whether the line has backing vocals now (so the row showed them).</param>
        public static (string Main, string? Backing) SplitBackingSuffix(string text, string lineText, bool lineHasBacking)
        {
            string trimmed = text.TrimEnd();
            string? unchanged = lineHasBacking ? string.Empty : null;

            if (trimmed == lineText.TrimEnd() || !trimmed.EndsWith(')'))
                return (text, unchanged);

            int depth = 0;
            int open = -1;

            for (int i = trimmed.Length - 1; i >= 0; i--)
            {
                if (trimmed[i] == ')')
                    depth++;
                else if (trimmed[i] == '(' && --depth == 0)
                {
                    open = i;
                    break;
                }
            }

            if (open <= 0 || trimmed[..open].Trim().Length == 0)
                return (text, unchanged);

            if (!lineHasBacking && lineText.TrimEnd().EndsWith(')'))
                return (text, null);

            return (trimmed[..open].TrimEnd(), trimmed[(open + 1)..^1]);
        }

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
