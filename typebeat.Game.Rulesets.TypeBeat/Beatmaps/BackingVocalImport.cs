// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using osu.Framework.Logging;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// THE IMPORT BOUNDARY for backing vocals (backlog 445, replacing backlog 255's strip): the brackets
    /// of an ingested lyrics document leave the TYPED lyric exactly as they always have, but what was in
    /// them is kept as the line's backing vocals instead of being thrown away.
    ///
    /// <para>This pass works on an aligner-shaped timing.json, the one document every import path ends
    /// in before the .osu is written, and on the in-editor re-align, which reads the same document:</para>
    /// <list type="bullet">
    /// <item>A line whose text carries bracketed spans keeps the unbracketed text (the same text
    /// <see cref="Typeability.StripBackingVocals"/> gives, so the typed lyric is unchanged) and gains a
    /// <c>"backing"</c> array of its ROUND bracketed words (a square bracket is an annotation, dropped
    /// as before: see <see cref="BackingVocals.Extract"/>).</item>
    /// <item>When the line's <c>words[]</c> pairs one to one with its whitespace tokens and no token mixes
    /// typed and bracketed text, each bracketed token becomes a backing word at ITS OWN aligner times,
    /// and is taken out of <c>words[]</c>, so the typed words keep their aligner timing (before this,
    /// any bracket sent the whole line to the decoder's interpolation fallback).</item>
    /// <item>Otherwise <c>words[]</c> is left alone (the line falls back to interpolation exactly as it
    /// did before) and each span is timed by where it sat in the line
    /// (<see cref="BackingVocals.Place"/>).</item>
    /// <item>A line with NOTHING left to type (a whole-line backing vocal, "(oh oh)") is removed as
    /// before, and its words attach to the line being sung when it starts (the last surviving line that
    /// starts no later; the first line when it comes before every one).</item>
    /// </list>
    /// <para>A document whose line texts carry no bracket at all is returned VERBATIM, so nothing that
    /// could not have been affected has a byte moved, and any parse problem also returns the input
    /// unchanged: this is a polish pass, not a gate.</para>
    /// </summary>
    public static class BackingVocalImport
    {
        public static string FromTimingJson(string timingJson)
        {
            if (string.IsNullOrEmpty(timingJson))
                return timingJson;

            try
            {
                if (JsonNode.Parse(timingJson) is not JsonObject root
                    || root["lines"] is not JsonArray lines)
                {
                    return timingJson;
                }

                var kept = new List<JsonNode?>();
                var orphans = new List<(double Start, List<BackingVocal> Words)>();
                bool changed = false;

                foreach (JsonNode? node in lines)
                {
                    // A non-object element, or one whose "text" is missing or not a string, is carried
                    // through untouched: the decode rejects it on its own terms.
                    if (node is not JsonObject line
                        || line["text"] is not JsonValue textValue
                        || !textValue.TryGetValue(out string? text)
                        || text == null)
                    {
                        kept.Add(node?.DeepClone());
                        continue;
                    }

                    var extraction = BackingVocals.Extract(text);

                    if (extraction.Main == text)
                    {
                        kept.Add(line.DeepClone());
                        continue;
                    }

                    changed = true;

                    double start = readDouble(line, "start_ms") ?? 0;
                    double end = Math.Max(start, readDouble(line, "end_ms") ?? start);

                    // The same emptiness rule the decode applies, so an import cannot store a line the
                    // decode would then drop (or drop one it would have kept).
                    bool freestyle = line["freestyle"] is JsonValue flag && flag.TryGetValue(out bool on) && on;
                    string normalized = Typeability.Normalize(extraction.Main, keepFreestyleMarkers: freestyle, keepSplitMarkers: true);
                    bool wholeLine = TimingJsonLoader.YieldsNoCells(normalized);

                    var (words, typedWords) = timeLine(line, text, extraction, start, end, wholeLine);

                    if (wholeLine)
                    {
                        if (words.Count > 0)
                            orphans.Add((start, words));

                        continue;
                    }

                    // The UNBRACKETED RAW text, not the normalized form: the rest of the normalization
                    // still happens on decode, exactly where it always did.
                    var copy = (JsonObject)line.DeepClone();
                    copy["text"] = extraction.Main;

                    if (typedWords != null)
                        copy["words"] = typedWords;

                    setBacking(copy, readBacking(copy).Concat(words));
                    kept.Add(copy);
                }

                if (!changed)
                    return timingJson;

                attach(kept, orphans);

                var array = new JsonArray();

                foreach (var node in kept)
                    array.Add(node);

                root["lines"] = array;
                return root.ToJsonString();
            }
            catch (JsonException)
            {
                return timingJson;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Reading backing vocals out of the timing failed; the lines keep their raw text");
                return timingJson;
            }
        }

        /// <summary>
        /// The backing words of one line, and the line's <c>words[]</c> with the bracketed tokens taken
        /// out when it pairs with the tokens cleanly (null when it has to be left as it is).
        /// </summary>
        private static (List<BackingVocal> Words, JsonArray? TypedWords) timeLine(JsonObject line, string text, BackingVocals.Extraction extraction,
                                                                                 double start, double end, bool wholeLine)
        {
            var tokens = classifyTokens(text);

            if (line["words"] is JsonArray wordsArray && wordsArray.Count == tokens.Count && tokens.Count > 0
                && tokens.All(t => t.Kind != TokenKind.Mixed)
                && wordsArray.All(w => w is JsonObject o && o["text"] is JsonValue v && v.TryGetValue(out string? wt) && !string.IsNullOrEmpty(wt)))
            {
                var backing = new List<BackingVocal>();
                var typed = new JsonArray();

                for (int i = 0; i < tokens.Count; i++)
                {
                    var word = (JsonObject)wordsArray[i]!;

                    if (tokens[i].Kind == TokenKind.Typed)
                    {
                        typed.Add(word.DeepClone());
                        continue;
                    }

                    string bvText = BackingVocals.CleanText(tokens[i].Unbracketed);

                    if (bvText.Length == 0)
                        continue;

                    double ws = readDouble(word, "start_ms") ?? start;
                    double we = Math.Max(ws, readDouble(word, "end_ms") ?? ws);
                    backing.Add(new BackingVocal(bvText, ws, we));
                }

                // A whole-line backing vocal keeps nothing typed, so it has no words[] to hand back.
                return (backing, wholeLine ? null : typed);
            }

            if (wholeLine)
            {
                // Untimed words across the line's own span, at least the minimum per word.
                var all = extraction.Spans.SelectMany(s => BackingVocals.Words(s.Text)).ToArray();
                return (BackingVocals.Spread(all, start, Math.Max(end, start + BackingVocals.MIN_SPAN_MS * all.Length)), null);
            }

            return (BackingVocals.Place(extraction.Spans, extraction.Main.Length, start, end, double.PositiveInfinity), null);
        }

        private enum TokenKind { Typed, Backing, Mixed }

        private readonly record struct Token(TokenKind Kind, string Unbracketed);

        /// <summary>
        /// The line's whitespace tokens, each classified by the bracket rule
        /// (<see cref="BackingVocals.Extract"/>): wholly outside brackets, wholly inside them (bracket
        /// characters count as inside), or a mix of the two. A token inside a SQUARE bracket is out of
        /// the typed line all the same, but carries no backing text: it is an annotation.
        /// </summary>
        private static List<Token> classifyTokens(string text)
        {
            var tokens = new List<Token>();
            int depth = 0;
            bool sung = false;
            int i = 0;

            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    i++;
                    continue;
                }

                bool anyTyped = false, anyBacking = false;
                var inner = new System.Text.StringBuilder();

                while (i < text.Length && !char.IsWhiteSpace(text[i]))
                {
                    char c = text[i];

                    if (c == '(' || c == '[')
                    {
                        if (depth == 0)
                            sung = BackingVocals.IsSungBracket(c);

                        depth++;
                        anyBacking = true;
                    }
                    else if (c == ')' || c == ']')
                    {
                        if (depth > 0)
                            depth--;

                        anyBacking = true;
                    }
                    else if (depth > 0)
                    {
                        anyBacking = true;

                        if (sung)
                            inner.Append(c);
                    }
                    else
                        anyTyped = true;

                    i++;
                }

                tokens.Add(new Token(anyTyped && anyBacking ? TokenKind.Mixed : anyBacking ? TokenKind.Backing : TokenKind.Typed, inner.ToString()));
            }

            return tokens;
        }

        /// <summary>
        /// Hands every whole-line backing vocal to the surviving line being sung when it starts: the last
        /// line starting no later than it, else the first line.
        /// </summary>
        private static void attach(List<JsonNode?> kept, List<(double Start, List<BackingVocal> Words)> orphans)
        {
            if (orphans.Count == 0)
                return;

            var lines = kept.OfType<JsonObject>().Where(l => readDouble(l, "start_ms") != null).ToList();

            if (lines.Count == 0)
                return;

            foreach (var (start, words) in orphans)
            {
                JsonObject target = lines[0];

                foreach (var line in lines)
                {
                    if (readDouble(line, "start_ms") <= start)
                        target = line;
                }

                setBacking(target, readBacking(target).Concat(words));
            }
        }

        /// <summary>The line's existing <c>"backing"</c> words (a document can arrive carrying some).</summary>
        private static IEnumerable<BackingVocal> readBacking(JsonObject line)
        {
            if (line[BackingVocals.WRITE_KEY] is not JsonArray)
                return Enumerable.Empty<BackingVocal>();

            using var doc = JsonDocument.Parse(line.ToJsonString());
            return BackingVocals.Read(doc.RootElement) ?? new List<BackingVocal>();
        }

        /// <summary>
        /// Puts <paramref name="words"/> on the line as its <c>"backing"</c> array, LAST in the object
        /// and in time order; removes the key for none.
        /// </summary>
        private static void setBacking(JsonObject line, IEnumerable<BackingVocal> words)
        {
            var all = words.OrderBy(w => w.StartTime).ToList();
            line.Remove(BackingVocals.WRITE_KEY);

            if (BackingVocals.ToJson(all) is JsonArray array)
                line[BackingVocals.WRITE_KEY] = array;
        }

        private static double? readDouble(JsonObject obj, string key)
            => obj[key] is JsonValue value && value.TryGetValue(out double d) && double.IsFinite(d) ? d : null;
    }
}
