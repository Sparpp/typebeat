// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using osu.Framework.Logging;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// The passes an imported timing.json goes through after the aligner (local or server) wrote it
    /// and before it is stored: freestyle flags, romanisation with the original text, and the
    /// import-side backing-vocal strip. Each returns its input verbatim when it has nothing to do.
    /// </summary>
    public static class TimingJsonPostProcess
    {
        /// <summary>
        /// Marks every line of an ALIGNER-produced timing.json whose text carries a '&amp;' with
        /// <c>"freestyle": true</c> (backlog 202). The aligner passes the mapper's markers through
        /// in the line text but knows nothing about the flag, and without it the decoder reads the
        /// ampersand as ordinary lyric punctuation and strips it. A document with no ampersand
        /// anywhere is returned VERBATIM, so nothing an aligner has ever produced before moves.
        /// Any parse problem also returns the input unchanged: this is a polish pass, not a gate.
        /// Any other failure does the same but is logged as an error.
        /// </summary>
        public static string FlagFreestyleLines(string timingJson)
        {
            if (string.IsNullOrEmpty(timingJson) || timingJson.IndexOf(Typeability.FREESTYLE_MARKER) < 0)
                return timingJson;

            try
            {
                if (JsonNode.Parse(timingJson) is not JsonObject root
                    || root["lines"] is not JsonArray lines)
                {
                    return timingJson;
                }

                bool changed = false;

                foreach (JsonNode? node in lines)
                {
                    if (node is not JsonObject line || line["freestyle"] != null)
                        continue;

                    if (line["text"]?.GetValue<string>() is not string text || text.IndexOf(Typeability.FREESTYLE_MARKER) < 0)
                        continue;

                    line["freestyle"] = true;
                    changed = true;
                }

                return changed ? root.ToJsonString() : timingJson;
            }
            catch (JsonException)
            {
                return timingJson;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Flagging freestyle lines in the aligner's timing failed; the lines keep their raw text");
                return timingJson;
            }
        }

        /// <summary>
        /// THE ORIGINAL TEXT for an ALIGNER-produced timing.json (backlog 330): every line (and word)
        /// whose text needs romanising gets the romanisation as its <c>text</c> and the source as its
        /// <c>original</c>, instead of reaching the decode raw and being deleted there. The LRC and
        /// TTML paths romanise as they parse (<see cref="LrcParser.Parse(string, string?)"/>,
        /// <see cref="TtmlParser.TryParseRaw"/>); this is the same rule for the one document that
        /// arrives already written.
        ///
        /// <para>With words[], a line whose source tokens match its words one for one pairs them
        /// (the line text is authoritative for spelling, as the decode's pairing is), and otherwise
        /// each word is romanised from its own text; the line's text is then the romanised words
        /// joined, so the pairing still holds. A word the romaniser cannot spell is written with an
        /// empty text beside its original and its syllables dropped. A word whose syllables spell
        /// its source exactly carries its cuts into the romanisation through the romaniser's units
        /// (see <see cref="LyricOriginals.RomanisedWord.MapSplits"/>), merging the syllables whose
        /// cut does not carry. Without words[] the line is romanised as a whole and an unspellable
        /// word survives only in the line's original. A document with nothing to romanise is
        /// returned VERBATIM, and any parse problem returns the input unchanged (any other failure
        /// too, logged as an error).</para>
        /// </summary>
        public static string RomaniseLines(string timingJson, string? language)
        {
            if (string.IsNullOrEmpty(timingJson))
                return timingJson;

            try
            {
                if (JsonNode.Parse(timingJson) is not JsonObject root || root["lines"] is not JsonArray lines)
                    return timingJson;

                bool changed = false;

                foreach (JsonNode? node in lines)
                {
                    if (node is not JsonObject line || line["text"] is not JsonValue textValue || !textValue.TryGetValue(out string? text))
                        continue;

                    var words = line["words"] as JsonArray;

                    bool needs(string? s) => LyricOriginals.CarriesOriginal(s) || Romaniser.NeedsRomanising(s, language);

                    if (!needs(text) && (words == null || !words.OfType<JsonObject>().Any(w => w["text"] is JsonValue v && v.TryGetValue(out string? t) && needs(t))))
                        continue;

                    changed = true;
                    string[] sourceTokens = LyricOriginals.CollapseWhitespace(text).Split(' ');

                    if (words == null || words.Count == 0)
                    {
                        var romanisedLine = LyricOriginals.RomaniseLine(text, language);
                        line["text"] = romanisedLine.Text;

                        if (romanisedLine.Original != null)
                            setAfterText(line, romanisedLine.Original);

                        continue;
                    }

                    bool byLineTokens = words.Count == sourceTokens.Length;
                    var expanded = new JsonArray();

                    for (int i = 0; i < words.Count; i++)
                    {
                        if (words[i] is not JsonObject sourceWord)
                            continue;

                        string source = byLineTokens
                            ? sourceTokens[i]
                            : sourceWord["text"] is JsonValue value && value.TryGetValue(out string? raw) ? raw ?? string.Empty : string.Empty;

                        foreach (JsonObject part in splitJapaneseTimedWord(sourceWord, source, language))
                            expanded.Add(part);
                    }

                    line["words"] = expanded;
                    var typed = new List<string>();
                    bool anyFlagged = false;

                    foreach (JsonNode? entry in expanded)
                    {
                        if (entry is not JsonObject word)
                            continue;

                        string source = word["text"] is JsonValue wv && wv.TryGetValue(out string? wt) ? wt ?? string.Empty : string.Empty;

                        var romanised = LyricOriginals.RomaniseWord(source, language);

                        if (romanised.Flagged)
                        {
                            anyFlagged = true;
                            word["text"] = string.Empty;
                            setAfterText(word, romanised.Original);
                            word.Remove("syllables");
                            word.Remove("split_chars");
                            continue;
                        }

                        word["text"] = romanised.Text;

                        if (romanised.Original != null)
                            setAfterText(word, romanised.Original);

                        carrySyllables(word, romanised);

                        if (romanised.Text.Length > 0)
                            typed.Add(romanised.Text);
                    }

                    line["text"] = string.Join(' ', typed);

                    string collapsed = LyricOriginals.CollapseWhitespace(text);

                    if ((anyFlagged || LyricOriginals.CarriesOriginal(collapsed)) && collapsed != string.Join(' ', typed))
                        setAfterText(line, collapsed);
                }

                return changed ? root.ToJsonString() : timingJson;
            }
            catch (JsonException)
            {
                return timingJson;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Romanising the aligner's timing failed; the lines keep their raw text");
                return timingJson;
            }
        }

        /// <summary>
        /// A timed Japanese phrase may arrive as one aligner word with its pieces listed as
        /// syllables. Move dictionary word boundaries into words[] before romanisation. Existing
        /// syllable times are retained inside each new word; a boundary inside a timed piece is
        /// interpolated across that piece.
        /// </summary>
        private static IReadOnlyList<JsonObject> splitJapaneseTimedWord(JsonObject word, string source, string? language)
        {
            IReadOnlyList<string>? parts = JapaneseReading.Segment(source, language);

            if (parts == null)
            {
                var unchanged = (JsonObject)word.DeepClone();
                unchanged["text"] = source;
                return new[] { unchanged };
            }

            if (!readMs(word["start_ms"], out double wordStart) || !readMs(word["end_ms"], out double wordEnd) || wordEnd <= wordStart)
            {
                var unchanged = (JsonObject)word.DeepClone();
                unchanged["text"] = source;
                return new[] { unchanged };
            }

            var syllables = new List<(string Text, double Start, double End)>();

            if (word["syllables"] is JsonArray authored)
            {
                foreach (JsonNode? node in authored)
                {
                    if (node is not JsonObject syllable || syllable["text"] is not JsonValue value
                        || !value.TryGetValue(out string? text) || text == null
                        || !readMs(syllable["start_ms"], out double start)
                        || !readMs(syllable["end_ms"], out double end))
                    {
                        syllables.Clear();
                        break;
                    }

                    syllables.Add((text, start, end));
                }
            }

            if (string.Concat(syllables.Select(s => s.Text)) != source)
                syllables.Clear();

            var result = new List<JsonObject>(parts.Count);
            int partStart = 0;

            foreach (string part in parts)
            {
                int partEnd = partStart + part.Length;
                var split = (JsonObject)word.DeepClone();
                split["text"] = part;
                split.Remove("original");
                split.Remove("split_chars");
                split.Remove("syllables");

                if (syllables.Count == 0)
                {
                    split["start_ms"] = wordStart + (wordEnd - wordStart) * partStart / source.Length;
                    split["end_ms"] = wordStart + (wordEnd - wordStart) * partEnd / source.Length;
                }
                else
                {
                    var pieces = new JsonArray();
                    int syllableStart = 0;

                    foreach (var syllable in syllables)
                    {
                        int syllableEnd = syllableStart + syllable.Text.Length;
                        int from = Math.Max(partStart, syllableStart);
                        int to = Math.Min(partEnd, syllableEnd);

                        if (from < to)
                        {
                            double duration = syllable.End - syllable.Start;
                            double start = syllable.Start + duration * (from - syllableStart) / syllable.Text.Length;
                            double end = syllable.Start + duration * (to - syllableStart) / syllable.Text.Length;
                            pieces.Add(new JsonObject
                            {
                                ["text"] = source.Substring(from, to - from),
                                ["start_ms"] = start,
                                ["end_ms"] = end,
                            });
                        }

                        syllableStart = syllableEnd;
                    }

                    split["start_ms"] = ((JsonObject)pieces[0]!)["start_ms"]!.DeepClone();
                    split["end_ms"] = ((JsonObject)pieces[^1]!)["end_ms"]!.DeepClone();
                    split["syllables"] = pieces;
                }

                result.Add(split);
                partStart = partEnd;
            }

            return result;
        }

        private static bool readMs(JsonNode? value, out double milliseconds)
            => double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds);

        /// <summary>
        /// Sets <c>"original"</c> on <paramref name="node"/> straight after its <c>"text"</c>, the
        /// position every writer of the format puts it in (see <see cref="LyricOriginals"/>).
        /// </summary>
        private static void setAfterText(JsonObject node, string? original)
        {
            var entries = node.Where(kv => kv.Key != "original").ToList();
            node.Clear();

            foreach (var (key, value) in entries)
            {
                node[key] = value;

                if (key == "text")
                    node["original"] = original;
            }
        }

        /// <summary>
        /// A romanised aligner word's syllables (see <see cref="RomaniseLines"/>): when the syllable
        /// texts spell the word's source exactly, the source cuts between them are carried into the
        /// romanisation as <c>split_chars</c>, a syllable whose cut does not carry merging into the
        /// one before it (its end becomes that syllable's end). Otherwise the syllables' times are
        /// left alone and the split is derived at decode, as for any word without one.
        /// </summary>
        private static void carrySyllables(JsonObject word, LyricOriginals.RomanisedWord romanised)
        {
            if (word["syllables"] is not JsonArray syllables || syllables.Count < 2)
                return;

            var texts = new List<string>();

            foreach (JsonNode? syllable in syllables)
            {
                if (syllable is not JsonObject s || s["text"] is not JsonValue v || !v.TryGetValue(out string? t) || t == null)
                    return;

                texts.Add(t);
            }

            if (string.Concat(texts) != romanised.Source)
                return;

            var cuts = new List<int>();
            int at = 0;

            for (int i = 0; i < texts.Count - 1; i++)
            {
                at += texts[i].Length;
                cuts.Add(at);
            }

            var kept = romanised.MapSplits(cuts);
            var keptCuts = new HashSet<int>(kept.Select(k => k.SourceCut));
            var merged = new JsonArray();
            JsonObject? current = null;

            for (int i = 0; i < syllables.Count; i++)
            {
                var syllable = (JsonObject)syllables[i]!.DeepClone();

                // Syllable i starts at cut i - 1; a cut that did not carry folds it into the one before.
                if (current != null && !keptCuts.Contains(i - 1))
                {
                    current["end_ms"] = syllable["end_ms"]?.DeepClone();
                    continue;
                }

                merged.Add(syllable);
                current = syllable;
            }

            // The syllable texts now spell the romanisation, cut where the kept splits say.
            var splits = kept.Select(k => k.TextSplit).ToList();
            var segmentStarts = new List<int> { 0 };
            segmentStarts.AddRange(splits);
            segmentStarts.Add(romanised.Text.Length);

            for (int i = 0; i < merged.Count && i + 1 < segmentStarts.Count; i++)
                ((JsonObject)merged[i]!)["text"] = romanised.Text.Substring(segmentStarts[i], segmentStarts[i + 1] - segmentStarts[i]);

            word["syllables"] = merged;

            if (splits.Count > 0)
            {
                var array = new JsonArray();

                foreach (int split in splits)
                    array.Add(split);

                word["split_chars"] = array;
            }
            else
            {
                word.Remove("split_chars");
            }
        }

        /// <summary>
        /// THE IMPORT-SIDE BACKING-VOCAL BOUNDARY (backlog 255, backlog 445). The bracketed spans of an
        /// ALIGNER-produced timing.json leave the line texts exactly as backlog 255 stripped them, so the
        /// .osu an import writes never stores a bracket in its typed lyric, but what was in them is kept as
        /// each line's backing vocals (a <c>"backing"</c> array) instead of being thrown away. See
        /// <see cref="BackingVocalImport.FromTimingJson"/>, the one implementation, which the in-editor
        /// re-align reads through as well (<see cref="TimingJsonLoader.TryParse"/>).
        /// </summary>
        public static string ExtractBackingVocalLines(string timingJson) => BackingVocalImport.FromTimingJson(timingJson);
    }
}
