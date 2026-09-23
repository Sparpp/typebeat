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
                // A rest is two absolute times, so it moves with the word it lives in. Its split is a
                // CHAR index like the others and cannot be invalidated by a shift either - so the whole
                // set of rests rides through a global offset with its shape intact.
                Pauses = u.Pauses.Select(pause => new WordPause(pause.StartTime + deltaMs, pause.EndTime + deltaMs, pause.SplitChar)).ToArray(),
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
        /// boundaries, else Word when some unit timing is Explicit (authored words[]) OR some word
        /// carries an authored pause, else Line. Line-granularity maps also carry one unit per token,
        /// but those are Interpolated, synthesized by the loader, not real word timing.
        ///
        /// <para>The pause counts as its own Word trigger rather than riding on the Explicit stamp its
        /// every setter gives it, because the two are separate facts: a REST is written inside
        /// <c>words[]</c>, so a map holding one must not be allowed to fall back to Line and drop it.
        /// That keeps the rule true even for a unit built by a caller that forgot the stamp.</para>
        /// </summary>
        public static TimingGranularity InferGranularity(IReadOnlyList<LyricLine> lines)
        {
            if (lines.Any(l => l.Units.Any(u => u.SyllableBoundaries.Count > 0)))
                return TimingGranularity.Syllable;
            if (lines.Any(l => l.Units.Any(u => u.Source == TimingSource.Explicit || u.Pauses.Count > 0)))
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
                Pauses = ClampPauses(unit, start, end),
            };
        }

        /// <summary>
        /// The rests a RETIMED word keeps: each one stays at ITS OWN time, exactly as a subdivision
        /// boundary does just above (<see cref="clampBoundaries"/>) - stretching or squeezing a word
        /// re-times the word around the dividers the mapper put inside it, and never drags a breath along
        /// with the edge they are pulling or squashes it into a shorter one.
        ///
        /// <para>A rest the new span can no longer hold (an edge on or past the word's own) is dropped
        /// rather than stretched onto an edge, which is the same rule the loader applies to one that no
        /// longer fits, and the reason a word pulled down to nothing comes back with no breath in it. The
        /// survivors keep their order: none of them moved, and they were in order already.</para>
        /// </summary>
        internal static IReadOnlyList<WordPause> ClampPauses(TimedUnit unit, double start, double end)
        {
            if (unit.Pauses.Count == 0)
                return Array.Empty<WordPause>();

            var kept = new List<WordPause>(unit.Pauses.Count);

            foreach (var pause in unit.Pauses)
            {
                if (pause.StartTime > start + 1e-3 && pause.EndTime < end - 1e-3)
                    kept.Add(pause);
            }

            return kept;
        }

        /// <summary>
        /// The rests a word keeps when its whole SPAN is REDISTRIBUTED - the word-count change that moves
        /// words wholesale, where the boundaries rescale (<see cref="rescaleBoundaries"/>) rather than
        /// clamp. Each rest keeps its relative position in the word, so the shape the mapper authored
        /// travels with the word it belongs to; one the new span cannot hold is dropped, exactly as a
        /// boundary that no longer fits is.
        /// </summary>
        private static IReadOnlyList<WordPause> scalePauses(TimedUnit unit, double start, double end)
        {
            if (unit.Pauses.Count == 0 || unit.EndTime <= unit.StartTime || end <= start)
                return Array.Empty<WordPause>();

            double scale = (end - start) / (unit.EndTime - unit.StartTime);
            var kept = new List<WordPause>(unit.Pauses.Count);

            foreach (var pause in unit.Pauses)
            {
                double ps = start + (pause.StartTime - unit.StartTime) * scale;
                double pe = start + (pause.EndTime - unit.StartTime) * scale;

                if (ps > start + 1e-3 && pe < end - 1e-3 && ps < pe)
                    kept.Add(new WordPause(ps, pe, pause.SplitChar));
            }

            return kept;
        }

        /// <summary>
        /// The rests a word keeps across a TEXT commit: they ride along only while the word came back
        /// spelled EXACTLY as it was. Their <see cref="WordPause.SplitChar"/> values are indices into that
        /// spelling, so a retyped word invalidates them the same way it invalidates an authored char
        /// split - there is no honest place for a rest to sit in a word that is no longer the one it was
        /// authored against.
        /// </summary>
        private static IReadOnlyList<WordPause> carryPausesAcrossText(TimedUnit unit, string token)
            => token == unit.Text ? unit.Pauses : Array.Empty<WordPause>();

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
        /// <item>a word carrying RESTS (see <see cref="InsertWordPause"/>): each one is a divider like
        /// the others, so it prints a pipe of its own and the pipes fill the word's cuts - its syllable
        /// splits with every rest's cut among them - in TEXT order, which is how "ple|ase" becomes
        /// "pl|ease". Surplus pipes are dropped and a cut left without a pipe keeps the value it showed,
        /// on the same terms as the rows above. A rest itself is NEVER removed from the box: it has its
        /// own gesture and a rest with no cut has nowhere to sit, so deleting its pipe leaves it exactly
        /// where it was.</item>
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
            // No backing-vocal strip (backlog 255): what the mapper typed is what the line stores,
            // so "hello (oh) now" commits verbatim and the brackets are ordinary lyric marks.
            string withMarkers = Typeability.Normalize(rawUserText,
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
                // A pipe that moved a word's REST is a change like any other, so the comparison is
                // over every cut the box can author, not only the syllable splits.
                if (textUnchanged && !authoredSubdivision && !anyRemoval
                    && !units.Where((u, i) => !sameSplits(u.SyllableSplits, line.Units[i].SyllableSplits)
                                              || !u.Pauses.SequenceEqual(line.Units[i].Pauses)).Any())
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
                        // The word is being un-subdivided, not retyped, so its rests stay.
                        Pauses = u.Pauses,
                    };
                }

                // A word carrying authored PAUSES: the pipes are ITS cuts, not a new subdivision. A rest
                // is a divider like any other, so '|' moves where a breath falls in the word exactly as it
                // moves a syllable split - and, unlike a syllable split, a rest is never DELETED from the
                // text box: it has its own gesture and a rest with no cut has nowhere to sit, so a commit
                // that leaves one no pipe keeps the cut it had. Checked BEFORE the "author a subdivision"
                // branch below, which would otherwise read a paused word's pipe as a request to subdivide
                // it.
                if (u.Pauses.Count > 0 && tokens[i] == u.Text)
                    return withPipeAuthoredCuts(u, tokens[i], wordPipes);

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
                        Pauses = carryPausesAcrossText(u, tokens[i]),
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
                    Pauses = carryPausesAcrossText(u, tokens[i]),
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
                    // A rest is a position INSIDE the word, so it travels the same way the boundaries
                    // do - rescaled into the span the redistribution gave this word.
                    Pauses = scalePauses(old, units[i].StartTime, units[i].EndTime),
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
        /// <see cref="Typeability.SPLIT_MARKER"/> at every word's EFFECTIVE cuts, the authored ones
        /// where there are any and the derived ones otherwise. Showing the derived split is deliberate:
        /// it is the split gameplay's judgement groups already use, so the mapper edits what the game
        /// does rather than an empty field.
        ///
        /// <para>A word carrying authored PAUSES prints their cuts too - every rest's own cut among its
        /// syllable cuts - so the mapper moves where a breath falls in the word with '|' exactly as they
        /// move a syllable split, and can see all of them at once on a word that has both. Identical to
        /// the stored text for a line with no subdivisions and no rests at all, and for a line whose
        /// tokens and units have drifted apart (nothing there can be paired safely).</para>
        /// </summary>
        public static string PipeDisplayText(LyricLine line)
        {
            string[] tokens = line.RawText.Split(' ');

            if (tokens.Length != line.Units.Count || !line.Units.Any(u => u.SyllableBoundaries.Count > 0 || u.Pauses.Count > 0))
                return line.RawText;

            var pieces = new string[tokens.Length];

            for (int i = 0; i < tokens.Length; i++)
            {
                var unit = line.Units[i];
                var cuts = Gameplay.PausedWord.Cuts(unit, unit.StartTime, unit.EndTime);

                pieces[i] = cuts.Count == 0
                    ? tokens[i]
                    : string.Join(Typeability.SPLIT_MARKER, SyllableSegments.SegmentTexts(tokens[i], cuts));
            }

            return string.Join(' ', pieces);
        }

        /// <summary>
        /// One word that already carries authored PAUSES, after a text commit: its pipes fill the word's
        /// cuts in TEXT order - its syllable cuts with every rest's own cut among them - and each rest
        /// keeps whatever cut ends up in its slot.
        ///
        /// <para>A rest behaves like a subdivision divider here and nothing more: the pipes are read
        /// positionally (as they are for a subdivided word), surplus pipes are dropped because a text
        /// commit never changes how many dividers a word has, and a divider left without a pipe keeps the
        /// value it already showed. A rest is NEVER removed by deleting its pipe: it has its own gesture,
        /// and a rest with no cut has nowhere to sit - so a commit that empties the box of pipes still
        /// leaves the word with the cuts it had, on the same terms
        /// <see cref="removesSubdivision(TimedUnit, string, IReadOnlyList{int})"/> sets for a subdivision
        /// it is allowed to remove.</para>
        ///
        /// <para>A set of pipes that would not describe legal cuts - not strictly ascending, a segment
        /// left EMPTY at either end of the word, or a cut a rest could not sit at - returns the word
        /// exactly as it was, so a typo costs the mapper nothing. The syllable cuts are stored as DERIVED
        /// when they say exactly what the syllabifier would, like every other split commit.</para>
        /// </summary>
        private static TimedUnit withPipeAuthoredCuts(TimedUnit unit, string token, IReadOnlyList<int> pipes)
        {
            var current = Gameplay.PausedWord.Cuts(unit, unit.StartTime, unit.EndTime);

            if (current.Count == 0)
                return unit;

            // Which of the word's cuts belong to a REST: the slot a rest's own character stands at, which
            // is exactly where its divider sits in the printed text. The word's rests share no character
            // (see PausedWord.UsableRests), so each slot names at most one of them.
            var restSlots = new HashSet<int>();

            for (int i = 0; i < current.Count; i++)
            {
                if (unit.Pauses.Any(pause => pause.SplitChar == current[i]))
                    restSlots.Add(i);
            }

            var target = current.ToList();

            for (int i = 0; i < target.Count && i < pipes.Count; i++)
                target[i] = pipes[i];

            for (int i = 1; i < target.Count; i++)
            {
                if (target[i] <= target[i - 1])
                    return unit;
            }

            if (target[0] <= 0 || target[^1] >= token.Length)
                return unit;

            var splits = new List<int>(target.Count);

            for (int i = 0; i < target.Count; i++)
            {
                if (!restSlots.Contains(i))
                    splits.Add(target[i]);
            }

            int segments = unit.SyllableBoundaries.Count + 1;

            // A word carrying RESTS never stores its cuts as "derived". The spread a paused word is
            // derived from is its STRETCHES' own - the syllabifier's answer for each sung run of text - not
            // its answer for the whole word, so a cut that happens to equal the whole-word answer (the
            // mapper moved a divider to exactly where the syllabifier would have put it: "mul|ti|plying"
            // out of "mul|tiply|ing") is NOT the same cut, and treating it as such would throw the edit
            // away and snap the characters back to the syllabifier's own division.
            var stored = splits.Count == 0 || !SyllableSegments.IsAuthoredValid(token, segments, splits)
                         || (unit.Pauses.Count == 0 && sameSplits(splits, SyllableSegments.Derived(token, segments)))
                ? Array.Empty<int>()
                : splits.ToArray();

            var pauses = new List<WordPause>(unit.Pauses.Count);

            foreach (int slot in restSlots.OrderBy(slot => slot))
            {
                var rest = unit.Pauses.First(pause => pause.SplitChar == current[slot]);
                pauses.Add(new WordPause(rest.StartTime, rest.EndTime, target[slot]));
            }

            // Nothing moved: keep the instance, so a box losing focus with the same pipes in it is not an
            // undo step.
            if (sameSplits(stored, unit.SyllableSplits) && pauses.SequenceEqual(unit.Pauses))
                return unit;

            return new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = unit.Source,
                Confidence = unit.Confidence,
                SyllableBoundaries = unit.SyllableBoundaries,
                SyllableSplits = stored,
                Pauses = pauses,
            };
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
            // else untypeable is stripped, and brackets are literal marks (backlog 255).
            string normalized = Typeability.Normalize(text, keepFreestyleMarkers: true);

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

        /// <summary>Words in a line: one per whitespace token (the unit list mirrors them).</summary>
        public static int WordCount(LyricLine line) => line.RawText.Split(' ').Length;

        /// <summary>
        /// Removes several words of one line as a SINGLE undo step: <see cref="RemoveWord"/> per
        /// index, highest first so the pending ones stay addressable as the word list shrinks.
        /// Indices that <see cref="RemoveWord"/> refuses (out of range, or the line's last surviving
        /// word) are skipped, so a caller may pass a whole selection without pre-filtering it.
        /// Returns how many words actually went.
        /// </summary>
        public static int RemoveWords(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, IReadOnlyList<int> unitIndices)
        {
            if (unitIndices.Count == 0)
                return 0;

            int removed = 0;

            editorBeatmap.BeginChange();

            foreach (int i in unitIndices.OrderByDescending(i => i))
            {
                if (RemoveWord(editorBeatmap, hitObject, i))
                    removed++;
            }

            editorBeatmap.EndChange();
            return removed;
        }

        /// <summary>
        /// Splits the word at <paramref name="unitIndex"/> into TWO words at the syllable subdivision
        /// <paramref name="boundaryIndex"/>: the characters left of that cut become one word and the
        /// characters right of it the next, their spans being the old word's own either side of the
        /// boundary. The subdivision is CONSUMED - it becomes the gap between the two words, which is
        /// the point of the operation - and any OTHER subdivision of the word rides into whichever of
        /// the two now owns it, with the second word's char indices measured from its own start.
        ///
        /// <para>The line's stored text gains a space at the cut, which is what makes the two tokens;
        /// everything else about the line is untouched. No-op when the cut cannot be expressed: an
        /// index that is not a subdivision of that word, a word whose subdivisions cannot all be named
        /// in characters (an over-forced short word, where the syllabifier answered with fewer cuts
        /// than segments), or a cut that would leave either word shorter than
        /// <see cref="MIN_SPAN_MS"/>.</para>
        /// </summary>
        public static bool SplitWord(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, int boundaryIndex)
        {
            var line = hitObject.Line;
            string[] tokens = line.RawText.Split(' ');

            if (unitIndex < 0 || unitIndex >= line.Units.Count || tokens.Length != line.Units.Count)
                return false;

            var unit = line.Units[unitIndex];

            if (boundaryIndex < 0 || boundaryIndex >= unit.SyllableBoundaries.Count)
                return false;

            // The EFFECTIVE split: the authored one where there is one, the syllabifier's answer
            // otherwise. It has to name every boundary for the cut to have a character to live
            // between, which is exactly the case this operation needs.
            var splits = SyllableSegments.SplitsFor(unit);

            if (splits.Count != unit.SyllableBoundaries.Count)
                return false;

            int cut = splits[boundaryIndex];
            double boundary = unit.SyllableBoundaries[boundaryIndex];

            if (cut <= 0 || cut >= unit.Text.Length)
                return false;

            if (boundary - unit.StartTime < MIN_SPAN_MS || unit.EndTime - boundary < MIN_SPAN_MS)
                return false;

            string firstText = unit.Text.Substring(0, cut);
            string secondText = unit.Text.Substring(cut);

            // The word's own rest follows the half its split character lands in, rebased onto that
            // half's spelling. A rest sitting exactly ON the cut is dropped: the gap between the two
            // new words already IS a break there, so keeping it would only author a silence inside a
            // word with nothing left to separate. A rest one of the halves has no room for goes too,
            // exactly as the loader would drop it on the way back in.
            // The word's rests follow the half their character lands in, rebased onto that half's spelling.
            // One sitting exactly ON the cut is dropped: the gap between the two new words already IS a
            // break there, so keeping it would only author a silence inside a word with nothing left to
            // separate. A rest a half has no room for goes too, exactly as the loader would drop it on the
            // way back in.
            var firstPauses = new List<WordPause>();
            var secondPauses = new List<WordPause>();

            foreach (var pause in unit.Pauses)
            {
                if (pause.SplitChar < cut
                    && pause.StartTime > unit.StartTime && pause.EndTime < boundary
                    && pause.SplitChar > 0 && pause.SplitChar < firstText.Length)
                {
                    firstPauses.Add(pause);
                }
                else if (pause.SplitChar > cut
                         && pause.StartTime > boundary && pause.EndTime < unit.EndTime
                         && pause.SplitChar - cut > 0 && pause.SplitChar - cut < secondText.Length)
                {
                    secondPauses.Add(new WordPause(pause.StartTime, pause.EndTime, pause.SplitChar - cut));
                }
            }

            var units = line.Units.ToList();

            units[unitIndex] = new TimedUnit
            {
                Text = firstText,
                StartTime = unit.StartTime,
                EndTime = boundary,
                Source = unit.Source,
                Confidence = unit.Confidence,
                // Both of these are left of the cut, so neither index moves.
                SyllableBoundaries = unit.SyllableBoundaries.Take(boundaryIndex).ToArray(),
                SyllableSplits = carriedSplits(firstText, splits, 0, boundaryIndex, 0, firstPauses.Count > 0),
                Pauses = Gameplay.PausedWord.UsableRests(firstText, unit.StartTime, boundary, firstPauses),
            };

            units.Insert(unitIndex + 1, new TimedUnit
            {
                Text = secondText,
                StartTime = boundary,
                EndTime = unit.EndTime,
                Source = unit.Source,
                Confidence = unit.Confidence,
                SyllableBoundaries = unit.SyllableBoundaries.Skip(boundaryIndex + 1).ToArray(),
                // This word's cuts are indices into ITS OWN token, so each shifts down by the
                // characters the first word took.
                SyllableSplits = carriedSplits(secondText, splits, boundaryIndex + 1, splits.Count, cut, secondPauses.Count > 0),
                Pauses = Gameplay.PausedWord.UsableRests(secondText, boundary, unit.EndTime, secondPauses),
            });

            tokens[unitIndex] = firstText + " " + secondText;

            editorBeatmap.BeginChange();

            hitObject.Line = rebuild(line, rawText: string.Join(' ', tokens), units: units.ToArray());
            editorBeatmap.Update(hitObject);

            // A word whose only subdivision this was leaves none behind, so the line's granularity has
            // to follow it down (the units stay Explicit, so that lands on Word, never on Line).
            syncGranularity(editorBeatmap, keepAuthoredWords: true);

            editorBeatmap.EndChange();
            return true;
        }

        /// <summary>
        /// The subdivision cuts a <see cref="SplitWord"/> side keeps: <c>splits[from..to]</c> rebased
        /// by <paramref name="shift"/> and kept only when that is a VALID authored split of
        /// <paramref name="token"/> that the syllabifier would not have answered anyway. A subset that
        /// is simply the derived split stays derived, exactly as
        /// <see cref="SetSyllableSplit"/>'s own rule keeps a pipe moved back onto the derived cut from
        /// pinning it; anything else falls back to derived rather than carrying an index that could
        /// re-cut the word.
        /// </summary>
        private static IReadOnlyList<int> carriedSplits(string token, IReadOnlyList<int> splits, int from, int to, int shift, bool carriesRests = false)
        {
            if (to <= from)
                return Array.Empty<int>();

            var kept = new int[to - from];

            for (int i = 0; i < kept.Length; i++)
                kept[i] = splits[from + i] - shift;

            int segments = kept.Length + 1;

            // As in the pipe commit, a half that carries a rest keeps its cuts rather than folding them
            // into "derived": its spread is derived per stretch, not from the syllabifier's whole-word answer.
            bool derived = !carriesRests && sameSplits(kept, SyllableSegments.Derived(token, segments));

            return SyllableSegments.IsAuthoredValid(token, segments, kept) && !derived
                ? kept
                : Array.Empty<int>();
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
            // Brackets are literal marks here too (backlog 255): no backing-vocal strip.
            string normalized = Typeability.Normalize(text);

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

        /// <summary>
        /// Deletes several lines as a SINGLE undo step: <see cref="DeleteLine"/> per line, LAST one
        /// first (the order <see cref="RemoveWords"/> takes, and the order a mapper deleting a run by
        /// hand would). Each freed span is inherited by the line before it, so the run telescopes
        /// back onto the surviving predecessor, whose window then caps as a reload would derive it.
        /// Lines already gone from the beatmap are skipped by <see cref="DeleteLine"/> itself.
        /// </summary>
        public static void DeleteLines(EditorBeatmap editorBeatmap, IReadOnlyList<TypeBeatHitObject> hitObjects)
        {
            if (hitObjects.Count == 0)
                return;

            editorBeatmap.BeginChange();

            // Snapshotted before the loop: DeleteLine renumbers, so the sort keys move under it.
            foreach (var hitObject in hitObjects.OrderByDescending(o => o.Line.StartTime).ThenByDescending(o => o.LineIndex).ToList())
                DeleteLine(editorBeatmap, hitObject);

            editorBeatmap.EndChange();
        }

        #endregion

        #region Syllable subdivisions (per-word dotted-line boundaries)

        /// <summary>
        /// Adds one syllable-subdivision boundary inside the given word unit. WHERE it lands is the
        /// mapper's call, in this order:
        ///
        /// <list type="number">
        /// <item><b>The caret wins.</b> When <paramref name="caretTime"/> (the editor's playhead, the
        /// caret of <c>snap to caret</c>) is inside this word, the boundary lands exactly on it -
        /// the mapper is pointing at the moment they want to cut, so they get that moment instead of
        /// a bisection they then have to drag. Its CHARACTERS are still cut inside the segment the
        /// caret landed in, the same "split the space you have" idiom the bisect below uses, and the
        /// word's split follows exactly as it does there (an authored split is bisected, a derived
        /// one stays derived).</item>
        /// <item><b>Otherwise the widest segment is bisected</b>, so successive presses keep
        /// splitting evenly and a mapper who never parks the caret in the word is unaffected.</item>
        /// </list>
        ///
        /// <para>A caret that sits in the word but within <see cref="MIN_SYLLABLE_MS"/> of one of
        /// its edges cannot cut two legal segments, and a caret on a boundary is not "inside" a
        /// segment at all, so both fall through to the bisect rather than refusing.</para>
        ///
        /// <para>A word carrying an authored REST is divided by it too, and its rest is taken OUT of the
        /// spans a divider can be spliced into - exactly as it belongs to no judgement group (see
        /// <see cref="PausedWord"/>) - so a caret parked in the breath falls through to the bisect (it is
        /// inside no sung span), and the bisect itself measures the sung parts of each span rather than
        /// the whole span. Without that, a new divider would land INSIDE the rest: it would separate
        /// characters the rest already separates, and the word's authored cuts would stop describing its
        /// halves at all, which is a word no further divider could move.</para>
        ///
        /// <para>The word becomes Explicit hand timing and the beatmap is promoted to Syllable
        /// granularity. The encoder only persists syllables[] for units that carry boundaries, so
        /// without the promotion a subdivision would silently vanish on save. No-op when there is no
        /// room to subdivide at all. Returns the new boundary time (for the UI to focus the fresh
        /// handle), or null when nothing was added. The inverse press is
        /// <see cref="RemoveNarrowestSyllableBoundary"/>.</para>
        /// </summary>
        /// <param name="editorBeatmap">The map being edited.</param>
        /// <param name="hitObject">The line owning the word.</param>
        /// <param name="unitIndex">The word unit within that line.</param>
        /// <param name="caretTime">The editor's caret (playhead) in song time, or null when the
        /// caller has none to offer. Only used when it falls inside the word.</param>
        public static double? AddSyllableBoundary(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, double? caretTime = null)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return null;

            var unit = line.Units[unitIndex];

            // Segment edges: word start, existing boundaries, word end.
            var edges = new List<double> { unit.StartTime };
            edges.AddRange(unit.SyllableBoundaries);
            edges.Add(unit.EndTime);

            if (caretTime is double caret && segmentForCaret(unit, edges, caret) is int caretSegment)
            {
                var caretBoundaries = unit.SyllableBoundaries.Append(caret).OrderBy(b => b).ToArray();
                replaceUnitBoundaries(editorBeatmap, hitObject, unitIndex, caretBoundaries, bisectSplit(unit, caretSegment));
                return caret;
            }

            // No caret in the word (or none in a sung span of it): split the widest one.
            double mid = double.NaN;
            double widest = 0;
            int widestSegment = -1;

            foreach ((double lo, double hi, int segment) in spliceableSpans(unit, edges))
            {
                double width = hi - lo;

                if (width > widest)
                {
                    widest = width;
                    widestSegment = segment;
                    mid = (lo + hi) / 2;
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
        /// The SUNG spans of a word's segments, as (lo, hi, segment index) triples: the segments
        /// <paramref name="edges"/> divides the word into, with the word's authored rest taken out of
        /// whichever one holds it - because a rest is not sung, and belongs to no syllable, so no divider
        /// may be spliced into it. A word with no rest yields exactly its segments, in order, which is
        /// what keeps this a no-op for every word that never had one.
        /// </summary>
        private static IEnumerable<(double Lo, double Hi, int Segment)> spliceableSpans(TimedUnit unit, IReadOnlyList<double> edges)
        {
            var rests = Gameplay.PausedWord.UsableRests(unit.Text, unit.StartTime, unit.EndTime, unit.Pauses);

            for (int i = 0; i < edges.Count - 1; i++)
            {
                double lo = edges[i];
                double hi = edges[i + 1];
                double cursor = lo;

                // Every rest straddling this segment takes a bite out of it: what is left either side of a
                // breath is what a divider can be spliced into.
                foreach (var rest in rests)
                {
                    if (rest.EndTime <= cursor || rest.StartTime >= hi)
                        continue;

                    if (rest.StartTime > cursor)
                        yield return (cursor, Math.Min(rest.StartTime, hi), i);

                    cursor = Math.Max(cursor, rest.EndTime);
                }

                if (cursor < hi)
                    yield return (cursor, hi, i);
            }
        }

        /// <summary>
        /// The segment of a word that <paramref name="caret"/> falls INSIDE with
        /// room to cut a legal pair of segments, or null when it is outside the word, sits exactly on
        /// a boundary (which is inside no segment), sits in the word's rest (which belongs to no segment
        /// either), or is closer than <see cref="MIN_SYLLABLE_MS"/> to one of that span's edges. A null
        /// answer is what sends <see cref="AddSyllableBoundary"/> back to its bisect.
        /// </summary>
        private static int? segmentForCaret(TimedUnit unit, IReadOnlyList<double> edges, double caret)
        {
            foreach ((double lo, double hi, int segment) in spliceableSpans(unit, edges))
            {
                if (caret <= lo || caret >= hi)
                    continue;

                return caret - lo >= MIN_SYLLABLE_MS && hi - caret >= MIN_SYLLABLE_MS ? segment : null;
            }

            return null;
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
        ///
        /// <para>A word carrying an authored REST is one no divider may be parked INSIDE: a divider in the
        /// breath would separate characters the rest already separates, and the word's authored cuts
        /// would stop describing its halves at all (see <c>PausedWord.AuthoredCutsUsable</c>), so the
        /// divider could never be moved again. A drag that LANDS in the rest therefore keeps the divider
        /// on the side it came from, against the rest's own edge - while a drag that sweeps clean across
        /// it (the cursor past the far edge by the next frame) is followed, so crossing a breath reads
        /// like crossing any other span.</para>
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

            foreach (var pause in unit.Pauses)
            {
                if (newTime <= pause.StartTime || newTime >= pause.EndTime)
                    continue;

                bool cameFromBeforeTheRest = unit.SyllableBoundaries[boundaryIndex] <= pause.StartTime;
                double wall = cameFromBeforeTheRest ? pause.StartTime - MIN_SYLLABLE_MS : pause.EndTime + MIN_SYLLABLE_MS;

                // Clamped again against the neighbours, so a divider with no room at all simply stops
                // where it already was allowed to be.
                newTime = Math.Clamp(wall, lower, upper);
                break;
            }

            var boundaries = unit.SyllableBoundaries.ToArray();
            boundaries[boundaryIndex] = newTime;

            // The boundary COUNT is unchanged, so the authored split still describes this word - but a
            // divider that has been dragged ACROSS a rest takes its CHARACTER with it: its cut is clamped
            // into the stretch its new time sits in, so the word's dividers still read left to right in
            // both senses and the characters part where the mapper put the line. (A cut the stretch cannot
            // hold at all is simply left where it was: the derivation ignores it and derives that stretch
            // evenly, exactly as it does with any stale authored split.)
            var splits = unit.SyllableSplits;

            if (SyllableSegments.IsAuthoredValid(unit.Text, boundaries.Length + 1, unit.SyllableSplits))
            {
                var recut = unit.SyllableSplits.ToArray();
                var (lo, hi) = stretchCharRange(unit, newTime);
                recut[boundaryIndex] = Math.Clamp(recut[boundaryIndex], lo, hi);
                splits = recut;
            }

            replaceUnitBoundaries(editorBeatmap, hitObject, unitIndex, boundaries, splits);
        }

        /// <summary>
        /// The character range of the sung stretch a TIME falls in: after the cut of every rest that ends
        /// before it and before the cut of the first rest that starts after it. Returned as the range a
        /// character CUT is clamped into (so both ends leave a character either side of it).
        /// </summary>
        private static (int Lo, int Hi) stretchCharRange(TimedUnit unit, double time)
        {
            int lo = 0;
            int hi = unit.Text.Length;

            foreach (var rest in Gameplay.PausedWord.UsableRests(unit.Text, unit.StartTime, unit.EndTime, unit.Pauses))
            {
                if (time >= rest.EndTime)
                {
                    lo = Math.Max(lo, rest.SplitChar);
                    continue;
                }

                if (time <= rest.StartTime)
                {
                    hi = Math.Min(hi, rest.SplitChar);
                    break;
                }
            }

            return (lo + 1, Math.Max(lo + 1, hi - 1));
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

            // The same rule the pipe commit follows: a word carrying rests keeps every cut it is given,
            // because the spread its stretches derive is not the syllabifier's whole-word answer.
            var stored = unit.Pauses.Count == 0 && sameSplits(splits, SyllableSegments.Derived(unit.Text, segments))
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
                // Moving one dotted line moves a CUT, not a rest: the word's breaths are untouched.
                Pauses = unit.Pauses,
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
            => replaceUnitShape(editorBeatmap, hitObject, unitIndex, boundaries, splits, hitObject.Line.Units[unitIndex].Pauses);

        /// <summary>
        /// Rebuilds a line with one unit's subdivision AND its authored rest replaced together - the
        /// shared tail of every edit that reshapes a word's dividers (subdivide / un-subdivide, and the
        /// SHIFT+drag that promotes a divider into a rest). The unit becomes Explicit and fully trusted
        /// (both are hand timing), the line stops being Estimated, and the beatmap's granularity is
        /// reconciled (up to Syllable while a boundary survives, down to Word when the last one goes -
        /// and never below Word while a rest is there, since a rest is persisted inside <c>words[]</c>).
        /// Single undo step.
        /// </summary>
        private static void replaceUnitShape(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex,
                                             IReadOnlyList<double> boundaries, IReadOnlyList<int> splits, IReadOnlyList<WordPause> pauses)
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
                // The rests the CALLER decided on: left alone by the subdivision edits (a word's breath is
                // a decision about its own spelling and span, neither of which they touch), and authored
                // by the SHIFT+drag that promotes a divider into one.
                Pauses = pauses,
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

        #region The authored pause inside a word (the timeline strip's Insert Pause)

        /// <summary>
        /// Inserts the Map Editor's authored pause into one word: a rest between two of its
        /// characters, for the multisyllabic words where the singer breathes mid-word. The mapper
        /// points at the breath with the playhead, so:
        ///
        /// <list type="number">
        /// <item><b>The playhead is the rest's START.</b></item>
        /// <item><b>The SPLIT snaps to the nearest character boundary at or after it</b> - the first
        /// character of the word whose own target time is not before the playhead, read through the
        /// very spread gameplay judges on (<c>TypingLine.CellTargetsFor</c>), so "the boundary" is the
        /// moment the player will actually be typing and not a second opinion about it. The rest is
        /// placed after that character's predecessor, so the character the mapper pointed at is the
        /// first one sung AFTER the breath. A playhead past every boundary takes the last one the word
        /// can hold, since a rest still needs a character on each side of it.</item>
        /// <item><b>The rest initially runs to that same boundary's time</b> - the moment the next
        /// character was due - which is the shortest rest that leaves every character in its own stretch
        /// and the one the mapper then widens by dragging its end edge.</item>
        /// </list>
        ///
        /// <para>A word may take SEVERAL rests, one per breath, so this ADDS one rather than replacing
        /// what is there: the new rest is kept only when it fits among the others - no overlap with an
        /// existing breath, its own character position, and its character in the same order as its time,
        /// which is the invariant <see cref="Gameplay.PausedWord"/> reads the word by.</para>
        ///
        /// <para>The two edges are clamped to the same minimum the subdivision boundaries use, and the
        /// word becomes Explicit hand timing with the beatmap promoted to at least Word granularity
        /// (<see cref="promoteToWordGranularity"/>): the encoder only persists <c>words[]</c>, and the
        /// pause is written inside it, so without the promotion the rest would vanish on save - the
        /// exact trap <see cref="AddSyllableBoundary"/> documents for a subdivision.</para>
        ///
        /// <para>No-op (null) on a word with fewer than two typeable characters - nothing to breathe
        /// between - or one too short to hold a rest with a character of room either side.
        /// Single undo step. Inserting over a rest the word already had replaces it.</para>
        /// </summary>
        /// <param name="editorBeatmap">The map being edited.</param>
        /// <param name="hitObject">The line owning the word.</param>
        /// <param name="unitIndex">The word unit within that line.</param>
        /// <param name="playheadTime">The editor's playhead (caret) in song time, which becomes the
        /// rest's start.</param>
        /// <returns>The pause that was written, or null when the word cannot hold one.</returns>
        public static WordPause? InsertWordPause(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, double playheadTime)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return null;

            var unit = line.Units[unitIndex];
            int typeable = Typeability.TypeableCount(unit.Text);

            if (typeable < 2)
                return null;

            // The word must hold room before the rest, the rest itself, and a character after it.
            double earliestStart = unit.StartTime + MIN_SYLLABLE_MS;
            double latestStart = unit.EndTime - MIN_SYLLABLE_MS * 2;

            if (latestStart < earliestStart)
                return null;

            var targets = Gameplay.TypingLine.CellTargetsFor(unit, typeable);

            // First character whose own target is at/after the playhead, clamped to the last one that
            // still leaves a character after it.
            int cell = 1;

            while (cell < typeable - 1 && targets[cell] < playheadTime)
                cell++;

            int split = rawIndexOfCell(unit.Text, cell);

            if (split <= 0 || split >= unit.Text.Length)
                return null;

            double pauseStart = Math.Clamp(playheadTime, earliestStart, latestStart);
            double pauseEnd = Math.Clamp(targets[cell], pauseStart + MIN_SYLLABLE_MS, unit.EndTime - MIN_SYLLABLE_MS);
            var pause = new WordPause(pauseStart, pauseEnd, split);

            var pauses = Gameplay.PausedWord.UsableRests(unit.Text, unit.StartTime, unit.EndTime, unit.Pauses.Append(pause));

            // A rest that this word cannot take (it would overlap a breath it already has, or share a
            // character with one) is refused rather than stored: the derivation would ignore it, and a
            // rest that does nothing is worse than a button that says so.
            if (pauses.Count != unit.Pauses.Count + 1)
                return null;

            replaceUnitPauses(editorBeatmap, hitObject, unitIndex, pauses);
            return pause;
        }

        /// <summary>
        /// PROMOTES one subdivision of a word into an authored PAUSE - the timeline strip's SHIFT+drag
        /// on a dotted line. The mapper drags the divider as usual, and the span it was dragged ACROSS
        /// becomes the rest: <paramref name="fromTime"/> is where the divider stood when the drag began
        /// (what the characters before it ran out at) and <paramref name="toTime"/> is the moment they
        /// let go, which is where the characters after it now begin. Its cut is the consumed divider's
        /// own character split, so the characters either side of the breath are exactly the ones that
        /// divider separated.
        ///
        /// <para>The divider is CONSUMED, exactly as <see cref="SplitWord"/> consumes one when it turns
        /// it into the gap between two words: the rest it becomes is a divider in its own right - it
        /// prints its own pipe, parts the word's text either side of itself, ticks the sub-tick and parts
        /// the judgement groups (see <see cref="WordPause"/>) - so the word's divider count does not change
        /// and its line box reads the same before and after: "re|member" dragged open is still
        /// "re|member", with the pipe now standing on a breath. A word may take several rests (one per
        /// breath), and this ADDS one.</para>
        ///
        /// <para>Both edges are clamped <see cref="MIN_SYLLABLE_MS"/> inside the word, exactly as the
        /// rest's own edge drags clamp them. No-op for a divider with no character cut to sit after (a
        /// syllabifier that degraded on an over-forced word), a rest that would not fit among the ones the
        /// word already has (see <see cref="InsertWordPause"/>), a rest the ENGINE could not honour (its
        /// split leaving every typeable cell on one side, which is the same derivation that would ignore
        /// it at play time, asked here so a mapper is never handed a rest that does nothing), and a sweep
        /// shorter than <see cref="MIN_SYLLABLE_MS"/> - a SHIFT+press that never really moved, which
        /// leaves the divider at the new position the drag gave it, exactly as a plain drag would.</para>
        ///
        /// <para>Single undo step, and it is the step the drag that owns it already opened: the strip
        /// latches where the divider started, re-times it exactly as a plain drag does while the mapper
        /// moves, and promotes the swept span on release. The word becomes Explicit hand timing and the
        /// beatmap is reconciled to carry it, as every divider edit is.</para>
        /// </summary>
        /// <returns>Whether a rest was authored.</returns>
        public static bool ExtendSubdivisionIntoPause(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex,
                                                      int boundaryIndex, double fromTime, double toTime)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return false;

            var unit = line.Units[unitIndex];

            if (boundaryIndex < 0 || boundaryIndex >= unit.SyllableBoundaries.Count)
                return false;

            // The consumed divider's own cut, which is what the rest will sit after.
            var splits = SyllableSegments.SplitsFor(unit);

            if (splits.Count != unit.SyllableBoundaries.Count)
                return false;

            int split = splits[boundaryIndex];

            if (split <= 0 || split >= unit.Text.Length)
                return false;

            int cellsBefore = 0;

            for (int i = 0; i < split; i++)
            {
                if (Typeability.IsCell(unit.Text[i]))
                    cellsBefore++;
            }

            int cells = Typeability.TypeableCount(unit.Text);

            if (cellsBefore <= 0 || cellsBefore >= cells)
                return false;

            // A sweep too short to be a drag is not one, however the clamping below would round it.
            if (Math.Abs(toTime - fromTime) < MIN_SYLLABLE_MS)
                return false;

            // Room for a character, the rest and a character, then the rest's own edges clamped into it.
            if (unit.EndTime - unit.StartTime < MIN_SYLLABLE_MS * 3)
                return false;

            double start = Math.Clamp(Math.Min(fromTime, toTime), unit.StartTime + MIN_SYLLABLE_MS, unit.EndTime - MIN_SYLLABLE_MS * 2);
            double end = Math.Clamp(Math.Max(fromTime, toTime), start + MIN_SYLLABLE_MS, unit.EndTime - MIN_SYLLABLE_MS);

            if (end - start < MIN_SYLLABLE_MS)
                return false;

            // The rest must fit among the ones this word already has, on the same terms every other rest
            // is held to.
            var added = Gameplay.PausedWord.UsableRests(unit.Text, unit.StartTime, unit.EndTime,
                unit.Pauses.Append(new WordPause(start, end, split)));

            if (added.Count != unit.Pauses.Count + 1)
                return false;

            // The divider's time goes with it; the rest of them still describe the same characters.
            var boundaries = unit.SyllableBoundaries.Where((_, i) => i != boundaryIndex).ToArray();
            var kept = SyllableSegments.IsAuthoredValid(unit.Text, unit.SyllableBoundaries.Count + 1, unit.SyllableSplits)
                ? unit.SyllableSplits.Where((_, i) => i != boundaryIndex).ToArray()
                : Array.Empty<int>();

            replaceUnitShape(editorBeatmap, hitObject, unitIndex, boundaries, kept, added);
            return true;
        }

        /// <summary>
        /// Drags the START edge of rest <paramref name="pauseIndex"/> of a word (its
        /// <paramref name="newStart"/> following the cursor), clamped to stay
        /// <see cref="MIN_SYLLABLE_MS"/> inside the word and from that rest's other edge and its
        /// neighbours' breaths, exactly as a dotted subdivision line is clamped between its neighbours.
        /// No-op on a word with no such rest, or when there is no legal slot. Single undo step.
        /// </summary>
        public static void SetWordPauseStart(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, int pauseIndex, double newStart)
            => moveWordPauseEdge(editorBeatmap, hitObject, unitIndex, pauseIndex, newStart, moveStart: true);

        /// <summary>
        /// Drags the END edge of rest <paramref name="pauseIndex"/> - the other half of the same gesture,
        /// and the edge the mapper actually widens to say how long the breath lasts. Clamped as
        /// <see cref="SetWordPauseStart"/>'s is. Single undo step.
        /// </summary>
        public static void SetWordPauseEnd(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, int pauseIndex, double newEnd)
            => moveWordPauseEdge(editorBeatmap, hitObject, unitIndex, pauseIndex, newEnd, moveStart: false);

        /// <summary>
        /// Takes rest <paramref name="pauseIndex"/> back out of a word (the double-click on that rest's
        /// greyed body), leaving the word's OTHER breaths exactly where they were. An index with no rest
        /// behind it is untouched, so it is safe to call unconditionally. Single undo step.
        /// </summary>
        public static void RemoveWordPause(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, int pauseIndex)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            var unit = line.Units[unitIndex];

            if (pauseIndex < 0 || pauseIndex >= unit.Pauses.Count)
                return;

            replaceUnitPauses(editorBeatmap, hitObject, unitIndex, unit.Pauses.Where((_, i) => i != pauseIndex).ToArray());
        }

        private static void moveWordPauseEdge(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, int pauseIndex, double newTime, bool moveStart)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            var unit = line.Units[unitIndex];

            if (pauseIndex < 0 || pauseIndex >= unit.Pauses.Count)
                return;

            var pause = unit.Pauses[pauseIndex];

            // The rest is clamped inside the word, and cannot grow THROUGH a neighbouring breath: the
            // nearer edge of the one before or after it is its wall, exactly as a neighbouring boundary is
            // a divider's.
            double lower = moveStart ? unit.StartTime + MIN_SYLLABLE_MS : pause.StartTime + MIN_SYLLABLE_MS;
            double upper = moveStart ? pause.EndTime - MIN_SYLLABLE_MS : unit.EndTime - MIN_SYLLABLE_MS;

            if (moveStart && pauseIndex > 0)
                lower = Math.Max(lower, unit.Pauses[pauseIndex - 1].EndTime + MIN_SYLLABLE_MS);

            if (!moveStart && pauseIndex < unit.Pauses.Count - 1)
                upper = Math.Min(upper, unit.Pauses[pauseIndex + 1].StartTime - MIN_SYLLABLE_MS);

            // The word (or the rest's other edge, or its neighbour) leaves no valid slot; no-op rather
            // than clamp into an inverted range.
            if (upper < lower)
                return;

            newTime = Math.Clamp(newTime, lower, upper);

            var pauses = unit.Pauses.ToArray();
            pauses[pauseIndex] = moveStart
                ? new WordPause(newTime, pause.EndTime, pause.SplitChar)
                : new WordPause(pause.StartTime, newTime, pause.SplitChar);

            replaceUnitPauses(editorBeatmap, hitObject, unitIndex, pauses);
        }

        /// <summary>
        /// Writes one unit's authored pauses back (an EMPTY list removes them all), stamping the word
        /// Explicit and trusted exactly as <see cref="replaceUnitBoundaries"/> does - a rest IS hand
        /// timing - and reconciling the beatmap's granularity so the saved map still carries the
        /// <c>words[]</c> the rests are written inside. Single undo step.
        /// </summary>
        private static void replaceUnitPauses(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, IReadOnlyList<WordPause> pauses)
        {
            var line = hitObject.Line;

            if (unitIndex < 0 || unitIndex >= line.Units.Count)
                return;

            var unit = line.Units[unitIndex];

            if (unit.Pauses.SequenceEqual(pauses))
                return;

            var units = line.Units.ToArray();

            units[unitIndex] = new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = TimingSource.Explicit,
                Confidence = 1,
                SyllableBoundaries = unit.SyllableBoundaries,
                SyllableSplits = unit.SyllableSplits,
                Pauses = pauses.ToArray(),
            };

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line, units: units);
            editorBeatmap.Update(hitObject);
            promoteToWordGranularity(editorBeatmap);
            editorBeatmap.EndChange();
        }

        /// <summary>
        /// The INDEX IN THE TOKEN of its <paramref name="cellIndex"/>-th typeable cell: "the third
        /// character" of "well-known" is 'l', not '-' or 'k', which is what a
        /// <see cref="WordPause.SplitChar"/> has to name. Falls back to the token's length for an index
        /// past the last cell, which every caller then rejects.
        /// </summary>
        private static int rawIndexOfCell(string token, int cellIndex)
        {
            int seen = 0;

            for (int i = 0; i < token.Length; i++)
            {
                if (!Typeability.IsCell(token[i]))
                    continue;

                if (seen == cellIndex)
                    return i;

                seen++;
            }

            return token.Length;
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
