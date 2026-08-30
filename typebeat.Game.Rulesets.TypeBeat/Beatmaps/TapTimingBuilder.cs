// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// One tap slot in a tap-timing queue: which line, which word inside it, and which SYLLABLE of
    /// that word. A word with no subdivisions contributes exactly one slot
    /// (<see cref="SyllableIndex"/> 0); a word carrying N <see cref="TimedUnit.SyllableBoundaries"/>
    /// contributes N+1 slots, so "remember me" with remember split three ways is four taps.
    /// Syllable 0 is the word's START; syllable s > 0 is the word's s'th subdivision boundary.
    /// </summary>
    public readonly record struct TapTarget(int LineIndex, int UnitIndex, int SyllableIndex = 0);

    /// <summary>
    /// The pure heart of tap timing (the editor's "Time" button): turns a recorded list of song
    /// times into a complete new <see cref="LyricLine"/> sheet, in one shot, with no mutation of
    /// anything along the way.
    ///
    /// <para>The recording surface never touches the beatmap: it only appends song times to a plain
    /// list. When the mapper finishes, <see cref="Build"/> is handed the CURRENT lines, the queue of
    /// word slots the session was timing, and the taps, and returns the whole sheet. The caller
    /// commits that through <see cref="TypeBeatEditorOperations.ReplaceLines"/>, so the entire pass
    /// is exactly ONE undo step and there are no mid-pass clamp fights: every clamp happens here,
    /// once, against the final numbers.</para>
    ///
    /// <para>Rules, all pinned by tests:</para>
    /// <list type="bullet">
    /// <item>A tap is a SYLLABLE start. The queue is contiguous in sheet order, so tap i lands on
    /// queue slot i. An undivided word is one slot, so its tap is its start, exactly as before; a
    /// subdivided word is one slot per syllable, where syllable 0 sets the word's start and each
    /// later syllable sets one of the word's subdivision boundary times.</item>
    /// <item>A word's END is the next word's start when that next word is in the SAME line (words in
    /// a phrase run together). A line's LAST word has no such next word, so it is given a DEFAULT
    /// WIDTH that scales with its character count (<see cref="DefaultWordDuration"/>) at the line's
    /// own measured per-character rate, so an instrumental gap after a line survives as a gap
    /// instead of being sung through, "everything" is held longer than "on", and the very last word
    /// of all is timed by the same rule. Whatever follows that word is pushed out far enough to
    /// leave it its default width at <see cref="DEFAULT_CHAR_MS"/> (see the ordering pass), because
    /// otherwise a squashed untimed sheet behind the pass collapses the word into an unreadable
    /// sliver; it grows past that, to the line's own cadence, only where the content behind already
    /// leaves the room, and the one thing never pushed is another TAP, which is the mapper's own
    /// word.</item>
    /// <item>A line's StartTime is its first word's start whenever that word was (re)timed; its
    /// SingEndTime is its last word's end; its EndTime is the next line's start (last line:
    /// SingEndTime + <see cref="TypeBeatEditorOperations.LAST_LINE_TAIL_MS"/>), preserving the
    /// format's EndTime_i == StartTime_(i+1) boundary invariant.</item>
    /// <item>FINISHING EARLY commits only what was tapped. Each remaining queued word keeps its
    /// existing time if that time still sits after the last tap; otherwise (the usual case on a
    /// fresh, untimed sheet) it is PACED on at the mean tapped word duration. Lines carrying paced
    /// words are flagged <see cref="LyricLine.Estimated"/>, which is exactly what that flag means:
    /// timing derived from hand stamps rather than acoustic evidence.</item>
    /// <item>COLLISIONS are resolved at commit time by one forward pass: everything is kept
    /// monotonically ordered with at least <see cref="TypeBeatEditorOperations.MIN_SPAN_MS"/>
    /// between consecutive word starts. Content BEFORE the queue is never moved (the first tap is
    /// clamped to sit after it); content AFTER the queue is pushed later only as far as ordering
    /// requires.</item>
    /// <item>Retimed words become <see cref="TimingSource.Explicit"/> hand timing; words the pass
    /// did not touch keep their source, confidence and subdivisions.</item>
    /// <item>SUBDIVISIONS follow the taps. A word whose every syllable slot got a tap has its
    /// boundaries SET to those taps (clamped to sit strictly inside the final word span, at least
    /// <see cref="TypeBeatEditorOperations.MIN_SYLLABLE_MS"/> apart). A retimed word whose syllables
    /// the taps could NOT cover drops its subdivisions instead: that is the mapper finishing early
    /// mid-word, seeking back into the middle of a word, or a word paced on in the untapped tail,
    /// and in every one of those the word moved wholesale so its old sub-word marks are meaningless.
    /// A word so narrow after collision clamping that its syllables cannot fit also drops them.</item>
    /// </list>
    /// </summary>
    public static class TapTimingBuilder
    {
        /// <summary>Word duration assumed when there is nothing to measure one from (single tap, empty line).</summary>
        public const double DEFAULT_WORD_MS = 400;

        /// <summary>
        /// Sung length assumed per TYPEABLE character when neither the line nor the pass offers a
        /// cadence to measure. <see cref="DEFAULT_WORD_MS"/> is exactly this times five, the mean
        /// length of an English word, so a five-letter word still gets the old flat default.
        /// </summary>
        public const double DEFAULT_CHAR_MS = DEFAULT_WORD_MS / 5;

        /// <summary>
        /// The contiguous run of word slots from (<paramref name="firstLine"/>,
        /// <paramref name="firstUnit"/>) to (<paramref name="lastLine"/>, <paramref name="lastUnit"/>)
        /// inclusive, in sheet order. Empty when the range is degenerate.
        /// </summary>
        public static List<TapTarget> BuildQueue(IReadOnlyList<LyricLine> lines, int firstLine, int firstUnit, int lastLine, int lastUnit)
        {
            var queue = new List<TapTarget>();

            if (lines.Count == 0)
                return queue;

            firstLine = Math.Clamp(firstLine, 0, lines.Count - 1);
            lastLine = Math.Clamp(lastLine, 0, lines.Count - 1);

            if (lastLine < firstLine)
                return queue;

            for (int l = firstLine; l <= lastLine; l++)
            {
                int from = l == firstLine ? Math.Max(0, firstUnit) : 0;
                int to = l == lastLine ? Math.Min(lastUnit, lines[l].Units.Count - 1) : lines[l].Units.Count - 1;

                for (int u = from; u <= to; u++)
                {
                    // One slot per syllable: an undivided word is still exactly one tap.
                    int syllables = SyllableCount(lines[l].Units[u]);

                    for (int s = 0; s < syllables; s++)
                        queue.Add(new TapTarget(l, u, s));
                }
            }

            return queue;
        }

        /// <summary>How many taps a word asks for: its subdivision count plus one.</summary>
        public static int SyllableCount(TimedUnit unit) => unit.SyllableBoundaries.Count + 1;

        /// <summary>
        /// The characters of <paramref name="text"/> that belong to syllable segment
        /// <paramref name="syllableIndex"/> of <paramref name="syllableCount"/>, under exactly the
        /// split the engine judges by: <see cref="Gameplay.TypingLine"/> spreads a word's k typeable
        /// chars evenly across the segments in index space, so typeable char j sits in segment
        /// floor(j * count / k). Non-typeable characters (punctuation) ride along with the typeable
        /// char before them. Concatenating every index reproduces <paramref name="text"/>, so the
        /// recording surface can show the mapper the exact char run each tap will drive.
        /// </summary>
        public static string SyllableTextOf(string text, int syllableIndex, int syllableCount)
        {
            if (syllableCount <= 1 || string.IsNullOrEmpty(text))
                return syllableIndex == 0 ? text : string.Empty;

            int k = 0;

            foreach (char c in text)
            {
                if (Typeability.IsCell(c))
                    k++;
            }

            if (k == 0)
                return syllableIndex == 0 ? text : string.Empty;

            var slice = new System.Text.StringBuilder();
            int j = 0;
            int segment = 0;

            foreach (char c in text)
            {
                if (Typeability.IsCell(c))
                {
                    segment = Math.Min((int)Math.Floor((double)j * syllableCount / k), syllableCount - 1);
                    j++;
                }

                if (segment == syllableIndex)
                    slice.Append(c);
            }

            return slice.ToString();
        }

        /// <summary>The whole sheet as one flat run of word slots, in sheet order.</summary>
        public static List<TapTarget> BuildQueue(IReadOnlyList<LyricLine> lines)
            => lines.Count == 0 ? new List<TapTarget>() : BuildQueue(lines, 0, 0, lines.Count - 1, lines[^1].Units.Count - 1);

        /// <summary>
        /// Builds the complete new sheet from <paramref name="taps"/> applied to
        /// <paramref name="queue"/>. Pure: <paramref name="lines"/> is never mutated. Returns a new
        /// list covering EVERY line, so the result can be handed straight to
        /// <see cref="TypeBeatEditorOperations.ReplaceLines"/>.
        /// </summary>
        public static IReadOnlyList<LyricLine> Build(IReadOnlyList<LyricLine> lines, IReadOnlyList<TapTarget> queue, IReadOnlyList<double> taps)
        {
            if (lines.Count == 0)
                return Array.Empty<LyricLine>();

            // Flatten the sheet into one ordered run of word slots, so "the next word" is a single
            // index step whether or not it crosses a line boundary.
            var slots = new List<(int line, int unit)>();
            var slotIndex = new Dictionary<(int, int), int>();

            for (int l = 0; l < lines.Count; l++)
            {
                for (int u = 0; u < lines[l].Units.Count; u++)
                {
                    slotIndex[(l, u)] = slots.Count;
                    slots.Add((l, u));
                }
            }

            int n = slots.Count;

            if (n == 0)
                return lines.ToList();

            double[] start = new double[n];
            double[] end = new double[n];
            bool[] retimed = new bool[n];
            bool[] paced = new bool[n];

            // Per word: the syllable taps recorded inside it (syllable 1..N, absolute ms), how many
            // of its slots got a tap, and how many it asked for. A word only keeps subdivisions when
            // every one of its slots was tapped.
            var syllableTaps = new List<double>?[n];
            int[] syllablesTapped = new int[n];
            int[] syllablesWanted = new int[n];

            for (int p = 0; p < n; p++)
            {
                var unit = lines[slots[p].line].Units[slots[p].unit];
                start[p] = unit.StartTime;
                end[p] = unit.EndTime;
                syllablesWanted[p] = SyllableCount(unit);
            }

            int tapped = Math.Min(taps.Count, queue.Count);

            // Nothing recorded: hand the sheet back untouched (the caller skips the commit).
            if (tapped == 0)
                return lines.ToList();

            // Pacing is a WORD rhythm, so it is measured from the word-start taps only. On a sheet
            // with no subdivisions every tap is a word start and this is the old mean exactly.
            double pace = meanWordGap(queue, taps, tapped);

            // Desired starts. Taps win outright; the untapped tail of the queue keeps its own time
            // when that time still makes sense, and is paced on otherwise.
            double lastTap = double.NegativeInfinity;

            for (int i = 0; i < queue.Count; i++)
            {
                if (!slotIndex.TryGetValue((queue[i].LineIndex, queue[i].UnitIndex), out int p))
                    continue;

                bool wordStart = queue[i].SyllableIndex == 0;

                if (i < tapped)
                {
                    double time = Math.Max(0, taps[i]);

                    if (wordStart)
                    {
                        start[p] = time;
                        retimed[p] = true;
                    }
                    else
                    {
                        (syllableTaps[p] ??= new List<double>()).Add(time);
                    }

                    syllablesTapped[p]++;
                    lastTap = time;
                }
                else if (!wordStart)
                {
                    // An untapped syllable slot decides nothing on its own: the word's fate was
                    // already settled by its syllable-0 slot, and the missing tap simply means the
                    // word cannot keep its subdivisions.
                }
                else if (start[p] <= lastTap)
                {
                    start[p] = lastTap + pace;
                    retimed[p] = true;
                    paced[p] = true;
                    lastTap = start[p];
                }
                else
                {
                    // Existing timing still sits after everything we recorded; leave it alone.
                    lastTap = start[p];
                }
            }

            // Per-typeable-character rate of the whole pass: the fallback for a line that offers no
            // cadence of its own (only its last word retimed, say).
            double charPace = meanCharGap(lines, queue, taps, tapped);

            // ONE forward pass fixes every ordering collision, against the final numbers. Slots
            // before the queue are already monotonic and untouched, so this only ever bites where a
            // tap crowded its predecessor or where content after the queue would now overlap.
            //
            // The gap it enforces is MIN_SPAN_MS everywhere except behind a retimed word that ENDS
            // a line, which is given room to be READ. That word has no next word of its own line to
            // run to, so without the room whatever sits behind it (a squashed untimed sheet, a line
            // the pass never reached) lands MIN_SPAN_MS later and collapses it into an unreadable
            // sliver. Two tiers, because moving content the mapper did not tap is a cost:
            //   - GUARANTEED room is the word's default width at DEFAULT_CHAR_MS, which is small
            //     (80ms a character) and bounded by the word's own length, so the shove is one
            //     word wide at worst however sloppy the taps were.
            //   - Room BEYOND that, up to the line's own measured cadence, is taken only where the
            //     content behind already leaves it, so a line's last word matches its line-mates
            //     whenever that costs nothing.
            // The one follower never pushed at all is another TAP: there the mapper said where the
            // next word goes, and a word between two real taps is exactly as long as they made it.
            double[] lastWidth = new double[lines.Count];
            double previous = double.NegativeInfinity;
            double required = TypeBeatEditorOperations.MIN_SPAN_MS;

            for (int p = 0; p < n; p++)
            {
                start[p] = Math.Max(start[p], previous + required);
                previous = start[p];
                required = TypeBeatEditorOperations.MIN_SPAN_MS;

                var (sl, su) = slots[p];

                if (su != lines[sl].Units.Count - 1 || !retimed[p])
                    continue;

                // Every start of this line is settled by the time the pass reaches its last slot
                // (it runs in sheet order), so the line's cadence is measured here, once, at the one
                // slot that needs it.
                lastWidth[sl] = DefaultWordDuration(lines[sl].Units[su].Text,
                    lineCharPaceOf(lines, slotIndex, start, retimed, sl, charPace));

                if (p + 1 >= n || (retimed[p + 1] && !paced[p + 1]))
                    continue;

                // The next slot opens the next LINE, and no word may spill past its own line's end,
                // which is where that line STARTS, lead-in included (see the line-boundary pass
                // below). So the room to clear is the guaranteed width plus that lead-in.
                int following = slots[p + 1].line;
                double guaranteed = Math.Min(lastWidth[sl], DefaultWordDuration(lines[sl].Units[su].Text, DEFAULT_CHAR_MS));
                required = guaranteed - Math.Min(0, lines[following].StartTime - lines[following].Units[0].StartTime);
            }

            // Word ends. Inside a line a word runs to the next word; the last word of a line takes
            // its default width, which the pass above has just made room for (the cap still bites
            // when the follower is a tap, where the mapper's own next word is the truth).
            for (int p = 0; p < n; p++)
            {
                var (l, u) = slots[p];
                bool lastOfLine = u == lines[l].Units.Count - 1;
                double ceiling = p + 1 < n ? start[p + 1] : double.MaxValue;

                if (retimed[p])
                {
                    end[p] = lastOfLine
                        ? Math.Min(ceiling, start[p] + lastWidth[l])
                        : ceiling;
                }

                end[p] = Math.Clamp(end[p], start[p], ceiling);
            }

            // Line boundaries. A tapped first word IS the line boundary (hand-placed, no guessing).
            // Otherwise the line keeps its lead-in, the gap it already had between its start and its
            // first word, so a line that merely got pushed out of a collision moves as a whole and a
            // line nothing touched reproduces its old start exactly.
            double[] lineStart = new double[lines.Count];
            double previousLineStart = double.NegativeInfinity;

            for (int l = 0; l < lines.Count; l++)
            {
                int first = slotIndex.TryGetValue((l, 0), out int p0) ? p0 : -1;
                double s;

                if (first < 0)
                    s = lines[l].StartTime;
                else if (retimed[first])
                    s = start[first];
                else
                    s = Math.Min(start[first], start[first] + (lines[l].StartTime - lines[l].Units[0].StartTime));

                lineStart[l] = Math.Max(s, previousLineStart + TypeBeatEditorOperations.MIN_SPAN_MS);
                previousLineStart = lineStart[l];
            }

            // Assemble. Every line is rebuilt (the model is init-only), but only retimed words
            // change source/confidence or lose subdivisions.
            var result = new List<LyricLine>(lines.Count);

            for (int l = 0; l < lines.Count; l++)
            {
                var line = lines[l];
                int count = line.Units.Count;

                // Interior lines end exactly where the next one starts (EndTime_i == StartTime_(i+1));
                // no word may spill past that, so the boundary invariant holds by construction.
                double ceiling = l + 1 < lines.Count ? lineStart[l + 1] : double.MaxValue;

                var units = new TimedUnit[count];
                bool anyPaced = false;
                bool anyRetimed = false;
                double cursor = lineStart[l];

                for (int u = 0; u < count; u++)
                {
                    int p = slotIndex[(l, u)];
                    var source = line.Units[u];

                    anyPaced |= paced[p];
                    anyRetimed |= retimed[p];

                    double s = Math.Clamp(start[p], cursor, ceiling);
                    double e = Math.Clamp(end[p], s, ceiling);
                    cursor = e;

                    var boundaries = boundariesFor(source, retimed[p], syllableTaps[p],
                        syllablesTapped[p] >= syllablesWanted[p], s, e);

                    units[u] = new TimedUnit
                    {
                        Text = source.Text,
                        StartTime = s,
                        EndTime = e,
                        // A tapped word is hand timing and fully trusted; a paced one is a guess and
                        // says so, exactly as the aligner's interpolated words do.
                        Source = retimed[p] ? (paced[p] ? TimingSource.Interpolated : TimingSource.Explicit) : source.Source,
                        Confidence = retimed[p] ? (paced[p] ? 0.5 : 1) : source.Confidence,
                        SyllableBoundaries = boundaries,
                        // The word's text never changes here, so an authored character split
                        // survives as long as the SEGMENT COUNT does; a re-tap that adds or drops a
                        // syllable leaves every later split paired with the wrong segment, so the
                        // word falls back to the derived split.
                        SyllableSplits = boundaries.Count == source.SyllableBoundaries.Count ? source.SyllableSplits : Array.Empty<int>(),
                    };
                }

                double singEnd = count > 0 ? units[^1].EndTime : lineStart[l];

                // Interior lines: the boundary invariant. The LAST line's typeable window is
                // reload-derived from its sung end, so a retimed one takes the full tail; an
                // untouched one keeps the window it already had.
                double lineEnd = l + 1 < lines.Count
                    ? lineStart[l + 1]
                    : anyRetimed
                        ? singEnd + TypeBeatEditorOperations.LAST_LINE_TAIL_MS
                        : Math.Max(line.EndTime, singEnd);

                result.Add(new LyricLine
                {
                    RawText = line.RawText,
                    StartTime = lineStart[l],
                    EndTime = lineEnd,
                    SingEndTime = singEnd,
                    Units = units,
                    // A hand-placed boundary needs no overrun grace; leave untouched lines alone.
                    SealGraceMs = anyRetimed ? 0 : line.SealGraceMs,
                    // Paced words are guesses => judge the line at the wider Line-granularity
                    // windows. A fully tapped line is real evidence and clears the flag.
                    Estimated = anyPaced || (!anyRetimed && line.Estimated),
                });
            }

            return result;
        }

        /// <summary>
        /// The subdivision boundaries a rebuilt word ends up with.
        ///
        /// <para>An untouched word keeps exactly what it had. A retimed word whose every syllable
        /// slot was tapped takes those taps, fitted into its final span. Anything else (a partly
        /// tapped word, a paced word, a word left too narrow to hold its syllables) drops its
        /// subdivisions: the word moved as a block and the old marks no longer describe it.</para>
        /// </summary>
        private static IReadOnlyList<double> boundariesFor(TimedUnit source, bool retimed, List<double>? marks,
                                                           bool complete, double start, double end)
        {
            if (!retimed)
                return source.SyllableBoundaries;

            if (!complete || marks == null || marks.Count == 0)
                return Array.Empty<double>();

            return fitBoundaries(marks, start, end);
        }

        /// <summary>
        /// Places <paramref name="marks"/> strictly inside (<paramref name="start"/>,
        /// <paramref name="end"/>), ascending, at least <see cref="TypeBeatEditorOperations.MIN_SYLLABLE_MS"/>
        /// apart, which is the invariant <see cref="TimedUnit.SyllableBoundaries"/> promises and the
        /// encoder round-trips. Taps that already sit comfortably inside the word pass through
        /// untouched, so a clean pass saves and decodes to the exact numbers the mapper tapped. A
        /// span with no room for every syllable keeps none of them.
        /// </summary>
        private static IReadOnlyList<double> fitBoundaries(List<double> marks, double start, double end)
        {
            int count = marks.Count;
            double min = TypeBeatEditorOperations.MIN_SYLLABLE_MS;

            if (end - start < (count + 1) * min)
                return Array.Empty<double>();

            double[] fitted = new double[count];
            double low = start;

            for (int i = 0; i < count; i++)
            {
                // Leave room for the syllables still to come, so the last one still clears the end.
                double ceiling = end - (count - i) * min;
                fitted[i] = Math.Clamp(marks[i], low + min, ceiling);
                low = fitted[i];
            }

            return fitted;
        }

        /// <summary>
        /// Mean interval between the WORD-START taps of the pass: how long a word takes, which is
        /// what the untapped tail is paced at. Syllable taps are deliberately excluded, or a
        /// subdivided pass would pace the remainder at a syllable's length.
        /// </summary>
        private static double meanWordGap(IReadOnlyList<TapTarget> queue, IReadOnlyList<double> taps, int tapped)
        {
            var wordStarts = new List<double>();

            for (int i = 0; i < tapped; i++)
            {
                if (queue[i].SyllableIndex == 0)
                    wordStarts.Add(taps[i]);
            }

            return meanGap(wordStarts, wordStarts.Count);
        }

        /// <summary>Mean interval between the first <paramref name="count"/> taps; the default when there is only one.</summary>
        private static double meanGap(IReadOnlyList<double> taps, int count)
        {
            if (count < 2)
                return DEFAULT_WORD_MS;

            double span = taps[count - 1] - taps[0];
            double mean = span / (count - 1);

            return mean >= TypeBeatEditorOperations.MIN_SPAN_MS ? mean : DEFAULT_WORD_MS;
        }

        /// <summary>
        /// The sung length <paramref name="text"/> is given when nothing else decides it, at
        /// <paramref name="perCharMs"/> per TYPEABLE character. This is what a line's LAST word
        /// takes: it has no next word of its own line to run to, and a flat default made either
        /// "on" or "everything" look wrong, so the width scales with what the player has to type.
        /// Never narrower than <see cref="TypeBeatEditorOperations.MIN_SPAN_MS"/>, and a word with
        /// no typeable character at all still counts as one.
        /// </summary>
        public static double DefaultWordDuration(string text, double perCharMs)
            => Math.Max(charsOf(text) * perCharMs, TypeBeatEditorOperations.MIN_SPAN_MS);

        /// <summary>A word's typeable character count, never zero (a word of pure punctuation still has a width).</summary>
        private static double charsOf(string text) => Math.Max(1, Typeability.TypeableCount(text));

        /// <summary>
        /// Milliseconds per typeable character of ONE line, measured from its own retimed words: how
        /// long each consecutive retimed pair gave the earlier word, over that word's characters.
        /// Falls back to <paramref name="fallback"/> (the pass's own rate) when the line has fewer
        /// than two retimed words to measure between.
        /// </summary>
        private static double lineCharPaceOf(IReadOnlyList<LyricLine> lines, Dictionary<(int, int), int> slotIndex,
                                             double[] start, bool[] retimed, int line, double fallback)
        {
            double total = 0;
            double chars = 0;
            int count = lines[line].Units.Count;

            for (int u = 1; u < count; u++)
            {
                int p = slotIndex[(line, u)];
                int previous = slotIndex[(line, u - 1)];

                if (!retimed[p] || !retimed[previous])
                    continue;

                total += start[p] - start[previous];
                chars += charsOf(lines[line].Units[u - 1].Text);
            }

            return chars > 0 && total > 0 ? total / chars : fallback;
        }

        /// <summary>
        /// Milliseconds per typeable character across the WHOLE pass, measured between consecutive
        /// word-start taps over the characters of the word each one opened. Syllable taps are
        /// excluded for the same reason <see cref="meanWordGap"/> excludes them: they time part of a
        /// word, not a word. Falls back to <see cref="DEFAULT_CHAR_MS"/> when the pass has fewer
        /// than two word starts to measure between.
        /// </summary>
        private static double meanCharGap(IReadOnlyList<LyricLine> lines, IReadOnlyList<TapTarget> queue,
                                          IReadOnlyList<double> taps, int tapped)
        {
            double total = 0;
            double chars = 0;
            double previousTime = 0;
            string? previousText = null;

            for (int i = 0; i < tapped; i++)
            {
                if (queue[i].SyllableIndex != 0)
                    continue;

                if (previousText != null)
                {
                    total += taps[i] - previousTime;
                    chars += charsOf(previousText);
                }

                previousTime = taps[i];
                previousText = textOf(lines, queue[i]);
            }

            return chars > 0 && total > 0 ? total / chars : DEFAULT_CHAR_MS;
        }

        /// <summary>The text of the word a tap slot points at, or empty when it points outside the sheet.</summary>
        private static string textOf(IReadOnlyList<LyricLine> lines, TapTarget target)
        {
            if (target.LineIndex < 0 || target.LineIndex >= lines.Count)
                return string.Empty;

            var units = lines[target.LineIndex].Units;
            return target.UnitIndex >= 0 && target.UnitIndex < units.Count ? units[target.UnitIndex].Text : string.Empty;
        }
    }
}
