// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Edit
{
    /// <summary>
    /// Tap timing for the BACKING track (backlog 501): the editor's "Time" button with the backing
    /// track selected times the backing words instead of the typed ones. The same recording surface and
    /// session are used (<see cref="TapTarget.Backing"/> marks the slots); only the commit differs, and
    /// it lives here.
    ///
    /// <para>One slot per backing ENTRY, so a subdivided backing word (backlog 452) is one tap per
    /// piece, exactly as a subdivided typed word is one tap per syllable. A tap is the entry's start.
    /// Its end: the next piece's start when the next entry is a piece of the same word (pieces touch by
    /// definition); else the key-up when the tap was held (<see cref="TapTimingSession.HOLD_THRESHOLD_MS"/>);
    /// else the next backing word of the same line's start, but no later than the word's default width
    /// at its line's own sung pace, so an ad-lib on its own does not last until the next one.</para>
    ///
    /// <para>The typed words and the line boundaries are never touched, and the words stay in their
    /// line: each line's result goes through <see cref="BackingVocals.Fit(IReadOnlyList{BackingVocal}?, double, double)"/>,
    /// so a tap outside the line's window lands on its edge. Finishing early keeps every untapped word
    /// that still starts after the last tap where it was, and moves the rest on after it at the pass's
    /// own word rhythm, each keeping its length, so the words stay in sung order.</para>
    /// </summary>
    public static class BackingTapTiming
    {
        /// <summary>
        /// The backing entries of lines <paramref name="firstLine"/> to <paramref name="lastLine"/> in
        /// sheet order, from entry <paramref name="firstIndex"/> of the first line to entry
        /// <paramref name="lastIndex"/> of the last. The range is widened to whole words, so a pass
        /// never times half of a subdivided word.
        /// </summary>
        public static List<TapTarget> BuildQueue(IReadOnlyList<LyricLine> lines, int firstLine, int lastLine, int firstIndex = 0, int lastIndex = int.MaxValue)
        {
            var queue = new List<TapTarget>();

            if (lines.Count == 0)
                return queue;

            firstLine = Math.Clamp(firstLine, 0, lines.Count - 1);
            lastLine = Math.Clamp(lastLine, 0, lines.Count - 1);

            for (int l = firstLine; l <= lastLine; l++)
            {
                var words = lines[l].BackingVocals;

                if (words.Count == 0)
                    continue;

                int from = l == firstLine ? BackingVocals.RunStart(words, Math.Clamp(firstIndex, 0, words.Count - 1)) : 0;
                int to = l == lastLine ? BackingVocals.RunEnd(words, Math.Clamp(lastIndex, 0, words.Count - 1)) : words.Count - 1;

                for (int i = from; i <= to; i++)
                    queue.Add(new TapTarget(l, i, Backing: true));
            }

            return queue;
        }

        /// <summary>
        /// The complete new sheet: <paramref name="lines"/> with the queued backing entries retimed by
        /// <paramref name="taps"/> (and <paramref name="releases"/>, the key-ups of held taps). Pure;
        /// a line the queue does not reach is handed back as the same object.
        /// </summary>
        public static IReadOnlyList<LyricLine> Build(IReadOnlyList<LyricLine> lines, IReadOnlyList<TapTarget> queue, IReadOnlyList<double> taps,
                                                     IReadOnlyList<double?>? releases = null)
        {
            int n = queue.Count;
            int tapped = Math.Min(taps.Count, n);

            if (lines.Count == 0 || tapped == 0)
                return lines.ToList();

            var words = lines.Select(l => l.BackingVocals.ToArray()).ToArray();
            BackingVocal entryOf(int i) => words[queue[i].LineIndex][queue[i].UnitIndex];

            // A piece of the same word as the entry before it in the queue.
            bool continues(int i) => i > 0 && entryOf(i).Joined
                                           && queue[i - 1].LineIndex == queue[i].LineIndex
                                           && queue[i - 1].UnitIndex == queue[i].UnitIndex - 1;

            double pace = wordPace(queue, taps, tapped, continues);

            double[] start = new double[n];
            double lastTap = double.NegativeInfinity;

            for (int i = 0; i < n; i++)
            {
                var word = entryOf(i);

                if (i < tapped)
                    start[i] = Math.Max(0, taps[i]);
                else if (word.StartTime > lastTap)
                    start[i] = word.StartTime;
                else if (continues(i))
                    start[i] = start[i - 1] + (entryOf(i - 1).EndTime - entryOf(i - 1).StartTime);
                else
                    start[i] = lastTap + pace;

                lastTap = start[i];
            }

            double[] end = new double[n];

            for (int i = 0; i < n; i++)
            {
                var word = entryOf(i);
                bool sameLineNext = i + 1 < n && queue[i + 1].LineIndex == queue[i].LineIndex;

                if (i + 1 < n && continues(i + 1))
                    end[i] = start[i + 1];
                else if (i >= tapped)
                    end[i] = start[i] + (word.EndTime - word.StartTime);
                else if (releases != null && i < releases.Count && releases[i] is double release)
                    end[i] = release;
                else
                {
                    double width = TapTimingBuilder.DefaultWordDuration(word.Text, sungCharPace(lines[queue[i].LineIndex]));
                    end[i] = Math.Min(start[i] + width, sameLineNext ? start[i + 1] : double.MaxValue);
                }

                end[i] = Math.Max(end[i], start[i]);
            }

            var touched = new bool[lines.Count];

            for (int i = 0; i < n; i++)
            {
                var t = queue[i];
                words[t.LineIndex][t.UnitIndex] = entryOf(i) with { StartTime = start[i], EndTime = end[i] };
                touched[t.LineIndex] = true;
            }

            var result = new List<LyricLine>(lines.Count);

            for (int l = 0; l < lines.Count; l++)
                result.Add(touched[l] ? BackingVocals.With(lines[l], words[l]) : lines[l]);

            return result;
        }

        /// <summary>
        /// Mean gap between the WORD-start taps of the pass (a piece's tap times part of a word, not a
        /// word), which is what an untapped word left behind is moved on at.
        /// </summary>
        private static double wordPace(IReadOnlyList<TapTarget> queue, IReadOnlyList<double> taps, int tapped, Func<int, bool> continues)
        {
            double first = double.NaN;
            double last = double.NaN;
            int count = 0;

            for (int i = 0; i < tapped; i++)
            {
                if (continues(i))
                    continue;

                if (count == 0)
                    first = taps[i];

                last = taps[i];
                count++;
            }

            if (count < 2)
                return TapTimingBuilder.DEFAULT_WORD_MS;

            double mean = (last - first) / (count - 1);
            return mean >= TypeBeatEditorOperations.MIN_SPAN_MS ? mean : TapTimingBuilder.DEFAULT_WORD_MS;
        }

        /// <summary>
        /// Milliseconds per typeable character of a line's TYPED words: their sung span over their
        /// characters. Backing words are sung over the line, so its pace is the best guess at theirs;
        /// <see cref="TapTimingBuilder.DEFAULT_CHAR_MS"/> when the line has nothing to measure.
        /// </summary>
        private static double sungCharPace(LyricLine line)
        {
            if (line.Units.Count == 0)
                return TapTimingBuilder.DEFAULT_CHAR_MS;

            double span = line.Units[^1].EndTime - line.Units[0].StartTime;
            double chars = line.Units.Sum(u => Math.Max(1, Typeability.TypeableCount(u.Text)));

            return span > 0 ? span / chars : TapTimingBuilder.DEFAULT_CHAR_MS;
        }
    }
}
