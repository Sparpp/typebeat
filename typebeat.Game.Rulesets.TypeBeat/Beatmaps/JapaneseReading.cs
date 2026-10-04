// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kawazu;
using osu.Framework.Logging;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Reads Japanese kanji through Kawazu's IPAdic dictionary, then lets the existing romaniser
    /// spell the resulting kana. The returned units map those Latin runs back to the authored
    /// characters, so imported syllable cuts and Polyglot can still use the original text.
    /// Kana-only words continue through <see cref="Romaniser"/> without loading the dictionary.
    /// </summary>
    internal static class JapaneseReading
    {
        private static readonly object gate = new object();
        private static readonly Lazy<KawazuConverter> converter = new Lazy<KawazuConverter>(() => new KawazuConverter());
        private static bool loggedFailure;

        /// <summary>
        /// Divides one authored run at Kawazu's dictionary word boundaries. Explicit spaces are
        /// handled by the caller; closing punctuation stays on the preceding word and opening
        /// punctuation moves with the following word. A failed lookup leaves
        /// the run intact so the ordinary romaniser can flag an unknown reading for the mapper.
        /// </summary>
        public static IReadOnlyList<string>? Segment(string source, string? language)
        {
            // Kana-only runs already have a direct reading. Keep their authored word and
            // syllable grouping rather than splitting them at dictionary boundaries.
            if (!isJapanese(language) || !source.Any(Utilities.IsKanji))
                return null;

            try
            {
                List<Division> divisions = getDivisions(source);

                if (!string.Equals(string.Concat(divisions.Select(d => d.Surface)), source, StringComparison.Ordinal))
                    return null;

                var words = new List<string>();
                string opening = string.Empty;

                foreach (Division division in divisions)
                {
                    string surface = division.Surface;

                    if (surface.Length == 0)
                        continue;

                    if (surface.All(char.IsPunctuation))
                    {
                        if (Romaniser.MARKS_OPENING.IndexOf(surface[0]) >= 0 || "([{“".Contains(surface[0]))
                            opening += surface;
                        else if (words.Count > 0)
                            words[^1] += surface;
                        else
                            opening += surface;

                        continue;
                    }

                    words.Add(opening + surface);
                    opening = string.Empty;
                }

                if (opening.Length > 0 && words.Count > 0)
                    words[^1] += opening;

                return words.Count > 1 ? words : null;
            }
            catch (Exception e)
            {
                logFailure(e);
                return null;
            }
        }

        public static Romaniser.Result? Romanise(string source, string? language)
        {
            if (!isJapanese(language) || !source.Any(Utilities.IsKanji))
                return null;

            try
            {
                // Kawazu's tagger is shared rather than reopening the dictionary for every word.
                // Serialize access because the underlying MeCab tagger is mutable.
                List<Division> divisions = getDivisions(source);

                var reading = new StringBuilder(source.Length * 2);
                var sourceStarts = new List<int>();
                var sourceEnds = new List<int>();
                int at = 0;

                foreach (JapaneseElement element in divisions.SelectMany(d => d))
                {
                    string surface = element.Element;
                    string kana = element.HiraNotation;

                    if (string.IsNullOrEmpty(surface) || string.IsNullOrEmpty(kana)
                        || at + surface.Length > source.Length
                        || !string.Equals(source.Substring(at, surface.Length), surface, StringComparison.Ordinal))
                        return null;

                    // An unknown kanji has no reliable reading. Leave that word flagged for the
                    // mapper rather than silently dropping it or inventing a pronunciation.
                    if (surface.Any(Utilities.IsKanji) && !kana.All(Utilities.IsKana))
                        return null;

                    reading.Append(kana);
                    bool direct = kana.Length == surface.Length && !surface.Any(Utilities.IsKanji);

                    for (int i = 0; i < kana.Length; i++)
                    {
                        int sourceChar = direct ? i : 0;
                        sourceStarts.Add(at + sourceChar);
                        sourceEnds.Add(direct ? at + i + 1 : at + surface.Length);
                    }

                    at += surface.Length;
                }

                if (at != source.Length || reading.Length == 0)
                    return null;

                Romaniser.Result romanised = Romaniser.Romanise(reading.ToString(), "japanese");

                if (!romanised.IsComplete)
                    return null;

                var units = new List<Romaniser.Unit>(romanised.Units.Count);

                foreach (Romaniser.Unit unit in romanised.Units)
                {
                    if (unit.SourceLength <= 0 || unit.SourceStart < 0 || unit.SourceStart + unit.SourceLength > sourceStarts.Count)
                        return null;

                    int start = sourceStarts[unit.SourceStart];
                    int end = sourceEnds[unit.SourceStart + unit.SourceLength - 1];
                    units.Add(new Romaniser.Unit(start, Math.Max(1, end - start), unit.TextStart, unit.TextLength, unit.Flagged));
                }

                return new Romaniser.Result(romanised.Text, romanised.Unromanised, units);
            }
            catch (Exception e)
            {
                logFailure(e);
                return null;
            }
        }

        private static List<Division> getDivisions(string source)
        {
            // Kawazu awaits its parse task with the caller's synchronization context. Starting
            // that async call on the pool prevents its continuation from needing the editor
            // thread while the synchronous authoring/Polyglot API waits for the result.
            lock (gate)
                return Task.Run(() => converter.Value.GetDivisions(source)).GetAwaiter().GetResult();
        }

        /// <summary>A dictionary reading, aligned with the original UTF-16 character range.</summary>
        internal readonly record struct Reading(int Start, int Length, string Kana);

        /// <summary>
        /// Read the complete run for context, retaining kanji word boundaries for input and ruby text.
        /// Kana and Latin text need no dictionary lookup. Unknown words remain direct-input cells.
        /// </summary>
        internal static IReadOnlyList<Reading> Read(string source)
        {
            if (!source.Any(Utilities.IsKanji))
                return new[] { new Reading(0, source.Length, source) };

            try
            {
                var result = new List<Reading>();
                int at = 0;

                foreach (JapaneseElement element in getDivisions(source).SelectMany(d => d))
                {
                    string surface = element.Element;
                    // MeCab omits ASCII whitespace from its elements. Keep the original offsets
                    // rather than rejecting every reading in an otherwise valid spaced line.
                    while (at < source.Length && char.IsWhiteSpace(source[at])
                           && !source.AsSpan(at).StartsWith(surface.AsSpan(), StringComparison.Ordinal))
                        at++;
                    if (surface.Length == 0 || at + surface.Length > source.Length
                        || !source.AsSpan(at, surface.Length).SequenceEqual(surface.AsSpan()))
                        return Array.Empty<Reading>();

                    string kana = element.HiraNotation;
                    if (!surface.Any(Utilities.IsKanji))
                        kana = surface;
                    if (!string.IsNullOrEmpty(kana) && (!surface.Any(Utilities.IsKanji) || kana.All(Utilities.IsKana)))
                        result.Add(new Reading(at, surface.Length, kana));
                    at += surface.Length;
                }

                while (at < source.Length && char.IsWhiteSpace(source[at]))
                    at++;
                return at == source.Length ? result : Array.Empty<Reading>();
            }
            catch (Exception e)
            {
                logFailure(e);
                return Array.Empty<Reading>();
            }
        }

        internal static bool IsJapanese(string? language) => isJapanese(language);

        private static bool isJapanese(string? language)
            => string.Equals(language?.Trim(), "japanese", StringComparison.OrdinalIgnoreCase)
               || string.Equals(language?.Trim(), "ja", StringComparison.OrdinalIgnoreCase);

        private static void logFailure(Exception e)
        {
            lock (gate)
            {
                if (loggedFailure)
                    return;

                loggedFailure = true;
                Logger.Error(e, "Japanese reading dictionary could not be loaded; kanji will need manual readings");
            }
        }
    }
}
