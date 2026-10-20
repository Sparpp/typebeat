// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Import
{
    internal static partial class JapaneseLyricGenerator
    {
        private static readonly JapaneseTextCache<string, bool> englishWords = new JapaneseTextCache<string, bool>(4096);

        // Dictionary membership alone cannot distinguish romaji: "sore made", "koi no" and
        // Japanese particles are also English dictionary words. Require phrase evidence instead.
        private static readonly HashSet<string> japaneseEnglishHomographs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "i", "u", "e", "o", "ai", "koi", "koto", "mono", "tame", "toki", "sore", "kore", "are",
            "dare", "dore", "koko", "soko", "doko", "made", "kara", "mata", "nara", "demo", "tada", "ima",
            "no", "to", "de", "ni", "mo", "me", "so", "he", "ha", "wa", "wo", "ga", "ka", "na", "ne",
            "yo", "ya", "sa", "shi", "te", "ta", "be", "you", "sou", "mai", "sake", "sushi", "nori",
        };

        private static readonly HashSet<string> englishPhraseMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "i", "you", "me", "we", "my", "your", "our", "he", "she", "it", "they", "them",
            "the", "an", "and", "of", "is", "are", "am", "be", "for", "with", "not", "never", "can", "will",
        };

        private static bool isEnglishWord(string word)
        {
            if (TypabilityModel.anyDictionaryKnows(word))
                return true;
            int apostrophe = word.IndexOf('\'');
            if (apostrophe <= 0)
                return false;
            string root = word[..apostrophe], suffix = word[(apostrophe + 1)..];
            return suffix is "m" or "re" or "ve" or "ll" or "d" or "s" && TypabilityModel.anyDictionaryKnows(root)
                   || suffix == "t" && (root is "can" or "won" || root.EndsWith('n') && TypabilityModel.anyDictionaryKnows(root[..^1]));
        }

        private static bool[] EnglishPhraseWords(string text, Match[] words)
        {
            var result = new bool[words.Length];
            var known = words.Select(w => englishWords.GetOrAdd(w.Value.ToLowerInvariant().Replace('’', '\''), isEnglishWord)).ToArray();
            var decodable = words.Select(w => JapaneseRomaji.ReadAuthored(w.Value) != null).ToArray();
            bool japaneseMajority = Enumerable.Range(0, words.Length).Count(i => !known[i] && decodable[i]) > words.Length / 2;
            for (int first = 0; first < words.Length;)
            {
                if (!known[first])
                {
                    first++;
                    continue;
                }
                int end = first + 1;
                while (end < words.Length && known[end]
                       && text[(words[end - 1].Index + words[end - 1].Length)..words[end].Index].All(c => char.IsWhiteSpace(c) || ",;:!?\"()-".Contains(c)))
                    end++;

                bool undecodable = false, marker = false;
                int content = 0, markers = 0;
                for (int i = first; i < end; i++)
                {
                    string word = words[i].Value;
                    undecodable |= !decodable[i];
                    if (englishPhraseMarkers.Contains(word))
                    {
                        marker = true;
                        markers++;
                    }
                    else if (word.Length >= 3 && !japaneseEnglishHomographs.Contains(word))
                        content++;
                }
                int next = end;
                // In a mostly Japanese line, dictionary overlap such as "no saki" or "no hate"
                // needs stronger English evidence than adjacent content words or a leading "no".
                bool phrase = end - first >= 2 && (undecodable || content > 0 && marker || markers > 1
                    || !japaneseMajority && content > 0 && (content > 1 || words[first].Value.Equals("no", StringComparison.OrdinalIgnoreCase)));
                if (phrase)
                {
                    // A trailing Japanese particle is outside the English phrase. Preserve "to"
                    // after an English verb when it leads to another English word, not as a tail.
                    while (end < words.Length && end > first && words[end - 1].Value.ToLowerInvariant() is "no" or "to" or "de")
                        end--;
                    if (first + 1 < end && words[first].Value.ToLowerInvariant() is "no" or "to" or "de"
                        && words[first + 1].Value.ToLowerInvariant() is "i" or "we" or "he" or "she" or "they")
                        first++;
                    for (int i = first; i < end; i++)
                        result[i] = true;
                }
                // Advance past the complete dictionary run, including any trimmed particles.
                first = next;
            }
            return result;
        }
    }
}
