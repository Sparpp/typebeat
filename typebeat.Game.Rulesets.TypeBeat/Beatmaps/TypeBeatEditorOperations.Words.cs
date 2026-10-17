// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    public static partial class TypeBeatEditorOperations
    {
        /// <summary>
        /// Joins adjacent words, including their originals, in one undo step. The former word
        /// boundary becomes a subdivision, or a pause when the words have a gap in their timing.
        /// Existing subdivisions and pauses keep their times and their character cuts.
        /// </summary>
        public static bool MergeWords(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int leftIndex)
        {
            if (!editorBeatmap.HitObjects.Contains(hitObject) || !tryMergeWords(hitObject.Line, leftIndex, editorBeatmap.Metadata.Language, out var merged))
                return false;

            commitWordMerge(editorBeatmap, hitObject, merged);
            return true;
        }

        /// <summary>
        /// Merges each contiguous run of selected words as one undo step, leaving unselected words
        /// untouched. Returns the selected indices after the merge, or none if no run can be merged.
        /// All joins are prepared before changing the map, so a refused join leaves it unchanged.
        /// </summary>
        public static int[] MergeSelectedWords(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, IEnumerable<int> selectedIndices)
        {
            int[] targets = selectedIndices.Distinct().OrderBy(i => i).ToArray();

            if (!editorBeatmap.HitObjects.Contains(hitObject) || targets.Length < 2
                || targets[0] < 0 || targets[^1] >= hitObject.Line.Units.Count)
                return Array.Empty<int>();

            var merged = hitObject.Line;
            int removed = 0;

            for (int i = targets.Length - 2; i >= 0; i--)
            {
                if (targets[i] + 1 != targets[i + 1])
                    continue;

                if (!tryMergeWords(merged, targets[i], editorBeatmap.Metadata.Language, out merged))
                    return Array.Empty<int>();

                removed++;
            }

            if (removed == 0)
                return Array.Empty<int>();

            var selection = new List<int>();
            removed = 0;

            for (int i = 0; i < targets.Length; i++)
            {
                if (i > 0 && targets[i - 1] + 1 == targets[i])
                    removed++;
                else
                    selection.Add(targets[i] - removed);
            }

            commitWordMerge(editorBeatmap, hitObject, merged);
            return selection.ToArray();
        }

        private static bool tryMergeWords(LyricLine line, int leftIndex, typebeat.Game.Beatmaps.BeatmapLanguage language, out LyricLine mergedLine)
        {
            mergedLine = line;
            string[] tokens = line.RawText.Split(' ');

            if (leftIndex < 0 || leftIndex + 1 >= line.Units.Count
                || tokens.Length != line.Units.Count || line.Units.Where((u, i) => u.Text != tokens[i]).Any()
                || line.UnromanisedWords.Any(w => w.Position == leftIndex + 1))
                return false;

            var left = line.Units[leftIndex];
            var right = line.Units[leftIndex + 1];
            var leftSplits = SyllableSegments.SplitsFor(left);
            var rightSplits = SyllableSegments.SplitsFor(right);

            if (left.EndTime > right.StartTime || left.StartTime >= left.EndTime || right.StartTime >= right.EndTime
                || leftSplits.Count != left.SyllableBoundaries.Count || rightSplits.Count != right.SyllableBoundaries.Count)
                return false;

            if (line.Original != null && line.Units.Any(u => u.Original == null))
            {
                var originals = Import.OriginalLyricGenerator.MatchWords(line, line.Original, language);
                var linked = line.Units.Select((u, i) => u.Original == null && originals[i] != null ? LrcParser.WithOriginal(u, originals[i]) : u).ToArray();
                line = rebuild(line, units: linked);
                left = linked[leftIndex];
                right = linked[leftIndex + 1];
            }
            bool gap = left.EndTime < right.StartTime;
            int cut = left.Text.Length;
            var merged = new TimedUnit
            {
                Text = left.Text + right.Text,
                Original = left.Original == null && right.Original == null ? null : (left.Original ?? left.Text) + (right.Original ?? right.Text),
                OriginalSplits = left.OriginalSplits.Concat(right.OriginalSplits.Select(link => new OriginalLyricSplit(
                    link.TextIndex + cut, link.OriginalIndex + (left.Original ?? left.Text).Length)))
                    .Concat(left.Original != null && right.Original != null
                        ? new[] { new OriginalLyricSplit(cut, left.Original.Length) } : Array.Empty<OriginalLyricSplit>())
                    .OrderBy(link => link.TextIndex).ToArray(),
                StartTime = left.StartTime,
                EndTime = right.EndTime,
                Source = TimingSource.Explicit,
                Confidence = Math.Min(left.Confidence, right.Confidence),
                SyllableBoundaries = left.SyllableBoundaries.Concat(gap ? Array.Empty<double>() : new[] { right.StartTime })
                                         .Concat(right.SyllableBoundaries).ToArray(),
                SyllableSplits = leftSplits.Concat(gap ? Array.Empty<int>() : new[] { cut })
                                   .Concat(rightSplits.Select(s => s + cut)).ToArray(),
                Pauses = left.Pauses.Concat(gap ? new[] { new WordPause(left.EndTime, right.StartTime, cut) } : Array.Empty<WordPause>())
                             .Concat(right.Pauses.Select(p => p with { SplitChar = p.SplitChar + cut })).ToArray(),
            };

            var units = line.Units.ToList();
            units[leftIndex] = merged;
            units.RemoveAt(leftIndex + 1);
            var pending = line.UnromanisedWords.Select(w => w.Position > leftIndex + 1 ? w with { Position = w.Position - 1 } : w).ToArray();

            // A caption spelled from the linked words follows their join. A separately authored
            // caption can have different spacing/wording and has no reliable word-index mapping.
            string? original = line.Original == JoinedOriginal(line.Units, line.UnromanisedWords)
                ? JoinedOriginal(units, pending)
                : line.Original;

            mergedLine = rebuild(line, rawText: string.Join(' ', units.Select(u => u.Text)), units: units,
                originals: (original, pending));
            return true;
        }

        private static void commitWordMerge(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, LyricLine merged)
        {
            editorBeatmap.BeginChange();
            hitObject.Line = merged;
            editorBeatmap.Update(hitObject);
            syncGranularity(editorBeatmap, keepAuthoredWords: true);
            editorBeatmap.EndChange();
        }
    }
}
