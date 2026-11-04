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
    ///
    /// <para>The indices are entries of the line's flat list, in which a subdivided word (backlog 452) is
    /// several consecutive entries (see <see cref="BackingVocals"/>). Edits that address a WORD (timing,
    /// moving, removing, copying) take the whole run whichever piece they are given; only an anchor and
    /// a divider belong to one piece.</para>
    /// </summary>
    public static partial class TypeBeatEditorOperations
    {
        /// <summary>
        /// Sets a line's backing vocals from TEXT: one word per whitespace-separated token, "h|a" being
        /// a word in two pieces. The first words keep the timing and placement of the words they replace,
        /// in order; words added beyond the old count are spread after the last kept one across the rest
        /// of the line's sung span (the whole sung span for a line that had none), and words removed from
        /// the end are dropped. Empty text removes them all. Returns whether anything changed.
        /// </summary>
        public static bool SetBackingVocalText(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, string? text)
        {
            var line = hitObject.Line;
            string[][] tokens = BackingVocals.Words(text).Select(BackingVocals.PiecesOf).Where(p => p.Length > 0).ToArray();
            var old = line.BackingVocals;
            int[] heads = Enumerable.Range(0, old.Count).Where(i => !old[i].Joined).ToArray();

            var words = new List<BackingVocal>(tokens.Length);

            for (int i = 0; i < Math.Min(tokens.Length, heads.Length); i++)
                words.AddRange(respell(old, heads[i], BackingVocals.RunEnd(old, heads[i]), tokens[i]));

            if (tokens.Length > heads.Length)
            {
                var added = tokens.Skip(heads.Length).ToArray();
                double from = words.Count > 0 ? Math.Max(words[^1].EndTime, line.StartTime) : line.StartTime;
                double to = Math.Max(line.SingEndTime, from);

                // No room left in the sung span: use the rest of the window, and failing that stack the
                // new words at its end with the minimum span each, for the mapper to drag apart.
                if (to - from < BackingVocals.MIN_SPAN_MS * added.Length)
                    to = Math.Max(line.EndTime, from);

                if (to - from < BackingVocals.MIN_SPAN_MS * added.Length)
                    from = Math.Max(line.StartTime, to - BackingVocals.MIN_SPAN_MS * added.Length);

                var spread = BackingVocals.Spread(added.Select(p => string.Concat(p)).ToArray(), from, to);

                for (int i = 0; i < spread.Count; i++)
                    words.AddRange(respell(new[] { spread[i] }, 0, 0, added[i]));
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

            var words = line.BackingVocals.ToArray();
            int head = BackingVocals.RunStart(words, index);
            int tail = BackingVocals.RunEnd(words, index);
            var (floor, ceiling) = backingStartRange(line, head);

            // A subdivided word's edges stop at its own pieces: the dividers stay where they are.
            if (tail > head)
                ceiling = Math.Max(floor, words[head + 1].StartTime - BackingVocals.MIN_SPAN_MS);

            double start = Math.Clamp(newStart, floor, ceiling);
            double lastStart = tail > head ? words[tail].StartTime : start;
            double end = Math.Clamp(newEnd, Math.Min(lastStart + BackingVocals.MIN_SPAN_MS, line.EndTime), line.EndTime);

            words[head] = words[head] with { StartTime = start };
            words[tail] = words[tail] with { EndTime = Math.Max(lastStart, end) };

            commitBacking(editorBeatmap, hitObject, words);
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

            var words = line.BackingVocals.ToArray();
            int head = BackingVocals.RunStart(words, index);
            int tail = BackingVocals.RunEnd(words, index);
            double length = Math.Max(BackingVocals.MIN_SPAN_MS, words[tail].EndTime - words[head].StartTime);
            var (floor, ceiling) = backingStartRange(line, head);
            double start = Math.Clamp(Math.Min(newStart, line.EndTime - length), floor, ceiling);
            double delta = start - words[head].StartTime;

            for (int i = head; i <= tail; i++)
                words[i] = words[i] with { StartTime = words[i].StartTime + delta, EndTime = words[i].EndTime + delta };

            words[head] = words[head] with { StartTime = start };
            words[tail] = words[tail] with { EndTime = Math.Min(line.EndTime, start + length) };

            commitBacking(editorBeatmap, hitObject, words);
        }

        /// <summary>
        /// "subdivide" on a backing word (backlog 452): one piece of it is cut in two, the second half
        /// popping up at the cut with its own anchor on the default place. A caret inside a piece with
        /// the room for it cuts there, anything else bisects the widest piece, so pressing again keeps
        /// splitting. The letters are shared out by where the cut falls in the piece. False when no
        /// piece can be cut: one letter long, or too short to leave both halves
        /// <see cref="BackingVocals.MIN_SPAN_MS"/>. A cut never passes the start of the next word, which
        /// would change the order the words are listed in.
        /// </summary>
        public static bool SubdivideBackingVocal(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index, double? caretTime)
        {
            var line = hitObject.Line;

            if (index < 0 || index >= line.BackingVocals.Count)
                return false;

            var words = line.BackingVocals.ToList();
            int head = BackingVocals.RunStart(words, index);
            int tail = BackingVocals.RunEnd(words, index);
            double nextStart = tail + 1 < words.Count ? words[tail + 1].StartTime : double.PositiveInfinity;

            double latestCut(int piece) => piece == tail ? Math.Min(words[piece].EndTime, nextStart) : words[piece].EndTime;

            int target = -1;
            double cut = 0;

            if (caretTime is double caret)
            {
                for (int i = head; i <= tail && target < 0; i++)
                {
                    if (words[i].Text.Length >= 2 && caret - words[i].StartTime >= BackingVocals.MIN_SPAN_MS
                                                  && words[i].EndTime - caret >= BackingVocals.MIN_SPAN_MS && caret <= latestCut(i))
                    {
                        target = i;
                        cut = caret;
                    }
                }
            }

            if (target < 0)
            {
                double widest = 0;

                for (int i = head; i <= tail; i++)
                {
                    double width = latestCut(i) - words[i].StartTime;

                    if (words[i].Text.Length >= 2 && width >= BackingVocals.MIN_SPAN_MS * 2 && width > widest)
                    {
                        widest = width;
                        target = i;
                        cut = words[i].StartTime + width / 2;
                    }
                }
            }

            if (target < 0)
                return false;

            var piece = words[target];
            int letters = Math.Clamp((int)Math.Round(piece.Text.Length * (cut - piece.StartTime) / (piece.EndTime - piece.StartTime), MidpointRounding.AwayFromZero), 1, piece.Text.Length - 1);

            // Never between the two halves of one character.
            if (char.IsLowSurrogate(piece.Text[letters]))
                letters = letters + 1 < piece.Text.Length ? letters + 1 : letters - 1;

            if (letters <= 0)
                return false;

            words[target] = piece with { Text = piece.Text[..letters], EndTime = cut };
            words.Insert(target + 1, new BackingVocal(piece.Text[letters..], cut, piece.EndTime, null, true));

            commitBacking(editorBeatmap, hitObject, words);
            return true;
        }

        /// <summary>
        /// "unsubdivide" on a backing word (backlog 452): the inverse press, merging its narrowest
        /// adjacent pair of pieces, so a word subdivided N times comes back after N presses. False for a
        /// word in one piece.
        /// </summary>
        public static bool UnsubdivideBackingVocal(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index)
        {
            var words = hitObject.Line.BackingVocals;

            if (index < 0 || index >= words.Count)
                return false;

            int head = BackingVocals.RunStart(words, index);
            int tail = BackingVocals.RunEnd(words, index);

            if (tail == head)
                return false;

            int narrowest = head + 1;

            for (int i = head + 2; i <= tail; i++)
            {
                if (words[i].EndTime - words[i - 1].StartTime < words[narrowest].EndTime - words[narrowest - 1].StartTime)
                    narrowest = i;
            }

            RemoveBackingSubdivision(editorBeatmap, hitObject, narrowest);
            return true;
        }

        /// <summary>
        /// Moves the divider in front of piece <paramref name="index"/> (the dotted line's drag), which
        /// is when that piece pops up: the two pieces either side keep at least
        /// <see cref="BackingVocals.MIN_SPAN_MS"/>, and it never passes the start of the next word.
        /// </summary>
        public static void SetBackingSubdivision(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index, double time)
        {
            var words = hitObject.Line.BackingVocals.ToArray();

            if (index <= 0 || index >= words.Length || !words[index].Joined)
                return;

            double low = words[index - 1].StartTime + BackingVocals.MIN_SPAN_MS;
            double high = words[index].EndTime - BackingVocals.MIN_SPAN_MS;

            if (index + 1 < words.Length)
                high = words[index + 1].Joined ? words[index + 1].StartTime - BackingVocals.MIN_SPAN_MS : Math.Min(high, words[index + 1].StartTime);

            if (high < low)
                return;

            double cut = Math.Clamp(time, low, high);

            words[index - 1] = words[index - 1] with { EndTime = cut };
            words[index] = words[index] with { StartTime = cut };

            commitBacking(editorBeatmap, hitObject, words);
        }

        /// <summary>Takes the divider in front of piece <paramref name="index"/> back out: that piece's letters and time go to the piece before it, whose anchor stays.</summary>
        public static void RemoveBackingSubdivision(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index)
        {
            var words = hitObject.Line.BackingVocals.ToList();

            if (index <= 0 || index >= words.Count || !words[index].Joined)
                return;

            words[index - 1] = words[index - 1] with { Text = words[index - 1].Text + words[index].Text, EndTime = words[index].EndTime };
            words.RemoveAt(index);

            commitBacking(editorBeatmap, hitObject, words);
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

            // A new word cannot land between the pieces of a subdivided one: it joins at its last piece.
            for (int i = 1; i < words.Count; i++)
            {
                if (words[i].Joined && start > words[i - 1].StartTime && start < words[i].StartTime)
                    start = words[BackingVocals.RunEnd(words, i)].StartTime;
            }

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
            => RemoveBackingVocals(editorBeatmap, hitObject, new[] { index });

        /// <summary>Removes the backing words at <paramref name="indices"/>, as one undo step.</summary>
        public static void RemoveBackingVocals(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, IEnumerable<int> indices)
        {
            var line = hitObject.Line;
            var gone = BackingVocals.ExpandRuns(line.BackingVocals, indices).ToHashSet();

            if (gone.Count == 0)
                return;

            editorBeatmap.BeginChange();
            setLine(editorBeatmap, hitObject, withBacking(line, line.BackingVocals.Where((_, i) => !gone.Contains(i)).ToArray()));
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Moves the backing words at <paramref name="indices"/> RIGIDLY by <paramref name="delta"/> from
        /// where a group drag found them (<paramref name="originalStarts"/>, <paramref name="originalEnds"/>):
        /// every one by the same distance, the distance held back so none leaves the line or passes the
        /// start of a backing word outside the group (so the order, and the default anchors, hold).
        /// </summary>
        public static void MoveBackingVocals(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, IReadOnlyList<int> indices,
                                             IReadOnlyList<double> originalStarts, IReadOnlyList<double> originalEnds, double delta)
        {
            var line = hitObject.Line;
            var words = line.BackingVocals.ToArray();
            var group = new HashSet<int>(indices);

            if (indices.Count == 0 || indices.Any(i => i < 0 || i >= words.Length))
                return;

            double low = double.NegativeInfinity, high = double.PositiveInfinity;

            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                double length = Math.Max(BackingVocals.MIN_SPAN_MS, originalEnds[k] - originalStarts[k]);

                double floor = line.StartTime;
                double ceiling = line.EndTime - Math.Min(length, line.EndTime - line.StartTime);

                for (int j = i - 1; j >= 0; j--)
                {
                    if (group.Contains(j))
                        continue;

                    floor = Math.Max(floor, words[j].StartTime);
                    break;
                }

                for (int j = i + 1; j < words.Length; j++)
                {
                    if (group.Contains(j))
                        continue;

                    ceiling = Math.Min(ceiling, words[j].StartTime);
                    break;
                }

                low = Math.Max(low, floor - originalStarts[k]);
                high = Math.Min(high, ceiling - originalStarts[k]);
            }

            double applied = Math.Clamp(delta, Math.Min(low, 0), Math.Max(high, 0));

            for (int k = 0; k < indices.Count; k++)
            {
                double start = originalStarts[k] + applied;
                words[indices[k]] = words[indices[k]] with { StartTime = start, EndTime = Math.Min(line.EndTime, start + Math.Max(0, originalEnds[k] - originalStarts[k])) };
            }

            if (words.SequenceEqual(line.BackingVocals))
                return;

            editorBeatmap.BeginChange();
            setLine(editorBeatmap, hitObject, withBacking(line, words));
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Copies the backing words at <paramref name="indices"/>: each word whole (text, span, placed
        /// anchor) timed from the FIRST one's start, and that start's own offset into its line, so a
        /// paste can put the run down at the same place in another line. Null for no word.
        /// </summary>
        public static LyricTimingClipboard.BackingTimingsPayload? CopyBackingVocals(TypeBeatHitObject hitObject, IEnumerable<int> indices)
        {
            var line = hitObject.Line;
            var picked = BackingVocals.ExpandRuns(line.BackingVocals, indices).Select(i => line.BackingVocals[i]).ToList();

            if (picked.Count == 0)
                return null;

            double first = picked[0].StartTime;

            return new LyricTimingClipboard.BackingTimingsPayload
            {
                LineOffset = first - line.StartTime,
                Words = picked.Select(w => new LyricTimingClipboard.BackingSpan
                {
                    Text = w.Text,
                    Start = w.StartTime - first,
                    End = w.EndTime - first,
                    Anchor = w.Anchor,
                    Joined = w.Joined,
                }).ToList(),
            };
        }

        /// <summary>
        /// Pastes copied backing words into <paramref name="hitObject"/>'s line, beside the ones it has:
        /// the run starts at <paramref name="playhead"/> when that is inside the line, else at the
        /// offset into the line it was copied from, and keeps its spacing, lengths and placed anchors.
        /// A word that would START past the line's end does not fit, and then nothing is pasted (null);
        /// otherwise the pasted words' indices among the line's backing words, ascending.
        /// </summary>
        public static int[]? PasteBackingVocals(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, double playhead, LyricTimingClipboard.BackingTimingsPayload payload)
        {
            var line = hitObject.Line;
            var copied = payload.Words.Select(w => (Word: w, Text: BackingVocals.CleanText(w.Text))).Where(w => w.Text.Length > 0).ToList();

            if (copied.Count == 0)
                return Array.Empty<int>();

            double origin = playhead >= line.StartTime && playhead < line.EndTime ? playhead : line.StartTime + payload.LineOffset;

            var pasted = copied.Select(c => new BackingVocal(c.Text, origin + c.Word.Start, origin + Math.Max(c.Word.Start, c.Word.End),
                c.Word.Anchor is double anchor ? BackingVocals.RoundAnchor(anchor) : null, c.Word.Joined)).ToArray();

            if (pasted.Any(w => w.StartTime < line.StartTime || w.StartTime >= line.EndTime))
                return null;

            // The pasted words go AFTER any already there at the same moment (Fit is stable), so each
            // is found again as the last word with its own text and start.
            var fitted = BackingVocals.Fit(line.BackingVocals.Concat(pasted).ToArray(), line.StartTime, line.EndTime);
            var at = new List<int>();

            foreach (var word in pasted)
            {
                for (int i = fitted.Count - 1; i >= 0; i--)
                {
                    if (fitted[i].Text == word.Text && fitted[i].StartTime == word.StartTime && !at.Contains(i))
                    {
                        at.Add(i);
                        break;
                    }
                }
            }

            editorBeatmap.BeginChange();
            setLine(editorBeatmap, hitObject, withBacking(line, fitted));
            editorBeatmap.EndChange();

            return at.OrderBy(i => i).ToArray();
        }

        /// <summary>The backing vocals of a line as the text the editor shows: the words joined by single spaces, a subdivided word's pieces by '|'.</summary>
        public static string BackingVocalText(LyricLine line) => BackingVocals.SpellOut(line.BackingVocals);

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

        /// <summary>
        /// Where the backing word whose first piece is <paramref name="head"/> may start, moved whole:
        /// inside the line, after the start of the word before it, and with its last piece no later than
        /// the start of the word after it.
        /// </summary>
        private static (double Floor, double Ceiling) backingStartRange(LyricLine line, int head)
        {
            var words = line.BackingVocals;
            int tail = BackingVocals.RunEnd(words, head);
            double floor = head > 0 ? Math.Max(line.StartTime, words[head - 1].StartTime) : line.StartTime;
            double ceiling = tail + 1 < words.Count ? Math.Min(line.EndTime, words[tail + 1].StartTime) : line.EndTime;
            return (floor, Math.Max(floor, ceiling - (words[tail].StartTime - words[head].StartTime)));
        }

        /// <summary>
        /// A word's pieces spelled anew over the span it already has: pieces that still exist keep their
        /// starts and anchors, the last one reaching the word's end, and pieces beyond the old count
        /// share out what the last old piece covered by their letters.
        /// </summary>
        private static IEnumerable<BackingVocal> respell(IReadOnlyList<BackingVocal> words, int head, int tail, string[] pieces)
        {
            int kept = Math.Min(tail - head + 1, pieces.Length);
            double end = words[tail].EndTime;
            double[] starts = new double[pieces.Length + 1];

            for (int i = 0; i < kept; i++)
                starts[i] = words[head + i].StartTime;

            int sharing = pieces.Skip(kept - 1).Sum(p => p.Length);

            for (int i = kept; i < pieces.Length; i++)
                starts[i] = starts[i - 1] + (end - starts[kept - 1]) * pieces[i - 1].Length / sharing;

            starts[pieces.Length] = end;

            for (int i = 0; i < pieces.Length; i++)
            {
                yield return new BackingVocal(pieces[i], starts[i], starts[i + 1], i < kept ? words[head + i].Anchor : null, i > 0);
            }
        }

        private static void commitBacking(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, IReadOnlyList<BackingVocal> words)
        {
            var line = hitObject.Line;

            if (words.SequenceEqual(line.BackingVocals))
                return;

            editorBeatmap.BeginChange();
            setLine(editorBeatmap, hitObject, withBacking(line, words));
            editorBeatmap.EndChange();
        }

        private static void replaceBacking(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int index, BackingVocal word)
        {
            var words = hitObject.Line.BackingVocals.ToArray();
            words[index] = word;

            commitBacking(editorBeatmap, hitObject, words);
        }

        private static LyricLine withBacking(LyricLine line, IReadOnlyList<BackingVocal> words) => BackingVocals.With(line, words);
    }
}
