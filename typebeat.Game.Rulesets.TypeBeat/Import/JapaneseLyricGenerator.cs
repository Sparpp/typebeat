// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    /// <summary>
    /// Converts authored romaji through the same IPAdic shipped for Japanese readings.
    /// The reverse index includes conjugated surfaces and ranks homophones with the dictionary's
    /// word and connection costs. A compound may cross authored timing words only when its
    /// surface can be partitioned back into those readings. Non-romaji stays intact.
    /// </summary>
    internal static class JapaneseLyricGenerator
    {
        private static readonly Lazy<ReadingDictionary> dictionary = new Lazy<ReadingDictionary>(() => new ReadingDictionary());
        private static readonly Regex romaji = new Regex(@"[a-zA-Z]+(?:['-][a-zA-Z]+)*-*", RegexOptions.Compiled);
        private const int beam_width = 32;
        private const int segmentation_cost = 2500;
        private const int particle_preference = 2500;

        internal static string? Generate(string text, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var paths = new List<Conversion> { new Conversion(string.Empty, 0, 0, null) };
            var matches = romaji.Matches(text).Cast<Match>().ToArray();
            int copied = 0;

            for (int i = 0; i < matches.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                Match match = matches[i];
                appendLiteral(text[copied..match.Index]);
                string? kana = JapaneseRomaji.ReadAuthored(match.Value);
                if (kana == null)
                {
                    appendLiteral(match.Value);
                    copied = match.Index + match.Length;
                    continue;
                }

                var words = new List<string> { normaliseReading(kana) };
                int consumed = match.Index + match.Length;
                // Decode each authored word independently, then score the complete phrase. Spaces
                // carry timing boundaries; they need not prevent a dictionary compound lookup.
                while (i + 1 < matches.Length && text[consumed..matches[i + 1].Index].Length > 0
                       && text[consumed..matches[i + 1].Index].All(char.IsWhiteSpace))
                {
                    string? next = JapaneseRomaji.ReadAuthored(matches[i + 1].Value);
                    if (next == null)
                        break;
                    words.Add(normaliseReading(next));
                    i++;
                    consumed = matches[i].Index + matches[i].Length;
                }
                paths = dictionary.Value.Convert(words, paths, token);
                copied = consumed;
            }

            appendLiteral(text[copied..]);
            Conversion? best = paths.MinBy(p => p.Cost + (dictionary.IsValueCreated ? dictionary.Value.Connection(p.Right, 0) : 0));
            var surfaces = new Stack<string>();

            for (var p = best; p != null; p = p.Previous)
                surfaces.Push(p.Surface);

            return LyricOriginals.OriginalFor(string.Concat(surfaces), text);

            void appendLiteral(string literal)
            {
                if (literal.Length > 0)
                    paths = paths.Select(p => new Conversion(literal, literal.All(char.IsWhiteSpace) ? p.Right : 0, p.Cost, p)).ToList();
            }
        }

        internal static bool HasDictionaryReading(string surface, string authored)
        {
            string? kana = JapaneseRomaji.ReadAuthored(authored);
            return kana != null && dictionary.Value.HasSurface(normaliseReading(kana), surface);
        }

        private static string normaliseReading(string reading)
        {
            string kana = JapaneseRomaji.Hiragana(reading);
            return kana.Contains('ー') ? JapaneseRomaji.ReadAuthored(Romaniser.Romanise(kana, "japanese").Text) ?? kana : kana;
        }

        private sealed record Conversion(string Surface, int Right, int Cost, Conversion? Previous);
        private readonly record struct Entry(string Surface, ushort Left, ushort Right, int Cost, bool Particle, bool Auxiliary);

        private sealed class ReadingDictionary
        {
            private readonly Dictionary<string, Entry[]> readings;
            private readonly short[] connections;
            private readonly int leftSize;
            private readonly int longestReading;

            public ReadingDictionary()
            {
                string directory = Path.Combine(AppContext.BaseDirectory, "IpaDic");
                byte[] data = File.ReadAllBytes(Path.Combine(directory, "sys.dic"));

                // MeCab dictionary version 102: 72-byte header, double-array trie, 16-byte
                // tokens, then null-terminated UTF-8 IPAdic features. No second dictionary is needed.
                if (data.Length < 72 || (unchecked((uint)readInt(data, 0)) ^ 0xef718f77u) != data.Length || readInt(data, 4) != 102
                    || Encoding.ASCII.GetString(data, 40, 5) != "UTF-8")
                    throw new InvalidDataException("The bundled Japanese dictionary has an unsupported format.");

                int trieSize = readInt(data, 24);
                int tokenSize = readInt(data, 28);
                int featureSize = readInt(data, 32);
                int tokenStart = checked(72 + trieSize);
                int featureStart = checked(tokenStart + tokenSize);

                if (trieSize % 8 != 0 || tokenSize % 16 != 0 || checked(featureStart + featureSize) != data.Length)
                    throw new InvalidDataException("The bundled Japanese dictionary is incomplete.");

                int count = trieSize / 8;
                var nodeAtBase = new int[count];

                for (int node = 0; node < count; node++)
                {
                    int value = readInt(data, 72 + node * 8);
                    if (value > 0 && value < count)
                        nodeAtBase[value] = node;
                }

                var index = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
                var pronunciationSpellings = new HashSet<string>(StringComparer.Ordinal);
                var surfaceBytes = new List<byte>();

                for (int node = 0; node < count; node++)
                {
                    int value = readInt(data, 72 + node * 8);
                    if (value >= 0 || readInt(data, 76 + node * 8) != node)
                        continue;

                    // Check stores the parent's base address. Following those parents reconstructs
                    // the actual surface, rather than replacing inflections with their base form.
                    surfaceBytes.Clear();
                    for (int at = nodeAtBase[node]; at != 0;)
                    {
                        int parent = readInt(data, 76 + at * 8);
                        surfaceBytes.Add(checked((byte)(at - parent - 1)));
                        at = nodeAtBase[parent];
                    }

                    surfaceBytes.Reverse();
                    string surface = Encoding.UTF8.GetString(surfaceBytes.ToArray());
                    int packed = -value - 1;
                    int first = packed >> 8;

                    for (int t = first; t < first + (packed & 255); t++)
                    {
                        int at = tokenStart + t * 16;
                        int feature = featureStart + readInt(data, at + 8);
                        int end = Array.IndexOf(data, (byte)0, feature);
                        string[] fields = Encoding.UTF8.GetString(data, feature, end - feature).Split(',');
                        // Entries for symbols/numbers can have kana readings (＆ is ト). They
                        // must never replace actual lyric words during reverse conversion.
                        if (fields.Length < 9 || fields[7] == "*"
                            || !surface.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ' or 'ー' or '々'
                                or >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff'))
                            continue;

                        var entry = new Entry(surface, readUShort(data, at), readUShort(data, at + 2), readShort(data, at + 6) + (fields[1] == "固有名詞" ? 2000 : 0), fields[0] == "助詞", fields[0] == "助動詞");
                        string reading = normaliseReading(fields[7]);
                        add(reading, entry);

                        // Pronunciation covers は/wa and へ/e, greetings, and long-vowel variants.
                        string pronunciation = normaliseReading(fields[8]);
                        if (pronunciation != reading && fields[8] != "*")
                        {
                            add(pronunciation, entry);
                            if (surface.Any(c => c is >= 'ぁ' and <= 'ゖ'))
                                pronunciationSpellings.Add(pronunciation);
                        }
                        if (surface == "を")
                            add("お", entry);
                    }
                }

                // IPAdic costs model forward segmentation, so proper names and phonetic katakana
                // variants can otherwise outrank an ordinary Japanese spelling of the same reading.
                // Prefer dictionary spellings such as こんにちは over phonetic こんにちわ when
                // their pronunciation entry matches the authored romaji.
                readings = index.ToDictionary(p => p.Key, p => p.Value.Distinct().Select(e => e with
                {
                    Cost = e.Cost + (e.Surface.All(c => c is >= 'ァ' and <= 'ヶ' or 'ー')
                                     && p.Value.Any(other => other.Surface.Any(c => c is >= 'ぁ' and <= 'ゖ')) ? 3500 : 0)
                           + (pronunciationSpellings.Contains(p.Key) && JapaneseRomaji.Hiragana(e.Surface) == p.Key ? 3500 : 0),
                }).OrderBy(e => e.Cost).Take(beam_width).ToArray(), StringComparer.Ordinal);
                longestReading = readings.Keys.Max(r => r.Length);
                byte[] matrix = File.ReadAllBytes(Path.Combine(directory, "matrix.bin"));
                leftSize = readUShort(matrix, 0);
                int rightSize = readUShort(matrix, 2);

                if (leftSize != readInt(data, 16) || rightSize != readInt(data, 20) || matrix.Length != 4 + leftSize * rightSize * 2)
                    throw new InvalidDataException("The bundled Japanese dictionary's connection matrix is incompatible.");

                connections = new short[leftSize * rightSize];
                for (int i = 0; i < connections.Length; i++)
                    connections[i] = readShort(matrix, 4 + i * 2);

                void add(string reading, Entry entry)
                {
                    if (!index.TryGetValue(reading, out var entries))
                        index.Add(reading, entries = new List<Entry>());
                    entries.Add(entry);
                }
            }

            public bool HasSurface(string reading, string surface)
                => readings.TryGetValue(reading, out var entries) && entries.Any(e => e.Surface == surface);

            public int Connection(int right, int left) => connections[right + leftSize * left];

            public List<Conversion> Convert(IReadOnlyList<string> words, List<Conversion> previous, CancellationToken token)
            {
                string kana = string.Concat(words);
                var boundaries = new HashSet<int>();
                int offset = 0;
                foreach (string word in words)
                {
                    boundaries.Add(offset);
                    offset += word.Length;
                }
                boundaries.Add(offset);
                var lattice = new Dictionary<int, Conversion>?[kana.Length + 1];
                lattice[0] = previous.GroupBy(p => p.Right).ToDictionary(g => g.Key, g => g.MinBy(p => p.Cost)!);

                for (int at = 0; at < kana.Length; at++)
                {
                    token.ThrowIfCancellationRequested();
                    if (lattice[at] == null)
                        continue;
                    var before = lattice[at]!.Values.OrderBy(p => p.Cost).Take(beam_width).ToArray();
                    for (int length = 1; length <= Math.Min(longestReading, kana.Length - at); length++)
                    {
                        if (readings.TryGetValue(kana.Substring(at, length), out var entries))
                            foreach (Entry entry in entries)
                                extend(entry, length);
                    }
                    extend(new Entry(kana[at].ToString(), 0, 0, 12000, false, false), 1);

                    void extend(Entry entry, int length)
                    {
                        int end = at + length;
                        string surface = entry.Surface;
                        // Allow a compound such as 不安定 across "fuan tei", keeping the
                        // surfaces "不安" and "定" in their existing word timing windows.
                        int[] splits = boundaries.Where(b => b > at && b < end).OrderBy(b => b).ToArray();
                        if (splits.Length > 0)
                        {
                            if (!boundaries.Contains(at) || !boundaries.Contains(end))
                                return;
                            string? aligned = splitSurface(surface, at, splits.Append(end).ToArray());
                            if (aligned == null)
                                return;
                            surface = aligned;
                        }
                        if (end < kana.Length && boundaries.Contains(end))
                            surface += " ";
                        int preference = 0;
                        if (boundaries.Contains(at) && boundaries.Contains(end) && length <= 2 && !entry.Particle
                            && readings.TryGetValue(kana.Substring(at, length), out var same) && same.Any(e => e.Particle))
                            preference = particle_preference;
                        Conversion best = before.MinBy(p => p.Cost + Connection(p.Right, entry.Left))!;
                        // Discourage invented chains of short nouns without penalising normal
                        // verb inflection or particle sequences, which need multiple entries.
                        int cost = best.Cost + Connection(best.Right, entry.Left) + entry.Cost + (entry.Particle || entry.Auxiliary ? 0 : segmentation_cost) + preference;
                        var after = lattice[end] ??= new Dictionary<int, Conversion>();
                        if (!after.TryGetValue(entry.Right, out var existing) || cost < existing.Cost)
                            after[entry.Right] = new Conversion(surface, entry.Right, cost, best);
                    }
                }
                return lattice[^1]!.Values.OrderBy(p => p.Cost).Take(beam_width).ToList();

                string? splitSurface(string surface, int start, int[] ends)
                {
                    return split(0, start, 0);
                    string? split(int source, int reading, int part)
                    {
                        string key = kana[reading..ends[part]];
                        for (int cut = source + 1; cut <= surface.Length; cut++)
                        {
                            string prefix = surface[source..cut];
                            if (normaliseReading(prefix) != key && !HasSurface(key, prefix))
                                continue;
                            if (part == ends.Length - 1)
                            {
                                if (cut == surface.Length)
                                    return prefix;
                                continue;
                            }
                            string? suffix = split(cut, ends[part], part + 1);
                            if (suffix != null)
                                return prefix + " " + suffix;
                        }
                        return null;
                    }
                }
            }

            private static int readInt(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            private static ushort readUShort(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
            private static short readShort(byte[] data, int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2));
        }
    }
}
