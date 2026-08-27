// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Gameplay/TypingLine.cs (regression-anchored).
// Pure C#: no osu.Framework dependencies. All times are double milliseconds.

using System;
using System.Collections.Generic;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    public enum CellState
    {
        Untyped,
        Correct,
        Wrong,
        Missed,
        AutoSkipped,

        /// <summary>
        /// A typeable cell the player ABANDONED with a word skip (backlog 167, see
        /// <see cref="TypingEngine.SpaceSkipsWord"/>): given up, but not yet lost. Nothing has been
        /// typed into it and nothing has been resolved for it, exactly as for
        /// <see cref="Untyped"/>; what the state adds is that a BACKSPACE steps transparently back
        /// over it and resets it to Untyped, so ONE press re-enters the word and the characters can
        /// then be earned for real.
        ///
        /// <para>It is the same shape <see cref="Wrong"/> has, which is why it is a state and not a
        /// resolution: a cell whose one osu result is DEFERRED because the play is not finished with
        /// it. A cell leaves this state in exactly two ways, that backspace or the line seal (where
        /// it resolves as the miss an untyped cell resolves as), which is what lets the skip's cost
        /// be charged on entry and given back on exit, and therefore paid exactly once.</para>
        /// </summary>
        Abandoned
    }

    public sealed class TypingCell
    {
        /// <summary>Display char as authored (normalized). For a freestyle cell this is the
        /// authoring marker (<see cref="Typeability.FREESTYLE_MARKER"/>), never a glyph the player
        /// has to produce; the display substitutes it (see <see cref="IsFreestyle"/>).</summary>
        public char Expected { get; }

        public bool IsTypeable { get; }

        /// <summary>
        /// FREESTYLE cell: any key on the typeable surface EXCEPT SPACE satisfies it, and the char
        /// the player actually pressed lands in <see cref="TypedChar"/> and stays on screen.
        /// Judgement is otherwise a completely normal typeable cell (same windows, points, combo,
        /// completion), and a space is rejected exactly as a wrong key on any other cell is.
        /// </summary>
        public bool IsFreestyle { get; }

        /// <summary>
        /// A COUNTABLE character: typeable and not a space. The unit of character DISTANCE in this
        /// game, shared by the Flashlight mod's visible window and the Fletcher mod's rush cap:
        /// spaces and punctuation ride along inside a run without spending its budget. A freestyle
        /// slot is countable like any other letter (the player presses a key for it).
        /// </summary>
        public bool IsCountable => IsTypeable && Expected != ' ';

        public double TargetTime { get; }

        /// <summary>
        /// Window tier this cell is judged at: the beatmap granularity normally, widened to
        /// Line for estimated lines and low-confidence words (unreliable timing gets tolerance).
        /// </summary>
        public TimingGranularity JudgeGranularity { get; }

        public CellState State { get; internal set; }

        public char? TypedChar { get; internal set; }

        /// <summary>Delta of the awarded correct keypress, or the wrong keypress.</summary>
        public double? JudgedDelta { get; internal set; }

        /// <summary>
        /// Delta of the FIRST correct judgement: set once, never cleared (survives backspace).
        /// Its presence makes later correct retypes scoring-inert, so backspace-retype cannot
        /// farm score/combo/accuracy.
        /// </summary>
        internal double? FirstCorrectDelta { get; set; }

        internal TypingCell(char expected, bool isTypeable, double targetTime, TimingGranularity judgeGranularity)
        {
            Expected = expected;
            IsTypeable = isTypeable;
            IsFreestyle = isTypeable && Typeability.IsFreestyle(expected);
            TargetTime = targetTime;
            JudgeGranularity = judgeGranularity;
            State = CellState.Untyped;
        }
    }

    /// <summary>
    /// One sung syllable of a <see cref="TypingLine"/> (backlog 174): the half-open cell range
    /// [<see cref="StartCell"/>, <see cref="EndCellExclusive"/>) it owns and the time span
    /// [<see cref="StartTime"/>, <see cref="EndTime"/>] it is sung over. Under
    /// <see cref="TypingEngine.SyllableTiming"/> a keypress on one of its cells is judged against
    /// that span: delta 0 anywhere inside it, distance to the nearer edge outside it.
    ///
    /// <para>Groups are also what the lyric stack LIGHTS on screen: the group being sung has its
    /// untyped cells painted white, under every sung playhead style since backlog 177. That is a
    /// SEPARATE concern from the judgement flag above, and both readings work off this same
    /// always-built list.</para>
    /// </summary>
    public readonly record struct SyllableGroup(int StartCell, int EndCellExclusive, double StartTime, double EndTime);

    public sealed class TypingLine
    {
        public LyricLine Source { get; }

        /// <summary>
        /// The line exactly as it is shown and exactly as it must be typed: the concatenated cell
        /// chars. Equal to <c>Source.RawText</c> under the Literate mod and to
        /// <see cref="Typeability.ToDefaultStream"/> of it otherwise. Never re-derived anywhere
        /// else: the stage renders the same cells this is built from.
        /// </summary>
        public string DisplayText { get; }

        public IReadOnlyList<TypingCell> Cells { get; }

        public double StartTime { get; }

        public double EndTime { get; }

        public double SingEndTime { get; }

        /// <summary>
        /// When this line becomes typeable: a constant cue lead before its first typeable cell's
        /// target (<see cref="TypingEngine.CUE_LEAD_MS"/>), never earlier than <see cref="StartTime"/>
        /// (the shared boundary; the previous line cannot seal before it). Independent of the
        /// boundary otherwise: a line whose vocals start late in its window activates late, and
        /// the gap in between is a dead zone where no line is active.
        /// </summary>
        public double ActivationTime { get; }

        /// <summary>
        /// When this line's vocals actually begin: the first typeable cell's target time, falling
        /// back to <see cref="StartTime"/> for a line with no typeable cells. Together with the
        /// previous line's <see cref="SingEndTime"/> this measures the *perceived* instrumental
        /// stretch between two lines (what a player hears as "no lyrics").
        /// </summary>
        public double FirstVocalTime { get; }

        /// <summary>
        /// Extra typeable time past <see cref="EndTime"/> before the engine may force-seal an
        /// incomplete line. Positive when source vocals overrun the boundary (overlapping lines)
        /// or when the last cell's target sits on the boundary itself.
        /// </summary>
        public double SealGraceMs { get; }

        public int TypeableCount { get; }

        /// <summary>
        /// The line's SYLLABLE groups (backlog 174): ordered, non-overlapping cell ranges, one per
        /// syllable of each whitespace token, with the time span each syllable is sung over. Built
        /// ALWAYS (cheap, pure) regardless of engine mode, so rendering and tests can read them even
        /// when <see cref="TypingEngine.SyllableTiming"/> is off. That is load-bearing, not merely
        /// tidy: the sung-group highlight renders off these groups whatever the engine is judging
        /// on, which is what lets a Release build show it at all.
        ///
        /// <para>Coverage is PARTIAL, and every consumer must tolerate a gap (backlog 178 weakened
        /// this from "every typeable non-space cell belongs to exactly one group"). Three kinds of
        /// cell belong to no group: a SPACE cell (the inter-word gap, or a hyphen turned into a
        /// typed space), a cell the default stream produced from punctuation a forced split
        /// isolated, and every cell of a token that is not a syllabifiable English word
        /// (<see cref="Syllabifier.IsSyllabifiable"/>: "wooooooords", "heyyyyy", "ohhh"). Membership
        /// must therefore be read through <see cref="SyllableIndexOf"/> rather than by range: a
        /// mid-token hyphen-space, and now a whole stylised word, can sit positionally inside a
        /// group's cell range while being in no group. Groups themselves never straddle a token, so
        /// an ungrouped token leaves a gap BETWEEN groups rather than a hole inside one.</para>
        /// </summary>
        public IReadOnlyList<SyllableGroup> Syllables { get; }

        /// <summary>Per display cell, the index into <see cref="Syllables"/> or -1 (space cells; punctuation-only groups the default stream deleted).</summary>
        private readonly int[] cellSyllable;

        /// <summary>
        /// Index into <see cref="Syllables"/> of the group that judges cell
        /// <paramref name="cellIndex"/>, or -1 when the cell is in no group (space cells, and any
        /// out-of-range index).
        /// </summary>
        public int SyllableIndexOf(int cellIndex)
            => cellIndex >= 0 && cellIndex < cellSyllable.Length ? cellSyllable[cellIndex] : -1;

        /// <summary>Per display cell, whether it is a CHAR-TIMED STRETCH cell (see <see cref="IsCharTimedStretch"/>).</summary>
        private readonly bool[] charTimedStretch;

        /// <summary>
        /// Whether cell <paramref name="cellIndex"/> is a STRETCH cell (backlog 209): one whose
        /// identity does not say WHEN inside its syllable it was meant to be pressed, so the span
        /// rule would hand it a delta of 0 for a press anywhere in the syllable. Two kinds qualify:
        ///
        /// <list type="bullet">
        /// <item>a FREESTYLE cell (<see cref="TypingCell.IsFreestyle"/>), which accepts any key at
        /// all, so a mashed section of them is indistinguishable from a played one; and</item>
        /// <item>a cell inside a run of THREE OR MORE consecutive cells of the same syllable whose
        /// characters fold equal (<see cref="Typeability.Fold"/>): the "000" of "1000", the "yyyy"
        /// of a subtimed "hey|yyyy". The keys are interchangeable, so the run can be typed out in
        /// one burst.</item>
        /// </list>
        ///
        /// <para>THREE is the threshold, and it is <see cref="Syllabifier.IsSyllabifiable"/>'s own:
        /// that gate calls a word stylised at three identical letters, so an ordinary doubled letter
        /// ("goo", "all") is a normal spelling and keeps the syllable span it is sung across. Runs
        /// are cut at the syllable boundary, because a span is what the rule hands out: two 'y's in
        /// one syllable and two in the next are two runs of two, each judged on its own span.</para>
        ///
        /// <para>Derived at construction and purely structural, so it says nothing about which rule
        /// is in force: <see cref="TypingEngine.CharTimedStretch"/> decides whether the judgement
        /// seam reads it (see <c>TypingEngine.judgedDeltaFor</c>). Deliberately NOT a grouping
        /// change: these cells stay in their syllable, because the lyric stack lights GROUPS and an
        /// ungrouped stretch would stop being highlighted while it is sung.</para>
        /// </summary>
        public bool IsCharTimedStretch(int cellIndex)
            => cellIndex >= 0 && cellIndex < charTimedStretch.Length && charTimedStretch[cellIndex];

        /// <summary>
        /// Piecewise-linear anchor points for <see cref="SungPositionAt"/>:
        /// (StartTime, 0), each typeable cell's (TargetTime, displayIndex), (SingEndTime, Cells.Count).
        /// Times are clamped monotonic non-decreasing at construction.
        /// </summary>
        private readonly List<(double time, double index)> sungPoints;

        private TypingLine(LyricLine source, IReadOnlyList<TypingCell> cells, double sealGraceMs, SyllableGroup[] syllables, int[] cellSyllable)
        {
            Source = source;
            Syllables = syllables;
            this.cellSyllable = cellSyllable;
            charTimedStretch = buildCharTimedStretch(cells, cellSyllable);

            var display = new System.Text.StringBuilder(cells.Count);

            foreach (var cell in cells)
                display.Append(cell.Expected);

            DisplayText = display.ToString();
            Cells = cells;
            StartTime = source.StartTime;
            EndTime = source.EndTime;
            SingEndTime = source.SingEndTime;
            SealGraceMs = sealGraceMs;

            int typeable = 0;

            foreach (var c in cells)
            {
                if (c.IsTypeable)
                    typeable++;
            }

            TypeableCount = typeable;

            double? firstTypeableTarget = null;

            foreach (var c in cells)
            {
                if (c.IsTypeable)
                {
                    firstTypeableTarget = c.TargetTime;
                    break;
                }
            }

            ActivationTime = firstTypeableTarget is double first
                ? Math.Max(StartTime, first - TypingEngine.CUE_LEAD_MS)
                : StartTime;

            FirstVocalTime = firstTypeableTarget ?? StartTime;

            // Pre-build the sung-position polyline, clamping times monotonic.
            sungPoints = new List<(double, double)>(typeable + 2) { (StartTime, 0) };
            double lastTime = StartTime;

            for (int i = 0; i < cells.Count; i++)
            {
                if (!cells[i].IsTypeable)
                    continue;

                double t = Math.Max(cells[i].TargetTime, lastTime);
                sungPoints.Add((t, i));
                lastTime = t;
            }

            sungPoints.Add((Math.Max(SingEndTime, lastTime), cells.Count));
        }

        /// <summary>
        /// Fractional display-cell index in [0, Cells.Count]: piecewise-linear through
        /// (StartTime, 0) .. (TargetTime_j, displayIndex_j) .. (SingEndTime, Cells.Count),
        /// clamped outside, zero-length time segments skipped (position jumps).
        /// </summary>
        public double SungPositionAt(double time)
        {
            if (time <= sungPoints[0].time)
                return sungPoints[0].index;

            if (time >= sungPoints[^1].time)
                return sungPoints[^1].index;

            // Left anchor: the LAST point with point.time <= time (this skips zero-length segments;
            // among points sharing one time we anchor at the greatest index).
            int left = 0;

            for (int i = 1; i < sungPoints.Count; i++)
            {
                if (sungPoints[i].time <= time)
                    left = i;
                else
                    break;
            }

            var (t0, v0) = sungPoints[left];
            var (t1, v1) = sungPoints[left + 1];

            if (t1 <= t0)
                return v1; // degenerate tail segment

            return v0 + (v1 - v0) * (time - t0) / (t1 - t0);
        }

        /// <summary>
        /// Flattens an authored line into the cell list gameplay judges and the stage renders.
        ///
        /// <para><paramref name="literate"/> selects WHICH STREAM the cells carry. Off (default):
        /// the cells are <see cref="Typeability.ToDefaultStream"/> of the authored text, so
        /// capitals fold to lower case, a hyphen becomes a typed space and every other supported
        /// mark is gone; that is what the player types AND what the display shows, because both
        /// read these same cells. On: one cell per authored char, punctuation and capitals
        /// included, every one of them typeable.</para>
        ///
        /// <para>Letter timings are IDENTICAL in both modes: the per-word char spread below counts
        /// only <see cref="Typeability.IsCell"/> chars (never punctuation), so turning the mod on
        /// adds cells without moving any of the existing ones.</para>
        /// </summary>
        public static TypingLine FromLyricLine(LyricLine line, TimingGranularity granularity = TimingGranularity.Line, bool literate = false)
        {
            string text = line.RawText;
            var units = line.Units;

            int n = text.Length;
            char[] expected = new char[n];
            bool[] isTypeable = new bool[n];
            double?[] targets = new double?[n];
            var judgeGrans = new TimingGranularity[n];

            for (int i = 0; i < n; i++)
                judgeGrans[i] = granularity;

            // First pass: walk the raw text token by token (spaces delimit tokens; token m maps to Units[m]).
            string[] tokens = text.Split(' ');
            int pos = 0;

            for (int m = 0; m < tokens.Length; m++)
            {
                string token = tokens[m];

                // Guard against malformed data where token count != unit count.
                TimedUnit? unit = units.Count > 0 ? units[Math.Min(m, units.Count - 1)] : null;

                double unitStart = unit?.StartTime ?? line.StartTime;
                double unitEnd = unit?.EndTime ?? line.SingEndTime;

                // Unreliable timing gets the widest windows: estimated lines and
                // low-confidence words are judged at the Line tier.
                TimingGranularity judgeGran = line.Estimated || (unit?.Confidence ?? 1) < SyncWindows.LOW_CONFIDENCE_SCORE
                    ? TimingGranularity.Line
                    : granularity;

                // k = number of typeable cells in this token (freestyle slots included: the player
                // presses a key for them, so they take a share of the word's time like any letter).
                int k = 0;

                foreach (char ch in token)
                {
                    if (Typeability.IsCell(ch))
                        k++;
                }

                // Syllable subdivisions warp the char-to-time mapping WITHIN the word: instead of
                // one flat ramp across [unitStart, unitEnd], the boundaries split it into segments
                // and the k chars are spread evenly across the segments in index-space, so the caret
                // reaches each boundary time at that boundary's proportional char and moves linearly
                // (but at a per-segment speed) between them. Empty boundaries => the flat ramp.
                //
                // An AUTHORED char split (backlog 181, "ap|ple") replaces that even distribution:
                // the mapper's own cut says how many chars ride each segment, so the same split
                // drives the targets here and the judgement groups in buildSyllables. Derived
                // (empty, or stale) keeps the index-even spread untouched, which is what makes a
                // map with no authored split flatten byte-identically to before.
                var boundaries = unit?.SyllableBoundaries ?? System.Array.Empty<double>();

                int[]? cellCuts = unit != null && SyllableSegments.IsAuthoredValid(token, boundaries.Count + 1, unit.SyllableSplits)
                    ? SyllableSegments.CellCuts(token, unit.SyllableSplits)
                    : null;

                int j = 0;

                foreach (char ch in token)
                {
                    expected[pos] = ch;
                    judgeGrans[pos] = judgeGran;

                    if (Typeability.IsCell(ch))
                    {
                        isTypeable[pos] = true;
                        // Typeable char j of k in unit u: first char AT unit start, piecewise across
                        // syllable boundaries (degenerates to u.Start + j*(u.End-u.Start)/k when undivided).
                        targets[pos] = syllableCharTarget(unitStart, unitEnd, boundaries, k, j, cellCuts);
                        j++;
                    }
                    // else: punctuation, resolved in the second pass.

                    pos++;
                }

                if (m < tokens.Length - 1)
                {
                    // Inter-word space cell: preceding unit's EndTime.
                    expected[pos] = ' ';
                    isTypeable[pos] = true;
                    targets[pos] = unitEnd;
                    judgeGrans[pos] = judgeGran;
                    pos++;
                }
            }

            // Second pass: time the chars pass one left untimed. After Typeability.Normalize the
            // only such chars are supported PUNCTUATION (letters, digits, spaces and freestyle
            // slots were all timed above, punctuation is excluded from the per-word char spread on
            // purpose so adding a mark never moves a letter).
            //
            // A run of marks between two timed chars is spread EVENLY across the gap: mark m of a
            // run of len takes prev + (m+1)*(next-prev)/(len+1). Under the Literate mod that gives
            // every mark its own slot in the sweep instead of colliding with the letter beside it,
            // and it is the same "distribute evenly across the span you have" rule the per-word
            // char spread itself uses. A run with nothing after it (trailing punctuation, the '.'
            // of "sat.") attaches to the PRECEDING char's target; one with nothing before it (a
            // line opening on a quote) attaches to the FOLLOWING char's.
            for (int i = 0; i < n; i++)
            {
                if (targets[i].HasValue)
                    continue;

                int end = i;

                while (end + 1 < n && !targets[end + 1].HasValue)
                    end++;

                double? before = i > 0 ? targets[i - 1] : null;
                double? after = end + 1 < n ? targets[end + 1] : null;
                int len = end - i + 1;

                for (int m = 0; m < len; m++)
                {
                    targets[i + m] = before is double p
                        ? (after is double q ? p + (m + 1) * (q - p) / (len + 1) : p)
                        : after ?? line.StartTime;
                }

                i = end;
            }

            // Guard: targets non-decreasing (clamp to previous if data is inverted).
            for (int i = 1; i < n; i++)
            {
                if (targets[i]!.Value < targets[i - 1]!.Value)
                    targets[i] = targets[i - 1];
            }

            TypingCell[] cells;
            List<int>? defaultSources = null;

            if (literate)
            {
                // One cell per authored char, all of them typed. A supported mark is a first-class
                // typeable cell here (and therefore COUNTABLE too: it costs a real keypress, so it
                // spends the Flashlight/Fletcher character budget like any other non-space char).
                cells = new TypingCell[n];

                for (int i = 0; i < n; i++)
                    cells[i] = new TypingCell(expected[i], isTypeable[i] || Typeability.IsPunctuation(expected[i]), targets[i]!.Value, judgeGrans[i]);
            }
            else
            {
                // The default stream. Each surviving char keeps the timing and judge tier of the
                // authored char it came from, so a hyphen-turned-space lands on the interpolated
                // slot the hyphen held between the two letters it separated.
                var sb = new System.Text.StringBuilder(n);
                var sources = new List<int>(n);
                Typeability.ProjectDefault(text, sb, sources);
                defaultSources = sources;

                cells = new TypingCell[sources.Count];

                for (int i = 0; i < sources.Count; i++)
                {
                    int src = sources[i];
                    cells[i] = new TypingCell(sb[i], Typeability.IsCell(sb[i]), targets[src]!.Value, judgeGrans[src]);
                }
            }

            // A last typeable cell whose target sits on the seal boundary loses the target-vs-seal
            // race every frame; grant a minimum finish window on top of any data-driven grace.
            double sealGrace = line.SealGraceMs;

            for (int i = cells.Length - 1; i >= 0; i--)
            {
                if (!cells[i].IsTypeable)
                    continue;

                if (cells[i].TargetTime >= line.EndTime - boundary_epsilon_ms)
                    sealGrace = Math.Max(sealGrace, min_boundary_grace_ms);

                break;
            }

            var (syllables, cellSyllable) = buildSyllables(line, tokens, cells, defaultSources);

            return new TypingLine(line, cells, Math.Min(sealGrace, max_seal_grace_ms), syllables, cellSyllable);
        }

        /// <summary>
        /// Groups the line's cells into SYLLABLES (backlog 174): per whitespace token, the
        /// <see cref="Syllabifier"/> decides WHICH characters form each syllable and the timing
        /// data decides WHEN it is sung. Pure derivation: no <see cref="TypingCell.TargetTime"/>
        /// moves, the classic sweep and every readout built on it stay byte-identical.
        ///
        /// <para>A token whose unit carries mapper subtimings
        /// (<see cref="TimedUnit.SyllableBoundaries"/>, N boundaries = N + 1 syllables) is split
        /// with the count FORCED to N + 1: the mapper's boundary times are the window edges, so
        /// syllable i spans [edge_i, edge_i+1] with edge_0 the unit's StartTime, the interior edges
        /// the boundary times themselves, and the last edge the unit's EndTime. When the
        /// syllabifier degrades to G &lt; N + 1 groups (an over-forced short word) the first G - 1
        /// boundary times are the interior edges and the last group runs to the unit's EndTime.</para>
        ///
        /// <para>A token WITHOUT subtimings is split naturally and each group's span is read off
        /// the EXISTING flat-ramp char targets: it starts at its first cell's TargetTime and ends
        /// where the next group starts (last group of the token: the unit's EndTime), so this case
        /// is exactly "group the chars you already timed" and nothing moves.</para>
        ///
        /// <para>That natural arm is GATED on <see cref="Syllabifier.IsSyllabifiable"/> (backlog
        /// 178). A token that fails it, a stylised spelling like "wooooooords" or "ohhh", gets NO
        /// groups at all and its cells stay at <see cref="SyllableIndexOf"/> -1, because the
        /// rule-based syllabifier was built for real English and on those words invents boundaries
        /// it cannot defend. The consequences are both the ones wanted: an ungrouped cell keeps the
        /// classic per-character point-delta judgement even under
        /// <see cref="TypingEngine.SyllableTiming"/> (see <c>TypingEngine.judgedDeltaFor</c>, which
        /// already falls back for a cell in no group), and it never wears the sung-group highlight,
        /// because the stage lights GROUPS. The playhead still sweeps across it, so a stylised word
        /// simply presents the way every word did before 174.</para>
        ///
        /// <para>The gate does NOT apply to a subtimed token: whatever it looks like, the mapper
        /// hand-authored its syllable count and boundary times, and that is authoritative (174's
        /// rule). A mapper who subtimes "heyyyyy" into three held syllables gets three groups.</para>
        ///
        /// <para>WHICH characters a subtimed token's groups take is <see cref="SyllableSegments"/>'s
        /// answer, not the syllabifier's directly: an AUTHORED split (backlog 181) wins over the
        /// forced analysis, and the SAME split feeds the per-char targets above. On an authored word
        /// that makes every cell's target fall inside its own group's span by construction, because
        /// both sides read the same cut of the same edges.</para>
        ///
        /// <para>Split indices index the TOKEN string and are mapped to cells through the same
        /// projection that assigned the targets, so punctuation (and, under Literate, its extra
        /// cells) lands inside the syllable of the letter it attaches to. A SPACE cell (the
        /// inter-word gap, or a hyphen turned into a typed space) belongs to NO group. A group
        /// whose every character the default stream deleted (forced splits can isolate punctuation)
        /// is dropped. Kept spans are clamped monotonic non-decreasing across the line, the same
        /// guard the targets themselves get against inverted data.</para>
        /// </summary>
        private static (SyllableGroup[] groups, int[] cellSyllable) buildSyllables(LyricLine line, string[] tokens, TypingCell[] cells, List<int>? defaultSources)
        {
            var units = line.Units;
            int[] rawGroup = new int[line.RawText.Length];
            Array.Fill(rawGroup, -1);

            // Provisional groups in token order: span edges, and whether the span is still to be
            // resolved from cell targets (NaN start; NaN end = "the next group's start").
            var starts = new List<double>();
            var ends = new List<double>();
            int tokStart = 0;

            for (int m = 0; m < tokens.Length; m++)
            {
                string token = tokens[m];

                // Same malformed-data clamp as the target-time walk above.
                TimedUnit? unit = units.Count > 0 ? units[Math.Min(m, units.Count - 1)] : null;

                double unitStart = unit?.StartTime ?? line.StartTime;
                double unitEnd = unit?.EndTime ?? line.SingEndTime;
                var boundaries = unit?.SyllableBoundaries ?? Array.Empty<double>();

                bool subtimed = boundaries.Count > 0;

                // A stylised spelling gets no groups at all UNLESS the mapper subtimed it, in which
                // case the hand-authored count wins over anything the rules would have guessed.
                if (token.Length > 0 && (subtimed || Syllabifier.IsSyllabifiable(token)))
                {
                    var splits = subtimed
                        ? SyllableSegments.SplitsFor(token, boundaries.Count + 1, unit?.SyllableSplits)
                        : Syllabifier.SplitPoints(token);

                    int groupBase = starts.Count;
                    int groupCount = splits.Count + 1;

                    for (int g = 0; g < groupCount; g++)
                    {
                        if (subtimed)
                        {
                            starts.Add(g == 0 ? unitStart : boundaries[g - 1]);
                            ends.Add(g == groupCount - 1 ? unitEnd : boundaries[g]);
                        }
                        else
                        {
                            starts.Add(double.NaN);
                            ends.Add(g == groupCount - 1 ? unitEnd : double.NaN);
                        }
                    }

                    // Token char t belongs to the group of the last split at or before it, so
                    // punctuation (transparent to the syllabifier) attaches to the syllable that
                    // surrounds it.
                    int inGroup = 0;

                    for (int t = 0; t < token.Length; t++)
                    {
                        if (inGroup < splits.Count && t == splits[inGroup])
                            inGroup++;

                        rawGroup[tokStart + t] = groupBase + inGroup;
                    }
                }

                tokStart += token.Length + 1; // the inter-word space raw char stays in no group
            }

            // Map raw-index groups onto cells through the same projection that assigned the
            // targets. A SPACE cell is in no group whatever raw char produced it (hyphens too).
            int[] cellSyllable = new int[cells.Length];
            Array.Fill(cellSyllable, -1);

            for (int i = 0; i < cells.Length; i++)
            {
                if (!cells[i].IsTypeable || cells[i].Expected == ' ')
                    continue;

                int src = defaultSources?[i] ?? i;
                cellSyllable[i] = rawGroup[src];
            }

            int provisional = starts.Count;
            int[] firstCell = new int[provisional];
            int[] lastCell = new int[provisional];
            Array.Fill(firstCell, -1);

            for (int i = 0; i < cellSyllable.Length; i++)
            {
                int g = cellSyllable[i];

                if (g < 0)
                    continue;

                if (firstCell[g] < 0)
                    firstCell[g] = i;

                lastCell[g] = i;
            }

            // Resolve the target-derived spans. Starts first (a group starts at its first cell's
            // target), then the NaN ends: only a non-last group of an un-subtimed token has one,
            // and its successor sits in the same token and always owns a letter or digit cell
            // (natural splits land on letters/digits, which every projection keeps), so its start
            // is known; the fallback degenerate span is defensive only.
            for (int g = 0; g < provisional; g++)
            {
                if (double.IsNaN(starts[g]) && firstCell[g] >= 0)
                    starts[g] = cells[firstCell[g]].TargetTime;
            }

            for (int g = 0; g < provisional; g++)
            {
                if (!double.IsNaN(ends[g]))
                    continue;

                double next = g + 1 < provisional ? starts[g + 1] : double.NaN;
                ends[g] = double.IsNaN(next) ? starts[g] : next;
            }

            // Compact to the groups that own at least one cell, clamping spans monotonic.
            var groups = new List<SyllableGroup>(provisional);
            int[] remap = new int[provisional];
            Array.Fill(remap, -1);
            double clock = double.NegativeInfinity;

            for (int g = 0; g < provisional; g++)
            {
                if (firstCell[g] < 0 || double.IsNaN(starts[g]))
                    continue;

                double start = Math.Max(starts[g], clock);
                double end = Math.Max(double.IsNaN(ends[g]) ? start : ends[g], start);
                clock = end;

                remap[g] = groups.Count;
                groups.Add(new SyllableGroup(firstCell[g], lastCell[g] + 1, start, end));
            }

            for (int i = 0; i < cellSyllable.Length; i++)
                cellSyllable[i] = cellSyllable[i] >= 0 ? remap[cellSyllable[i]] : -1;

            return (groups.ToArray(), cellSyllable);
        }

        /// <summary>
        /// The <see cref="IsCharTimedStretch"/> flags, derived once from the cells and their group
        /// membership (backlog 209). ADDITIVE: it reads <paramref name="cellSyllable"/> and never
        /// writes it, so no cell's group, target or span moves and every construction pin on this
        /// line (the parity fixtures included) sees exactly what it saw before.
        ///
        /// <para>One left-to-right pass. A run extends while the next cell sits in the SAME group
        /// (a cell in no group can never extend one, so spaces, punctuation the default stream
        /// isolated and whole stylised tokens all break runs) and its character folds equal to the
        /// run's. A closed run of three or more marks all of its cells; a freestyle cell is marked
        /// whatever its neighbours are.</para>
        /// </summary>
        private static bool[] buildCharTimedStretch(IReadOnlyList<TypingCell> cells, int[] cellSyllable)
        {
            bool[] flags = new bool[cells.Count];

            for (int i = 0; i < cells.Count; i++)
                flags[i] = cells[i].IsFreestyle;

            int runStart = 0;

            for (int i = 1; i <= cells.Count; i++)
            {
                bool extends = i < cells.Count
                               && cellSyllable[i] >= 0
                               && cellSyllable[i] == cellSyllable[runStart]
                               && Typeability.Fold(cells[i].Expected) == Typeability.Fold(cells[runStart].Expected);

                if (extends)
                    continue;

                if (cellSyllable[runStart] >= 0 && i - runStart >= STRETCH_RUN_LENGTH)
                {
                    for (int j = runStart; j < i; j++)
                        flags[j] = true;
                }

                runStart = i;
            }

            return flags;
        }

        /// <summary>
        /// How many identical characters in a row make a STRETCH (backlog 209). Three, which is
        /// <see cref="Syllabifier.IsSyllabifiable"/>'s own threshold for calling a spelling
        /// stylised, so the two answers about "hey" versus "heyyy" cannot disagree.
        /// </summary>
        public const int STRETCH_RUN_LENGTH = 3;

        /// <summary>
        /// Target time of typeable char <paramref name="j"/> (0-based, of <paramref name="k"/> in the
        /// word) under piecewise-linear syllable timing. The word spans [unitStart, unitEnd]; each
        /// entry of <paramref name="boundaries"/> (absolute ms, strictly inside, ascending) splits it
        /// into one more segment. The k chars are distributed evenly by index across the segments, so
        /// segment s covers char-index range [s*k/S, (s+1)*k/S] mapped linearly onto its time range,
        /// where S = segment count. With no boundaries this is exactly unitStart + j*(unitEnd-unitStart)/k;
        /// char j = 0 always lands on unitStart. Monotonic non-decreasing in j because boundaries are sorted.
        ///
        /// <para><paramref name="cellCuts"/>, when given (<see cref="SyllableSegments.CellCuts"/> of
        /// an AUTHORED split, backlog 181), replaces that even distribution with the mapper's own:
        /// segment s covers cell-index range [cuts[s], cuts[s+1]) instead of [s*k/S, (s+1)*k/S], so
        /// "ap|ple" puts two chars on the first syllable and three on the second however long the
        /// word is. Null means derived, and the arithmetic below is then untouched.</para>
        /// </summary>
        internal static double syllableCharTarget(double unitStart, double unitEnd, IReadOnlyList<double> boundaries, int k, int j, IReadOnlyList<int>? cellCuts = null)
        {
            if (k <= 0)
                return unitStart;

            if (boundaries.Count == 0)
                return unitStart + (double)j * (unitEnd - unitStart) / k;

            int segments = boundaries.Count + 1;
            int s;
            double segIndexLo;
            double segIndexHi;

            if (cellCuts != null && cellCuts.Count == segments + 1)
            {
                s = SyllableSegments.SegmentOf(cellCuts, j);
                segIndexLo = cellCuts[s];
                segIndexHi = cellCuts[s + 1];
            }
            else
            {
                // Which segment holds char index j (floor of j scaled into segment-space), clamped to the last.
                s = (int)Math.Floor((double)j * segments / k);

                if (s >= segments)
                    s = segments - 1;

                segIndexLo = (double)s * k / segments;
                segIndexHi = (double)(s + 1) * k / segments;
            }

            double timeLo = s == 0 ? unitStart : boundaries[s - 1];
            double timeHi = s == segments - 1 ? unitEnd : boundaries[s];

            if (segIndexHi <= segIndexLo)
                return timeLo;

            return timeLo + (j - segIndexLo) / (segIndexHi - segIndexLo) * (timeHi - timeLo);
        }

        private const double boundary_epsilon_ms = 30;
        private const double min_boundary_grace_ms = 250;
        private const double max_seal_grace_ms = 700;
    }
}
