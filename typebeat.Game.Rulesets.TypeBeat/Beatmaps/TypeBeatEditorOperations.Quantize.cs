// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    public static partial class TypeBeatEditorOperations
    {
        public static bool QuantizeWords(EditorBeatmap beatmap, TypeBeatHitObject hitObject, IEnumerable<int> selected, Func<double, double> snap)
        {
            if (!beatmap.HitObjects.Contains(hitObject))
                return false;
            var line = hitObject.Line;
            var units = line.Units.ToArray();
            bool changed = false;
            foreach (int i in selected.Distinct().OrderBy(i => i))
            {
                if (i < 0 || i >= units.Length)
                    continue;
                var unit = units[i];
                double start = snap(unit.StartTime), end = snap(unit.EndTime);
                // A quantize cannot erase a word or move it across unselected neighbours.
                if (end - start < MIN_WORD_SPAN_MS || start < 0 || end > unitCeiling(beatmap, hitObject, units.Length - 1))
                    continue;
                if (start == unit.StartTime && end == unit.EndTime)
                    continue;
                units[i] = retime(unit, start, end, TimingSource.Explicit);
                changed = true;
            }
            if (!changed || units.Zip(units.Skip(1), (a, b) => a.EndTime > b.StartTime).Any(overlap => overlap))
                return false;
            var ordered = OrderedLines(beatmap);
            int lineIndex = ordered.IndexOf(hitObject);
            if (lineIndex > 0 && units[0].StartTime < ordered[lineIndex - 1].Line.SingEndTime)
                return false;
            beatmap.BeginChange();
            if (units[0].StartTime < line.StartTime)
                SetLineStart(beatmap, hitObject, units[0].StartTime);
            hitObject.Line = rebuild(hitObject.Line, units: units);
            beatmap.Update(hitObject);
            syncSingEndToLastUnit(beatmap, hitObject, lastUnitEnd(line));
            syncGranularity(beatmap, keepAuthoredWords: true);
            beatmap.EndChange();
            return true;
        }
    }
}
