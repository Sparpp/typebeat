// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    internal static partial class JapaneseLyricGenerator
    {
        private static readonly Lazy<SurfaceDictionary> surfaces = new Lazy<SurfaceDictionary>(() => new SurfaceDictionary());

        /// <summary>
        /// Forward lookups walk IPAdic's existing surface trie. Checking a reading in the editor
        /// must not build the complete reverse index and connection matrix used by Generate.
        /// </summary>
        private sealed class SurfaceDictionary
        {
            private readonly byte[] data;
            private readonly int trieCount;
            private readonly int tokenStart;
            private readonly int featureStart;
            private readonly JapaneseTextCache<string, string[]> readings = new JapaneseTextCache<string, string[]>(4096);

            internal SurfaceDictionary()
            {
                data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "IpaDic", "sys.dic"));
                if (data.Length < 72 || (unchecked((uint)readInt(0)) ^ 0xef718f77u) != data.Length || readInt(4) != 102
                    || Encoding.ASCII.GetString(data, 40, 5) != "UTF-8")
                    throw new InvalidDataException("The bundled Japanese dictionary has an unsupported format.");
                int trieSize = readInt(24), tokenSize = readInt(28), featureSize = readInt(32);
                tokenStart = checked(72 + trieSize);
                featureStart = checked(tokenStart + tokenSize);
                if (trieSize % 8 != 0 || tokenSize % 16 != 0 || checked(featureStart + featureSize) != data.Length)
                    throw new InvalidDataException("The bundled Japanese dictionary is incomplete.");
                trieCount = trieSize / 8;
            }

            internal bool MatchesReading(string reading, string surface)
            {
                var failed = new HashSet<(int Surface, int Kana)>();
                return match(0, 0);

                bool match(int source, int kana)
                {
                    if (source == surface.Length || kana == reading.Length)
                        return source == surface.Length && kana == reading.Length;
                    if (!failed.Add((source, kana)))
                        return false;
                    // Retain alternate compound readings such as 一人 + で = hitoride.
                    for (int length = 1; length <= surface.Length - source; length++)
                    {
                        foreach (string candidate in readings.GetOrAdd(surface.Substring(source, length), lookup))
                        {
                            if (reading.AsSpan(kana).StartsWith(candidate.AsSpan(), StringComparison.Ordinal)
                                && match(source + length, kana + candidate.Length))
                                return true;
                        }
                    }
                    string direct = JapaneseRomaji.Hiragana(surface[source].ToString());
                    return reading.AsSpan(kana).StartsWith(direct.AsSpan(), StringComparison.Ordinal)
                           && match(source + 1, kana + direct.Length);
                }
            }

            private string[] lookup(string surface)
            {
                // Match the generation dictionary's exclusion of symbol/number readings.
                if (!surface.All(c => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヶ' or 'ー' or '々'
                    or >= '\u3400' and <= '\u4dbf' or >= '\u4e00' and <= '\u9fff'))
                    return Array.Empty<string>();
                int node = readInt(72);
                foreach (byte value in Encoding.UTF8.GetBytes(surface))
                {
                    int next = node + value + 1;
                    if (next < 0 || next >= trieCount || readInt(76 + next * 8) != node)
                        return Array.Empty<string>();
                    node = readInt(72 + next * 8);
                }
                if (node < 0 || node >= trieCount || readInt(76 + node * 8) != node)
                    return Array.Empty<string>();
                int terminal = readInt(72 + node * 8);
                if (terminal >= 0)
                    return Array.Empty<string>();
                int packed = -terminal - 1, first = packed >> 8;
                var result = new HashSet<string>(StringComparer.Ordinal);
                for (int token = first; token < first + (packed & 255); token++)
                {
                    int feature = featureStart + readInt(tokenStart + token * 16 + 8);
                    int end = Array.IndexOf(data, (byte)0, feature);
                    string[] fields = Encoding.UTF8.GetString(data, feature, end - feature).Split(',');
                    if (fields.Length < 9 || fields[7] == "*")
                        continue;
                    result.Add(normaliseReading(fields[7]));
                    if (fields[8] != "*")
                        result.Add(normaliseReading(fields[8]));
                }
                if (surface == "を")
                    result.Add("お");
                return result.ToArray();
            }

            private int readInt(int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        }
    }
}
