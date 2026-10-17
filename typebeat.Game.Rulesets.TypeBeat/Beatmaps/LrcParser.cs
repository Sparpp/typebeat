// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Ported verbatim from type!beat TypeBeat.Game/Beatmaps/LrcParser.cs (regression-anchored).
// Only the namespace changed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
    ///
    /// <para>ENHANCED LRC (backlog 356): a line carrying inline <c>&lt;mm:ss.xx&gt;</c> word stamps
    /// (the A2 extension) is WORD-TIMED. Each stamped word starts at its stamp and runs to the next
    /// stamp on the line (or the line's closing stamp); the words between two stamps share that span
    /// char-weighted, exactly as <see cref="InterpolateUnits"/> shares a line. A line with no inline
    /// stamp never reaches that path, so a plain LRC parses character for character as before.</para>
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
            => Parse(lrcContent, language, out _);

        /// <summary>Parses an LRC, reporting how many inline word stamps (backlog 356) had to be clamped.</summary>
        /// <param name="lrcContent">The .lrc text.</param>
        /// <param name="language">See <see cref="Parse(string, string?)"/>.</param>
        /// <param name="clampedWordStamps">How many inline word stamps (backlog 356) were out of order
        /// or outside their line and were clamped into it. Always 0 for a plain LRC.</param>
        public static IReadOnlyList<LyricLine> Parse(string lrcContent, string? language, out int clampedWordStamps)
        {
            clampedWordStamps = 0;
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
            var entries = new List<(double Time, string Text, LyricOriginals.RomanisedLine? Romanised, StampedLine? Stamps)>();

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
                if (entries[i].Text.Length > 0 || entries[i].Romanised?.Original != null || entries[i].Stamps != null)
                    emitted.Add(i);
            }

            for (int e = 0; e < emitted.Count; e++)
            {
                int idx = emitted[e];
                double start = entries[idx].Time;
                string text = entries[idx].Text;
                var romanised = entries[idx].Romanised;
                var stamps = entries[idx].Stamps;

                // An unromanised word sings for as long as its letters take, so it counts toward the
                // line's sung length as if typed; a line without one counts exactly as before.
                int typeable = Typeability.TypeableCount(text) + (romanised == null ? 0 : unromanisedWeight(romanised)) + (stamps?.FlaggedWeight ?? 0);

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

                    // A word-stamped last line with nothing after it reaches at least as far as its
                    // own stamps say it is sung, so they are not clamped back into a guessed length.
                    if (terminator == null && stamps != null)
                        end = Math.Max(end, stamps.SungEnd());
                }

                if (end < start)
                    end = start;

                double singEnd = start + Math.Min(end - start, MAX_MS_PER_TYPEABLE_CHAR * typeable);

                if (stamps != null)
                {
                    result.Add(stampedLine(stamps, text, start, end, singEnd, ref clampedWordStamps));
                    continue;
                }

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
            OriginalSplits = original == unit.Original ? unit.OriginalSplits : Array.Empty<OriginalLyricSplit>(),
        };

        /// <summary>
        /// Parses "mm:ss.xx" and "mm:ss.xxx" (also tolerates "m:ss.x"), bare or inside either kind of
        /// LRC bracket: the square brackets of a line tag or the angle brackets of an inline word
        /// stamp (backlog 356).
        /// </summary>
        public static bool TryParseTimestamp(string token, out double milliseconds)
        {
            milliseconds = 0;
            if (string.IsNullOrWhiteSpace(token))
                return false;

            token = token.Trim();

            if (token.Length >= 2 && ((token[0] == '[' && token[^1] == ']') || (token[0] == '<' && token[^1] == '>')))
                token = token.Substring(1, token.Length - 2).Trim();

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

        private static void extractEntries(string rawLine, double offset, List<(double Time, string Text, LyricOriginals.RomanisedLine? Romanised, StampedLine? Stamps)> entries,
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

            string rawText = rawLine.Substring(idx);

            // An enhanced line written without a line tag is placed by its first word stamp.
            if (times.Count == 0 && leading_inline_stamp.Match(rawText) is { Success: true } leading
                                 && TryParseTimestamp(leading.Groups[1].Value, out double leadingMs))
            {
                times.Add(leadingMs - offset);
            }

            if (times.Count == 0)
                return; // Non-timestamped line, skipped entirely.

            // ENHANCED LRC (backlog 356). Only a line that carries an inline word stamp takes this
            // path, so every plain line below parses exactly as it always has.
            if (inline_stamp.IsMatch(rawText))
            {
                extractStampedEntries(rawText, times, offset, entries, language);
                return;
            }

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
                    entries.Add((t, romanisedText, romanised, null));

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
                entries.Add((t, text, null, null));
        }

        #region Enhanced LRC (backlog 356)

        private const string stamp_pattern = @"<([0-9]+:[0-9]{1,2}(?:\.[0-9]{1,3})?)>";

        private static readonly Regex inline_stamp = new Regex(stamp_pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex leading_inline_stamp = new Regex(@"^\s*" + stamp_pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex stamp_here = new Regex(@"\G" + stamp_pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Whether <paramref name="lrcContent"/> is an ENHANCED LRC: some timestamped line carries an
        /// inline <c>&lt;mm:ss.xx&gt;</c> word stamp (a line opening with one counts as timestamped).
        /// Such a file is word-timed by its own author, so the importer takes it as it stands rather
        /// than aligning it or collapsing it to line stamps.
        /// </summary>
        public static bool HasWordStamps(string? lrcContent)
        {
            if (string.IsNullOrEmpty(lrcContent))
                return false;

            foreach (string raw in lrcContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.TrimStart('﻿');
                int idx = 0;
                while (idx < line.Length && char.IsWhiteSpace(line[idx]))
                    idx++;

                bool tagged = false;

                while (idx < line.Length && line[idx] == '[')
                {
                    int close = line.IndexOf(']', idx);
                    if (close < 0)
                        break;

                    if (TryParseTimestamp(line.Substring(idx + 1, close - idx - 1), out _))
                        tagged = true;

                    idx = close + 1;
                }

                string rest = line.Substring(idx);

                if ((tagged && inline_stamp.IsMatch(rest)) || leading_inline_stamp.IsMatch(rest))
                    return true;
            }

            return false;
        }

        /// <summary>One word of a word-stamped line: what the map stores for it, and its own stamp if it has one.</summary>
        private readonly record struct StampedWord(string Text, bool Flagged, string Source, string? Original, double? Stamp);

        /// <summary>
        /// A word-stamped line before it is placed: its words, its closing stamp, and the shift a
        /// duplicated leading tag moves the whole line by (the stamps are written for the FIRST tag).
        /// </summary>
        private sealed record StampedLine(IReadOnlyList<StampedWord> Words, double? Closing, double Shift, string? Original)
        {
            /// <summary>The letters of the unromanised words, the weight they take in the line's timing.</summary>
            public int FlaggedWeight => Words.Where(w => w.Flagged).Sum(w => StringInfo.ParseCombiningCharacters(w.Source).Length);

            /// <summary>
            /// How far the line's own stamps say it is sung: its closing stamp, else its last word
            /// stamp plus the vocal-density allowance for the words from there on.
            /// </summary>
            public double SungEnd()
            {
                if (Closing is double closing)
                    return closing + Shift;

                for (int i = Words.Count - 1; i >= 0; i--)
                {
                    if (Words[i].Stamp is double stamp)
                        return stamp + Shift + MAX_MS_PER_TYPEABLE_CHAR * weight(Words, i, Words.Count);
                }

                return double.NegativeInfinity;
            }
        }

        private static int weight(IReadOnlyList<StampedWord> words, int from, int to)
        {
            int total = 0;

            for (int i = from; i < to; i++)
            {
                total += words[i].Flagged
                    ? StringInfo.ParseCombiningCharacters(words[i].Source).Length
                    : Typeability.TypeableCount(words[i].Text);
            }

            return total;
        }

        /// <summary>
        /// Reads a line that carries inline word stamps. The stamps are lifted out of the text, the
        /// backing vocals are stripped by the <see cref="Typeability.StripBackingVocals"/> rule (a
        /// stamp inside one times a backing vocal and is dropped), and each word takes the LAST stamp
        /// standing between the previous word and its own first character. A stamp strictly inside
        /// a word is not a word boundary and is passed over; a stamp after the last word closes the
        /// line. The words are made typeable exactly as the plain path makes a line typeable:
        /// normalized, or romanised word by word (backlog 330) when the line needs it, so each word
        /// keeps its own original.
        /// </summary>
        private static void extractStampedEntries(string rawText, List<double> times, double offset,
                                                  List<(double Time, string Text, LyricOriginals.RomanisedLine? Romanised, StampedLine? Stamps)> entries,
                                                  string? language)
        {
            var clean = new StringBuilder(rawText.Length);
            var stampsAt = new List<(int Position, double Time)>();
            int depth = 0;

            for (int i = 0; i < rawText.Length;)
            {
                char c = rawText[i];

                if (c == '<' && stamp_here.Match(rawText, i) is { Success: true } match && TryParseTimestamp(match.Groups[1].Value, out double ms))
                {
                    if (depth == 0)
                        stampsAt.Add((clean.Length, ms - offset));

                    i += match.Length;
                    continue;
                }

                if (c == '(' || c == '[')
                    depth++;
                else if (c == ')' || c == ']')
                {
                    if (depth > 0)
                        depth--;
                }
                else if (depth == 0)
                    clean.Append(c);

                i++;
            }

            string unbracketed = clean.ToString();
            bool romanise = LyricOriginals.CarriesOriginal(unbracketed) || Romaniser.NeedsRomanising(unbracketed, language);

            var words = new List<StampedWord>();
            int previousEnd = 0;
            int nextStamp = 0;
            double? carried = null;

            foreach ((int tokenStart, string token) in whitespaceTokens(unbracketed))
            {
                IReadOnlyList<string> pieces = romanise ? JapaneseReading.Segment(token, language) ?? new[] { token } : new[] { token };
                int at = tokenStart;

                foreach (string piece in pieces)
                {
                    double? stamp = null;

                    while (nextStamp < stampsAt.Count && stampsAt[nextStamp].Position <= at)
                    {
                        if (stampsAt[nextStamp].Position >= previousEnd)
                            stamp = stampsAt[nextStamp].Time;

                        nextStamp++;
                    }

                    at += piece.Length;
                    previousEnd = at;

                    // A word that types as nothing hands its stamp on to the next word that has none.
                    stamp ??= carried;
                    carried = null;

                    string text;
                    bool flagged = false;
                    string? original = null;

                    if (romanise)
                    {
                        var romanised = LyricOriginals.RomaniseWord(piece, language, keepMarkers: true);
                        text = LyricOriginals.CollapseWhitespace(romanised.Text);
                        flagged = romanised.Flagged;
                        original = romanised.Original;
                    }
                    else
                        text = LyricOriginals.CollapseWhitespace(Typeability.Normalize(piece, keepFreestyleMarkers: true, keepSplitMarkers: true));

                    if (!flagged && text.Length == 0)
                    {
                        carried = stamp;
                        continue;
                    }

                    words.Add(new StampedWord(text, flagged, piece, original, stamp));
                }
            }

            double? closing = null;

            for (; nextStamp < stampsAt.Count; nextStamp++)
            {
                if (stampsAt[nextStamp].Position >= previousEnd)
                    closing = stampsAt[nextStamp].Time;
            }

            closing ??= carried;

            string lineText = string.Join(' ', words.Where(w => !w.Flagged).Select(w => w.Text));
            bool anyFlagged = words.Any(w => w.Flagged);
            string stored = SplitMarkers.Carries(lineText) ? SplitMarkers.Strip(lineText).Text : lineText;

            if (Typeability.ToDefaultStream(stored).Length == 0 && !anyFlagged)
            {
                // Nothing to type: a line of bare stamps is the boundary marker a bare line tag is,
                // anything else (backing vocals only, punctuation) vanishes as it does on the plain path.
                if (Typeability.Normalize(inline_stamp.Replace(rawText, string.Empty)).Length > 0)
                    return;

                foreach (double t in times)
                    entries.Add((t, string.Empty, null, null));

                return;
            }

            string? lineOriginal = null;

            if (romanise)
            {
                string source = LyricOriginals.CollapseWhitespace(unbracketed);
                lineOriginal = anyFlagged ? source : LyricOriginals.OriginalFor(source, lineText);
            }

            var stampedLine = new StampedLine(words, closing, 0, lineOriginal);

            foreach (double t in times)
                entries.Add((t, lineText, null, stampedLine with { Shift = t - times[0] }));
        }

        private static IEnumerable<(int Start, string Token)> whitespaceTokens(string text)
        {
            int i = 0;

            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;

                int start = i;

                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                    i++;

                if (i > start)
                    yield return (start, text.Substring(start, i - start));
            }
        }

        /// <summary>
        /// Places a word-stamped line in [<paramref name="start"/>, <paramref name="end"/>]. Every
        /// stamp is first clamped into the line and made monotone (each one moved is counted in
        /// <paramref name="clamped"/>), then the words are timed in GROUPS: a stamped word and the
        /// unstamped words after it share [its stamp, the next stamp], char-weighted by
        /// <see cref="InterpolateWords"/>, the words before the first stamp share [the line tag, that
        /// stamp], and the last group ends at the closing stamp, else where the line's singing ends
        /// (<paramref name="plainSingEnd"/>, what the plain path would give the whole line) or after
        /// the vocal-density allowance for its own letters if that reaches further, bounded by the
        /// line's end either way. A stamped word is
        /// <see cref="TimingSource.Explicit"/>; the line sings until its last group ends.
        /// </summary>
        private static LyricLine stampedLine(StampedLine stamps, string text, double start, double end, double plainSingEnd, ref int clamped)
        {
            var words = stamps.Words;
            var at = new double?[words.Count];
            double floor = start;

            for (int i = 0; i < words.Count; i++)
            {
                if (words[i].Stamp is double stamp)
                    at[i] = clampStamp(stamp + stamps.Shift, ref floor, end, ref clamped);
            }

            double? closing = stamps.Closing is double c ? clampStamp(c + stamps.Shift, ref floor, end, ref clamped) : null;

            var units = new List<TimedUnit>();
            var pending = new List<UnromanisedWord>();
            double singEnd = start;

            for (int g = 0; g < words.Count;)
            {
                int next = g + 1;
                while (next < words.Count && at[next] == null)
                    next++;

                double from = at[g] ?? start;
                double to = next < words.Count
                    ? at[next]!.Value
                    : closing ?? Math.Min(end, Math.Max(plainSingEnd, from + MAX_MS_PER_TYPEABLE_CHAR * weight(words, g, next)));

                var group = new List<(string Text, bool Flagged, string Source, string? Original)>();
                for (int i = g; i < next; i++)
                    group.Add((words[i].Text, words[i].Flagged, words[i].Source, words[i].Original));

                var (groupUnits, groupPending) = InterpolateWords(group, from, to);

                if (at[g] != null && !words[g].Flagged && groupUnits.Count > 0)
                    groupUnits[0] = asExplicit(groupUnits[0]);

                int baseIndex = units.Count;
                units.AddRange(groupUnits);
                pending.AddRange(groupPending.Select(p => p with { Position = p.Position + baseIndex }));

                singEnd = to;
                g = next;
            }

            return new LyricLine
            {
                RawText = SplitMarkers.Carries(text) ? SplitMarkers.Strip(text).Text : text,
                StartTime = start,
                EndTime = end,
                SingEndTime = singEnd,
                Units = units,
                Original = stamps.Original,
                UnromanisedWords = pending,
            };
        }

        private static double clampStamp(double time, ref double floor, double ceiling, ref int clamped)
        {
            if (time < floor)
            {
                time = floor;
                clamped++;
            }
            else if (time > ceiling)
            {
                time = ceiling;
                clamped++;
            }

            floor = time;
            return time;
        }

        private static TimedUnit asExplicit(TimedUnit unit) => new TimedUnit
        {
            Text = unit.Text,
            StartTime = unit.StartTime,
            EndTime = unit.EndTime,
            Source = TimingSource.Explicit,
            Confidence = 1,
            SyllableBoundaries = unit.SyllableBoundaries,
            SyllableSplits = unit.SyllableSplits,
            Pauses = unit.Pauses,
            Original = unit.Original, OriginalSplits = unit.OriginalSplits,
        };

        #endregion
    }
}
