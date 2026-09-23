// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// Writes the version-2 timing.json that <see cref="TimingJsonLoader"/> reads, for lyric lines
    /// that were SYNTHESIZED from a lyrics file rather than produced by the aligner: the LRC path
    /// (line stamps) and the TTML path (the song's own word timing).
    ///
    /// <para>It lives beside the loader rather than in the importer because the two are a pair and
    /// the round trip has to be exact: what this writes is what the loader rebuilds, including the
    /// line windows. The window rule itself stays in ONE place - the loader decides a non-last
    /// line's hard seal and clamps its words into it - so nothing here re-derives it, and a caller
    /// that wants final lines goes through <see cref="TimingJsonLoader.TryParse"/> rather than
    /// getting a second opinion.</para>
    /// </summary>
    public static class SynthesizedTimingJson
    {
        /// <summary>
        /// Serializes <paramref name="lines"/> (with words[] / syllables[] where they carry them).
        /// </summary>
        /// <param name="lines">The lines to write, in song order.</param>
        /// <param name="wordTiming">
        /// Whether a line with EXPLICIT word timing still writes its <c>words[]</c> when it carries
        /// no syllable subdivision. A TTML does (dropping the stamps would throw away the
        /// granularity the file was aligned at), while the LRC path does not, which is what keeps
        /// every pre-TTML import byte-identical. A line that has no explicit word timing - a
        /// line-timed TTML, whose words are interpolated - writes none from either path, so it
        /// decodes to the loader's own line-granularity shape.
        /// </param>
        /// <param name="songEndMs">The <c>song_end_ms</c> header value.</param>
        public static string Write(IReadOnlyList<LyricLine> lines, bool wordTiming, double? songEndMs)
        {
            var payload = new
            {
                version = TimingJsonLoader.SUPPORTED_VERSION,
                song_end_ms = songEndMs,
                lines = lines.Select(l => lineJson(l, wordTiming)).ToArray()
            };

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// One line object. Text is emitted through a real JSON writer so punctuation, quotes and
        /// unicode escape correctly.
        /// </summary>
        private static object lineJson(LyricLine line, bool wordTiming)
        {
            bool freestyle = line.RawText.IndexOf(Typeability.FREESTYLE_MARKER) >= 0;
            bool subdivided = line.Units.Any(u => u.SyllableBoundaries.Count > 0);
            bool explicitWords = line.Units.Any(u => u.Source == TimingSource.Explicit);

            if (!freestyle && !subdivided && !(wordTiming && explicitWords))
            {
                // The shape every pipe-free, ampersand-free LRC import has always had.
                return new
                {
                    text = line.RawText,
                    start_ms = line.StartTime,
                    end_ms = line.SingEndTime,
                };
            }

            var json = new JsonObject
            {
                ["text"] = line.RawText,
                ["start_ms"] = line.StartTime,
                ["end_ms"] = line.SingEndTime,
            };

            // '&' is an opt-in the decoder needs before it will read an ampersand as a freestyle
            // cell rather than as lyric punctuation.
            if (freestyle)
                json["freestyle"] = true;

            if (subdivided || (wordTiming && explicitWords))
            {
                var words = new JsonArray();

                foreach (var unit in line.Units)
                {
                    var word = new JsonObject
                    {
                        ["text"] = unit.Text,
                        ["start_ms"] = unit.StartTime,
                        ["end_ms"] = unit.EndTime,
                    };

                    if (unit.SyllableBoundaries.Count > 0)
                    {
                        var edges = new List<double> { unit.StartTime };
                        edges.AddRange(unit.SyllableBoundaries);
                        edges.Add(unit.EndTime);

                        var segmentTexts = Gameplay.SyllableSegments.SegmentTexts(unit.Text, Gameplay.SyllableSegments.SplitsFor(unit));
                        var syllables = new JsonArray();

                        for (int i = 0; i < edges.Count - 1; i++)
                        {
                            syllables.Add(new JsonObject
                            {
                                ["text"] = i < segmentTexts.Count ? segmentTexts[i] : string.Empty,
                                ["start_ms"] = edges[i],
                                ["end_ms"] = edges[i + 1],
                            });
                        }

                        word["syllables"] = syllables;

                        var splitChars = new JsonArray();

                        foreach (int split in unit.SyllableSplits)
                            splitChars.Add(split);

                        if (splitChars.Count > 0)
                            word["split_chars"] = splitChars;
                    }

                    // THE AUTHORED PAUSES (the editor's Insert Pause), written BESIDE the syllable data
                    // rather than inside it, and only when the word actually carries any: a word without a
                    // rest persists exactly as it always did, so no existing map's bytes move. They are
                    // deliberately not nested under syllables[] - the common case is a word the mapper
                    // never subdivided, and nesting them there would silently drop the rests of every such
                    // word.
                    if (unit.Pauses.Count > 0)
                    {
                        var pauses = new JsonArray();

                        foreach (var pause in unit.Pauses)
                        {
                            pauses.Add(new JsonObject
                            {
                                ["start_ms"] = pause.StartTime,
                                ["end_ms"] = pause.EndTime,
                                ["split"] = pause.SplitChar,
                            });
                        }

                        word["pauses"] = pauses;
                    }

                    words.Add(word);
                }

                json["words"] = words;
            }

            return json;
        }
    }
}
