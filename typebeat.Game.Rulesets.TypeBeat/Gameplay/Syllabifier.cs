// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Pure C#: no osu.Framework dependencies (house style of Gameplay/Judgement.cs).

using System;
using System.Collections.Generic;

namespace typebeat.Game.Rulesets.TypeBeat.Gameplay
{
    /// <summary>
    /// Rule-based English syllabification of a single gameplay word (one whitespace-delimited token
    /// of the default typed stream, see <see cref="Beatmaps.Typeability.ToDefaultStream"/>).
    ///
    /// <para>The analysis is orthographic phonotactics, not a dictionary: vowel groups are syllable
    /// nuclei (with <c>y</c> a vowel when it is not an onset glide and <c>u</c> silent in
    /// <c>qu</c>/<c>gu</c>+vowel), a run of adjacent vowels is one nucleus except for the common
    /// hiatus pairs (<c>ia io iu ua uo eo</c> and <c>ie</c> before <c>t</c>, plus <c>-Ving</c>),
    /// silent terminal <c>e</c> is dropped (including <c>-ed</c>/<c>-es</c> forms) except the
    /// syllabic <c>C+le</c>/<c>C+re</c> case, and boundaries fall V|CV, VC|CV with inseparable
    /// onset clusters and digraphs kept whole, and doubled consonants split down the middle. A
    /// small table pins very common lyric words the rules cannot get right (people, every,
    /// something and friends).</para>
    ///
    /// <para>The analysis assumes a real English spelling, so it does not answer for a STYLISED
    /// one: <see cref="IsSyllabifiable"/> is the gate that says whether a token is one of those, and
    /// a caller that cares about junk splits asks it FIRST. <see cref="CountSyllables"/> and
    /// <see cref="SplitPoints"/> themselves answer for every input, gate or no gate, because a
    /// mapper's hand-authored subtimings must still be honoured on a word that looks stylised.</para>
    ///
    /// <para>Non-letter input is defensive territory: punctuation (apostrophes, the freestyle
    /// marker <c>&amp;</c>, anything else) is transparent, it attaches to the surrounding syllable
    /// and never starts one. A maximal digit run is ONE syllable of its own (a number is read as
    /// one spoken chunk: "24" is one group, "b2b" is three). Uppercase input is folded before
    /// analysis; indices always refer to the original string.</para>
    /// </summary>
    public static class Syllabifier
    {
        /// <summary>
        /// Whether <paramref name="word"/> looks like an English word the orthographic rules above
        /// can actually analyse, rather than a STYLISED spelling. Pure and side-effect free, and the
        /// gate <see cref="TypingLine"/> uses to decide whether a token gets syllable groups at all:
        /// lyrics are full of "wooooooords", "heyyyyy", "naaaah" and "ohhh", and the phonotactics
        /// here were built for real English, so on those they produce junk splits and a confidence
        /// they have not earned. A word that fails this test is left UNGROUPED, keeping the classic
        /// per-character presentation and judgement, which is the honest answer.
        ///
        /// <para>ONE rule, deliberately: a run of THREE OR MORE identical LETTERS is not a standard
        /// English spelling, so the word fails. That covers the whole stylised family (a held vowel,
        /// a dragged consonant, a hummed or hushed noise) and it steals nothing, because DOUBLED
        /// letters are ordinary English and pass: little, good, hello, ooh, aah, la, na, and every
        /// word of the <c>SyllabifierTest</c> corpus. Case is folded, so WOOOO fails too.</para>
        ///
        /// <para>The scope is narrow on purpose. DIGITS are never a reason to fail (a digit run
        /// already has its own defined behaviour, one spoken chunk, so "1000" and "b2b" stay
        /// grouped), and punctuation only ever BREAKS a run, it never makes one. A vowel-less
        /// consonant noise ("brr", "pfft", "tsk") was considered and deliberately NOT added: "hmm"
        /// is pinned in the corpus at one syllable, which is also the right answer, so that rule
        /// would have cost a correct grouping to buy nothing. Short or common words are never
        /// rejected: there is no dictionary here and adding one is not the job.</para>
        ///
        /// <para>Null/empty is false, matching <see cref="CountSyllables"/> returning 0 for it:
        /// there is nothing to syllabify.</para>
        /// </summary>
        public static bool IsSyllabifiable(string word)
        {
            if (string.IsNullOrEmpty(word))
                return false;

            // Compare against the PREVIOUS character rather than carrying a sentinel: a run only
            // ever extends when both ends are the same letter, so a digit or a punctuation mark
            // simply fails the test and resets the count.
            int run = 1;

            for (int i = 1; i < word.Length; i++)
            {
                char c = char.ToLowerInvariant(word[i]);
                char previous = char.ToLowerInvariant(word[i - 1]);

                run = char.IsLetter(c) && c == previous ? run + 1 : 1;

                if (run >= 3)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Number of syllables in an English word. Never less than 1 for a non-empty word
        /// (a vowel-less or all-punctuation token counts as one group); 0 for null/empty.
        /// </summary>
        public static int CountSyllables(string word)
        {
            if (string.IsNullOrEmpty(word))
                return 0;

            return naturalSplits(word).Count + 1;
        }

        /// <summary>
        /// The 0-based indices into <paramref name="word"/> at which a new syllable STARTS,
        /// strictly ascending, never containing 0, count == syllables - 1.
        ///
        /// <para><paramref name="forcedCount"/>, when given, is authoritative (a mapper's
        /// hand-authored subtimings say how many syllables the word has) and the natural analysis
        /// is bent to hit it: extra splits are added at the best remaining interior position of the
        /// longest group, surplus boundaries are merged weakest (smallest merged group) first.
        /// Over-forcing degrades gracefully: a word can carry at most <c>word.Length - 1</c>
        /// splits, so the caller can be handed fewer groups than it asked for (a 3-letter word
        /// forced to 5 syllables yields 3 single-character groups). A forced count below 1 is
        /// treated as 1.</para>
        /// </summary>
        public static IReadOnlyList<int> SplitPoints(string word, int? forcedCount = null)
        {
            if (string.IsNullOrEmpty(word))
                return Array.Empty<int>();

            var splits = naturalSplits(word);

            if (forcedCount is not int forced)
                return splits;

            int target = Math.Clamp(forced - 1, 0, word.Length - 1);

            while (splits.Count > target)
                mergeWeakest(splits, word.Length);

            while (splits.Count < target && addBestSplit(splits, word))
            {
            }

            return splits;
        }

        /// <summary>
        /// Very common lyric words whose pronunciation the orthographic rules cannot recover
        /// (mostly medial silent e in compounds, and the ev(e)ry family which is sung and listed
        /// by Merriam-Webster with the first e elided). Keyed on the exact lower-cased word.
        /// </summary>
        private static readonly Dictionary<string, int[]> exceptions = new Dictionary<string, int[]>
        {
            ["people"] = new[] { 3 }, // peo|ple: "eo" is one nucleus here, unlike vid|e|o
            ["every"] = new[] { 2 }, // ev|ery, 2 syllables (sung form; formal ev|er|y is 3)
            ["everything"] = new[] { 2, 5 }, // ev|ery|thing
            ["everyone"] = new[] { 2, 5 }, // ev|ery|one
            ["everybody"] = new[] { 2, 5, 7 }, // ev|ery|bo|dy
            ["everywhere"] = new[] { 2, 5 }, // ev|ery|where
            ["something"] = new[] { 4 }, // some|thing: medial silent e
            ["sometimes"] = new[] { 4 }, // some|times
            ["somewhere"] = new[] { 4 }, // some|where
            ["someone"] = new[] { 4 }, // some|one
            ["somebody"] = new[] { 4, 6 }, // some|bo|dy
            ["lovely"] = new[] { 4 }, // love|ly: medial silent e before suffix
            ["lonely"] = new[] { 4 }, // lone|ly
            ["maybe"] = new[] { 3 }, // may|be: compound of may + be, final e is a nucleus
            ["million"] = new[] { 3 }, // mil|lion: "io" fuses here but splits in li|on
            ["billion"] = new[] { 3 }, // bil|lion
            ["create"] = new[] { 3 }, // cre|ate: "ea" is hiatus here, unlike dream
        };

        // Consonant pairs that stay together as the onset of the following syllable (inseparable
        // clusters and digraphs): ta|ble, a|pron, no|thing, e|qual.
        private static readonly HashSet<string> two_clusters = new HashSet<string>
        {
            "bl", "br", "ch", "cl", "cr", "dr", "fl", "fr", "gh", "gl", "gr", "ph", "pl", "pr",
            "qu", "sc", "sh", "sk", "sl", "sm", "sn", "sp", "st", "sw", "th", "tr", "tw", "wh", "wr",
        };

        // Three-consonant onsets: in|stru|ment.
        private static readonly HashSet<string> three_clusters = new HashSet<string>
        {
            "chr", "phr", "sch", "scr", "shr", "spl", "spr", "squ", "str", "thr",
        };

        private static List<int> naturalSplits(string word)
        {
            var chars = word.ToCharArray();

            for (int i = 0; i < chars.Length; i++)
                chars[i] = char.ToLowerInvariant(chars[i]);

            string lower = new string(chars);

            if (exceptions.TryGetValue(lower, out int[]? pinned))
                return new List<int>(pinned);

            var boundaries = new List<int>();
            var segment = new List<int>(); // original indices of the current letter run (punctuation is transparent)
            bool anyContent = false;
            bool inDigits = false;

            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];

                if (char.IsLetter(c))
                {
                    if (inDigits)
                    {
                        // the digit run just before this letter was its own syllable.
                        boundaries.Add(i);
                        inDigits = false;
                    }

                    segment.Add(i);
                    anyContent = true;
                }
                else if (char.IsDigit(c))
                {
                    if (!inDigits)
                    {
                        flushSegment(lower, segment, boundaries);

                        if (anyContent)
                            boundaries.Add(i);

                        inDigits = true;
                        anyContent = true;
                    }
                }

                // anything else (apostrophes, punctuation, the freestyle marker) is transparent:
                // it attaches to whichever syllable surrounds it and never starts one.
            }

            flushSegment(lower, segment, boundaries);
            return boundaries;
        }

        private static void flushSegment(string lower, List<int> segment, List<int> boundaries)
        {
            if (segment.Count == 0)
                return;

            analyzeLetters(lower, segment, boundaries);
            segment.Clear();
        }

        /// <summary>
        /// The phonotactic core: syllabifies one contiguous letter sequence (seg holds the original
        /// index of each letter) and appends the resulting split indices to <paramref name="boundaries"/>.
        /// </summary>
        private static void analyzeLetters(string lower, List<int> seg, List<int> boundaries)
        {
            int n = seg.Count;
            var s = new char[n];

            for (int j = 0; j < n; j++)
                s[j] = lower[seg[j]];

            bool endsIng = n >= 4 && s[n - 3] == 'i' && s[n - 2] == 'n' && s[n - 1] == 'g';

            // 1. classify vowels.
            var vowel = new bool[n];

            for (int j = 0; j < n; j++)
            {
                char c = s[j];

                if (isVowelLetter(c))
                    vowel[j] = true;
                else if (c == 'y')
                {
                    char next = j + 1 < n ? s[j + 1] : '\0';

                    if (!isVowelLetter(next))
                        vowel[j] = true; // no following vowel: y is the nucleus (rhythm, happy, cry)
                    else if (next == 'e' && j + 1 == n - 1)
                        vowel[j] = true; // word-final "ye": the e is silent and y carries the nucleus (goodbye, dye)
                    else if (endsIng && j == n - 4)
                        vowel[j] = true; // "-ying": dy|ing, say|ing (the split is forced below)

                    // otherwise y is an onset glide (yes, beyond, canyon)
                }
            }

            // u is not a nucleus in "qu" (quiet, equal) nor in "gu" + vowel (guard, guess, league).
            for (int j = 1; j < n; j++)
            {
                if (s[j] == 'u' && vowel[j] && (s[j - 1] == 'q' || (s[j - 1] == 'g' && j + 1 < n && isVowelLetter(s[j + 1]))))
                    vowel[j] = false;
            }

            // 2. nucleus groups: maximal vowel runs, broken at hiatus pairs.
            var groups = new List<(int start, int end)>();

            for (int j = 0; j < n; j++)
            {
                if (!vowel[j])
                    continue;

                int start = j;

                while (j + 1 < n && vowel[j + 1] && !isHiatus(s, endsIng, j))
                    j++;

                groups.Add((start, j));
            }

            // 3. silent final e. Only ever a single-vowel group (a preceding vowel keeps it: goes,
            // memories) and only when another nucleus remains (the, be, bye keep theirs).
            bool syllabicLe = false;

            if (groups.Count >= 2)
            {
                (int start, int end) last = groups[^1];

                if (last.start == last.end && s[last.start] == 'e')
                {
                    int p = last.start;
                    bool silent = false;

                    if (p == n - 1)
                    {
                        // plain final e: silent (make, love, alone) except syllabic C+le / C+re
                        // (table, little, acre), where the e-group survives as the last syllable.
                        char prev = s[p - 1];
                        syllabicLe = (prev == 'l' || prev == 'r') && p >= 2 && !vowel[p - 2];
                        silent = !syllabicLe;
                    }
                    else if (p == n - 2 && s[n - 1] == 'd')
                    {
                        // -ed: silent (loved, called) unless after t/d (wanted, needed).
                        silent = s[p - 1] != 't' && s[p - 1] != 'd';
                    }
                    else if (p == n - 2 && s[n - 1] == 's')
                    {
                        // -es: silent (makes, times, clothes) unless after a sibilant
                        // (wishes, places, changes, churches).
                        char prev = s[p - 1];
                        bool sibilant = prev is 's' or 'z' or 'x' or 'c' or 'g'
                                        || (prev == 'h' && p >= 2 && (s[p - 2] == 's' || s[p - 2] == 'c'));
                        silent = !sibilant;
                    }

                    if (silent)
                        groups.RemoveAt(groups.Count - 1);
                }
            }

            // 4. place one boundary between each pair of adjacent nuclei.
            for (int g = 1; g < groups.Count; g++)
            {
                int prevEnd = groups[g - 1].end;
                int curStart = groups[g].start;
                int gap = curStart - prevEnd - 1;
                int split;

                if (gap == 0)
                    split = curStart; // hiatus: the new nucleus starts the syllable (qui|et, radi|o)
                else if (gap == 1)
                    split = prevEnd + 1; // V|CV: the consonant onsets the second syllable (o|pen)
                else if (syllabicLe && g == groups.Count - 1)
                    split = curStart - 2; // the syllabic-le/re syllable is Cle/Cre (tur|tle, a|cre)
                else if (s[prevEnd + 1] == s[prevEnd + 2])
                    split = prevEnd + 2; // doubled consonant splits down the middle (bet|ter, lit|tle)
                else if (gap >= 3 && three_clusters.Contains(new string(s, curStart - 3, 3)))
                    split = curStart - 3; // in|stru...
                else if (two_clusters.Contains(new string(s, curStart - 2, 2)))
                    split = curStart - 2; // inseparable cluster/digraph onsets the second syllable (ta|ble, no|thing)
                else
                    split = curStart - 1; // otherwise a single-consonant onset (bet|ter shape: VC|CV, pump|kin)

                boundaries.Add(seg[split]);
            }
        }

        /// <summary>
        /// Whether adjacent vowels at j, j+1 are two nuclei (hiatus) rather than one digraph.
        /// </summary>
        private static bool isHiatus(char[] s, bool endsIng, int j)
        {
            char a = s[j], b = s[j + 1];
            int k = j + 1;

            // vowel + "-ing" is always two syllables: be|ing, go|ing, dy|ing, say|ing.
            if (endsIng && b == 'i' && k == s.Length - 3)
                return true;

            switch (a)
            {
                case 'i' when b is 'a' or 'o' or 'u':
                    // ...except -tion/-cial/-gion/-cious style endings, where the i fuses with the
                    // preceding softened consonant: na|tion, spe|cial, but radi|o, med|i|a, gen|i|us.
                    return j == 0 || !isSoftener(s[j - 1]);

                case 'i' when b == 'e':
                    // qui|et, di|et; believe, friend, die stay fused.
                    return k + 1 < s.Length && s[k + 1] == 't';

                case 'e' when b == 'o':
                    // vide|o, stere|o split; gor|geous (softener before) does not.
                    return j == 0 || !isSoftener(s[j - 1]);

                case 'u' when b is 'a' or 'o':
                    return true; // u|su|al, act|u|al, du|o

                default:
                    return false;
            }
        }

        private static bool isSoftener(char c) => c is 'c' or 'g' or 's' or 't' or 'x';

        private static bool isVowelLetter(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

        /// <summary>
        /// Removes the boundary whose removal produces the smallest merged group (the weakest
        /// boundary separates the two smallest neighbours); leftmost on ties.
        /// </summary>
        private static void mergeWeakest(List<int> splits, int length)
        {
            int best = 0;
            int bestMerged = int.MaxValue;

            for (int i = 0; i < splits.Count; i++)
            {
                int left = i == 0 ? 0 : splits[i - 1];
                int right = i == splits.Count - 1 ? length : splits[i + 1];
                int merged = right - left;

                if (merged < bestMerged)
                {
                    bestMerged = merged;
                    best = i;
                }
            }

            splits.RemoveAt(best);
        }

        /// <summary>
        /// Adds one split at the best interior position of the longest current group: prefer a
        /// vowel-to-consonant transition (a fresh onset: fi|re), then consonant-to-vowel, then
        /// inside a vowel run, then anywhere; nearest the group's midpoint on ties. Returns false
        /// when every group is a single character, so no position remains.
        /// </summary>
        private static bool addBestSplit(List<int> splits, string word)
        {
            int bestStart = -1, bestLen = 1;

            for (int i = 0; i <= splits.Count; i++)
            {
                int start = i == 0 ? 0 : splits[i - 1];
                int end = i == splits.Count ? word.Length : splits[i];

                if (end - start > bestLen)
                {
                    bestLen = end - start;
                    bestStart = start;
                }
            }

            if (bestStart < 0)
                return false;

            double mid = bestStart + bestLen / 2.0;
            int bestPos = -1;
            int bestClass = int.MaxValue;
            double bestDist = double.MaxValue;

            for (int p = bestStart + 1; p < bestStart + bestLen; p++)
            {
                int cls = transitionClass(word, p);
                double dist = Math.Abs(p - mid);

                if (cls < bestClass || (cls == bestClass && dist < bestDist))
                {
                    bestClass = cls;
                    bestDist = dist;
                    bestPos = p;
                }
            }

            int idx = splits.BinarySearch(bestPos);
            splits.Insert(~idx, bestPos);
            return true;
        }

        private static int transitionClass(string word, int p)
        {
            char a = char.ToLowerInvariant(word[p - 1]);
            char b = char.ToLowerInvariant(word[p]);

            if (!char.IsLetter(a) || !char.IsLetter(b))
                return 4;

            bool va = isVowelish(a), vb = isVowelish(b);

            if (va && !vb) return 1; // V|C: the consonant onsets the new group
            if (!va && vb) return 2; // C|V
            if (va && vb) return 3; // splitting inside a vowel run (fi|re when over-forced)

            return 4; // C|C
        }

        private static bool isVowelish(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
    }
}
