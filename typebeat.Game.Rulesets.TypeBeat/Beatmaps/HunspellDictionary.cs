// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// A hunspell dictionary: the .dic stems and the .aff rules that expand them, which is what the
    /// Typability Index's spell-check predictor reads (see <see cref="TypabilityModel"/>).
    ///
    /// <para>WHY NOT JUST A WORD LIST. Expanding every dictionary's affixes into surface forms at
    /// load time would multiply each one by its own inflection for a feature that only ever asks a
    /// yes/no question. So the two files are kept in the shape hunspell stores them and a lookup is
    /// answered by <em>stripping</em> candidate affixes off the query: the stem lookup is one hash
    /// probe, and the affix rules are only walked for a word that is not already a stem.</para>
    ///
    /// <para>WHAT IT IMPLEMENTS, out of the whole hunspell format, is the subset the four shipped
    /// English dictionaries use and the predictor can observe: the stem list, prefix and suffix
    /// rules with their conditions, strip and add, prefix+suffix and two-suffix chains through the
    /// rules' continuation classes, ICONV input conversion, ONLYINCOMPOUND (a stem that may not
    /// stand alone), and hunspell's own case handling - an initial capital and an all-caps word are
    /// read through their lower-case forms, and a possessive is the '-s affix the dictionaries
    /// declare.</para>
    ///
    /// <para>WHAT IT DOES NOT: the COMPOUNDRULE machinery as such. What those rules are reached for
    /// in English - a hyphenated compound, a run of digits, a number with its ordinal suffix - is
    /// answered by the rules in <see cref="Contains"/> instead, and the TypabilityModel test diffs
    /// the whole approach against the real hunspell over every word the two oracles' sentences
    /// hold.</para>
    /// </summary>
    internal sealed class HunspellDictionary
    {
        /// <summary>Where a .dic word's own name ends and its flags (or morphology) begin.</summary>
        private static readonly SearchValues<char> word_terminators = SearchValues.Create(" \t");

        /// <summary>One affix rule: its flag, what it strips, what it adds, and the stem it applies to.</summary>
        private readonly record struct AffixRule(char Flag, bool Prefix, string Strip, string Add, string Continuation, string Condition, bool CrossProduct);

        /// <summary>A stem's flags and whether the dictionary bars it from standing alone.</summary>
        private readonly record struct Stem(string Flags, bool OnlyInCompound);

        /// <summary>One element of a hunspell affix condition: a literal, a dot, or a character class.</summary>
        private readonly record struct ConditionElement(char Literal, bool Any, string? Class, bool Negated);

        private readonly Dictionary<string, Stem> stems = new Dictionary<string, Stem>(StringComparer.Ordinal);
        private readonly List<AffixRule> prefixes = new List<AffixRule>();
        private readonly List<AffixRule> suffixes = new List<AffixRule>();
        private readonly HashSet<char> onlyInCompound = new HashSet<char>();
        private readonly List<(string From, string To)> conversions = new List<(string, string)>();

        private HunspellDictionary()
        {
        }

        /// <summary>Loads a dictionary from its .aff and .dic text.</summary>
        public static HunspellDictionary Load(string aff, string dic)
        {
            var dictionary = new HunspellDictionary();
            dictionary.readAff(aff);
            dictionary.readDic(dic);

            return dictionary;
        }

        /// <summary>
        /// Whether hunspell would accept <paramref name="word"/>: its own form, its lower-case form
        /// when it opens with a capital or is all caps, the base word of a possessive, or any form
        /// one of the affix rules produces from a stem.
        /// </summary>
        public bool Contains(string word)
        {
            if (string.IsNullOrEmpty(word))
                return false;

            if (conversions.Count > 0)
            {
                var converted = new StringBuilder(word);

                foreach ((string from, string to) in conversions)
                    converted.Replace(from, to);

                word = converted.ToString();
            }

            if (spell(word))
                return true;

            // Case handling, and only the two shapes hunspell folds: a word that opens with a
            // capital and carries no others may be the lower-case entry ("Christmas"), and a word
            // whose letters are ALL capitals may be either that or the capitalised one ("DON'T" ->
            // "don't"). A word with capitals scattered through it ("mErcy") is not folded at all,
            // which is the reading the authors' table holds it to.
            string lower = word.ToLowerInvariant();
            bool firstOnly = word.Length > 0 && char.IsUpper(word[0]) && !word.Skip(1).Any(char.IsUpper);
            bool allCaps = word.Any(char.IsLetter) && !word.Any(char.IsLower);

            if (firstOnly && spell(lower))
                return true;

            if (allCaps && (spell(lower) || spell(char.ToUpperInvariant(lower[0]) + lower.Substring(1))))
                return true;

            // A possessive is an ordinary affix of the dictionaries ("cat's", "mercy's"),
            // not a rule of the checker.

            // A NUMBER is a compound of the dictionaries' own numeral entries, which hunspell reads
            // as any run of digits and separators, and any run of digits carrying the ordinal suffix
            // English actually gives that number: "21st" and "12th" are words, "21th" and "13st" are
            // not.
            if (isNumeral(word) || isOrdinal(word))
                return true;

            // A hyphenated word is a COMPOUND of its parts, and hunspell accepts one exactly when it
            // accepts every part: "around-the-clock" and "oh-oh" are words, "around-zzqx" is not. An
            // empty part is no part at all, so a leading or trailing hyphen (which the predictor's
            // edge trim has already taken off) would be fine either way.
            if (word.IndexOf('-') >= 0)
            {
                string[] parts = word.Split('-');
                bool complete = false;

                foreach (string part in parts)
                {
                    if (part.Length == 0)
                        continue;

                    complete = true;

                    if (!Contains(part))
                        return false;
                }

                if (complete)
                    return true;
            }

            return false;
        }

        /// <summary>A run of digits with the separators a number may carry, and nothing else.</summary>
        private static bool isNumeral(string word)
        {
            if (word.Length == 0 || !char.IsDigit(word[0]))
                return false;

            foreach (char c in word)
            {
                if (!char.IsDigit(c) && c != '.' && c != ',')
                    return false;
            }

            return true;
        }

        /// <summary>
        /// A run of digits plus the ordinal suffix that number takes: "11th", "12th" and "13th"
        /// end in th, every other number takes th unless its last digit is 1, 2 or 3.
        /// </summary>
        private static bool isOrdinal(string word)
        {
            if (word.Length < 3)
                return false;

            string suffix = word.Substring(word.Length - 2).ToLowerInvariant();
            string digits = word.Substring(0, word.Length - 2);

            if (!isNumeral(digits) || digits.Contains('.') || digits.Contains(','))
                return false;

            if (suffix != "st" && suffix != "nd" && suffix != "rd" && suffix != "th")
                return false;

            int value = digits.Length <= 9 ? int.Parse(digits, CultureInfo.InvariantCulture) : -1;
            int lastTwo = value < 0 ? -1 : value % 100;
            string expected = lastTwo >= 11 && lastTwo <= 13
                ? "th"
                : (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

            return suffix == expected;
        }

        /// <summary>The dictionary's own spell test, at the word's exact case.</summary>
        private bool spell(string word)
        {
            if (word.Length == 0)
                return false;

            if (isStem(word, string.Empty))
                return true;

            foreach (AffixRule suffix in suffixes)
            {
                if (!stripSuffix(word, suffix, out string withoutSuffix))
                    continue;

                if (isStem(withoutSuffix, suffix.Flag.ToString()))
                    return true;

                // Hunspell spells a doubly-inflected form through the rules' continuation classes:
                // the inner rule's continuation set has to carry the flag of the suffix already
                // stripped.
                foreach (AffixRule inner in suffixes)
                {
                    if (suffix.Continuation.IndexOf(inner.Flag) < 0)
                        continue;

                    if (!stripSuffix(withoutSuffix, inner, out string doublyStripped))
                        continue;

                    if (isStem(doublyStripped, new string(new[] { suffix.Flag, inner.Flag })))
                        return true;
                }

                foreach (AffixRule prefix in prefixes)
                {
                    if (!prefix.CrossProduct || !stripPrefix(withoutSuffix, prefix, out string stem))
                        continue;

                    if (isStem(stem, prefix.Flag.ToString() + suffix.Flag))
                        return true;
                }
            }

            foreach (AffixRule prefix in prefixes)
            {
                if (stripPrefix(word, prefix, out string stem) && isStem(stem, prefix.Flag.ToString()))
                    return true;
            }

            return false;
        }

        private bool isStem(string word, string requiredFlags) => stems.TryGetValue(word, out Stem stem) && !stem.OnlyInCompound && hasAll(stem.Flags, requiredFlags);

        /// <summary>
        /// Strips <paramref name="rule"/>'s addition off the end of <paramref name="word"/>, which
        /// leaves the stem the rule would have built it from; false when the rule does not fit, its
        /// stem condition does not hold, or the result would be the word itself.
        /// </summary>
        private static bool stripSuffix(string word, AffixRule rule, out string stem)
        {
            stem = string.Empty;

            string candidate;

            if (rule.Add.Length == 0)
            {
                // A rule that adds nothing produces the stem MINUS its strip, so its lookup is the
                // strip handed back.
                if (rule.Strip.Length == 0)
                    return false;

                candidate = word + rule.Strip;
            }
            else
            {
                if (word.Length < rule.Add.Length || !word.EndsWith(rule.Add, StringComparison.Ordinal))
                    return false;

                candidate = string.Concat(word.AsSpan(0, word.Length - rule.Add.Length), rule.Strip.AsSpan());
            }

            if (!matchesCondition(candidate, rule.Condition, atEnd: true))
                return false;

            stem = candidate;
            return true;
        }

        /// <summary>Strips <paramref name="rule"/>'s addition off the front of the word, as above.</summary>
        private static bool stripPrefix(string word, AffixRule rule, out string stem)
        {
            stem = string.Empty;

            if (rule.Add.Length == 0 || !word.StartsWith(rule.Add, StringComparison.Ordinal))
                return false;

            string candidate = string.Concat(rule.Strip.AsSpan(), word.AsSpan(rule.Add.Length));

            if (!matchesCondition(candidate, rule.Condition, atEnd: false))
                return false;

            stem = candidate;
            return true;
        }

        /// <summary>
        /// A hunspell affix condition, matched against the stem and anchored at the affix's end of
        /// it: a suffix's pattern reads the END of the stem, a prefix's the START. '.' matches any
        /// character, '[...]' is a character class and '[^...]' its negation, and a trailing '.'
        /// only marks the pattern's boundary.
        /// </summary>
        private static bool matchesCondition(string stem, string condition, bool atEnd)
        {
            if (condition.Length == 0)
                return true;

            List<ConditionElement> pattern = parseCondition(condition);

            // A condition of nothing but the terminator - a bare "." - is what most of the shipped
            // rules carry, and it means the rule applies to any stem at all.
            if (pattern.Count == 0)
                return true;

            if (stem.Length < pattern.Count)
                return false;

            for (int i = 0; i < pattern.Count; i++)
            {
                char character = stem[atEnd ? stem.Length - pattern.Count + i : i];
                ConditionElement element = pattern[i];

                if (element.Any)
                    continue;

                bool inClass = element.Class != null && element.Class.IndexOf(char.ToLowerInvariant(character)) >= 0;

                if (element.Class != null ? element.Negated ? inClass : !inClass : char.ToLowerInvariant(element.Literal) != char.ToLowerInvariant(character))
                    return false;
            }

            return true;
        }

        private static List<ConditionElement> parseCondition(string condition)
        {
            var elements = new List<ConditionElement>(condition.Length);
            int index = 0;

            while (index < condition.Length)
            {
                char c = condition[index];

                if (c == '.')
                {
                    // A dot at the end is hunspell's pattern terminator rather than a wildcard.
                    if (index == condition.Length - 1)
                        break;

                    elements.Add(new ConditionElement('\0', true, null, false));
                    index++;
                    continue;
                }

                if (c == '[')
                {
                    int close = condition.IndexOf(']', index + 1);

                    if (close < 0)
                        return elements;

                    string body = condition.Substring(index + 1, close - index - 1);
                    bool negated = body.StartsWith('^');

                    if (negated)
                        body = body.Substring(1);

                    elements.Add(new ConditionElement('\0', false, body, negated));
                    index = close + 1;
                    continue;
                }

                elements.Add(new ConditionElement(c, false, null, false));
                index++;
            }

            return elements;
        }

        private static bool hasAll(string flags, string required)
        {
            foreach (char flag in required)
            {
                if (flags.IndexOf(flag) < 0)
                    return false;
            }

            return true;
        }

        private void readAff(string aff)
        {
            string[] lines = splitLines(aff);
            int index = 0;

            while (index < lines.Length)
            {
                string[] parts = partsOf(lines[index]);
                index++;

                if (parts.Length == 0 || parts[0].StartsWith('#'))
                    continue;

                switch (parts[0])
                {
                    case "ICONV":
                        index = readConversion(lines, index, parts);
                        break;

                    case "ONLYINCOMPOUND":
                        if (parts.Length >= 2 && parts[1].Length > 0)
                            onlyInCompound.Add(parts[1][0]);
                        break;

                    case "PFX":
                    case "SFX":
                        index = readAffixBlock(lines, index, parts);
                        break;
                }
            }
        }

        /// <summary>An ICONV table: the directive carrying its own count, then that many pair lines.</summary>
        private int readConversion(string[] lines, int index, string[] header)
        {
            if (header.Length < 2 || !int.TryParse(header[1], out int count))
                return index;

            for (int i = 0; i < count && index < lines.Length; i++)
            {
                string[] parts = partsOf(lines[index]);
                index++;

                if (parts.Length >= 3)
                    conversions.Add((parts[1], parts[2]));
            }

            return index;
        }

        /// <summary>
        /// One affix block: the header carries the flag, the cross-product switch and the rule
        /// count, and each rule carries strip, add (with optional continuation flags after a slash)
        /// and the stem condition.
        /// </summary>
        private int readAffixBlock(string[] lines, int index, string[] header)
        {
            if (header.Length < 4 || header[1].Length == 0 || !int.TryParse(header[3], out int count))
                return index;

            bool prefix = header[0] == "PFX";
            char flag = header[1][0];
            bool crossProduct = header[2] == "Y";

            for (int i = 0; i < count && index < lines.Length; i++)
            {
                string[] parts = partsOf(lines[index]);
                index++;

                if (parts.Length < 5 || parts[0] != header[0])
                    continue;

                string add = parts[3];
                string continuation = string.Empty;
                int slash = add.IndexOf('/');

                if (slash >= 0)
                {
                    continuation = add.Substring(slash + 1);
                    add = add.Substring(0, slash);
                }

                // hunspell writes "0" where it means the empty string, in the strip and the addition
                // alike; a literal zero can never be part of an affix, so the mapping is safe.
                var rule = new AffixRule(flag, prefix, unzero(parts[2]), unzero(add), continuation, parts[4], crossProduct);

                if (prefix)
                    prefixes.Add(rule);
                else
                    suffixes.Add(rule);
            }

            return index;
        }

        private static string unzero(string value) => value == "0" ? string.Empty : value;

        private void readDic(string dic)
        {
            string[] lines = splitLines(dic);

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int end = line.AsSpan().IndexOfAny(word_terminators);

                if (end >= 0)
                    line = line.Substring(0, end);

                if (line.Length == 0)
                    continue;

                int slash = line.IndexOf('/');
                string word = slash >= 0 ? line.Substring(0, slash) : line;
                string flags = slash >= 0 ? line.Substring(slash + 1) : string.Empty;

                if (word.Length == 0)
                    continue;

                bool barred = false;

                foreach (char flag in flags)
                {
                    if (onlyInCompound.Contains(flag))
                    {
                        barred = true;
                        break;
                    }
                }

                stems[word] = new Stem(flags, barred);
            }
        }

        private static string[] splitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static string[] partsOf(string line)
        {
            string trimmed = line.Trim();

            return trimmed.Length == 0 ? Array.Empty<string>() : trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
