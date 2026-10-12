// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Kawazu;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// Generates Japanese spellings from the bundled dictionary and recovers other languages
    /// through LRCLIB. Only original metadata is returned; the map's romanized text and authored
    /// timings are never regenerated.
    /// </summary>
    public static class OriginalLyricGenerator
    {
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly Regex stamps = new Regex(@"[\[<](\d+:\d+(?:\.\d+)?)[\]>]", RegexOptions.Compiled);

        public static bool CanGenerate(BeatmapLanguage language)
            => language != BeatmapLanguage.English && language != BeatmapLanguage.Unspecified && language != BeatmapLanguage.Instrumental;

        public static Task<IReadOnlyList<string?>> GenerateAsync(string artist, string title, BeatmapLanguage language,
                                                                 IReadOnlyList<LyricLine> lines, CancellationToken token)
            => GenerateAsync(client, artist, title, language, lines, token);

        internal static async Task<IReadOnlyList<string?>> GenerateAsync(HttpClient http, string artist, string title, BeatmapLanguage language,
                                                                        IReadOnlyList<LyricLine> lines, CancellationToken token)
        {
            if (!CanGenerate(language))
                throw new InvalidOperationException("Select a non-English lyric language before generating original lyrics.");

            token.ThrowIfCancellationRequested();

            if (language == BeatmapLanguage.Japanese)
                return await Task.Run(() => (IReadOnlyList<string?>)lines.Select(line =>
                    LyricOriginals.HasOriginals(new[] { line }) ? null : JapaneseLyricGenerator.Generate(line.RawText, token)).ToArray(), token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
                throw new InvalidOperationException("Set the song's artist and title before generating lyrics.");

            string url = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("typebeat/1.0 (+https://github.com/Sparpp/typebeat)");
            using var response = await http.SendAsync(request, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);

            IReadOnlyList<string?> best = new string?[lines.Count];
            int bestCount = 0;

            foreach (var track in json.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();

                // Search may return a different artist/version. Never accept lyrics on title alone.
                if (!sameMetadata(value(track, "artistName"), artist) || !sameMetadata(value(track, "trackName"), title))
                    continue;

                foreach (string field in new[] { "syncedLyrics", "plainLyrics" })
                {
                    string? content = value(track, field);

                    if (string.IsNullOrWhiteSpace(content) || !scriptMatches(content, language))
                        continue;

                    var originals = MatchLines(lines, content, language, token);
                    int count = originals.Count(o => o != null);

                    if (count > bestCount)
                    {
                        best = originals;
                        bestCount = count;
                    }
                }
            }

            return best;
        }

        private static string? value(JsonElement element, string name)
            => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

        private static bool sameMetadata(string? candidate, string expected)
            => string.Equals(LyricOriginals.CollapseWhitespace(candidate), LyricOriginals.CollapseWhitespace(expected), StringComparison.OrdinalIgnoreCase);

        private static bool scriptMatches(string content, BeatmapLanguage language)
        {
            bool kana = content.Any(c => c is >= '\u3040' and <= '\u30ff');
            bool han = content.Any(c => c is >= '\u3400' and <= '\u9fff');

            return language switch
            {
                BeatmapLanguage.Japanese => kana || han,
                BeatmapLanguage.Chinese => han && !kana,
                BeatmapLanguage.Korean => content.Any(c => c is >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff'),
                BeatmapLanguage.Russian => content.Any(c => c is >= '\u0400' and <= '\u052f'),
                BeatmapLanguage.French or BeatmapLanguage.German or BeatmapLanguage.Spanish or BeatmapLanguage.Italian
                    or BeatmapLanguage.Polish or BeatmapLanguage.Swedish => LyricOriginals.CarriesOriginal(content) && !Romaniser.HasNonLatin(content),
                _ => LyricOriginals.CarriesOriginal(content),
            };
        }

        internal static IReadOnlyList<string?> MatchLines(IReadOnlyList<LyricLine> lines, string content, BeatmapLanguage language,
                                                         CancellationToken token = default)
        {
            var source = new List<(string Text, string Key, double? Time)>();

            foreach (string raw in content.Split('\n'))
            {
                token.ThrowIfCancellationRequested();
                var times = stamps.Matches(raw);
                string text = LyricOriginals.CollapseWhitespace(stamps.Replace(raw, string.Empty));

                if (text.Length == 0 || text.StartsWith('['))
                    continue;

                var romanised = LyricOriginals.RomaniseWord(string.Concat(text.Split(' ')), language.ToCanonicalName());
                string key = romanised.Flagged ? string.Empty : letters(romanised.Text);

                if (times.Count == 0)
                    source.Add((text, key, null));
                else
                {
                    // Repeated line stamps describe repeated verses; inline word stamps do not.
                    foreach (Match time in times)
                    {
                        if (time.Value[0] == '[' && LrcParser.TryParseTimestamp(time.Groups[1].Value, out double milliseconds))
                            source.Add((text, key, milliseconds));
                    }
                }
            }

            if (source.Count > 0 && source.All(s => s.Time != null))
                source = source.OrderBy(s => s.Time).ToList();

            var result = new string?[lines.Count];
            int next = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string key = letters(lines[i].RawText);
                int match = -1;

                for (int s = next; s < source.Count; s++)
                {
                    if (key.Length > 0 && key == source[s].Key)
                    {
                        match = s;
                        break;
                    }
                }

                // Scripts without a local reading (e.g. hanzi) can still be recovered from the
                // selected song's synchronized lines, or its complete, equally sized plain lyrics.
                if (match < 0)
                {
                    double bestSimilarity = 0.85;

                    for (int s = next; s < source.Count; s++)
                    {
                        double score = similarity(key, source[s].Key);

                        if (score > bestSimilarity)
                        {
                            match = s;
                            bestSimilarity = score;
                        }
                    }
                }

                if (match < 0)
                {
                    for (int s = next; s < source.Count; s++)
                    {
                        if (source[s].Key.Length == 0 && ((source[s].Time is double time && Math.Abs(time - lines[i].StartTime) <= 1500)
                                                        || (source[s].Time == null && source.Count == lines.Count && s == i)))
                        {
                            match = s;
                            break;
                        }
                    }
                }

                if (match < 0)
                    continue;

                result[i] = LyricOriginals.OriginalFor(source[match].Text, lines[i].RawText);
                next = match + 1;
            }

            return result;
        }

        /// <summary>Preserves word timing when the source-to-romanized boundaries can be recovered exactly.</summary>
        internal static string?[] MatchWords(LyricLine line, string original, BeatmapLanguage language)
        {
            var result = new string?[line.Units.Count];
            string[] words = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length == line.Units.Count && words.Select((w, i) =>
                    (letters(LyricOriginals.RomaniseWord(w, language.ToCanonicalName()).Text) == letters(line.Units[i].Text)
                     || language == BeatmapLanguage.Japanese && japaneseWordMatches(w, line.Units[i].Text))).All(equal => equal))
                return words.Select((w, i) => LyricOriginals.OriginalFor(w, line.Units[i].Text)).ToArray();

            string joined = string.Concat(words);
            var reading = LyricOriginals.RomaniseWord(joined, language.ToCanonicalName());

            if (reading.Flagged || !string.Equals(reading.Text, string.Concat(line.Units.Select(u => u.Text)), StringComparison.OrdinalIgnoreCase))
                return result;

            int sourceStart = 0;
            int textEnd = 0;

            for (int i = 0; i < line.Units.Count; i++)
            {
                textEnd += line.Units[i].Text.Length;
                int? sourceEnd = i == line.Units.Count - 1 ? joined.Length : reading.SourceIndexOfTextSplit(textEnd);

                if (sourceEnd == null || sourceEnd <= sourceStart)
                    return new string?[line.Units.Count];

                result[i] = LyricOriginals.OriginalFor(joined.Substring(sourceStart, sourceEnd.Value - sourceStart), line.Units[i].Text);
                sourceStart = sourceEnd.Value;
            }

            return result;
        }

        private static bool japaneseWordMatches(string original, string authored)
        {
            string kana = JapaneseRomaji.Hiragana(string.Concat(JapaneseReading.Read(original).Select(r => r.Kana)));
            kana = new string(kana.Where(Utilities.IsKana).ToArray());
            string input = new string(authored.ToLowerInvariant().Where(c => char.IsLetter(c) || c is '\'' or '-').ToArray());
            // A compound's timed piece can have another reading when tagged in isolation
            // (定 is also a name). Accept the reading validated by the reverse dictionary.
            return kana.Length > 0 && (JapaneseRomaji.Matches(kana, input) & JapaneseRomaji.Match.Complete) != 0
                   || JapaneseLyricGenerator.HasDictionaryReading(new string(original.Where(char.IsLetter).ToArray()), input);
        }

        private static string letters(string text) => new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        // Allows small differences between authored romaji and the dictionary reading, without
        // pairing an unrelated translated line or a short interjection with arbitrary lyrics.
        private static double similarity(string left, string right)
        {
            int length = Math.Max(left.Length, right.Length);

            if (Math.Min(left.Length, right.Length) < 6 || Math.Abs(left.Length - right.Length) > length * 0.15)
                return 0;

            int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
            int[] current = new int[right.Length + 1];

            for (int i = 1; i <= left.Length; i++)
            {
                current[0] = i;

                for (int j = 1; j <= right.Length; j++)
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));

                (previous, current) = (current, previous);
            }

            return 1 - previous[right.Length] / (double)length;
        }
    }
}
