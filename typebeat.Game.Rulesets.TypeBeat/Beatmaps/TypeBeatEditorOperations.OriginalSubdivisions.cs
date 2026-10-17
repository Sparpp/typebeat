// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using typebeat.Game.Rulesets.TypeBeat.Import;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    public static partial class TypeBeatEditorOperations
    {
        public static string? OriginalSubdivisionLanguage(EditorBeatmap beatmap, LyricLine line)
            => LyricOriginals.RomanisationLanguage(beatmap.Metadata.Language, new[] { OriginalCaption(line) });

        private static LyricLine normalizeOriginalSubdivisions(EditorBeatmap beatmap, LyricLine line)
        {
            bool captionMatchesWords = line.Original == null
                                       || string.Concat(line.Units.Select(u => string.Concat((u.Original ?? u.Text).Where(c => !char.IsWhiteSpace(c)))))
                                       == string.Concat(line.Original.Where(c => !char.IsWhiteSpace(c)));
            var originals = !string.IsNullOrWhiteSpace(line.Original) && (!captionMatchesWords || line.Units.Any(u => u.Original == null))
                ? OriginalLyricGenerator.MatchWords(line, line.Original, beatmap.Metadata.Language) : new string?[line.Units.Count];
            var units = line.Units.Select((u, i) =>
                originals[i] != null && originals[i] != u.Original ? LrcParser.WithOriginal(u, originals[i]) : u).ToArray();
            return units.Where((u, i) => !ReferenceEquals(u, line.Units[i])).Any() ? rebuild(line, units: units) : line;
        }

        /// <summary>Reconcile older maps on editor entry, in one undoable change.</summary>
        public static void NormalizeOriginalSubdivisions(EditorBeatmap beatmap)
        {
            var changes = OrderedLines(beatmap).Select(h => (Object: h, Line: normalizeOriginalSubdivisions(beatmap, h.Line)))
                .Where(c => !ReferenceEquals(c.Object.Line, c.Line)).ToArray();
            if (changes.Length == 0)
                return;
            beatmap.BeginChange();
            foreach (var change in changes)
            {
                change.Object.Line = change.Line;
                beatmap.Update(change.Object);
            }
            syncGranularity(beatmap, keepAuthoredWords: true);
            beatmap.EndChange();
        }

        /// <summary>The original caption with markers mapped from the linked words, retaining its whitespace.</summary>
        public static string? OriginalPipeDisplayText(LyricLine line, string? language = null)
        {
            string? caption = OriginalCaption(line);
            if (caption == null)
                return null;
            language ??= LyricOriginals.DetectLanguage(new[] { caption });
            var insertions = new List<int>();
            int cursor = 0;
            foreach (var slot in WordSlots(line))
            {
                string text = slot.Original ?? slot.Text;
                int start = caption.IndexOf(text, cursor, StringComparison.Ordinal);
                if (start < 0 || caption[cursor..start].Any(c => !char.IsWhiteSpace(c)))
                    return caption;
                if (slot.UnitIndex is int index && slot.Original != null)
                {
                    var projected = OriginalLyricSubdivisions.Project(line.Units[index], language);
                    insertions.AddRange(Gameplay.PausedWord.Cuts(projected, projected.StartTime, projected.EndTime, Typeability.IsPolyglotTypeCell).Select(c => start + c));
                }
                cursor = start + text.Length;
            }
            var result = new StringBuilder(caption);
            foreach (int position in insertions.Distinct().OrderByDescending(c => c))
                result.Insert(position, Typeability.SPLIT_MARKER);
            return result.ToString();
        }

        private static TimedUnit applyOriginalPipes(TimedUnit unit, string original, IReadOnlyList<int> pipes, string? language)
        {
            var visible = unit.Original == null ? Array.Empty<int>()
                : Gameplay.PausedWord.Cuts(OriginalLyricSubdivisions.Project(unit, language), unit.StartTime, unit.EndTime,
                    Typeability.IsPolyglotTypeCell).ToArray();
            unit = LrcParser.WithOriginal(unit, original);
            // Editing the spelling, or attaching originals, must preserve romaji-only cuts.
            if (pipes.SequenceEqual(visible))
                return unit;
            var requested = pipes.Select(p => OriginalLyricSubdivisions.TextCut(unit, p, language)).ToArray();
            // A mapper may explicitly link custom spellings which the reading cannot align.
            // Existing inferred/explicit links retain their identity when the source marker moves.
            if (unit.OriginalSplits.Count > 0 || requested.Any(p => !p.HasValue))
                return applyManualOriginalPipes(unit, pipes, language);
            var mapped = requested.Where(p => p.HasValue).Select(p => p!.Value).Distinct().OrderBy(p => p).ToArray();
            var shared = OriginalLyricSubdivisions.AlignAutomaticCuts(unit, language);
            var changed = applyPipes(new[] { shared }, new[] { unit.Text }, new[] { (IReadOnlyList<int>)mapped }, out _)[0];
            var splits = Gameplay.SyllableSegments.SplitsFor(unit);
            var hidden = splits.Select((cut, i) => (Cut: cut, Time: unit.SyllableBoundaries[i]))
                               .Where(c => !OriginalLyricSubdivisions.SourceCut(unit, c.Cut, language).HasValue).ToArray();
            var hiddenPauses = unit.Pauses.Where(p => !OriginalLyricSubdivisions.SourceCut(unit, p.SplitChar, language).HasValue).ToArray();
            if (hidden.Length == 0 && hiddenPauses.Length == 0)
                return changed;

            var combined = hidden.Concat(Gameplay.SyllableSegments.SplitsFor(changed)
                                 .Select((cut, i) => (Cut: cut, Time: changed.SyllableBoundaries[i])))
                                 .OrderBy(c => c.Cut).ToArray();
            var pauses = hiddenPauses.Concat(changed.Pauses).OrderBy(p => p.StartTime).ToArray();
            var merged = new TimedUnit
            {
                Text = unit.Text, Original = original, StartTime = unit.StartTime, EndTime = unit.EndTime,
                Source = changed.Source, Confidence = changed.Confidence,
                SyllableSplits = combined.Select(c => c.Cut).ToArray(), SyllableBoundaries = combined.Select(c => c.Time).ToArray(), Pauses = pauses,
            };
            // A moved shared cut retains its time unless it crosses a hidden cut. New cuts, or
            // crossed cuts, divide the gaps between the surviving romanized timing anchors.
            bool countChanged = mapped.Length != Gameplay.PausedWord.Cuts(shared, shared.StartTime, shared.EndTime).Count;
            if (!countChanged && combined.Where((c, i) => i > 0 && c.Time <= combined[i - 1].Time).Any() == false)
                return merged;
            var anchors = new TimedUnit
            {
                Text = unit.Text, Original = original, StartTime = unit.StartTime, EndTime = unit.EndTime,
                Source = unit.Source, Confidence = unit.Confidence,
                SyllableSplits = unit.SyllableSplits, SyllableBoundaries = unit.SyllableBoundaries, Pauses = pauses,
            };
            var allCuts = combined.Select(c => c.Cut).Concat(pauses.Select(p => p.SplitChar)).Distinct().OrderBy(c => c).ToArray();
            return withChangedPipeCount(anchors, allCuts) ?? unit;
        }

        private static TimedUnit applyManualOriginalPipes(TimedUnit unit, IReadOnlyList<int> pipes, string? language)
        {
            var glyphs = System.Globalization.StringInfo.ParseCombiningCharacters(unit.Original ?? "");
            if (pipes.Any(p => p <= 0 || !glyphs.Contains(p)) || pipes.Count >= unit.Text.Length)
                return unit;
            var allCuts = Gameplay.PausedWord.Cuts(unit, unit.StartTime, unit.EndTime).ToArray();
            var shared = allCuts.Where(c => OriginalLyricSubdivisions.SourceCut(unit, c, language).HasValue).ToArray();
            var links = new List<OriginalLyricSplit>();
            int previous = 0;
            for (int i = 0; i < pipes.Count; i++)
            {
                int? inferred = OriginalLyricSubdivisions.TextCut(unit, pipes[i], language);
                int cut = shared.Length == pipes.Count ? shared[i]
                    : allCuts.Length == pipes.Count ? allCuts[i]
                    : inferred ?? allCuts.FirstOrDefault(c => c > previous);
                if (cut <= previous || cut >= unit.Text.Length - (pipes.Count - i - 1))
                    cut = Math.Clamp((int)Math.Round(pipes[i] * unit.Text.Length / (double)unit.Original!.Length),
                        previous + 1, unit.Text.Length - (pipes.Count - i));
                links.Add(new OriginalLyricSplit(cut, pipes[i]));
                previous = cut;
            }
            var cuts = allCuts.Concat(links.Select(l => l.TextIndex)).Distinct().OrderBy(c => c).ToArray();
            var changed = cuts.SequenceEqual(allCuts) ? unit : withChangedPipeCount(unit, cuts) ?? unit;
            return OriginalLyricSubdivisions.WithExplicit(changed, links);
        }

        private static (string Text, IReadOnlyList<int> Cuts) originalMarkers(string text)
        {
            var result = new StringBuilder();
            var cuts = new List<int>();
            foreach (char c in LyricOriginals.CollapseWhitespace(text))
            {
                if (c == Typeability.SPLIT_MARKER)
                    cuts.Add(result.Length);
                else
                    result.Append(c);
            }
            return (result.ToString(), cuts);
        }

        private static bool sameOriginalWord(TimedUnit left, TimedUnit right)
            => left.Text == right.Text && left.Original == right.Original
               && left.SyllableBoundaries.SequenceEqual(right.SyllableBoundaries)
               && left.SyllableSplits.SequenceEqual(right.SyllableSplits) && left.Pauses.SequenceEqual(right.Pauses)
               && left.OriginalSplits.SequenceEqual(right.OriginalSplits);

        private static string updatedOriginalCaption(LyricLine line, List<(TimedUnit? Unit, UnromanisedWord? Pending)> entries, string caption)
        {
            var slots = WordSlots(line);
            if (slots.Count != entries.Count)
                return caption;
            var result = new StringBuilder();
            int cursor = 0;
            for (int i = 0; i < slots.Count; i++)
            {
                string before = slots[i].Original ?? slots[i].Text;
                int at = caption.IndexOf(before, cursor, StringComparison.Ordinal);
                if (at < 0 || caption[cursor..at].Any(c => !char.IsWhiteSpace(c)))
                    return caption;
                result.Append(caption[cursor..at]);
                result.Append(entries[i].Unit?.Original ?? entries[i].Pending?.Original ?? entries[i].Unit?.Text);
                cursor = at + before.Length;
            }
            return result.Append(caption[cursor..]).ToString();
        }

        private static IReadOnlyList<int>? bisectOriginalSplit(TimedUnit unit, int segment, string? language, out OriginalLyricSplit? manual)
        {
            manual = null;
            var splits = Gameplay.SyllableSegments.SplitsFor(unit).ToList();
            if (segment < 0 || segment > splits.Count)
                return null;
            int lo = segment == 0 ? 0 : splits[segment - 1];
            int hi = segment == splits.Count ? unit.Text.Length : splits[segment];
            var candidates = Enumerable.Range(lo + 1, Math.Max(0, hi - lo - 1))
                .Where(c => OriginalLyricSubdivisions.SourceCut(unit, c, language).HasValue).ToArray();
            if (candidates.Length == 0)
            {
                if (hi - lo < 2 || unit.Original == null)
                    return null;
                int sourceLo = lo == 0 ? 0 : OriginalLyricSubdivisions.SourceCut(unit, lo, language) ?? 0;
                int sourceHi = hi == unit.Text.Length ? unit.Original.Length
                    : OriginalLyricSubdivisions.SourceCut(unit, hi, language) ?? unit.Original.Length;
                var sourceCuts = System.Globalization.StringInfo.ParseCombiningCharacters(unit.Original)
                    .Where(c => c > sourceLo && c < sourceHi).ToArray();
                if (sourceCuts.Length == 0)
                    return null;
                int cut = (lo + hi) / 2;
                manual = new OriginalLyricSplit(cut, sourceCuts[(sourceCuts.Length - 1) / 2]);
                splits.Insert(segment, cut);
            }
            else
                splits.Insert(segment, candidates[(candidates.Length - 1) / 2]);
            return splits;
        }
    }
}
