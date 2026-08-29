// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Editor mutations on a type!beat beatmap that respect the immutable <see cref="LyricLine"/>/
    /// <see cref="TimedUnit"/> model (every edit rebuilds instances) and go through
    /// <see cref="EditorBeatmap"/> transactions so they are undoable.
    /// </summary>
    public static class TypeBeatEditorOperations
    {
        /// <summary>
        /// Shifts every stored line and word time by <paramref name="deltaMs"/> (positive = later),
        /// baking a global lyric-vs-song offset into the map data. The shift is clamped so nothing
        /// moves before 0, preserving all relative timing. This is distinct from the player-side
        /// LyricOffsetMs preference, which never touches the map.
        /// </summary>
        public static void ShiftAllTimes(EditorBeatmap editorBeatmap, double deltaMs)
        {
            var objects = editorBeatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            if (objects.Count == 0 || deltaMs == 0)
                return;

            double earliest = objects.Min(o => o.Line.StartTime);
            double applied = Math.Max(deltaMs, -earliest);

            if (applied == 0)
                return;

            editorBeatmap.BeginChange();

            foreach (var o in objects)
            {
                o.Line = ShiftLine(o.Line, applied);
                o.StartTime = o.Line.StartTime;
                editorBeatmap.Update(o);
            }

            editorBeatmap.EndChange();
        }

        /// <summary>Returns a copy of <paramref name="line"/> with all times moved by <paramref name="deltaMs"/>.</summary>
        public static LyricLine ShiftLine(LyricLine line, double deltaMs) => new LyricLine
        {
            RawText = line.RawText,
            StartTime = line.StartTime + deltaMs,
            EndTime = line.EndTime + deltaMs,
            SingEndTime = line.SingEndTime + deltaMs,
            SealGraceMs = line.SealGraceMs,
            Estimated = line.Estimated,
            Units = line.Units.Select(u => new TimedUnit
            {
                Text = u.Text,
                StartTime = u.StartTime + deltaMs,
                EndTime = u.EndTime + deltaMs,
                Source = u.Source,
                Confidence = u.Confidence,
                SyllableBoundaries = u.SyllableBoundaries.Count == 0
                    ? u.SyllableBoundaries
                    : u.SyllableBoundaries.Select(b => b + deltaMs).ToArray(),
                // Splits are CHAR indices: a time shift cannot invalidate one, so they ride
                // through every shift/offset operation untouched.
                SyllableSplits = u.SyllableSplits,
            }).ToArray(),
        };

        /// <summary>
        /// Replaces every hit object with fresh ones built from <paramref name="lines"/> (e.g. the
        /// output of an in-editor re-alignment). Wrapped in one transaction so it is a single undo.
        /// </summary>
        public static void ReplaceLines(EditorBeatmap editorBeatmap, IReadOnlyList<LyricLine> lines, TimingGranularity granularity)
        {
            editorBeatmap.BeginChange();
            editorBeatmap.Clear();

            var objects = new List<Rulesets.Objects.HitObject>(lines.Count);

            for (int i = 0; i < lines.Count; i++)
            {
                objects.Add(new TypeBeatHitObject
                {
                    StartTime = lines[i].StartTime,
                    LineIndex = i,
                    Line = lines[i],
                    Granularity = granularity,
                });
            }

            editorBeatmap.AddRange(objects);
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// The finest granularity the unit data requires: Syllable when any word carries subdivision
        /// boundaries, else Word when some unit timing is Explicit (authored words[]), else Line.
        /// Line-granularity maps also carry one unit per token, but those are Interpolated,
        /// synthesized by the loader, not real word timing.
        /// </summary>
        public static TimingGranularity InferGranularity(IReadOnlyList<LyricLine> lines)
        {
            if (lines.Any(l => l.Units.Any(u => u.SyllableBoundaries.Count > 0)))
                return TimingGranularity.Syllable;
            if (lines.Any(l => l.Units.Any(u => u.Source == TimingSource.Explicit)))
                return TimingGranularity.Word;
            return TimingGranularity.Line;
        }

        /// <summary>Smallest line/word span the editor will produce, so nothing degenerates to zero width.</summary>
        public const double MIN_SPAN_MS = 30;

        /// <summary>Smallest syllable segment (and gap between subdivision boundaries) the editor will produce.</summary>
        public const double MIN_SYLLABLE_MS = 20;

        /// <summary>The last line's typeable window extends this far past its sung end (mirrors the loader).</summary>
        public const double LAST_LINE_TAIL_MS = TimingJsonLoader.LAST_LINE_TAIL_MS;

        #region Single-line rebuild helpers (model is init-only: every edit builds new instances)

        private static LyricLine rebuild(LyricLine line, string? rawText = null, double? start = null, double? end = null,
                                         double? singEnd = null, IReadOnlyList<TimedUnit>? units = null, double? sealGrace = null)
            => new LyricLine
            {
                RawText = rawText ?? line.RawText,
                StartTime = start ?? line.StartTime,
                EndTime = end ?? line.EndTime,
                SingEndTime = singEnd ?? line.SingEndTime,
                Units = units ?? line.Units,
                SealGraceMs = sealGrace ?? line.SealGraceMs,
                Estimated = line.Estimated,
            };

        private static TimedUnit retime(TimedUnit unit, double start, double end, TimingSource? source = null, double? confidence = null)
        {
            // Syllable subdivisions ride along, clamped to the new span (any that fall outside the
            // re-timed window are dropped: the word shrank past them).
            var boundaries = clampBoundaries(unit.SyllableBoundaries, start, end);

            return new TimedUnit
            {
                Text = unit.Text,
                StartTime = start,
                EndTime = end,
                Source = source ?? unit.Source,
                Confidence = confidence ?? unit.Confidence,
                SyllableBoundaries = boundaries,
                // An authored char split is only meaningful against the boundary count it was
                // authored for: if the clamp dropped one, every remaining split would pair with the
                // wrong segment, so the word falls back to the derived split rather than lie.
                SyllableSplits = boundaries.Count == unit.SyllableBoundaries.Count ? unit.SyllableSplits : Array.Empty<int>(),
            };
        }

        /// <summary>Keeps only boundaries strictly inside (start, end), sorted; empty stays empty.</summary>
        private static IReadOnlyList<double> clampBoundaries(IReadOnlyList<double> boundaries, double start, double end)
        {
            if (boundaries.Count == 0)
                return boundaries;

            var kept = boundaries.Where(b => b > start + 1e-3 && b < end - 1e-3).Distinct().OrderBy(b => b).ToArray();
            return kept.Length == 0 ? System.Array.Empty<double>() : kept;
        }

        #endregion

        #region Interactive edits (each wraps its own transaction => one undo step)

        /// <summary>
        /// Moves the boundary between a line and its predecessor: the line's StartTime and the
        /// previous line's EndTime move together (the format derives EndTime from the next line's
        /// start, so they are one degree of freedom). Clamped so both lines keep
        /// <see cref="MIN_SPAN_MS"/>; sung ends and unit times are re-clamped into their windows.
        /// </summary>
        public static void SetLineStart(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, double newStart)
        {
            var ordered = orderedLines(editorBeatmap);
            int index = ordered.IndexOf(hitObject);

            if (index < 0)
                return;

            var previous = index > 0 ? ordered[index - 1] : null;

            double min = previous != null ? previous.Line.StartTime + MIN_SPAN_MS : 0;
            double max = hitObject.Line.EndTime - MIN_SPAN_MS;

            // Degenerate window: this line and its predecessor are already so compressed that
            // there is no room to move the boundary without violating MIN_SPAN_MS. No-op rather
            // than clamp into an inverted range (which would crash Math.Clamp below).
            if (max < min)
                return;

            newStart = Math.Clamp(newStart, min, max);

            editorBeatmap.BeginChange();

            var line = hitObject.Line;
            double newSingEnd = Math.Clamp(line.SingEndTime, newStart, line.EndTime);
            hitObject.Line = rebuild(line,
                start: newStart,
                singEnd: newSingEnd,
                units: unitsFor(hitObject, line.RawText, line.Units, newStart, newSingEnd, line.EndTime));
            hitObject.StartTime = newStart;
            editorBeatmap.Update(hitObject);

            if (previous != null)
            {
                var prevLine = previous.Line;
                double prevSingEnd = Math.Clamp(prevLine.SingEndTime, prevLine.StartTime, newStart);
                previous.Line = rebuild(prevLine,
                    end: newStart,
                    singEnd: prevSingEnd,
                    units: unitsFor(previous, prevLine.RawText, prevLine.Units, prevLine.StartTime, prevSingEnd, newStart));
                editorBeatmap.Update(previous);
            }

            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Sets a line's sung end (the vocal-end estimate; persisted as end_ms). For the LAST line
        /// this also drags the derived typeable window (EndTime = singEnd + tail), mirroring reload.
        ///
        /// <para>Since backlog 246 this is NOT a lever the mapper reaches directly: the editor has no
        /// sung-end marker any more, and end_ms is auto-derived from the last word's end (see
        /// <see cref="syncSingEndToLastUnit"/>). The one gesture that still routes here is the
        /// LINE-granularity block-end drag, which needs this op's whole-line re-spread through
        /// <see cref="unitsFor"/>. See <see cref="SetUnitEnd"/>.</para>
        /// </summary>
        public static void SetSingEnd(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, double newSingEnd)
        {
            var ordered = orderedLines(editorBeatmap);
            bool isLast = ordered.Count > 0 && ordered[^1] == hitObject;

            var line = hitObject.Line;
            double singEndMin = line.StartTime + MIN_SPAN_MS;
            double singEndMax = isLast ? double.MaxValue : line.EndTime;

            // A non-last line shorter than MIN_SPAN_MS has no movable sung-end; no-op rather than
            // clamp into an inverted [min, max] (which would crash Math.Clamp).
            if (singEndMax < singEndMin)
                return;

            newSingEnd = Math.Clamp(newSingEnd, singEndMin, singEndMax);

            // The last line's typeable window is derived on reload as min(song_end, singEnd + tail),
            // so it must stay within [singEnd, singEnd + tail] or the reload clamps it differently
            // than the editor showed.
            double newEnd = isLast ? Math.Clamp(line.EndTime, newSingEnd, newSingEnd + LAST_LINE_TAIL_MS) : line.EndTime;

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line,
                singEnd: newSingEnd,
                end: newEnd,
                units: unitsFor(hitObject, line.RawText, line.Units, line.StartTime, newSingEnd, newEnd));
            editorBeatmap.Update(hitObject);
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Resizes one word unit's edges independently. Each edge is clamped inside the line's
        /// window and against the neighbouring units (the loader forces non-decreasing order on
        /// reload; allowing overlap here would silently drift). A hand-timed unit becomes Explicit
        /// and fully trusted, the line stops being Estimated, and the beatmap is promoted to Word
        /// granularity if it was Line. The encoder only persists words[] for Word maps, so without
        /// the flip a hand-timed word would silently vanish on save.
        /// </summary>
        public static void SetUnitTiming(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, double newStart, double newEnd)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            double lower = unitIndex > 0 ? line.Units[unitIndex - 1].EndTime : line.StartTime;
            double upper = unitIndex < line.Units.Count - 1 ? line.Units[unitIndex + 1].StartTime : line.EndTime;

            // The neighbours (or the line window) leave this unit less than MIN_SPAN_MS of room;
            // there is nowhere to retime it to. No-op rather than clamp into an inverted range.
            // (Aligner output routinely packs short function words under 30ms apart.)
            if (upper - lower < MIN_SPAN_MS)
                return;

            newStart = Math.Clamp(newStart, lower, upper - MIN_SPAN_MS);
            newEnd = Math.Clamp(newEnd, newStart + MIN_SPAN_MS, upper);

            applyUnit(editorBeatmap, hitObject, unitIndex, newStart, newEnd);
        }

        /// <summary>
        /// The word-block END drag, which since backlog 246 is also the editor's only sung-end lever
        /// (the blue sung-end flag is gone).
        ///
        /// <para>On a Word or Syllable map this is a plain <see cref="SetUnitTiming"/> resize, and
        /// the line's end_ms follows the last word by itself through
        /// <see cref="syncSingEndToLastUnit"/>.</para>
        ///
        /// <para>On a LINE-granularity map, dragging the LAST word's end is instead the line-wide
        /// re-spread the flag used to perform: such a map has no authored word timing, so the line's
        /// own bounds ARE its timing and every unit re-interpolates across the new span
        /// (<see cref="SetSingEnd"/> then <see cref="unitsFor"/>). Dragging an INTERIOR block on the
        /// same map still promotes it to Word granularity, because dragging one IS authoring word
        /// timing; only the block that sits at the line's sung end carries the line-wide meaning.</para>
        /// </summary>
        public static void SetUnitEnd(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, double newStart, double newEnd)
        {
            if (hitObject.Granularity == TimingGranularity.Line && unitIndex >= 0 && unitIndex == hitObject.Line.Units.Count - 1)
            {
                SetSingEnd(editorBeatmap, hitObject, newEnd);
                return;
            }

            SetUnitTiming(editorBeatmap, hitObject, unitIndex, newStart, newEnd);
        }

        /// <summary>
        /// Moves the boundary that two TOUCHING word units share: the left word's end and the right
        /// word's start move together, the way <see cref="SetLineStart"/> moves a line boundary.
        /// This is the SHIFT gesture on a word edge; a plain edge drag keeps
        /// <see cref="SetUnitTiming"/>'s single-block semantics, where the neighbour is a hard wall.
        ///
        /// <para>No-op unless <paramref name="leftIndex"/> and the unit after it both exist and
        /// their times touch EXACTLY (left.EndTime == right.StartTime). That is the invariant a
        /// clamped plain drag produces, and exact equality is the point: a real gap between two
        /// words is legal data (an instrumental beat, a breath), so grabbing one of its two
        /// independent edges must not silently close it.</para>
        ///
        /// <para>The new boundary is clamped to
        /// [left.StartTime + <see cref="MIN_SPAN_MS"/>, right.EndTime - <see cref="MIN_SPAN_MS"/>],
        /// so neither word degenerates; a pair whose combined span cannot hold two minimum spans is
        /// left alone rather than clamped into an inverted range. Both words become Explicit hand
        /// timing and each keeps only the syllable subdivisions still inside its new span (the same
        /// rule every other resize applies). Single undo step.</para>
        /// </summary>
        public static void SetSharedUnitBoundary(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int leftIndex, double newTime)
        {
            var line = hitObject.Line;

            if (leftIndex < 0 || leftIndex + 1 >= line.Units.Count)
                return;

            var left = line.Units[leftIndex];
            var right = line.Units[leftIndex + 1];

            // Only a genuinely SHARED edge has two sides to move.
            if (left.EndTime != right.StartTime)
                return;

            double min = left.StartTime + MIN_SPAN_MS;
            double max = right.EndTime - MIN_SPAN_MS;

            // The pair is already narrower than two minimum spans: there is no boundary position
            // that leaves both words legal. No-op rather than clamp into an inverted range.
            if (max < min)
                return;

            newTime = Math.Clamp(newTime, min, max);

            // One outer transaction around both writes, so the pair moves as a single undo step
            // (applyUnit opens its own nested transaction, which the change handler ref-counts).
            editorBeatmap.BeginChange();
            applyUnit(editorBeatmap, hitObject, leftIndex, left.StartTime, newTime);
            applyUnit(editorBeatmap, hitObject, leftIndex + 1, newTime, right.EndTime);
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Moves one word unit as a RIGID block (its duration is preserved), clamped so the whole
        /// word stays between its neighbours. Dragging a word into the next one just stops it at
        /// the boundary; it never gets squashed (which independent-edge clamping would do).
        /// </summary>
        public static void MoveUnit(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, double newStart)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            var current = line.Units[unitIndex];
            double duration = current.EndTime - current.StartTime;

            double lower = unitIndex > 0 ? line.Units[unitIndex - 1].EndTime : line.StartTime;
            double upper = unitIndex < line.Units.Count - 1 ? line.Units[unitIndex + 1].StartTime : line.EndTime;

            // No room to fit the word whole between its neighbours; stop rather than resize it.
            if (upper - lower < duration)
                return;

            newStart = Math.Clamp(newStart, lower, upper - duration);
            applyUnit(editorBeatmap, hitObject, unitIndex, newStart, newStart + duration);
        }

        /// <summary>How a group edit transforms each selected unit: rigid move, or drag one edge.</summary>
        public enum UnitGroupEdit
        {
            Move,
            ResizeStart,
            ResizeEnd,
        }

        /// <summary>
        /// Applies ONE uniform time delta to a group of selected word units at once, moving or
        /// stretching them all by the same amount (the distance the mouse travelled), never
        /// clipping each edge straight to the cursor (which would squash individuals differently).
        /// The delta is clamped once, globally, so no unit crosses a non-selected neighbour, the
        /// line window, or shrinks below <see cref="MIN_SPAN_MS"/>. Base positions are the caller's
        /// captured originals (<paramref name="origStart"/>/<paramref name="origEnd"/>), so repeated
        /// per-frame calls stay stable. Selected units become Explicit/Word-granularity, as with a
        /// single hand edit.
        /// </summary>
        public static void EditUnitGroup(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject,
            IReadOnlyList<int> indices, IReadOnlyList<double> origStart, IReadOnlyList<double> origEnd,
            double delta, UnitGroupEdit mode)
        {
            var line = hitObject.Line;
            int count = line.Units.Count;
            double previousLastEnd = lastUnitEnd(line);

            if (indices.Count == 0 || indices.Count != origStart.Count || indices.Count != origEnd.Count)
                return;

            var selected = new HashSet<int>(indices);

            foreach (int i in indices)
            {
                if (i < 0 || i >= count)
                    return;
            }

            // Widest uniform delta every selected unit can take without violating a constraint.
            double minDelta = double.NegativeInfinity;
            double maxDelta = double.PositiveInfinity;

            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                double s = origStart[k];
                double e = origEnd[k];
                double low, high;

                switch (mode)
                {
                    case UnitGroupEdit.ResizeStart:
                        // Start edge moves, end fixed: bounded by the left neighbour's end (fixed in
                        // this mode, so reading it live is stable) and keeping MIN_SPAN width.
                        low = (i > 0 ? line.Units[i - 1].EndTime : line.StartTime) - s;
                        high = (e - MIN_SPAN_MS) - s;
                        break;

                    case UnitGroupEdit.ResizeEnd:
                        // End edge moves, start fixed: bounded by MIN_SPAN width and the right
                        // neighbour's start (fixed in this mode).
                        low = (s + MIN_SPAN_MS) - e;
                        high = (i < count - 1 ? line.Units[i + 1].StartTime : line.EndTime) - e;
                        break;

                    default: // Move, bounded only by the nearest NON-selected neighbours, since the
                             // selected units all translate together and keep their relative spacing.
                        low = nearestNonSelectedEnd(line, selected, i) - s;
                        high = nearestNonSelectedStart(line, selected, i) - e;
                        break;
                }

                minDelta = Math.Max(minDelta, low);
                maxDelta = Math.Min(maxDelta, high);
            }

            // The current (delta == 0) layout is valid, so [minDelta, maxDelta] always contains 0.
            if (minDelta > maxDelta)
                return;

            double applied = Math.Clamp(delta, minDelta, maxDelta);

            var units = line.Units.ToArray();

            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                double ns = origStart[k];
                double ne = origEnd[k];

                switch (mode)
                {
                    case UnitGroupEdit.ResizeStart: ns += applied; break;
                    case UnitGroupEdit.ResizeEnd: ne += applied; break;
                    default: ns += applied; ne += applied; break;
                }

                units[i] = retime(units[i], ns, ne, TimingSource.Explicit, 1);
            }

            editorBeatmap.BeginChange();
            hitObject.Line = new LyricLine
            {
                RawText = line.RawText,
                StartTime = line.StartTime,
                EndTime = line.EndTime,
                SingEndTime = line.SingEndTime,
                Units = units,
                SealGraceMs = line.SealGraceMs,
                Estimated = false,
            };
            editorBeatmap.Update(hitObject);
            promoteToWordGranularity(editorBeatmap);
            syncSingEndToLastUnit(editorBeatmap, hitObject, previousLastEnd);
            editorBeatmap.EndChange();
        }

        private static double nearestNonSelectedEnd(LyricLine line, HashSet<int> selected, int i)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                if (!selected.Contains(j))
                    return line.Units[j].EndTime;
            }

            return line.StartTime;
        }

        private static double nearestNonSelectedStart(LyricLine line, HashSet<int> selected, int i)
        {
            for (int j = i + 1; j < line.Units.Count; j++)
            {
                if (!selected.Contains(j))
                    return line.Units[j].StartTime;
            }

            return line.EndTime;
        }

        /// <summary>Writes one unit's [start, end] back (Explicit, trusted), clearing Estimated and promoting granularity.</summary>
        private static void applyUnit(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, double newStart, double newEnd)
        {
            var line = hitObject.Line;
            double previousLastEnd = lastUnitEnd(line);
            var units = line.Units.ToArray();
            units[unitIndex] = retime(units[unitIndex], newStart, newEnd, TimingSource.Explicit, 1);

            editorBeatmap.BeginChange();
            hitObject.Line = new LyricLine
            {
                RawText = line.RawText,
                StartTime = line.StartTime,
                EndTime = line.EndTime,
                SingEndTime = line.SingEndTime,
                Units = units,
                SealGraceMs = line.SealGraceMs,
                Estimated = false, // hand timing IS acoustic evidence; judge at full granularity again.
            };
            editorBeatmap.Update(hitObject);
            promoteToWordGranularity(editorBeatmap);
            syncSingEndToLastUnit(editorBeatmap, hitObject, previousLastEnd);
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Tap-to-time: stamps the unit's START at the given (playhead) time, keeping its end
        /// (pushed if needed); retime a whole line by ear in one playback pass. Same Explicit
        /// promotion rules as <see cref="SetUnitTiming"/>.
        /// </summary>
        public static void StampUnitStart(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, double time)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            double end = Math.Max(line.Units[unitIndex].EndTime, time + MIN_SPAN_MS);
            SetUnitTiming(editorBeatmap, hitObject, unitIndex, time, end);
        }

        /// <summary>
        /// Replaces a line's typed text ("yeah" -> "yeaaaaaaaah"). The raw input is normalized
        /// through the game's typeability rules, except for two authoring seams that survive it:
        /// each '&amp;' (<see cref="Typeability.FREESTYLE_MARKER"/>) is STORED as a FREESTYLE cell,
        /// a slot the player may fill with any key but space; each '|'
        /// (<see cref="Typeability.SPLIT_MARKER"/>) is READ as a syllable split and then stripped,
        /// so it never reaches the stored lyric. When the token count is unchanged, each word
        /// keeps its timing; otherwise timings are redistributed (char-weighted) across the sung
        /// window. Returns false (no change) when the text normalizes to empty; an empty line
        /// cannot exist in the format; delete the line instead.
        ///
        /// <para>The pipe matrix, per word (see <see cref="splitsFromPipes"/> for the code). The
        /// rule behind all of it: the committed line box is AUTHORITATIVE for every word whose
        /// token text came back unchanged, since the box is always pre-filled with
        /// <see cref="PipeDisplayText"/> and therefore always shows the mapper the pipes they are
        /// committing.</para>
        /// <list type="bullet">
        /// <item>a word with NO subdivisions: the pipes AUTHOR one (backlog 202). The word's own
        /// span is cut into (pipes + 1) EQUAL segments and the split is recorded where the pipes
        /// sat, so "fri|ed" on a plain word is the same gesture as adding a dotted line on the
        /// timeline and dragging its characters. The map is promoted with it, because the encoder
        /// persists syllables[] only for units that carry boundaries.</item>
        /// <item>B boundaries, ZERO pipes, same token text: the subdivision is REMOVED (backlog
        /// 204), boundaries and split both, which is how a mapper un-subdivides a word from the
        /// line box: deleting the pipe of "fri|ed" has to mean something, and the only thing it can
        /// mean is "this word is not subdivided". The word keeps its own span and becomes Explicit
        /// hand timing, like every other hand edit. No granularity demotion follows: the encoder
        /// simply writes no syllables[] for a boundary-free unit.</item>
        /// <item>B boundaries, B or more pipes: the first B pipe positions become the authored
        /// split; surplus pipes are dropped (the word is already subdivided, and a text commit
        /// does not change a boundary COUNT).</item>
        /// <item>B boundaries, fewer pipes but at least one: the pipes given replace the leading
        /// splits and the remaining ones keep the value the word already showed (authored or
        /// derived). Only the ZERO case removes.</item>
        /// <item>a pipe that would leave a segment EMPTY (at the start or end of the word, or on
        /// top of another pipe): the whole word keeps its previous split, so a typo cannot silently
        /// re-cut it.</item>
        /// <item>a result equal to the DERIVED split is stored as derived (empty), so committing a
        /// line the mapper did not actually re-split writes no <c>split_chars</c> at all.</item>
        /// </list>
        ///
        /// <para>A word count change redistributes every span, but no longer drops every
        /// subdivision: <see cref="alignSubdivisions"/> anchors the words that came back spelled
        /// exactly as they were and rescales their boundaries into their new spans, so inserting or
        /// deleting one word leaves the others subdivided. A REWORDED word re-derives, as it always
        /// did.</para>
        /// </summary>
        public static bool SetLineText(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, string rawUserText)
        {
            // Both authoring seams survive Normalize here; every other untypeable char is stripped.
            string withMarkers = Typeability.Normalize(Typeability.StripBackingVocals(rawUserText),
                keepFreestyleMarkers: true, keepSplitMarkers: true);

            var (normalized, pipes) = SplitMarkers.Strip(withMarkers);

            // Rejected when there is nothing to TYPE, measured on the default stream: text that is
            // only punctuation ("...") normalizes non-empty but would give the player no cell, and
            // an empty line cannot exist in the format.
            if (Typeability.ToDefaultStream(normalized).Length == 0)
                return false;

            var line = hitObject.Line;

            // Measured on the STRIPPED text, so "fri|ed" over "fried" reads as unchanged text; the
            // pipes are the whole edit there, and they are picked up below rather than here.
            bool textUnchanged = normalized == line.RawText;
            bool anyPipes = pipes.Any(p => p.Count > 0);

            string[] tokens = normalized.Split(' ');
            IReadOnlyList<TimedUnit> units;
            bool authoredSubdivision = false;

            // Deleting a pipe leaves the STRIPPED text exactly as it was, so the no-op early-outs
            // below have to ask whether the pipe SET shrank against what the box was showing;
            // otherwise the one gesture that removes a subdivision is the one gesture swallowed.
            bool anyRemoval = removesSubdivision(line.Units, tokens, pipes);

            if (hitObject.Granularity == TimingGranularity.Line)
            {
                if (textUnchanged && !anyPipes && !anyRemoval)
                    return true;

                // Line-granularity maps persist no word data; units are always the loader's
                // interpolation, which is text-weight-dependent, so re-derive with the new text.
                // The pipes then subdivide those fresh units exactly as they would on a word map
                // (and a deleted pipe simply does not come back through the re-derivation).
                units = LrcParser.InterpolateUnits(normalized, line.StartTime, line.SingEndTime);

                if (anyPipes && tokens.Length == units.Count)
                    units = applyPipes(units, tokens, pipes, out authoredSubdivision);
            }
            else if (tokens.Length == line.Units.Count)
            {
                // Same word count: keep every word's timing, swap the text, and re-read each word's
                // subdivision from where its pipes now sit (or remove it, where they no longer do).
                units = applyPipes(line.Units, tokens, pipes, out authoredSubdivision);

                // A commit that moved neither the text nor a single pipe is a no-op, so a map with
                // no authored splits does not start carrying them just because a box lost focus.
                if (textUnchanged && !authoredSubdivision && !anyRemoval
                    && !units.Where((u, i) => !sameSplits(u.SyllableSplits, line.Units[i].SyllableSplits)).Any())
                {
                    return true;
                }
            }
            else
            {
                // Word count changed: every span is redistributed within the sung window, but the
                // words that came back spelled exactly as they were are anchored first, so their
                // subdivisions ride the redistribution instead of being thrown away with it. The
                // pipes are then read over the result, so this commit's own cuts land too.
                units = alignSubdivisions(line.Units, LrcParser.InterpolateUnits(normalized, line.StartTime, line.SingEndTime));
                units = applyPipes(units, tokens, pipes, out authoredSubdivision);
            }

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line, rawText: normalized, units: units);
            editorBeatmap.Update(hitObject);
            // A word-count change re-spreads the units across the line's EXISTING sung window, so the
            // new last word lands exactly on the stored end_ms and this is a no-op; a same-count
            // commit keeps every word's timing, so it is a no-op there too. Called anyway so the
            // rule holds by construction rather than by a coincidence of how the units are built.
            syncSingEndToLastUnit(editorBeatmap, hitObject, lastUnitEnd(line));

            // A pipe that just created a boundary needs the map to carry sub-word data at all, or
            // the encoder would drop it on the next save (see AddSyllableBoundary's note). Only
            // done when something was actually authored, so an ordinary text commit never moves a
            // map's granularity in either direction.
            if (authoredSubdivision)
                syncGranularity(editorBeatmap, keepAuthoredWords: true);

            editorBeatmap.EndChange();
            return true;
        }

        /// <summary>
        /// One line's units after a text commit: each word takes its new token text and, from its
        /// pipes, either an AUTHORED subdivision (a word that had none: see
        /// <see cref="SplitMarkers.Authored"/>), a REMOVED one (a subdivided word whose pipes are
        /// all gone: see <see cref="removesSubdivision(TimedUnit, string, IReadOnlyList{int})"/>)
        /// or a re-read of its existing split (<see cref="splitsFromPipes"/>).
        /// <paramref name="authored"/> reports whether any word gained a boundary, which is what
        /// forces the granularity promotion. A removal deliberately reports nothing: granularity
        /// never moves DOWN for it (the encoder omits syllables[] for a boundary-free unit on its
        /// own, and a demotion would risk dropping the map's words[] with it).
        /// </summary>
        private static IReadOnlyList<TimedUnit> applyPipes(IReadOnlyList<TimedUnit> source, string[] tokens,
                                                           IReadOnlyList<IReadOnlyList<int>> pipes, out bool authored)
        {
            bool any = false;

            var units = source.Select((u, i) =>
            {
                var wordPipes = i < pipes.Count ? pipes[i] : Array.Empty<int>();

                if (removesSubdivision(u, tokens[i], wordPipes))
                {
                    return new TimedUnit
                    {
                        Text = tokens[i],
                        StartTime = u.StartTime,
                        EndTime = u.EndTime,
                        // Un-subdividing a word IS a timing decision, exactly as subdividing it is,
                        // so the word carries the same Explicit/trusted stamp either way.
                        Source = TimingSource.Explicit,
                        Confidence = 1,
                    };
                }

                if (u.SyllableBoundaries.Count == 0 && wordPipes.Count > 0
                    && SplitMarkers.Authored(tokens[i], u.StartTime, u.EndTime, wordPipes) is (double[] boundaries, int[] splits))
                {
                    any = true;

                    return new TimedUnit
                    {
                        Text = tokens[i],
                        StartTime = u.StartTime,
                        EndTime = u.EndTime,
                        // A hand-placed subdivision IS hand timing, exactly as it is when the
                        // mapper adds the dotted line on the timeline strip instead.
                        Source = TimingSource.Explicit,
                        Confidence = 1,
                        SyllableBoundaries = boundaries,
                        SyllableSplits = splits,
                    };
                }

                return new TimedUnit
                {
                    Text = tokens[i],
                    StartTime = u.StartTime,
                    EndTime = u.EndTime,
                    Source = u.Source,
                    Confidence = u.Confidence,
                    SyllableBoundaries = u.SyllableBoundaries,
                    SyllableSplits = splitsFromPipes(tokens[i], u.SyllableBoundaries.Count + 1, u.SyllableSplits, wordPipes),
                };
            }).ToArray();

            authored = any;
            return units;
        }

        /// <summary>
        /// Whether one word's committed token DELETES its subdivision: it carries boundaries, it
        /// came back spelled EXACTLY as it was, and not one pipe is left in it. All three matter.
        /// The pipes are only an instruction about a word the mapper could actually see (the box is
        /// pre-filled with <see cref="PipeDisplayText"/>), and a RETYPED word is a different word,
        /// which is why "ape" over a subdivided "apple" keeps its boundary times and merely drops
        /// the char split that no longer fits (the older, forgiving rule).
        /// </summary>
        private static bool removesSubdivision(TimedUnit unit, string token, IReadOnlyList<int> wordPipes)
            => unit.SyllableBoundaries.Count > 0 && wordPipes.Count == 0 && token == unit.Text;

        /// <summary>Whether any word of the line is being un-subdivided by this commit.</summary>
        private static bool removesSubdivision(IReadOnlyList<TimedUnit> units, string[] tokens, IReadOnlyList<IReadOnlyList<int>> pipes)
        {
            if (tokens.Length != units.Count)
                return false;

            for (int i = 0; i < units.Count; i++)
            {
                if (removesSubdivision(units[i], tokens[i], i < pipes.Count ? pipes[i] : Array.Empty<int>()))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Carries subdivisions across a WORD COUNT change. <paramref name="redistributed"/> is the
        /// char-weighted re-interpolation of the new text (the span every new word takes, unchanged
        /// by this method); this pairs those words with <paramref name="previous"/> by a two-pointer
        /// walk over IDENTICAL token text, in order, and gives every paired word its old boundaries
        /// RESCALED proportionally into its new span, with the authored split carried verbatim (the
        /// text and the boundary count are identical, so it still describes the word).
        ///
        /// <para>The walk is deliberately dumb and forward-only: for each new word it takes the
        /// FIRST still-unclaimed old word with the same text, so inserting, appending or deleting
        /// one word leaves every other word's subdivision alone, and ambiguity ("na na na") resolves
        /// leftmost. Its limits are the price of that predictability, and they are by design: a
        /// REWORDED word matches nothing and re-derives (there is no honest place to put the
        /// syllables of a word that no longer exists), and REORDERING keeps only the words the
        /// forward walk still meets in order.</para>
        /// </summary>
        private static IReadOnlyList<TimedUnit> alignSubdivisions(IReadOnlyList<TimedUnit> previous, IReadOnlyList<TimedUnit> redistributed)
        {
            var units = redistributed.ToArray();
            int from = 0;

            for (int i = 0; i < units.Length; i++)
            {
                int match = -1;

                for (int j = from; j < previous.Count; j++)
                {
                    if (previous[j].Text == units[i].Text)
                    {
                        match = j;
                        break;
                    }
                }

                if (match < 0)
                    continue;

                from = match + 1;
                var old = previous[match];

                if (old.SyllableBoundaries.Count == 0)
                    continue;

                var moved = rescaleBoundaries(old.SyllableBoundaries, old.StartTime, old.EndTime, units[i].StartTime, units[i].EndTime);

                // A degenerate span on either side: the word comes through unsubdivided rather than
                // carrying a split that describes a boundary count it no longer has.
                if (moved.Count == 0)
                    continue;

                units[i] = new TimedUnit
                {
                    Text = units[i].Text,
                    // Only the SUBDIVISION travels: the span is the redistribution's, and so is the
                    // Source, because nobody hand-timed where this word now sits.
                    StartTime = units[i].StartTime,
                    EndTime = units[i].EndTime,
                    Source = units[i].Source,
                    Confidence = units[i].Confidence,
                    SyllableBoundaries = moved,
                    SyllableSplits = old.SyllableSplits,
                };
            }

            return units;
        }

        /// <summary>
        /// Boundaries moved from [<paramref name="oldStart"/>, <paramref name="oldEnd"/>] onto
        /// [<paramref name="start"/>, <paramref name="end"/>], each keeping its relative position
        /// in the word (so a boundary strictly inside the old span stays strictly inside the new
        /// one). A degenerate span on either side has no proportion to preserve, so it authors
        /// nothing rather than piling boundaries onto an edge.
        /// </summary>
        private static IReadOnlyList<double> rescaleBoundaries(IReadOnlyList<double> boundaries, double oldStart, double oldEnd, double start, double end)
        {
            if (oldEnd <= oldStart || end <= start || boundaries.Count == 0)
                return Array.Empty<double>();

            double scale = (end - start) / (oldEnd - oldStart);
            var moved = new double[boundaries.Count];

            for (int i = 0; i < boundaries.Count; i++)
                moved[i] = start + (boundaries[i] - oldStart) * scale;

            return moved;
        }

        #region Syllable splits on the line text ("ap|ple")

        /// <summary>
        /// A line's text as the editor's line box SHOWS it: the stored text with a
        /// <see cref="Typeability.SPLIT_MARKER"/> at every subdivided word's EFFECTIVE split, the
        /// authored one where there is one and the derived one otherwise. Showing the derived split
        /// is deliberate: it is the split gameplay's judgement groups already use, so the mapper
        /// edits what the game does rather than an empty field. Identical to the stored text for a
        /// line with no subdivisions at all, and for a line whose tokens and units have drifted
        /// apart (nothing there can be paired safely).
        /// </summary>
        public static string PipeDisplayText(LyricLine line)
        {
            string[] tokens = line.RawText.Split(' ');

            if (tokens.Length != line.Units.Count || !line.Units.Any(u => u.SyllableBoundaries.Count > 0))
                return line.RawText;

            var pieces = new string[tokens.Length];

            for (int i = 0; i < tokens.Length; i++)
            {
                var unit = line.Units[i];

                pieces[i] = unit.SyllableBoundaries.Count == 0
                    ? tokens[i]
                    : string.Join(Typeability.SPLIT_MARKER,
                        SyllableSegments.SegmentTexts(tokens[i], SyllableSegments.SplitsFor(tokens[i], unit.SyllableBoundaries.Count + 1, unit.SyllableSplits)));
            }

            return string.Join(' ', pieces);
        }

        /// <summary>
        /// One word's authored split after a text commit, for a word that ALREADY has boundaries:
        /// see the matrix on <see cref="SetLineText"/> (a word with none takes
        /// <see cref="SplitMarkers.Authored"/> instead). Returns <paramref name="current"/>
        /// unchanged when the pipes do not describe a valid split, so a typo costs the mapper
        /// nothing.
        /// </summary>
        private static IReadOnlyList<int> splitsFromPipes(string token, int segments, IReadOnlyList<int> current, IReadOnlyList<int> pipes)
        {
            if (segments < 2)
                return Array.Empty<int>();

            int wanted = segments - 1;

            // Start from what the word currently SHOWS, so moving one pipe of a three-way split
            // leaves the other two where the mapper saw them.
            var target = SyllableSegments.SplitsFor(token, segments, current).ToList();

            // The derived split degrades to fewer than `wanted` on an over-forced short word; pad
            // with an impossible index so the validity check below rejects it rather than guessing.
            while (target.Count < wanted)
                target.Add(-1);

            if (target.Count > wanted)
                target.RemoveRange(wanted, target.Count - wanted);

            for (int i = 0; i < wanted && i < pipes.Count; i++)
                target[i] = pipes[i];

            if (!SyllableSegments.IsAuthoredValid(token, segments, target))
                return current;

            // Landing exactly on the derived split stays DERIVED: the result is identical and the
            // map keeps no split_chars it does not need.
            return sameSplits(target, SyllableSegments.Derived(token, segments)) ? Array.Empty<int>() : target;
        }

        private static bool sameSplits(IReadOnlyList<int> a, IReadOnlyList<int> b)
        {
            if (a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }

        #endregion

        /// <summary>Placeholder text a freshly inserted word carries until the mapper types over it.</summary>
        public const string NEW_WORD_TEXT = "word";

        /// <summary>
        /// Inserts one word into a line, immediately AFTER <paramref name="afterUnitIndex"/>
        /// (a negative or out-of-range index appends at the line's end). The word is a single
        /// token: the mapper renames it by retyping the line text, which keeps every word's timing
        /// while the token count is unchanged (see <see cref="SetLineText"/>).
        ///
        /// Timing is carved so no existing word moves where possible: the new word takes the free
        /// gap after its anchor (the word it was inserted after), capped at the anchor's own
        /// duration so an append at the end of a line does not swallow the whole tail. When the
        /// words are packed edge to edge the anchor is BISECTED and the new word takes its second
        /// half (the same "split the space you have" idiom as <see cref="AddSyllableBoundary"/>);
        /// any of the anchor's syllable subdivisions that fall outside its shortened span go with
        /// the halved segment. Returns false when there is no room at all, or when the text
        /// normalizes to something other than a single token.
        ///
        /// Line-granularity maps persist no word data (the loader re-interpolates units from the
        /// text on every load), so there the edit IS the text edit and the units are re-derived.
        /// Single undo step.
        /// </summary>
        public static bool AddWord(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int afterUnitIndex, string text = NEW_WORD_TEXT)
        {
            // Same authoring seam as SetLineText: '&' survives as a FREESTYLE cell, everything
            // else untypeable is stripped.
            string normalized = Typeability.Normalize(Typeability.StripBackingVocals(text), keepFreestyleMarkers: true);

            if (Typeability.ToDefaultStream(normalized).Length == 0 || normalized.Contains(' '))
                return false;

            var line = hitObject.Line;
            string[] tokens = line.RawText.Split(' ');
            int n = line.Units.Count;
            int insertAt;
            IReadOnlyList<TimedUnit> units;

            if (hitObject.Granularity == TimingGranularity.Line)
            {
                // No persisted word timing: the words ARE the tokens, and unitsFor re-interpolates
                // the whole line below, so nothing has to be carved here.
                insertAt = afterUnitIndex >= 0 ? Math.Min(afterUnitIndex + 1, tokens.Length) : tokens.Length;
                units = line.Units;
            }
            else
            {
                // Word/Syllable maps persist units verbatim, so the token/unit pairing is the
                // invariant every word op rests on. A line that has drifted out of it is left
                // alone rather than corrupted further.
                if (n == 0 || tokens.Length != n)
                    return false;

                insertAt = afterUnitIndex >= 0 && afterUnitIndex < n ? afterUnitIndex + 1 : n;

                var anchor = line.Units[insertAt - 1];
                double anchorSpan = anchor.EndTime - anchor.StartTime;
                double lo = anchor.EndTime;
                double hi = insertAt < n ? line.Units[insertAt].StartTime : line.EndTime;

                var rebuilt = line.Units.ToList();
                double start, end;

                if (hi - lo >= MIN_SPAN_MS)
                {
                    start = lo;
                    end = lo + Math.Min(hi - lo, Math.Max(MIN_SPAN_MS, anchorSpan));
                }
                else if (anchorSpan >= MIN_SPAN_MS * 2)
                {
                    double mid = (anchor.StartTime + anchor.EndTime) / 2;
                    start = mid;
                    end = anchor.EndTime;
                    rebuilt[insertAt - 1] = retime(anchor, anchor.StartTime, mid);
                }
                else
                {
                    // Neither a gap nor an anchor wide enough to halve: nowhere to put a word.
                    return false;
                }

                rebuilt.Insert(insertAt, new TimedUnit
                {
                    Text = normalized,
                    StartTime = start,
                    EndTime = end,
                    // Editor-authored placement is hand timing: it must persist in words[] verbatim.
                    Source = TimingSource.Explicit,
                    Confidence = 1,
                });

                units = rebuilt;
            }

            var newTokens = tokens.ToList();
            newTokens.Insert(insertAt, normalized);
            string rawText = string.Join(' ', newTokens);

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line,
                rawText: rawText,
                units: unitsFor(hitObject, rawText, units, line.StartTime, line.SingEndTime, line.EndTime));
            editorBeatmap.Update(hitObject);
            // Bisecting the anchor can strip subdivisions that no longer fit inside its half.
            syncGranularity(editorBeatmap, keepAuthoredWords: true);
            // An append at the tail gives the line a new last word, so its sung end follows.
            syncSingEndToLastUnit(editorBeatmap, hitObject, lastUnitEnd(line));
            editorBeatmap.EndChange();
            return true;
        }

        /// <summary>
        /// Removes one word from a line: its token, its unit and its syllable subdivisions all go,
        /// and the span it held is left as a gap (no neighbour is stretched over it, so nothing the
        /// mapper already timed shifts under them). The LAST remaining word is never removed: an
        /// empty line cannot exist in the format (the decoder drops a line whose text normalizes to
        /// nothing), so delete the line instead. Returns false when nothing was removed.
        /// Single undo step.
        /// </summary>
        public static bool RemoveWord(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex)
        {
            var line = hitObject.Line;
            string[] tokens = line.RawText.Split(' ');
            int n = line.Units.Count;

            if (unitIndex < 0)
                return false;

            IReadOnlyList<TimedUnit> units;

            if (hitObject.Granularity == TimingGranularity.Line)
            {
                // Words are the tokens here (units are re-interpolated on every load).
                if (unitIndex >= tokens.Length || tokens.Length <= 1)
                    return false;

                units = line.Units;
            }
            else
            {
                if (unitIndex >= n || tokens.Length != n || n <= 1)
                    return false;

                units = line.Units.Where((_, i) => i != unitIndex).ToArray();
            }

            string rawText = string.Join(' ', tokens.Where((_, i) => i != unitIndex));

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line,
                rawText: rawText,
                units: unitsFor(hitObject, rawText, units, line.StartTime, line.SingEndTime, line.EndTime));
            editorBeatmap.Update(hitObject);
            // The removed word may have carried the map's last syllable subdivisions.
            syncGranularity(editorBeatmap, keepAuthoredWords: true);
            // Removing the TAIL word leaves an earlier word last, so the line's sung end follows it.
            syncSingEndToLastUnit(editorBeatmap, hitObject, lastUnitEnd(line));
            editorBeatmap.EndChange();
            return true;
        }

        /// <summary>
        /// Splits a line before the given unit index: words [0, index) stay, words [index, n)
        /// become a new line starting at that word's start time. No-op for edge indices.
        /// </summary>
        public static void SplitLine(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int firstUnitOfSecondLine)
        {
            var line = hitObject.Line;
            string[] tokens = line.RawText.Split(' ');

            if (firstUnitOfSecondLine <= 0 || firstUnitOfSecondLine >= line.Units.Count || tokens.Length != line.Units.Count)
                return;

            double boundary = line.Units[firstUnitOfSecondLine].StartTime;

            if (boundary - line.StartTime < MIN_SPAN_MS || line.EndTime - boundary < MIN_SPAN_MS)
                return;

            var firstUnits = line.Units.Take(firstUnitOfSecondLine).ToArray();
            var secondUnits = line.Units.Skip(firstUnitOfSecondLine).ToArray();

            string firstText = string.Join(' ', tokens.Take(firstUnitOfSecondLine));
            string secondText = string.Join(' ', tokens.Skip(firstUnitOfSecondLine));
            double firstSingEnd = Math.Clamp(firstUnits[^1].EndTime, line.StartTime, boundary);
            double secondSingEnd = Math.Max(line.SingEndTime, boundary + MIN_SPAN_MS);

            editorBeatmap.BeginChange();

            hitObject.Line = rebuild(line,
                rawText: firstText,
                end: boundary,
                singEnd: firstSingEnd,
                units: unitsFor(hitObject, firstText, firstUnits, line.StartTime, firstSingEnd, boundary),
                sealGrace: 0);
            editorBeatmap.Update(hitObject);

            editorBeatmap.Add(new TypeBeatHitObject
            {
                StartTime = boundary,
                Line = rebuild(line,
                    rawText: secondText,
                    start: boundary,
                    singEnd: secondSingEnd,
                    units: unitsFor(hitObject, secondText, secondUnits, boundary, secondSingEnd, line.EndTime)),
                Granularity = hitObject.Granularity,
            });

            renumber(editorBeatmap);
            editorBeatmap.EndChange();
        }

        /// <summary>Merges a line with its successor (text joined, timing spans both). No-op on the last line.</summary>
        public static void MergeWithNext(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject)
        {
            var ordered = orderedLines(editorBeatmap);
            int index = ordered.IndexOf(hitObject);

            if (index < 0 || index >= ordered.Count - 1)
                return;

            var next = ordered[index + 1];
            var a = hitObject.Line;
            var b = next.Line;

            editorBeatmap.BeginChange();

            string mergedText = a.RawText + " " + b.RawText;

            hitObject.Line = new LyricLine
            {
                RawText = mergedText,
                StartTime = a.StartTime,
                EndTime = b.EndTime,
                SingEndTime = b.SingEndTime,
                Units = unitsFor(hitObject, mergedText, a.Units.Concat(b.Units).ToArray(), a.StartTime, b.SingEndTime, b.EndTime),
                SealGraceMs = b.SealGraceMs,
                Estimated = a.Estimated || b.Estimated,
            };
            editorBeatmap.Update(hitObject);
            editorBeatmap.Remove(next);

            renumber(editorBeatmap);
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Inserts a new line at the given time with placeholder text. The predecessor's typeable
        /// window shrinks to end at the new line's start (the boundary invariant); the new line
        /// runs to where the old window ended (or a default span when appended at the end).
        /// </summary>
        public static TypeBeatHitObject? AddLine(EditorBeatmap editorBeatmap, double startTime, string text = "new line")
        {
            string normalized = Typeability.Normalize(Typeability.StripBackingVocals(text));

            if (Typeability.ToDefaultStream(normalized).Length == 0)
                return null;

            var ordered = orderedLines(editorBeatmap);

            // Reject if any existing line starts within MIN_SPAN_MS of the requested time (covers
            // too-close-to-previous, too-close-to-following, and exact-collision in one guard);
            // otherwise the new line would overlap a neighbour and its saved EndTime (derived from
            // the true next line's start) would not match what the editor showed.
            if (ordered.Any(o => Math.Abs(o.Line.StartTime - startTime) < MIN_SPAN_MS))
                return null;

            var previous = ordered.LastOrDefault(o => o.Line.StartTime < startTime);
            var following = ordered.FirstOrDefault(o => o.Line.StartTime > startTime);

            // end == the true next line's start keeps the boundary invariant EndTime_i == StartTime_(i+1).
            double end = following?.Line.StartTime ?? (previous?.Line.EndTime is double prevEnd && prevEnd > startTime + MIN_SPAN_MS ? prevEnd : startTime + 2000);
            double singEnd = Math.Min(end, startTime + Math.Max(MIN_SPAN_MS, (end - startTime) * 0.8));

            editorBeatmap.BeginChange();

            if (previous != null)
            {
                var prevLine = previous.Line;
                double prevSingEnd = Math.Clamp(prevLine.SingEndTime, prevLine.StartTime, startTime);
                previous.Line = rebuild(prevLine,
                    end: startTime,
                    singEnd: prevSingEnd,
                    units: unitsFor(previous, prevLine.RawText, prevLine.Units, prevLine.StartTime, prevSingEnd, startTime));
                editorBeatmap.Update(previous);
            }

            var added = new TypeBeatHitObject
            {
                StartTime = startTime,
                Line = new LyricLine
                {
                    RawText = normalized,
                    StartTime = startTime,
                    EndTime = end,
                    SingEndTime = singEnd,
                    Units = LrcParser.InterpolateUnits(normalized, startTime, singEnd),
                },
                Granularity = ordered.FirstOrDefault()?.Granularity ?? TimingGranularity.Line,
            };

            editorBeatmap.Add(added);
            renumber(editorBeatmap);
            editorBeatmap.EndChange();

            return added;
        }

        /// <summary>Deletes a line; the predecessor's typeable window extends over the freed span (as reload would derive).</summary>
        public static void DeleteLine(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject)
        {
            var ordered = orderedLines(editorBeatmap);
            int index = ordered.IndexOf(hitObject);

            if (index < 0)
                return;

            editorBeatmap.BeginChange();

            if (index > 0)
            {
                var previous = ordered[index - 1];

                // The predecessor inherits the freed span. When it becomes the LAST line, the
                // reload-derived window caps at singEnd + tail; apply the same cap here.
                double inheritedEnd = index == ordered.Count - 1
                    ? Math.Clamp(hitObject.Line.EndTime, previous.Line.SingEndTime, previous.Line.SingEndTime + LAST_LINE_TAIL_MS)
                    : hitObject.Line.EndTime;

                previous.Line = rebuild(previous.Line, end: inheritedEnd);
                editorBeatmap.Update(previous);
            }

            editorBeatmap.Remove(hitObject);
            renumber(editorBeatmap);
            editorBeatmap.EndChange();
        }

        #endregion

        #region Syllable subdivisions (per-word dotted-line boundaries)

        /// <summary>
        /// Adds one syllable-subdivision boundary inside the given word unit, bisecting its widest
        /// current segment (so successive presses keep splitting evenly). The word becomes Explicit
        /// hand timing and the beatmap is promoted to Syllable granularity. The encoder only
        /// persists syllables[] for units that carry boundaries, so without the promotion a
        /// subdivision would silently vanish on save. No-op when the widest segment is too narrow
        /// to split into two <see cref="MIN_SYLLABLE_MS"/> halves. Returns the new boundary time (for
        /// the UI to focus the fresh handle), or null when nothing was added.
        /// The inverse press is <see cref="RemoveNarrowestSyllableBoundary"/>.
        /// </summary>
        public static double? AddSyllableBoundary(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return null;

            var unit = line.Units[unitIndex];

            // Segment edges: word start, existing boundaries, word end. Split the widest gap.
            var edges = new List<double> { unit.StartTime };
            edges.AddRange(unit.SyllableBoundaries);
            edges.Add(unit.EndTime);

            double mid = double.NaN;
            double widest = 0;
            int widestSegment = -1;

            for (int i = 0; i < edges.Count - 1; i++)
            {
                double width = edges[i + 1] - edges[i];

                if (width > widest)
                {
                    widest = width;
                    widestSegment = i;
                    mid = (edges[i] + edges[i + 1]) / 2;
                }
            }

            // Even the widest segment cannot hold two MIN_SYLLABLE_MS halves; no room to subdivide.
            if (double.IsNaN(mid) || widest < MIN_SYLLABLE_MS * 2)
                return null;

            var boundaries = unit.SyllableBoundaries.Append(mid).OrderBy(b => b).ToArray();
            replaceUnitBoundaries(editorBeatmap, hitObject, unitIndex, boundaries, bisectSplit(unit, widestSegment));
            return mid;
        }

        /// <summary>
        /// The authored split a word keeps when <see cref="AddSyllableBoundary"/> bisects segment
        /// <paramref name="segment"/> in TIME: its characters are bisected too, so the new dotted
        /// line lands between "ap" and "ple" rather than re-cutting the whole word.
        ///
        /// <para>A word still on the DERIVED split keeps deriving (empty): the syllabifier simply
        /// re-answers for the higher count, which is exactly what happened before splits existed.
        /// A segment of fewer than two characters cannot be bisected, so the word falls back to
        /// derived rather than authoring an empty segment.</para>
        /// </summary>
        private static IReadOnlyList<int> bisectSplit(TimedUnit unit, int segment)
        {
            int segments = unit.SyllableBoundaries.Count + 1;

            if (segment < 0 || !SyllableSegments.IsAuthoredValid(unit.Text, segments, unit.SyllableSplits))
                return Array.Empty<int>();

            var splits = unit.SyllableSplits.ToList();
            int lo = segment > 0 ? splits[segment - 1] : 0;
            int hi = segment < splits.Count ? splits[segment] : unit.Text.Length;

            if (hi - lo < 2)
                return Array.Empty<int>();

            splits.Insert(segment, lo + (hi - lo) / 2);
            return splits;
        }

        /// <summary>
        /// Drags syllable boundary <paramref name="boundaryIndex"/> of a word to
        /// <paramref name="newTime"/>, clamped to stay <see cref="MIN_SYLLABLE_MS"/> inside the word
        /// and from its adjacent boundaries (order preserved). Single undo step.
        /// </summary>
        public static void SetSyllableBoundary(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, int boundaryIndex, double newTime)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            var unit = line.Units[unitIndex];

            if (boundaryIndex < 0 || boundaryIndex >= unit.SyllableBoundaries.Count)
                return;

            double lower = (boundaryIndex > 0 ? unit.SyllableBoundaries[boundaryIndex - 1] : unit.StartTime) + MIN_SYLLABLE_MS;
            double upper = (boundaryIndex < unit.SyllableBoundaries.Count - 1 ? unit.SyllableBoundaries[boundaryIndex + 1] : unit.EndTime) - MIN_SYLLABLE_MS;

            // The word (or the neighbouring boundaries) leaves no valid slot; no-op rather than
            // clamp into an inverted range.
            if (upper < lower)
                return;

            newTime = Math.Clamp(newTime, lower, upper);

            var boundaries = unit.SyllableBoundaries.ToArray();
            boundaries[boundaryIndex] = newTime;
            // The boundary COUNT is unchanged, so the authored split still describes this word.
            replaceUnitBoundaries(editorBeatmap, hitObject, unitIndex, boundaries, unit.SyllableSplits);
        }

        /// <summary>
        /// Moves ONE syllable split of a word: character <paramref name="charIndex"/> becomes the
        /// first character of segment <paramref name="boundaryIndex"/> + 1, so "apple" with
        /// charIndex 2 on boundary 0 reads "ap|ple". Clamped to stay strictly between its
        /// neighbouring splits and inside the word, so no segment is ever emptied; a word with no
        /// legal slot left is a no-op.
        ///
        /// <para>A word still on the DERIVED split is MATERIALISED first (the split it currently
        /// shows becomes the split it stores), so moving one dotted line leaves every other one
        /// exactly where the mapper saw it. A result that happens to equal the derived split is
        /// stored as derived, so nothing is pinned that did not need pinning.</para>
        ///
        /// <para>Only the character split moves: no time, no boundary count, so granularity is
        /// untouched. Single undo step.</para>
        /// </summary>
        public static void SetSyllableSplit(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, int boundaryIndex, int charIndex)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            var unit = line.Units[unitIndex];
            int segments = unit.SyllableBoundaries.Count + 1;

            if (boundaryIndex < 0 || boundaryIndex >= segments - 1)
                return;

            var splits = SyllableSegments.SplitsFor(unit.Text, segments, unit.SyllableSplits).ToList();

            // The syllabifier could not even produce this many segments (an over-forced short
            // word); there is nothing coherent to author against.
            if (splits.Count != segments - 1)
                return;

            int lower = (boundaryIndex > 0 ? splits[boundaryIndex - 1] : 0) + 1;
            int upper = (boundaryIndex < splits.Count - 1 ? splits[boundaryIndex + 1] : unit.Text.Length) - 1;

            if (upper < lower)
                return;

            splits[boundaryIndex] = Math.Clamp(charIndex, lower, upper);

            var stored = sameSplits(splits, SyllableSegments.Derived(unit.Text, segments))
                ? Array.Empty<int>()
                : splits.ToArray();

            if (sameSplits(stored, unit.SyllableSplits))
                return;

            var units = line.Units.ToArray();

            units[unitIndex] = new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = unit.Source,
                Confidence = unit.Confidence,
                SyllableBoundaries = unit.SyllableBoundaries,
                SyllableSplits = stored,
            };

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line, units: units);
            editorBeatmap.Update(hitObject);
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Removes syllable boundary <paramref name="boundaryIndex"/> from a word, merging the two
        /// segments it split. When the word's last boundary goes the beatmap reconciles back down to
        /// Word granularity. Single undo step.
        /// </summary>
        public static void RemoveSyllableBoundary(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, int boundaryIndex)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            var unit = line.Units[unitIndex];

            if (boundaryIndex < 0 || boundaryIndex >= unit.SyllableBoundaries.Count)
                return;

            var boundaries = unit.SyllableBoundaries.Where((_, i) => i != boundaryIndex).ToArray();

            // The split that cut the two merged segments apart goes with the boundary; the rest
            // still describe the same characters. A derived word stays derived.
            var splits = SyllableSegments.IsAuthoredValid(unit.Text, unit.SyllableBoundaries.Count + 1, unit.SyllableSplits)
                ? unit.SyllableSplits.Where((_, i) => i != boundaryIndex).ToArray()
                : Array.Empty<int>();

            replaceUnitBoundaries(editorBeatmap, hitObject, unitIndex, boundaries, splits);
        }

        /// <summary>
        /// Removes ONE syllable-subdivision boundary from a word: the inverse of
        /// <see cref="AddSyllableBoundary"/>, and the op behind the editor's "unsubdivide" button.
        ///
        /// <para>WHICH boundary is the mirror of the add rule. Add BISECTS the widest segment, so
        /// remove MERGES the narrowest adjacent PAIR: the boundary whose removal produces the
        /// shortest merged segment goes. Ties take the leftmost, so the choice is deterministic.
        /// Under the add rule's own even splitting the two are exact inverses (subdivide then
        /// unsubdivide gives the word back), and on a hand-dragged word it takes back the finest cut
        /// rather than the one that happens to sit first.</para>
        ///
        /// <para>A word with a single boundary comes back unsubdivided; a word with none is a no-op
        /// (false). Same Explicit stamp and granularity reconciliation as every other subdivision
        /// edit, since it goes through <see cref="RemoveSyllableBoundary"/>. Single undo step.</para>
        /// </summary>
        public static bool RemoveNarrowestSyllableBoundary(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return false;

            var unit = line.Units[unitIndex];

            if (unit.SyllableBoundaries.Count == 0)
                return false;

            // Segment edges: word start, existing boundaries, word end. Boundary b sits between
            // segment b and segment b + 1, so removing it merges [edges[b], edges[b + 2]].
            var edges = new List<double> { unit.StartTime };
            edges.AddRange(unit.SyllableBoundaries);
            edges.Add(unit.EndTime);

            int narrowest = 0;
            double width = double.PositiveInfinity;

            for (int b = 0; b < unit.SyllableBoundaries.Count; b++)
            {
                double merged = edges[b + 2] - edges[b];

                if (merged < width)
                {
                    width = merged;
                    narrowest = b;
                }
            }

            RemoveSyllableBoundary(editorBeatmap, hitObject, unitIndex, narrowest);
            return true;
        }

        /// <summary>
        /// Rebuilds a line with one unit's syllable boundaries replaced. The unit becomes Explicit
        /// and fully trusted (subdivision IS hand timing), the line stops being Estimated, and the
        /// beatmap's granularity is reconciled (up to Syllable while any boundary survives, back to
        /// Word when the last one is removed). Single undo step.
        /// </summary>
        private static void replaceUnitBoundaries(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex,
                                                  IReadOnlyList<double> boundaries, IReadOnlyList<int> splits)
        {
            var line = hitObject.Line;
            var units = line.Units.ToArray();
            var unit = units[unitIndex];

            // Last gate on the authored split: it must be a valid cut of THIS word into the new
            // segment count, or the word goes back to the derived split.
            bool keepSplits = SyllableSegments.IsAuthoredValid(unit.Text, boundaries.Count + 1, splits);

            units[unitIndex] = new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = TimingSource.Explicit,
                Confidence = 1,
                SyllableBoundaries = boundaries.Count == 0 ? Array.Empty<double>() : boundaries.ToArray(),
                SyllableSplits = keepSplits ? splits.ToArray() : Array.Empty<int>(),
            };

            editorBeatmap.BeginChange();
            hitObject.Line = new LyricLine
            {
                RawText = line.RawText,
                StartTime = line.StartTime,
                EndTime = line.EndTime,
                SingEndTime = line.SingEndTime,
                Units = units,
                SealGraceMs = line.SealGraceMs,
                Estimated = false,
            };
            editorBeatmap.Update(hitObject);
            syncGranularity(editorBeatmap);
            editorBeatmap.EndChange();
        }

        #endregion

        #region Timing copy/paste (see LyricTimingClipboard for the payload semantics)

        /// <summary>
        /// Snapshots the given lines' INTERNAL timing (unit spans + sung end, as offsets from each
        /// line's start) in the given order. Pair with <see cref="PasteLineTimings"/>.
        /// </summary>
        public static LyricTimingClipboard.LineTimingsPayload CopyLineTimings(IEnumerable<TypeBeatHitObject> lines)
        {
            var payload = new LyricTimingClipboard.LineTimingsPayload();

            foreach (var hitObject in lines)
            {
                var line = hitObject.Line;
                var entry = new LyricTimingClipboard.LineTimings { SingEndOffset = line.SingEndTime - line.StartTime };

                foreach (var unit in line.Units)
                {
                    entry.Units.Add(new LyricTimingClipboard.UnitSpan
                    {
                        Start = unit.StartTime - line.StartTime,
                        End = unit.EndTime - line.StartTime,
                    });
                }

                payload.Lines.Add(entry);
            }

            return payload;
        }

        /// <summary>
        /// Applies copied line timings onto <paramref name="targets"/> (in order), REBASED to each
        /// target's own start; line boundaries never move, so nothing cascades through the
        /// shared-boundary chain. One copied line broadcasts to every target (chorus line repeated
        /// N times); multiple copied lines zip positionally (extra targets are left untouched).
        ///
        /// Timings are pasted regardless of whether the words match ("if the words are different,
        /// overwrite"): with equal word counts each word takes the source word's span; with more
        /// target words than source spans, the leftovers are interpolated across the remaining
        /// sung window; with fewer, surplus spans are dropped. Everything is clamped monotonically
        /// into the target's window, pasted words become Explicit hand timing, and the whole paste
        /// is a single undo step.
        /// </summary>
        public static void PasteLineTimings(EditorBeatmap editorBeatmap, IReadOnlyList<TypeBeatHitObject> targets, LyricTimingClipboard.LineTimingsPayload payload)
        {
            if (targets.Count == 0 || payload.Lines.Count == 0)
                return;

            var ordered = orderedLines(editorBeatmap);
            bool broadcast = payload.Lines.Count == 1;
            int pairCount = broadcast ? targets.Count : Math.Min(targets.Count, payload.Lines.Count);

            editorBeatmap.BeginChange();

            for (int t = 0; t < pairCount; t++)
            {
                var target = targets[t];

                if (!editorBeatmap.HitObjects.Contains(target))
                    continue;

                var source = payload.Lines[broadcast ? 0 : t];
                var line = target.Line;

                // Sung end rebased into the target's window. For the LAST line the typeable
                // window is reload-derived as min(song_end, singEnd + tail); apply the same
                // clamp SetSingEnd uses or the saved map would reopen differently.
                bool isLast = ordered.Count > 0 && ordered[^1] == target;
                double singEnd = Math.Clamp(line.StartTime + source.SingEndOffset, line.StartTime + MIN_SPAN_MS, line.EndTime);
                double end = isLast ? Math.Clamp(line.EndTime, singEnd, singEnd + LAST_LINE_TAIL_MS) : line.EndTime;

                int n = line.Units.Count;
                int mapped = Math.Min(n, source.Units.Count);
                var units = new TimedUnit[n];

                for (int i = 0; i < mapped; i++)
                {
                    units[i] = retime(line.Units[i],
                        line.StartTime + source.Units[i].Start,
                        line.StartTime + source.Units[i].End,
                        TimingSource.Explicit, 1);
                }

                // More words than the source pattern has spans: spread the leftovers across the
                // remaining sung window (the same surface interpolation lives on) so the line
                // stays fully timed. They are synthesized, so they stay Interpolated. Each gets
                // at least MIN_SPAN_MS where the window allows, so none degenerates to zero width.
                if (mapped < n)
                {
                    double from = mapped > 0 ? units[mapped - 1].EndTime : line.StartTime;
                    int remaining = n - mapped;
                    double to = Math.Min(end, Math.Max(singEnd, from + MIN_SPAN_MS * remaining));

                    for (int i = 0; i < remaining; i++)
                    {
                        double s = from + (to - from) * i / remaining;
                        double e = from + (to - from) * (i + 1) / remaining;
                        units[mapped + i] = retime(line.Units[mapped + i], s, e, TimingSource.Interpolated, 0.5);
                    }
                }

                target.Line = new LyricLine
                {
                    RawText = line.RawText,
                    StartTime = line.StartTime,
                    EndTime = end,
                    SingEndTime = singEnd,
                    Units = clampUnits(units, line.StartTime, end),
                    SealGraceMs = line.SealGraceMs,
                    Estimated = false, // pasted hand timing is acoustic evidence, same as a drag.
                };
                editorBeatmap.Update(target);
            }

            promoteToWordGranularity(editorBeatmap);
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// Snapshots the given word units' spans (in ascending index order, gaps collapsed) as
        /// offsets from the FIRST selected unit's start. Pair with <see cref="PasteUnitTimings"/>.
        /// </summary>
        public static LyricTimingClipboard.UnitTimingsPayload? CopyUnitTimings(TypeBeatHitObject hitObject, IEnumerable<int> indices)
        {
            var line = hitObject.Line;
            var sorted = indices.Where(i => i >= 0 && i < line.Units.Count).Distinct().OrderBy(i => i).ToList();

            if (sorted.Count == 0)
                return null;

            double anchor = line.Units[sorted[0]].StartTime;
            var payload = new LyricTimingClipboard.UnitTimingsPayload();

            foreach (int i in sorted)
            {
                payload.Units.Add(new LyricTimingClipboard.UnitSpan
                {
                    Start = line.Units[i].StartTime - anchor,
                    End = line.Units[i].EndTime - anchor,
                });
            }

            return payload;
        }

        /// <summary>
        /// Applies a copied unit-run pattern to consecutive words starting at
        /// <paramref name="anchorIndex"/>, anchored at that word's CURRENT start (the phrase stays
        /// where it sits; its internal rhythm is overwritten). Spans past the end of the line's
        /// word list are dropped; the result is clamped monotonically into the line window (words
        /// after the pasted run are pushed, never reordered). Single undo step.
        ///
        /// <para>SYLLABLE SPLITS DO NOT TRAVEL, and neither do subdivision boundaries: the payload
        /// is word SPANS only. Each target word keeps its own boundaries (re-clamped into the
        /// pasted span) and therefore its own split, dropped to derived only when the clamp cost it
        /// a boundary. That is the only defensible choice for a char index: the payload carries no
        /// text, so a split copied off "apple" would land on whatever word sits at that position in
        /// the target line and cut it somewhere meaningless.</para>
        /// </summary>
        public static void PasteUnitTimings(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int anchorIndex, LyricTimingClipboard.UnitTimingsPayload payload)
        {
            var line = hitObject.Line;

            if (anchorIndex < 0 || anchorIndex >= line.Units.Count || payload.Units.Count == 0)
                return;

            double anchor = line.Units[anchorIndex].StartTime;
            var units = line.Units.ToArray();

            for (int k = 0; k < payload.Units.Count && anchorIndex + k < units.Length; k++)
            {
                units[anchorIndex + k] = retime(units[anchorIndex + k],
                    anchor + payload.Units[k].Start,
                    anchor + payload.Units[k].End,
                    TimingSource.Explicit, 1);
            }

            editorBeatmap.BeginChange();
            hitObject.Line = new LyricLine
            {
                RawText = line.RawText,
                StartTime = line.StartTime,
                EndTime = line.EndTime,
                SingEndTime = line.SingEndTime,
                Units = clampUnits(units, line.StartTime, line.EndTime),
                SealGraceMs = line.SealGraceMs,
                Estimated = false,
            };
            editorBeatmap.Update(hitObject);
            promoteToWordGranularity(editorBeatmap);
            // A pasted run that reaches the last word overwrites its end, so the sung end follows.
            syncSingEndToLastUnit(editorBeatmap, hitObject, lastUnitEnd(line));
            editorBeatmap.EndChange();
        }

        #endregion

        #region Invariant maintenance

        /// <summary>Hit objects in typing order (LineIndex is renumbered from this ordering).</summary>
        public static List<TypeBeatHitObject> OrderedLines(EditorBeatmap editorBeatmap)
            => editorBeatmap.HitObjects.OfType<TypeBeatHitObject>().OrderBy(o => o.Line.StartTime).ThenBy(o => o.LineIndex).ToList();

        private static List<TypeBeatHitObject> orderedLines(EditorBeatmap editorBeatmap) => OrderedLines(editorBeatmap);

        /// <summary>
        /// The reload-faithful units for a line after its window changed. Line-granularity maps
        /// persist no words[]; the loader re-interpolates units over [start, singEnd] on every
        /// load, so the editor must derive them the same way or unit times drift on reload.
        /// Word maps persist units verbatim; they are preserved, clamped into the new window.
        /// </summary>
        private static IReadOnlyList<TimedUnit> unitsFor(TypeBeatHitObject hitObject, string rawText, IReadOnlyList<TimedUnit> currentUnits, double start, double singEnd, double end)
            => hitObject.Granularity == TimingGranularity.Line
                ? LrcParser.InterpolateUnits(rawText, start, singEnd)
                : clampUnits(currentUnits, start, end);

        /// <summary>A line's last word end, or NaN when it has no units (so any comparison reads as "moved").</summary>
        private static double lastUnitEnd(LyricLine line) => line.Units.Count > 0 ? line.Units[^1].EndTime : double.NaN;

        /// <summary>
        /// Auto-derives a line's sung end (persisted as end_ms) from its LAST WORD's end. Backlog 246
        /// removed the editor's sung-end marker, so end_ms is no longer authored directly: it follows
        /// the last word. Call this from inside an op's transaction, passing the end that unit had
        /// BEFORE the edit (<see cref="lastUnitEnd"/> taken up front).
        ///
        /// <para>ONLY when that end actually MOVED, and this is the whole point of the guard. A
        /// changed end_ms is genuine map CONTENT, not bookkeeping: it is what
        /// <see cref="Gameplay.InstrumentalGaps"/> perceives an instrumental stretch from
        /// (next.FirstVocalTime - prev.SingEndTime), and the SERVER mirrors those rules to compute the
        /// skip allowance its play-time anti-cheat gate subtracts. Rewriting end_ms on an edit that
        /// did not touch the last word would therefore silently re-rank honest plays. So a map whose
        /// stored end_ms sits past its last word (trailing vocals, an aligner estimate, a sung-end
        /// flag dragged before this rule existed) keeps that value verbatim until the last word is
        /// itself re-timed, at which point the mapper HAS made a content decision and end_ms follows.</para>
        ///
        /// <para>The LAST line's typeable window is reload-derived as min(song_end, singEnd + tail),
        /// so it is re-derived alongside the sung end here for the same reason
        /// <see cref="SetSingEnd"/> does it.</para>
        /// </summary>
        private static void syncSingEndToLastUnit(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, double previousLastUnitEnd)
        {
            var line = hitObject.Line;

            if (line.Units.Count == 0)
                return;

            double moved = line.Units[^1].EndTime;

            // Not this edit's doing: leave the stored end_ms exactly as the map carries it.
            if (moved == previousLastUnitEnd)
                return;

            double singEnd = Math.Clamp(moved, line.StartTime, line.EndTime);

            var ordered = orderedLines(editorBeatmap);
            bool isLast = ordered.Count > 0 && ordered[^1] == hitObject;
            double end = isLast ? Math.Clamp(line.EndTime, singEnd, singEnd + LAST_LINE_TAIL_MS) : line.EndTime;

            if (singEnd == line.SingEndTime && end == line.EndTime)
                return;

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line, singEnd: singEnd, end: end);
            editorBeatmap.Update(hitObject);
            editorBeatmap.EndChange();
        }

        private static IReadOnlyList<TimedUnit> clampUnits(IReadOnlyList<TimedUnit> units, double start, double end)
        {
            var result = new TimedUnit[units.Count];
            double previousEnd = start;

            for (int i = 0; i < units.Count; i++)
            {
                double s = Math.Clamp(units[i].StartTime, previousEnd, end);
                double e = Math.Clamp(units[i].EndTime, s, end);
                result[i] = retime(units[i], s, e);
                previousEnd = e;
            }

            return result;
        }

        /// <summary>
        /// Promotes a Line-granularity beatmap to Word once any unit timing is Explicit. Keyed off
        /// <see cref="InferGranularity"/>'s predicate so boundary drags (which merely re-clamp
        /// Interpolated units) never trigger a spurious promotion. Idempotent.
        /// </summary>
        private static void promoteToWordGranularity(EditorBeatmap editorBeatmap)
        {
            var objects = editorBeatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            if (objects.Count == 0 || objects[0].Granularity != TimingGranularity.Line)
                return;

            if (InferGranularity(objects.Select(o => o.Line).ToList()) != TimingGranularity.Word)
                return;

            foreach (var o in objects)
            {
                o.Granularity = TimingGranularity.Word;
                editorBeatmap.Update(o);
            }
        }

        /// <summary>
        /// Sets every line to the granularity its unit data now requires (<see cref="InferGranularity"/>):
        /// promotes when subdivision boundaries appear (up to Syllable), demotes Syllable→Word when the
        /// last boundary is removed. Never falls below Word while any hand timing remains (removing a
        /// boundary leaves the word Explicit). Idempotent; used by the syllable ops, which can move
        /// granularity in either direction, unlike <see cref="promoteToWordGranularity"/>.
        ///
        /// <paramref name="keepAuthoredWords"/> floors an already-authored map at Word: removing a
        /// WORD can strip the map's last Explicit unit, and demoting to Line there would make the
        /// encoder omit words[] and silently discard every remaining hand timing on save.
        /// </summary>
        private static void syncGranularity(EditorBeatmap editorBeatmap, bool keepAuthoredWords = false)
        {
            var objects = editorBeatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            if (objects.Count == 0)
                return;

            var target = InferGranularity(objects.Select(o => o.Line).ToList());

            if (keepAuthoredWords && target == TimingGranularity.Line && objects[0].Granularity != TimingGranularity.Line)
                target = TimingGranularity.Word;

            foreach (var o in objects)
            {
                if (o.Granularity != target)
                {
                    o.Granularity = target;
                    editorBeatmap.Update(o);
                }
            }
        }

        private static void renumber(EditorBeatmap editorBeatmap)
        {
            var ordered = orderedLines(editorBeatmap);

            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].LineIndex != i)
                {
                    ordered[i].LineIndex = i;
                    editorBeatmap.Update(ordered[i]);
                }
            }
        }

        #endregion
    }
}
