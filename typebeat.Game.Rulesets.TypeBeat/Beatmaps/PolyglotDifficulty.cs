// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using typebeat.Game.Rulesets.Mods;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Mods;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// An input projection for difficulty and map pace. Latin/Cyrillic originals cost one cell per character;
    /// composition keys add demand at the Shift cost without inventing scoring cells. Japanese
    /// becomes the shortest accepted romaji stream at the live original-character targets.
    /// The authored beatmap and its scoring objects are never modified.
    /// </summary>
    internal static class PolyglotDifficulty
    {
        internal sealed record InputSegment(string Text, double Start, double End, double[] Targets);
        private sealed record InputCost(double Extra, IReadOnlyList<InputSegment>? Segments = null);
        private static readonly ConditionalWeakTable<TimedUnit, InputCost> costs = new ConditionalWeakTable<TimedUnit, InputCost>();

        internal static double ExtraInputs(TimedUnit unit) => costs.TryGetValue(unit, out var cost) ? cost.Extra : 0;

        internal static IReadOnlyList<InputSegment>? SegmentsFor(TimedUnit unit)
            => costs.TryGetValue(unit, out var cost) ? cost.Segments : null;

        internal static IReadOnlyList<LyricLine> LinesFor(IEnumerable<LyricLine> source, IReadOnlyList<Mod>? mods, string? language = null, bool forPace = false)
        {
            var lines = source as IReadOnlyList<LyricLine> ?? source.ToArray();
            if (!PolyglotRatingSupport.Enabled(mods) || !PolyglotRatingSupport.Supports(lines, mods, language))
                return lines;
            language = PolyglotRatingSupport.LanguageFor(lines, language);
            bool japanese = JapaneseReading.IsJapanese(language);
            bool literate = Scoring.PerformancePoints.IsLiterate(mods);
            return lines.Select(line => project(line, language, literate, japanese, forPace)).ToArray();
        }

        private static LyricLine project(LyricLine line, string? language, bool literate, bool japanese, bool forPace)
        {
            if (line.Original == null && line.UnromanisedWords.Count == 0 && line.Units.All(u => u.Original == null))
                return line;
            var derived = PolyglotLine.Derive(line, language, japaneseCharacterTiming: japanese).Line;
            if (ReferenceEquals(derived, line))
                return line;
            if (japanese)
                return projectJapanese(line, language, literate, forPace);
            var units = new List<TimedUnit>();
            foreach (var unit in derived.Units)
            {
                var text = new StringBuilder(unit.Text.Length);
                var positions = new int[unit.Text.Length + 1];
                for (int i = 0; i < unit.Text.Length; i++)
                {
                    char c = unit.Text[i];
                    char key = forPace && literate && Typeability.IsPolyglotPunctuation(c) ? 'a' : proxy(c, language);
                    // WPM counts a composed letter's extra inputs in full; SR weights them at the
                    // Shift surcharge rate. Ordinary Shift presses follow the nomod WPM convention.
                    int extra = forPace && char.IsLetter(c) ? CompositionInputs(c, language) : 0;
                    text.Append(key, 1 + extra);
                    positions[i + 1] = text.Length;
                }
                var projected = new TimedUnit
                {
                    Text = text.ToString(),
                    StartTime = unit.StartTime, EndTime = unit.EndTime,
                    Source = unit.Source, Confidence = unit.Confidence,
                    SyllableSplits = forPace ? unit.SyllableSplits.Select(c => positions[Math.Clamp(c, 0, unit.Text.Length)]).ToArray() : unit.SyllableSplits,
                    SyllableBoundaries = unit.SyllableBoundaries,
                    Pauses = forPace ? unit.Pauses.Select(p => p with { SplitChar = positions[Math.Clamp(p.SplitChar, 0, unit.Text.Length)] }).ToArray() : unit.Pauses,
                };
                if (!forPace)
                    costs.Add(projected, new InputCost(unit.Text.Where(c => Typeability.IsPolyglotTypeCell(c) || literate && Typeability.IsPolyglotPunctuation(c)).Sum(c => CompositionInputs(c, language))));
                units.Add(projected);
            }
            return new LyricLine
            {
                RawText = string.Join(' ', units.Select(u => u.Text)), Units = units,
                StartTime = line.StartTime, EndTime = line.EndTime, SingEndTime = line.SingEndTime,
                SealGraceMs = line.SealGraceMs, Estimated = line.Estimated,
            };
        }

        private static LyricLine projectJapanese(LyricLine line, string? language, bool literate, bool forPace)
        {
            var typing = TypingLine.ForMods(line, literate, true, language, alignSubdivisionTargets: true, japaneseCharacterTiming: true);
            var plan = typing.JapaneseInput!;
            var inputs = new List<(char Key, double Start, double End)>();
            var segments = new List<(int First, int Count, double Start, double End)>();
            for (int cell = 0; cell < typing.Cells.Count; cell++)
            {
                var current = typing.Cells[cell];
                if (!current.IsTypeable)
                    continue;
                var group = plan.At(cell);
                if (group == null)
                {
                    double end = cell + 1 < typing.Cells.Count ? typing.Cells[cell + 1].TargetTime : typing.Source.SingEndTime;
                    if (current.Expected != ' ')
                        segments.Add((inputs.Count, 1, current.TargetTime, end));
                    char key = forPace && Typeability.IsPolyglotPunctuation(current.Expected) ? 'a' : proxy(current.Expected, language);
                    inputs.Add((key, current.TargetTime, end));
                    continue;
                }
                if (group.StartCell != cell)
                    continue;
                string input = JapaneseRomaji.ShortestInput(group.Reading, plan.Following(group)) ?? "a";
                int[]? ends = JapaneseRomaji.InputEnds(group.Reading, input, plan.Following(group));
                int kana = 0, previous = 0;
                for (int g = group.StartCell; g < group.EndCellExclusive; g++)
                {
                    kana += (plan.CharacterReadingAt(g) ?? "").Length;
                    int end = ends != null && kana > 0 && kana <= ends.Length ? ends[kana - 1]
                        : g == group.EndCellExclusive - 1 ? input.Length : previous;
                    end = Math.Clamp(end, previous, input.Length);
                    double startTime = typing.Cells[g].TargetTime;
                    double endTime = typing.JapaneseCharacterEnd(g);
                    int count = end - previous;
                    if (count > 0)
                        segments.Add((inputs.Count, count, startTime, endTime));
                    for (int key = 0; key < count; key++)
                    {
                        double target = startTime + Math.Max(0, endTime - startTime) * key / Math.Max(1, count > 2 ? count - 1 : count);
                        // Romaji punctuation (the long-vowel hyphen) is an actual Japanese input,
                        // even without Literate. Give it a letter cell on the default rating surface.
                        char inputKey = input[previous + key];
                        inputs.Add(((!literate || forPace) && !Typeability.IsTypeable(inputKey) ? 'a' : inputKey, target, endTime));
                    }
                    previous = end;
                }
                cell = group.EndCellExclusive - 1;
            }

            // Every input gets an authored subdivision, so rhythm sees the same key targets as
            // a regular map explicitly mapped with the shortest romaji spelling. Spaces remain
            // ordinary word separators; gaps between character windows remain actual pauses.
            var units = new List<TimedUnit>();
            int at = 0;
            while (at < inputs.Count)
            {
                if (inputs[at].Key == ' ')
                {
                    at++;
                    continue;
                }
                int first = at;
                while (at < inputs.Count && inputs[at].Key != ' ')
                    at++;
                var run = inputs.GetRange(first, at - first);
                double start = run[0].Start;
                double end = Math.Max(start + 0.001 * run.Count, Math.Max(run[^1].End, run[^1].Start + 0.001));
                var boundaries = new double[run.Count - 1];
                var pauses = new List<WordPause>();
                double previous = start;
                for (int i = 1; i < run.Count; i++)
                {
                    boundaries[i - 1] = Math.Clamp(run[i].Start, previous + 0.000001, end - (run.Count - i) * 0.000001);
                    previous = boundaries[i - 1];
                    if (run[i].Start > run[i - 1].End + 0.001)
                        pauses.Add(new WordPause(run[i - 1].End, run[i].Start, i));
                }
                var unit = new TimedUnit
                {
                    Text = new string(run.Select(k => k.Key).ToArray()), StartTime = start, EndTime = end,
                    SyllableSplits = Enumerable.Range(1, boundaries.Length).ToArray(),
                    SyllableBoundaries = boundaries, Pauses = pauses,
                };
                var windows = segments.Where(s => s.First >= first && s.First < at).Select(s =>
                {
                    var keys = inputs.GetRange(s.First, s.Count);
                    return new InputSegment(new string(keys.Select(k => k.Key).ToArray()), s.Start, s.End,
                        keys.Select(k => k.Start).ToArray());
                }).ToArray();
                costs.Add(unit, new InputCost(0, windows));
                units.Add(unit);
            }
            return new LyricLine
            {
                RawText = string.Join(' ', units.Select(u => u.Text)), Units = units,
                StartTime = line.StartTime, EndTime = line.EndTime, SingEndTime = line.SingEndTime,
                SealGraceMs = line.SealGraceMs, Estimated = line.Estimated,
            };
        }

        // A fixed native-layout baseline, independent of the player's active OS layout. Cyrillic
        // letters and direct national-layout accents are native keys. ë accepts plain e in gameplay, so has no composition cost.
        internal static int CompositionInputs(char c, string? language = null)
        {
            if (c <= 127 || c is 'ë' or 'Ë'
                || PolyglotRatingSupport.IsNativeLetter(c, language)
                || !char.IsLetter(c) && !Typeability.IsPolyglotPunctuation(c))
                return 0;
            string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            int marks = decomposed.Count(x => CharUnicodeInfo.GetUnicodeCategory(x) == UnicodeCategory.NonSpacingMark);
            return c is >= '\u0400' and <= '\u052f' && marks == 0 ? 0 : Math.Max(1, marks);
        }

        private static char proxy(char c, string? language)
        {
            if (c <= 127)
                return c;
            // Physical keys on the standard Russian layout, preserving one key per letter.
            const string russian = "йцукенгшщзхъфывапролджэячсмитьбюё";
            const string keys = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
            int index = russian.IndexOf(char.ToLowerInvariant(c));
            if (index >= 0)
            {
                // Punctuation-shaped physical keys still represent LETTER cells here.
                char key = keys[index];
                key = char.IsLetter(key) ? key : 'a';
                return char.IsUpper(c) ? char.ToUpperInvariant(key) : key;
            }
            if (Typeability.IsPolyglotPunctuation(c))
            {
                char foldedMark = Typeability.FoldTypographic(c);
                return Typeability.IsPunctuation(foldedMark) && foldedMark != '-' ? foldedMark : '.';
            }
            if (!Typeability.IsPolyglotTypeCell(c))
                return '|';
            string folded = Romaniser.Romanise(c.ToString(), language).Text;
            char keyProxy = folded.FirstOrDefault(Typeability.IsTypeable, 'a');
            return char.IsUpper(c) ? char.ToUpperInvariant(keyProxy) : char.ToLowerInvariant(keyProxy);
        }
    }
}
