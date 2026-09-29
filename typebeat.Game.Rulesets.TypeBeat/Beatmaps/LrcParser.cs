// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Beatmaps/LrcParser.cs (regression-anchored).
// Only the namespace changed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Pure static LRC parser; the fallback path for maps without a timing.json.
    /// Handles [mm:ss.xx]/[mm:ss.xxx] tags, multiple leading tags (duplicate the line),
    /// [offset:] shifting, a trailing bare terminator timestamp, the vocal-density cap,
    /// and char-weighted word-unit interpolation.
    ///
    /// <para>Both AUTHORING MARKS survive the LRC's normalization (backlog 202): a '|'
    /// (<see cref="Typeability.SPLIT_MARKER"/>) subdivides its word, evenly, and is then stripped;
    /// a '&amp;' (<see cref="Typeability.FREESTYLE_MARKER"/>) survives into the stored lyric as a
    /// freestyle cell, which also makes a line of nothing but ampersands a real (freestyle) line
    /// sitting exactly where the LRC put it rather than a vanished one. A line carrying neither
    /// mark parses exactly as it always has, character for character.</para>
    /// </summary>
    public static class LrcParser
    {
        public const double MAX_MS_PER_TYPEABLE_CHAR = 350;
        public const double DEFAULT_LAST_LINE_DURATION_MS = 5000;

        /// <param name="lrcContent">The .lrc text.</param>
        /// <param name="language">The language to romanise non-Latin lines under (backlog 330; see
        /// <see cref="Romaniser"/>). Only a line that needs romanising reads it, so a Latin LRC parses
        /// exactly as it always has whatever it says.</param>
        public static IReadOnlyList<LyricLine> Parse(string lrcContent, string? language = null)
        {
            var result = new List<LyricLine>();
            if (string.IsNullOrEmpty(lrcContent))
                return result;

            // Strip a leading BOM if present.
            if (lrcContent[0] == '﻿')
                lrcContent = lrcContent.Substring(1);

            string[] rawLines = lrcContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // First pass: resolve a single [offset:] value (last occurrence wins).
            double offset = 0;

            foreach (string raw in rawLines)
            {
                if (tryReadOffset(raw, out double parsed))
                    offset = parsed;
            }

            // Second pass: collect every timestamped entry (empty text allowed; those are
            // pure boundary/terminator markers).
            var entries = new List<(double Time, string Text, LyricOriginals.RomanisedLine? Romanised)>();

            foreach (string raw in rawLines)
            {
                extractEntries(raw, offset, entries, language);
            }

            if (entries.Count == 0)
                return result;

            // Stable sort by time so duplicated leading tags keep insertion order at ties.
            entries.Sort((a, b) => a.Time.CompareTo(b.Time));

            // Indices of entries that carry real (non-empty normalized) text, or an ORIGINAL the
            // romaniser could not spell (backlog 330), which is a lyric line and not a terminator.
            var emitted = new List<int>();

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Text.Length > 0 || entries[i].Romanised?.Original != null)
                    emitted.Add(i);
            }

            for (int e = 0; e < emitted.Count; e++)
            {
                int idx = emitted[e];
                double start = entries[idx].Time;
                string text = entries[idx].Text;
                var romanised = entries[idx].Romanised;

                // An unromanised word sings for as long as its letters take, so it counts toward the
                // line's sung length as if typed; a line without one counts exactly as before.
                int typeable = Typeability.TypeableCount(text) + (romanised == null ? 0 : unromanisedWeight(romanised));

                double end;

                if (e < emitted.Count - 1)
                {
                    // Non-last line: hard seal at the next emitted line's start.
                    end = entries[emitted[e + 1]].Time;
                }
                else
                {
                    // Last emitted line: use a trailing terminator timestamp if one exists
                    // after it, otherwise fall back to a bounded default duration.
                    double? terminator = null;

                    for (int j = idx + 1; j < entries.Count; j++)
                    {
                        if (entries[j].Time > start)
                        {
                            terminator = entries[j].Time;
                            break;
                        }
                    }

                    end = terminator ?? start + Math.Min(DEFAULT_LAST_LINE_DURATION_MS, MAX_MS_PER_TYPEABLE_CHAR * typeable);
                }

                if (end < start)
                    end = start;

                double singEnd = start + Math.Min(end - start, MAX_MS_PER_TYPEABLE_CHAR * typeable);

                if (romanised != null)
                {
                    result.Add(romanisedLine(romanised, text, start, end, singEnd));
                    continue;
                }

                result.Add(new LyricLine
                {
                    // The pipes TIME the units (below) but are authoring marks, so the stored
                    // lyric is the text without them.
                    RawText = SplitMarkers.Carries(text) ? SplitMarkers.Strip(text).Text : text,
                    StartTime = start,
                    EndTime = end,
                    SingEndTime = singEnd,
                    Units = InterpolateUnits(text, start, singEnd)
                });
            }

            return result;
        }

        /// <summary>The letters of a line's unromanised words, the weight they take in its timing.</summary>
        private static int unromanisedWeight(LyricOriginals.RomanisedLine romanised)
        {
            int weight = 0;

            foreach (var word in romanised.Words)
            {
                if (word.Flagged)
                    weight += System.Globalization.StringInfo.ParseCombiningCharacters(word.Source).Length;
            }

            return weight;
        }

        /// <summary>
        /// A line whose source needed ROMANISING (backlog 330): the stored text is the romanisation,
        /// the source is kept as the line's and each word's original, and a word the romaniser could
        /// not spell becomes an <see cref="UnromanisedWord"/> holding its share of the line. The
        /// words are spread over the sung span char-weighted exactly as <see cref="InterpolateUnits"/>
        /// spreads them (an unromanised word weighs its letter count, as if it were typed), so a line
        /// with none interpolates identically to the romanised text alone.
        /// </summary>
        private static LyricLine romanisedLine(LyricOriginals.RomanisedLine romanised, string text, double start, double end, double singEnd)
        {
            string stored = SplitMarkers.Carries(text) ? SplitMarkers.Strip(text).Text : text;
            var words = romanised.Words.Where(w => w.Flagged || w.Text.Length > 0).ToList();

            List<TimedUnit> units;
            IReadOnlyList<UnromanisedWord> pending;

            if (!romanised.AnyFlagged)
            {
                units = InterpolateUnits(text, start, singEnd).ToList();
                pending = Array.Empty<UnromanisedWord>();

                var typed = words.Where(w => !w.Flagged).ToList();

                if (typed.Count == units.Count)
                {
                    for (int i = 0; i < units.Count; i++)
                    {
                        if (typed[i].Original is string original)
                            units[i] = WithOriginal(units[i], original);
                    }
                }
            }
            else
            {
                (units, pending) = InterpolateWords(words.Select(w => (w.Text, w.Flagged, w.Source, w.Original)).ToList(), start, singEnd);
            }

            return new LyricLine
            {
                RawText = stored,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = units,
                Original = romanised.Original,
                UnromanisedWords = pending,
            };
        }

        /// <summary>
        /// Spreads a line's words over [<paramref name="start"/>, <paramref name="end"/>] char-weighted,
        /// the <see cref="InterpolateUnits"/> rule, with the UNROMANISED words (backlog 330) taking a
        /// share too (their letter count, as if typed) and coming back as
        /// <see cref="UnromanisedWord"/>s at their place instead of as units. Each typed word keeps
        /// its original.
        /// </summary>
        internal static (List<TimedUnit> Units, IReadOnlyList<UnromanisedWord> Pending) InterpolateWords(
            IReadOnlyList<(string Text, bool Flagged, string Source, string? Original)> words, double start, double end)
        {
            var units = new List<TimedUnit>();
            var pending = new List<UnromanisedWord>();

            if (words.Count == 0)
                return (units, pending);

            double[] weights = words.Select(w => w.Flagged
                ? StringInfo.ParseCombiningCharacters(w.Source).Length + 1.0
                : Typeability.TypeableCount(w.Text) + 1.0).ToArray();

            double total = weights.Sum();
            double cumulative = 0;

            for (int i = 0; i < words.Count; i++)
            {
                double from = start + (end - start) * (cumulative / total);
                cumulative += weights[i];
                double to = start + (end - start) * (cumulative / total);

                if (words[i].Flagged)
                {
                    pending.Add(new UnromanisedWord(units.Count, LyricOriginals.CollapseWhitespace(words[i].Source), from, to));
                    continue;
                }

                foreach (var u in InterpolateUnits(words[i].Text, from, to))
                    units.Add(WithOriginal(u, words[i].Original));
            }

            return (units, pending);
        }

        /// <summary><paramref name="unit"/> with <paramref name="original"/> as its original, everything else kept.</summary>
        internal static TimedUnit WithOriginal(TimedUnit unit, string? original) => new TimedUnit
        {
            Text = unit.Text,
            StartTime = unit.StartTime,
            EndTime = unit.EndTime,
            Source = unit.Source,
            Confidence = unit.Confidence,
            SyllableBoundaries = unit.SyllableBoundaries,
            SyllableSplits = unit.SyllableSplits,
            Pauses = unit.Pauses,
            Original = original,
        };

        /// <summary>Parses "mm:ss.xx" and "mm:ss.xxx" (also tolerates "m:ss.x").</summary>
        public static bool TryParseTimestamp(string token, out double milliseconds)
        {
            milliseconds = 0;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            token = token.Trim();

            int colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1)
                return false;

            string minutesPart = token.Substring(0, colon);
            string rest = token.Substring(colon + 1);

            if (!int.TryParse(minutesPart, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
                return false;

            int dot = rest.IndexOf('.');
            string secondsPart;
            string fractionPart;

            if (dot < 0)
            {
                secondsPart = rest;
                fractionPart = string.Empty;
            }
            else
            {
                secondsPart = rest.Substring(0, dot);
                fractionPart = rest.Substring(dot + 1);
            }

            if (!int.TryParse(secondsPart, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
                return false;

            double fractionMs = 0;

            if (fractionPart.Length > 0)
            {
                if (!int.TryParse(fractionPart, NumberStyles.None, CultureInfo.InvariantCulture, out int frac))
                    return false;

                // ".48" -> 480ms, ".395" -> 395ms; scale by the number of fractional digits.
                fractionMs = frac / Math.Pow(10, fractionPart.Length) * 1000.0;
            }

            milliseconds = minutes * 60000.0 + seconds * 1000.0 + fractionMs;
            return true;
        }

        /// <summary>
        /// Distributes [start, end] over the whitespace tokens of <paramref name="normalizedText"/>,
        /// weighting each token by (typeableCount + 1). Source = Interpolated.
        /// Shared by the LRC path and TimingJsonLoader's per-line fallback.
        ///
        /// <para>A token carrying '|' (<see cref="Typeability.SPLIT_MARKER"/>) additionally AUTHORS
        /// a syllable subdivision of its own word: the pipes are stripped from its text, its span is
        /// cut into (pipes + 1) EQUAL segments, and the split is recorded at the characters the
        /// pipes sat between. Text with no pipe in it takes the original path untouched, so every
        /// caller that never sees one (which is every map written before backlog 202) is
        /// byte-identical.</para>
        /// </summary>
        internal static IReadOnlyList<TimedUnit> InterpolateUnits(string normalizedText, double start, double end)
        {
            var units = new List<TimedUnit>();
            if (string.IsNullOrEmpty(normalizedText))
                return units;

            IReadOnlyList<IReadOnlyList<int>> pipes = Array.Empty<IReadOnlyList<int>>();

            if (SplitMarkers.Carries(normalizedText))
            {
                (normalizedText, pipes) = SplitMarkers.Strip(normalizedText);

                // Nothing but pipes: there is no word left to time.
                if (normalizedText.Length == 0)
                    return units;
            }

            string[] tokens = normalizedText.Split(' ');

            double totalWeight = 0;
            double[] weights = new double[tokens.Length];

            for (int i = 0; i < tokens.Length; i++)
            {
                weights[i] = Typeability.TypeableCount(tokens[i]) + 1;
                totalWeight += weights[i];
            }

            if (totalWeight <= 0)
                totalWeight = tokens.Length;

            double span = end - start;
            double cumulative = 0;

            for (int i = 0; i < tokens.Length; i++)
            {
                double unitStart = start + span * (cumulative / totalWeight);
                cumulative += weights[i];
                double unitEnd = start + span * (cumulative / totalWeight);

                var authored = i < pipes.Count
                    ? SplitMarkers.Authored(tokens[i], unitStart, unitEnd, pipes[i])
                    : null;

                units.Add(new TimedUnit
                {
                    Text = tokens[i],
                    StartTime = unitStart,
                    EndTime = unitEnd,
                    Source = TimingSource.Interpolated,
                    SyllableBoundaries = authored?.Boundaries ?? Array.Empty<double>(),
                    SyllableSplits = authored?.Splits ?? Array.Empty<int>(),
                });
            }

            return units;
        }

        private static bool tryReadOffset(string rawLine, out double offset)
        {
            offset = 0;
            if (string.IsNullOrEmpty(rawLine))
                return false;

            int idx = 0;
            while (idx < rawLine.Length && char.IsWhiteSpace(rawLine[idx]))
                idx++;

            while (idx < rawLine.Length && rawLine[idx] == '[')
            {
                int close = rawLine.IndexOf(']', idx);
                if (close < 0)
                    return false;

                string inner = rawLine.Substring(idx + 1, close - idx - 1);
                idx = close + 1;

                int c = inner.IndexOf(':');

                if (c > 0 && inner.Substring(0, c).Trim().Equals("offset", StringComparison.OrdinalIgnoreCase))
                {
                    string value = inner.Substring(c + 1).Trim();

                    // Accept a leading '+' which int.TryParse(NumberStyles.Integer) already allows.
                    if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double parsed))
                    {
                        offset = parsed;
                        return true;
                    }
                }
            }

            return false;
        }

        private static void extractEntries(string rawLine, double offset, List<(double Time, string Text, LyricOriginals.RomanisedLine? Romanised)> entries,
                                           string? language)
        {
            if (string.IsNullOrEmpty(rawLine))
                return;

            int idx = 0;
            while (idx < rawLine.Length && char.IsWhiteSpace(rawLine[idx]))
                idx++;

            var times = new List<double>();

            while (idx < rawLine.Length && rawLine[idx] == '[')
            {
                int close = rawLine.IndexOf(']', idx);
                if (close < 0)
                    break;

                string inner = rawLine.Substring(idx + 1, close - idx - 1);
                idx = close + 1;

                if (TryParseTimestamp(inner, out double ms))
                    times.Add(ms - offset);
                // Metadata tags ([ti:], [ar:], [offset:], [Lyrics], ...) are silently skipped.
            }

            if (times.Count == 0)
                return; // Non-timestamped line, skipped entirely.

            string rawText = rawLine.Substring(idx);

            // THE ORIGINAL TEXT (backlog 330). A line written in a script Normalize would delete (or
            // with any letter it would have to respell) is romanised word by word first, and its
            // source kept as the original, instead of being thrown away. A plain ASCII line never
            // takes this branch, so it parses character for character as before.
            string unbracketed = Typeability.StripBackingVocals(rawText);

            if (LyricOriginals.CarriesOriginal(unbracketed) || Romaniser.NeedsRomanising(unbracketed, language))
            {
                var romanised = LyricOriginals.RomaniseLine(unbracketed, language, keepMarkers: true);
                string romanisedText = romanised.Text;
                string romanisedStored = SplitMarkers.Carries(romanisedText) ? SplitMarkers.Strip(romanisedText).Text : romanisedText;

                // Nothing typed and nothing left to romanise by hand (a line of symbols): the same
                // vanishing a Latin line of punctuation gets below.
                if (Typeability.ToDefaultStream(romanisedStored).Length == 0 && !romanised.AnyFlagged)
                    return;

                foreach (double t in times)
                    entries.Add((t, romanisedText, romanised));

                return;
            }

            // Both authoring marks survive here (backlog 202): '&' becomes a freestyle cell of the
            // stored lyric, '|' subdivides its word and is stripped once its position is read.
            string text = Typeability.Normalize(unbracketed,
                keepFreestyleMarkers: true, keepSplitMarkers: true);

            // Emptiness is judged on what will actually be STORED, so a token of nothing but pipes
            // cannot smuggle an empty line through.
            string stored = SplitMarkers.Carries(text) ? SplitMarkers.Strip(text).Text : text;

            // A line with nothing to TYPE (a backing-vocal-only line, all bracketed, or one that is
            // nothing but punctuation) vanishes entirely; it must NOT linger as an empty entry, or
            // it would masquerade as a boundary/terminator marker. Genuine bare-timestamp
            // terminators had no text to begin with and pass through unchanged. Emptiness is
            // measured on the DEFAULT stream, because punctuation now survives normalization while
            // still being nothing the player types.
            if (Typeability.ToDefaultStream(stored).Length == 0)
            {
                if (Typeability.Normalize(rawText).Length > 0)
                    return;

                // Junk that normalizes away entirely (including a bare "|") stays the empty
                // boundary/terminator entry it has always been.
                text = string.Empty;
            }

            foreach (double t in times)
                entries.Add((t, text, null));
        }
    }
}
