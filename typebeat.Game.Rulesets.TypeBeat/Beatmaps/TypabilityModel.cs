// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace typebeat.Game.Rulesets.TypeBeat.Beatmaps
{
    /// <summary>
    /// The Typability Index COMPUTED in the client, which is what lets a map the game has never seen
    /// be scored at all: <see cref="TypabilityIndex"/> can only look a line up in the table bundled
    /// with the catalogue, so an imported map's own lyrics used to read as unscored (z = 0).
    ///
    /// <para>WHERE THE ARITHMETIC COMES FROM. The authors' own predictor calculation (Emily
    /// Williams, <c>The-Typability-Index-main/scripts/2-calculate-predictors.Rmd</c>), the regression
    /// the Typability Lab refits without its keystroke term, and the aids both read: the Gutenberg
    /// bigram frequencies, the keyboard geometry, the top-1000 lemma list, quanteda/nsyllable's
    /// syllable dictionary and the four hunspell dictionaries. The tables are emitted into this
    /// project's resources by <c>tools/star-rating-sandbox/sync-typability-model.R</c>, which is the
    /// single place any of them is derived; this file only reads them.</para>
    ///
    /// <para>THE SEVEN PREDICTORS, and the study's own sentence as the unit:
    /// <c>highFreqWordProp</c> (share of words in the top-1000 lemmas, after trimming edge
    /// punctuation and folding possessives), <c>lowercasePropNonSpace</c>, <c>meanSyllsPerWord</c>
    /// (quanteda's word tokens, nsyllable's dictionary, vowel runs for a word it does not carry),
    /// <c>symbolsPropNonSpace</c>, <c>biFreqMean</c> (the mean Gutenberg frequency of the line's
    /// within-word character bigrams, unmatched ones dropped), <c>propCharsNonDictWords</c> (the
    /// share of characters in words no dictionary recognises) and <c>propRightHand</c>. The
    /// keystroke feature the published model also carries is DELIBERATELY absent: type!beat's own
    /// difficulty model already charges for every keypress, so keeping it would double-charge the
    /// same cost (see the lab's README).</para>
    ///
    /// <para>COVERAGE is the same figure the table's rows carry: <c>1 - propCharsNonDictWords</c>,
    /// how much of the line is made of dictionary words. A low-coverage line is not really English,
    /// and the index was fitted on English, so <see cref="TypabilityIndex"/> leaves it out.</para>
    /// </summary>
    internal static class TypabilityModel
    {
        private const string resource_prefix = "typebeat.Game.Rulesets.TypeBeat.Resources.typability.";

        /// <summary>The seven predictors of the keystroke-free variant, in the order the model lists them.</summary>
        internal readonly record struct Features(
            double Characters,
            double HighFreqWordProp,
            double LowercasePropNonSpace,
            double MeanSyllsPerWord,
            double SymbolsPropNonSpace,
            double BiFreqMean,
            double PropCharsNonDictWords,
            double PropRightHand);

        /// <summary>What the regression made of a line: its z and the coverage the gate reads.</summary>
        internal readonly record struct Reading(double Z, double Coverage, Features Features);

        private static readonly Lazy<Data> data = new Lazy<Data>(Data.load);

        /// <summary>The four dictionaries, loaded on first use (they are the bulk of the data).</summary>
        private static readonly Lazy<HunspellDictionary[]> dictionaries = new Lazy<HunspellDictionary[]>(() => new[]
        {
            loadDictionary("en_US"),
            loadDictionary("en_GB"),
            loadDictionary("en_CA"),
            loadDictionary("en_AU"),
        });

        /// <summary>
        /// Scores one line: its z, its coverage, and the predictors they were built from. False when
        /// the study's model cannot score the line at all (no words, no bigrams it has frequencies
        /// for), which the caller reads exactly as the table would read an absent row.
        /// </summary>
        internal static bool TryScore(string line, out Reading reading)
        {
            reading = default;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            string sentence = standardiseSymbols(line);
            Features features;

            try
            {
                features = extract(sentence);
            }
            catch (Exception)
            {
                return false;
            }

            double z = data.Value.predict(features);
            double coverage = 1 - features.PropCharsNonDictWords;

            if (double.IsNaN(coverage) || double.IsInfinity(coverage))
                coverage = 0;

            if (double.IsNaN(z) || double.IsInfinity(z))
                return false;

            reading = new Reading(z, Math.Min(1, Math.Max(0, coverage)), features);
            return true;
        }

        /// <summary>
        /// The authors' <c>standardiseSymbols()</c>: the typographic variants a source text may
        /// carry, folded onto the ASCII forms the rest of the calculation (and the dictionaries)
        /// assume. It runs before ANY count, so a curly apostrophe is one character either way.
        /// </summary>
        internal static string standardiseSymbols(string sentence)
        {
            var builder = new StringBuilder(sentence.Length);

            foreach (char c in sentence)
            {
                switch (c)
                {
                    case '\u00A0' or '\u202F':
                        builder.Append(' ');
                        break;
                    case '\u2013' or '\u2014' or '\u2212':
                        builder.Append('-');
                        break;
                    case '\u2018' or '\u2019' or '\u02BC':
                        builder.Append('\'');
                        break;
                    case '\u201C' or '\u201D' or '\u00AB' or '\u00BB':
                        builder.Append('"');
                        break;
                    case '\u2026':
                        builder.Append("...");
                        break;
                    case '\u00D7':
                        builder.Append('x');
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>The seven predictors of one standardised sentence.</summary>
        private static Features extract(string sentence)
        {
            int characters = countCharacters(sentence);
            int uppercase = 0, lowercase = 0, numbers = 0, spaces = 0;

            foreach (char c in sentence)
            {
                if (c >= 'A' && c <= 'Z')
                    uppercase++;
                else if (c >= 'a' && c <= 'z')
                    lowercase++;
                else if (c >= '0' && c <= '9')
                    numbers++;
                else if (c == ' ')
                    spaces++;
            }

            int letters = uppercase + lowercase;
            int nonSpace = characters - spaces;
            int symbols = characters - (letters + numbers + spaces);

            List<(string Word, string Trimmed)> words = splitWords(sentence);

            double highFreqWordProp = words.Count > 0
                ? words.Count(w => data.Value.top1000.Contains(stripPossessive(w.Word))) / (double)words.Count
                : double.NaN;

            double propCharsNonDictWords = nonDictCharacterShare(words, characters);

            return new Features(
                characters,
                highFreqWordProp,
                nonSpace > 0 ? lowercase / (double)nonSpace : double.NaN,
                meanSyllablesPerWord(sentence),
                nonSpace > 0 ? symbols / (double)nonSpace : double.NaN,
                meanBigramFrequency(sentence),
                propCharsNonDictWords,
                rightHandProportion(sentence));
        }

        /// <summary>
        /// The sentence's words as the two word-frequency predictors read them: split on spaces,
        /// then on commas (a comma is not always followed by one), then stripped of the punctuation
        /// that clings to either end, and lowercased. The TRIMMED form is kept because the
        /// spell-check predictor counts its characters, while the lemma lookup strips a possessive
        /// off it.
        /// </summary>
        private static List<(string Word, string Trimmed)> splitWords(string sentence)
        {
            var words = new List<(string, string)>();

            foreach (string piece in sentence.Split(' '))
            {
                foreach (string part in piece.Split(','))
                {
                    string trimmed = trimEdgePunctuation(part);

                    if (trimmed.Length > 0)
                        words.Add((trimmed.ToLowerInvariant(), trimmed));
                }
            }

            return words;
        }

        /// <summary>
        /// The authors' edge trim, which is an ASCII class rather than a Unicode one: everything
        /// that is not [A-Za-z0-9] comes off either end, so "cafe\u0301" and "caf\u00e9" both lose
        /// their accent here, exactly as they do for the R pipeline.
        /// </summary>
        private static string trimEdgePunctuation(string word)
        {
            int start = 0, end = word.Length;

            while (start < end && !isAsciiAlphanumeric(word[start]))
                start++;

            while (end > start && !isAsciiAlphanumeric(word[end - 1]))
                end--;

            return word.Substring(start, end - start);
        }

        private static bool isAsciiAlphanumeric(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');

        /// <summary>
        /// The authors' possessive fold for the lemma lookup: the trailing "'s", "'m" or "'d" comes
        /// off, or the trailing "'re", "'ve", "'ll" or "n't".
        /// </summary>
        private static string stripPossessive(string word)
        {
            if (word.Length > 2 && (word.EndsWith("'s", StringComparison.Ordinal) || word.EndsWith("'m", StringComparison.Ordinal) || word.EndsWith("'d", StringComparison.Ordinal)))
                return word.Substring(0, word.Length - 2);

            if (word.Length > 3 && (word.EndsWith("'re", StringComparison.Ordinal) || word.EndsWith("'ve", StringComparison.Ordinal)
                                    || word.EndsWith("'ll", StringComparison.Ordinal) || word.EndsWith("n't", StringComparison.Ordinal)))
                return word.Substring(0, word.Length - 3);

            return word;
        }

        /// <summary>
        /// The share of the sentence's characters that sit in words NO dictionary recognises, which
        /// is the spell-check predictor and the inversion of the coverage the caller gates on.
        /// </summary>
        private static double nonDictCharacterShare(List<(string Word, string Trimmed)> words, int characters)
        {
            double unknownCharacters = 0;

            foreach ((string word, string trimmed) in words)
            {
                _ = word;

                if (!anyDictionaryKnows(trimmed))
                    unknownCharacters += countCharacters(trimmed);
            }

            return characters > 0 ? unknownCharacters / characters : double.NaN;
        }

        /// <summary>
        /// Whether ANY of the four dictionaries recognises a word, which is the authors' own test:
        /// they ask the US, UK, Canadian and Australian dictionaries in turn and count a word known
        /// as soon as one of them claims it. The word arrives with its edge punctuation already
        /// trimmed, so a trailing apostrophe is not part of it.
        /// </summary>
        internal static bool anyDictionaryKnows(string word) => dictionaries.Value.Any(dictionary => dictionary.Contains(word));

        /// <summary>
        /// The mean Gutenberg frequency of the line's character bigrams. The authors build the
        /// bigrams from the LOWER-CASED sentence with its spaces removed, so a bigram spans the two
        /// words a space separated; a bigram the corpus does not carry is dropped rather than
        /// counted as zero, so a line of no known bigrams has no value at all.
        /// </summary>
        private static double meanBigramFrequency(string sentence)
        {
            string stripped = sentence.ToLowerInvariant().Replace(" ", string.Empty);

            if (stripped.Length < 2)
                return double.NaN;

            double total = 0;
            int matched = 0;

            for (int i = 0; i + 1 < stripped.Length; i++)
            {
                if (data.Value.bigrams.TryGetValue(stripped.Substring(i, 2), out double frequency))
                {
                    total += frequency;
                    matched++;
                }
            }

            return matched > 0 ? total / matched : double.NaN;
        }

        /// <summary>
        /// Mean syllables per WORD as quanteda's readability statistic reads it: the sentence is
        /// tokenised into words, and each word contributes its nsyllable entry, or its vowel runs
        /// when the dictionary does not carry it, or one when it has no vowel at all.
        /// </summary>
        private static double meanSyllablesPerWord(string sentence)
        {
            List<string> tokens = wordTokens(sentence);

            if (tokens.Count == 0)
                return double.NaN;

            double total = 0;

            foreach (string token in tokens)
            {
                string lower = token.ToLowerInvariant();

                if (data.Value.syllables.TryGetValue(lower, out int syllables))
                {
                    total += syllables;
                    continue;
                }

                int runs = vowelRuns(token);
                total += runs > 0 ? runs : 1;
            }

            return total / tokens.Count;
        }

        /// <summary>
        /// quanteda's word tokens as the readability statistic takes them: runs of letters and
        /// digits, with an apostrophe or a decimal comma/point allowed inside, hyphens splitting a
        /// word in two and a token that is nothing but punctuation disappearing. Backing vocals and
        /// odd symbols therefore cost the sentence a word or two, exactly as they do for the authors.
        /// </summary>
        internal static List<string> wordTokens(string sentence)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();

            for (int i = 0; i < sentence.Length; i++)
            {
                char c = sentence[i];

                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                    continue;
                }

                char next = i + 1 < sentence.Length ? sentence[i + 1] : '\0';
                bool between = current.Length > 0 && char.IsLetterOrDigit(next);

                // UAX #29's word rules, as far as English lyric text can tell them apart: an
                // apostrophe joins letters on both sides, a point joins letters or digits on both
                // sides, and a comma or colon joins digits on both sides.
                bool joins = between && ((c == '\'' || c == '\u2019')
                                         || (c == '.' && char.IsLetterOrDigit(current[current.Length - 1]))
                                         || ((c == ',' || c == ':') && char.IsDigit(current[current.Length - 1]) && char.IsDigit(next)));

                if (joins)
                {
                    current.Append(c);
                    continue;
                }

                flush();
            }

            flush();
            return tokens;

            void flush()
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
        }

        /// <summary>How many vowel runs a word has, which is the nsyllable fallback for a word it does not carry.</summary>
        private static int vowelRuns(string word)
        {
            int runs = 0;
            bool previous = false;

            foreach (char c in word)
            {
                bool vowel = "aeiouy".IndexOf(char.ToLowerInvariant(c)) >= 0;

                if (vowel && !previous)
                    runs++;

                previous = vowel;
            }

            return runs;
        }

        /// <summary>
        /// The share of the sentence's non-space characters the keyboard's own geometry puts on the
        /// right hand, which is what the finger-alternation predictor prices. A character the
        /// keyboard table does not carry (an accented letter, a symbol) counts towards the total but
        /// towards neither hand, exactly as the authors' join leaves it.
        /// </summary>
        private static double rightHandProportion(string sentence)
        {
            int total = 0, right = 0;

            foreach (char c in sentence)
            {
                if (c == ' ')
                    continue;

                total++;

                if (data.Value.keyboard.TryGetValue(char.ToLowerInvariant(c), out string? hand) && hand == "right")
                    right++;
            }

            return total > 0 ? right / (double)total : double.NaN;
        }

        /// <summary>
        /// R's <c>nchar()</c>: characters rather than UTF-16 code units, so an astral symbol counts
        /// once. Every proportion in the model is taken over this count.
        /// </summary>
        internal static int countCharacters(string text)
        {
            int count = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i++;

                count++;
            }

            return count;
        }

        private static HunspellDictionary loadDictionary(string language)
            => HunspellDictionary.Load(resource($"{resource_prefix}dictionaries.{language}.aff"), resource($"{resource_prefix}dictionaries.{language}.dic"));

        /// <summary>Reads one embedded resource, by its name's own tail so the folder shape is not load-bearing.</summary>
        private static string resource(string name)
        {
            Assembly assembly = typeof(TypabilityModel).GetTypeInfo().Assembly;
            string? match = assembly.GetManifestResourceNames().FirstOrDefault(candidate => candidate.EndsWith(name, StringComparison.Ordinal));

            if (match == null)
                throw new FileNotFoundException($"The typability model's resource '{name}' is missing; regenerate the tables with sync-typability-model.R.");

            using Stream stream = assembly.GetManifestResourceStream(match)
                                  ?? throw new FileNotFoundException($"The typability model's resource '{name}' cannot be read.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>The tables and coefficients the features and the regression read.</summary>
        private sealed class Data
        {
            private readonly Dictionary<string, double> coefficients = new Dictionary<string, double>(StringComparer.Ordinal);
            private double intercept;
            private readonly double[] coefficientOrder;

            public readonly HashSet<string> top1000 = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, double> bigrams = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<char, string> keyboard = new Dictionary<char, string>();
            public readonly Dictionary<string, int> syllables = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>The predictors the regression reads, in coefficient order.</summary>
            public static readonly string[] predictorOrder =
            {
                "highFreqWordProp",
                "lowercasePropNonSpace",
                "meanSyllsPerWord",
                "symbolsPropNonSpace",
                "biFreqMean",
                "propCharsNonDictWords",
                "propRightHand",
            };

            private Data()
            {
                coefficientOrder = new double[predictorOrder.Length];
                intercept = 0;
            }

            public static Data load()
            {
                var loaded = new Data();

                foreach (string line in lines($"{resource_prefix}model.tsv"))
                {
                    string[] parts = line.Split('\t');

                    if (parts.Length < 2 || parts[0].StartsWith('#') || parts[0] == "term")
                        continue;

                    if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        loaded.coefficients[parts[0]] = value;
                }

                for (int i = 0; i < predictorOrder.Length; i++)
                {
                    if (!loaded.coefficients.TryGetValue(predictorOrder[i], out loaded.coefficientOrder[i]))
                        throw new InvalidDataException($"The typability model has no coefficient for {predictorOrder[i]}.");
                }

                if (!loaded.coefficients.TryGetValue("(Intercept)", out loaded.intercept))
                    throw new InvalidDataException("The typability model has no intercept.");

                foreach (string line in lines($"{resource_prefix}bigrams.tsv"))
                {
                    string[] parts = line.Split('\t');

                    if (parts.Length >= 2 && !parts[0].StartsWith('#')
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double frequency))
                        loaded.bigrams[parts[0]] = frequency;
                }

                foreach (string line in lines($"{resource_prefix}keyboard.tsv"))
                {
                    string[] parts = line.Split('\t');

                    if (parts.Length >= 2 && parts[0].Length > 0 && !parts[0].StartsWith('#'))
                        loaded.keyboard[parts[0][0]] = parts[1];
                }

                foreach (string line in lines($"{resource_prefix}top1000.txt"))
                {
                    string word = line.Trim();

                    if (word.Length > 0 && !word.StartsWith('#'))
                        loaded.top1000.Add(word);
                }

                foreach (string line in lines($"{resource_prefix}syllables.tsv"))
                {
                    int tab = line.IndexOf('\t');

                    if (tab > 0 && !line.StartsWith('#')
                        && int.TryParse(line.AsSpan(tab + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                        loaded.syllables[line.Substring(0, tab)] = count;
                }

                return loaded;
            }

            public double predict(Features features)
            {
                double[] values =
                {
                    features.HighFreqWordProp,
                    features.LowercasePropNonSpace,
                    features.MeanSyllsPerWord,
                    features.SymbolsPropNonSpace,
                    features.BiFreqMean,
                    features.PropCharsNonDictWords,
                    features.PropRightHand,
                };

                double z = intercept;

                for (int i = 0; i < values.Length; i++)
                    z += coefficientOrder[i] * values[i];

                return z;
            }

            private static IEnumerable<string> lines(string name)
            {
                return resource(name).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            }
        }
    }
}
