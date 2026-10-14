// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
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
        /// Existing subdivisions and pauses keep their times and their character cuts. Refused when
        /// the merged word would carry both holds and pauses (a word has one or the other).
        /// </summary>
        public static bool MergeWords(EditorBeatmap editorBeatmap, TypeBeatHitObject hitObject, int leftIndex)
        {
            var line = hitObject.Line;
            string[] tokens = line.RawText.Split(' ');

            if (!editorBeatmap.HitObjects.Contains(hitObject) || leftIndex < 0 || leftIndex + 1 >= line.Units.Count
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

            bool gap = left.EndTime < right.StartTime;
            int cut = left.Text.Length;
            var merged = new TimedUnit
            {
                Objects = new WordObjects(left.Objects.AutoFill || right.Objects.AutoFill)
                {
                    Holds = left.Objects.Holds.Concat(right.Objects.Holds.Select(h => h with { Char = h.Char + cut })).ToArray(),
                    // Picks survive only when both sides have them, so the joined word is never half-picked.
                    TypedLetters = left.Objects.AutoFill && right.Objects.AutoFill && left.Objects.TypedLetters.Count > 0 && right.Objects.TypedLetters.Count > 0
                        ? left.Objects.TypedLetters.Concat(right.Objects.TypedLetters.Select(i => i + cut)).ToArray()
                        : Array.Empty<int>(),
                },
                Text = left.Text + right.Text,
                Original = left.Original == null && right.Original == null ? null : (left.Original ?? left.Text) + (right.Original ?? right.Text),
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

            // A word carries pauses or holds, never both.
            if (merged.Objects.Holds.Count > 0 && merged.Pauses.Count > 0)
                return false;

            var units = line.Units.ToList();
            units[leftIndex] = merged;
            units.RemoveAt(leftIndex + 1);
            var pending = line.UnromanisedWords.Select(w => w.Position > leftIndex + 1 ? w with { Position = w.Position - 1 } : w).ToArray();

            // A caption spelled from the linked words follows their join. A separately authored
            // caption can have different spacing/wording and has no reliable word-index mapping.
            string? original = line.Original == JoinedOriginal(line.Units, line.UnromanisedWords)
                ? JoinedOriginal(units, pending)
                : line.Original;

            editorBeatmap.BeginChange();
            hitObject.Line = rebuild(line, rawText: string.Join(' ', units.Select(u => u.Text)), units: units,
                originals: (original, pending));
            editorBeatmap.Update(hitObject);
            syncGranularity(editorBeatmap, keepAuthoredWords: true);
            editorBeatmap.EndChange();
            return true;
        }
    }
}
