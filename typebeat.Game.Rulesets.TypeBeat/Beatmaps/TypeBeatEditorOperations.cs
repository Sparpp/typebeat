// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Screens.Edit;
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
            => new TimedUnit
            {
                Text = unit.Text,
                StartTime = start,
                EndTime = end,
                Source = source ?? unit.Source,
                Confidence = confidence ?? unit.Confidence,
                // Syllable subdivisions ride along, clamped to the new span (any that fall outside
                // the re-timed window are dropped: the word shrank past them).
                SyllableBoundaries = clampBoundaries(unit.SyllableBoundaries, start, end),
            };

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
        /// through the game's typeability rules. When the token count is unchanged, each word
        /// keeps its timing; otherwise timings are redistributed (char-weighted) across the sung
        /// window. Returns false (no change) when the text normalizes to empty; an empty line
        /// cannot exist in the format; delete the line instead.
        /// </summary>
        public static bool SetLineText(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, string rawUserText)
        {
            string normalized = Typeability.Normalize(Typeability.StripBackingVocals(rawUserText));

            if (normalized.Length == 0)
                return false;

            var line = hitObject.Line;

            if (normalized == line.RawText)
                return true;

            string[] tokens = normalized.Split(' ');
            IReadOnlyList<TimedUnit> units;

            if (hitObject.Granularity == TimingGranularity.Line)
            {
                // Line-granularity maps persist no word data; units are always the loader's
                // interpolation, which is text-weight-dependent, so re-derive with the new text.
                units = LrcParser.InterpolateUnits(normalized, line.StartTime, line.SingEndTime);
            }
            else if (tokens.Length == line.Units.Count)
            {
                // Same word count: keep every word's timing (and its subdivisions), swap the text.
                units = line.Units.Select((u, i) => new TimedUnit
                {
                    Text = tokens[i],
                    StartTime = u.StartTime,
                    EndTime = u.EndTime,
                    Source = u.Source,
                    Confidence = u.Confidence,
                    SyllableBoundaries = u.SyllableBoundaries,
                }).ToArray();
            }
            else
            {
                // Word count changed: no per-word mapping exists, so redistribute within the sung window.
                units = LrcParser.InterpolateUnits(normalized, line.StartTime, line.SingEndTime);
            }

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line, rawText: normalized, units: units);
            editorBeatmap.Update(hitObject);
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

            if (normalized.Length == 0)
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

            for (int i = 0; i < edges.Count - 1; i++)
            {
                double width = edges[i + 1] - edges[i];

                if (width > widest)
                {
                    widest = width;
                    mid = (edges[i] + edges[i + 1]) / 2;
                }
            }

            // Even the widest segment cannot hold two MIN_SYLLABLE_MS halves; no room to subdivide.
            if (double.IsNaN(mid) || widest < MIN_SYLLABLE_MS * 2)
                return null;

            var boundaries = unit.SyllableBoundaries.Append(mid).OrderBy(b => b).ToArray();
            replaceUnitBoundaries(editorBeatmap, hitObject, unitIndex, boundaries);
            return mid;
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
            replaceUnitBoundaries(editorBeatmap, hitObject, unitIndex, boundaries);
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
            replaceUnitBoundaries(editorBeatmap, hitObject, unitIndex, boundaries);
        }

        /// <summary>
        /// Rebuilds a line with one unit's syllable boundaries replaced. The unit becomes Explicit
        /// and fully trusted (subdivision IS hand timing), the line stops being Estimated, and the
        /// beatmap's granularity is reconciled (up to Syllable while any boundary survives, back to
        /// Word when the last one is removed). Single undo step.
        /// </summary>
        private static void replaceUnitBoundaries(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int unitIndex, IReadOnlyList<double> boundaries)
        {
            var line = hitObject.Line;
            var units = line.Units.ToArray();
            var unit = units[unitIndex];

            units[unitIndex] = new TimedUnit
            {
                Text = unit.Text,
                StartTime = unit.StartTime,
                EndTime = unit.EndTime,
                Source = TimingSource.Explicit,
                Confidence = 1,
                SyllableBoundaries = boundaries.Count == 0 ? Array.Empty<double>() : boundaries.ToArray(),
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
        /// </summary>
        private static void syncGranularity(EditorBeatmap editorBeatmap)
        {
            var objects = editorBeatmap.HitObjects.OfType<TypeBeatHitObject>().ToList();

            if (objects.Count == 0)
                return;

            var target = InferGranularity(objects.Select(o => o.Line).ToList());

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
