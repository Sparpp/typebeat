// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// The in-client Typability Index, pinned against the two readings it has to reproduce.
//
// The Index used to be a TABLE: typability-lines.tsv carries a score for every lyric line of the
// bundled catalogue and nothing else, so a map the player imported read as unscored (z = 0).
// TypabilityModel computes the same score from the authors' own predictor calculation instead.
//
// Two oracles, because they catch different mistakes:
//
//   * The authors' published predictor table (output/processed_data/
//     all_dhakal_sentences_with_predictors.txt, 1525 sentences) carries all thirty of their
//     predictors, so a mismatch names the FEATURE that drifted rather than only its consequence.
//   * typability.json (3,852 bundled lyric lines, from the Typability Lab's own scoring path)
//     carries the z the model consumes, so it is the end-to-end pin - features AND coefficients,
//     over the real text the game rates.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypabilityModelTest
    {
        private static string repoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "typebeat.sln")))
                dir = dir.Parent;

            Assert.That(dir, Is.Not.Null, "could not locate the repository root (no typebeat.sln above the test output directory)");
            return dir!.FullName;
        }

        private static string predictorTablePath()
            => SandboxFixtures.RequireTypabilityIndex(Path.Combine(repoRoot(), "The-Typability-Index-main"),
                "output", "processed_data", "all_dhakal_sentences_with_predictors.txt");

        private static string catalogueScoresPath()
            => SandboxFixtures.RequireSandbox(Path.Combine(repoRoot(), "tools", "star-rating-sandbox"), "typability.json");

        /// <summary>
        /// The authors' own predictor table, as (header, row) tab-separated pairs. The file is UTF-8
        /// and every field is quoted by nobody: a sentence's own tabs would break it, and none has one.
        /// </summary>
        private static (string[] Header, List<string[]> Rows) readTable()
        {
            string[] lines = File.ReadAllLines(predictorTablePath());
            var rows = new List<string[]>(lines.Length - 1);

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                    rows.Add(lines[i].Split('\t'));
            }

            return (lines[0].Split('\t'), rows);
        }

        private static int column(string[] header, string name)
        {
            int index = Array.IndexOf(header, name);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"the authors' table has no column {name}");
            return index;
        }

        private static double value(string[] row, int column)
        {
            Assert.That(column, Is.LessThan(row.Length), "the row is short");
            return double.Parse(row[column], CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// EVERY FEATURE, against the authors' own numbers. A failure here names the predictor that
        /// drifted: the word lists, the bigram table, the syllable dictionary, the keyboard geometry,
        /// or the four hunspell dictionaries.
        /// </summary>
        [Test]
        public void EveryPredictorMatchesTheAuthorsOwnTable()
        {
            (string[] header, List<string[]> rows) = readTable();
            int sentence = column(header, "sentence");
            int characters = column(header, "numChars");
            int highFreq = column(header, "highFreqWordProp");
            int lowercase = column(header, "lowercasePropNonSpace");
            int syllables = column(header, "meanSyllsPerWord");
            int symbols = column(header, "symbolsPropNonSpace");
            int bigrams = column(header, "biFreqMean");
            int nonDict = column(header, "propCharsNonDictWords");
            int rightHand = column(header, "propRightHand");

            var worst = new Dictionary<string, (double Deviation, string Sentence)>();
            int scored = 0;

            void compare(string name, double actual, double expected, string text)
            {
                double deviation = Math.Abs(actual - expected);

                if (!worst.TryGetValue(name, out (double Deviation, string Sentence) current) || deviation > current.Deviation)
                    worst[name] = (deviation, text);
            }

            foreach (string[] row in rows)
            {
                string text = row[sentence];

                if (!TypabilityModel.TryScore(text, out TypabilityModel.Reading reading))
                {
                    // The authors' pipeline leaves a line it cannot score out of the fitted frame, so
                    // a row without a finite z is not a row this model has to answer.
                    continue;
                }

                scored++;
                TypabilityModel.Features features = reading.Features;

                compare("characters", features.Characters, value(row, characters), text);
                compare("highFreqWordProp", features.HighFreqWordProp, value(row, highFreq), text);
                compare("lowercasePropNonSpace", features.LowercasePropNonSpace, value(row, lowercase), text);
                compare("meanSyllsPerWord", features.MeanSyllsPerWord, value(row, syllables), text);
                compare("symbolsPropNonSpace", features.SymbolsPropNonSpace, value(row, symbols), text);
                compare("biFreqMean", features.BiFreqMean, value(row, bigrams), text);
                compare("propCharsNonDictWords", features.PropCharsNonDictWords, value(row, nonDict), text);
                compare("propRightHand", features.PropRightHand, value(row, rightHand), text);
            }

            foreach ((string name, (double deviation, string text)) in worst)
                TestContext.WriteLine($"{name,-24} worst {deviation:0.00000000e+00}  {text}");

            Assert.That(scored, Is.GreaterThan(1500), $"only {scored} of the authors' sentences were scoreable");

            // The counts and the proportions taken straight off the text agree exactly; the two
            // predictors that go through a data table (the syllable dictionary and the bigram
            // frequencies) agree to the precision the table carries, and the dictionary predictor is
            // allowed the slack the Typability Lab's own validation reports for it (the shipped
            // dictionaries and the authors' differ about one word).
            assertWorst(worst, "characters", 0);
            assertWorst(worst, "highFreqWordProp", 1e-9);
            assertWorst(worst, "lowercasePropNonSpace", 1e-9);
            assertWorst(worst, "symbolsPropNonSpace", 1e-9);
            assertWorst(worst, "propRightHand", 1e-9);
            assertWorst(worst, "meanSyllsPerWord", 1e-6);
            assertWorst(worst, "biFreqMean", 1e-3);
            assertWorst(worst, "propCharsNonDictWords", 0.05);
        }

        /// <summary>
        /// The Z-SCORE, over every lyric line of the bundled catalogue, against the scores the
        /// sandbox already ships. This is the end-to-end pin: the features above and the fitted
        /// coefficients together, on the text the game rates.
        /// </summary>
        [Test]
        public void EveryBundledLyricLineScoresWhatTheCatalogueSays()
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(catalogueScoresPath()));
            JsonElement lines = document.RootElement.GetProperty("lines");

            double worstZ = 0, worstCoverage = 0, worstCharacters = 0;
            string worstZSentence = string.Empty;
            int compared = 0, unscored = 0;

            foreach (JsonProperty property in lines.EnumerateObject())
            {
                string text = property.Name;
                JsonElement expected = property.Value;
                JsonElement zElement = expected.GetProperty("z");

                if (zElement.ValueKind != JsonValueKind.Number)
                {
                    // The lab's own JSON writes a z it could not compute as the string "NaN" (three
                    // one-letter lines). That is exactly the state this model reports by refusing to
                    // score a line, so the two agree on WHICH lines have no score.
                    Assert.That(TypabilityModel.TryScore(text, out _), Is.False, $"{text}: the lab could not score it, so the model must not claim to");
                    unscored++;
                    continue;
                }

                double wantZ = zElement.GetDouble();
                double wantCoverage = expected.GetProperty("coverage").GetDouble();
                double wantCharacters = expected.GetProperty("characters").GetDouble();

                if (!TypabilityModel.TryScore(text, out TypabilityModel.Reading reading))
                {
                    unscored++;
                    continue;
                }

                compared++;
                double zDeviation = Math.Abs(reading.Z - wantZ);

                if (zDeviation > worstZ)
                {
                    worstZ = zDeviation;
                    worstZSentence = text;
                }

                worstCoverage = Math.Max(worstCoverage, Math.Abs(reading.Coverage - wantCoverage));
                worstCharacters = Math.Max(worstCharacters, Math.Abs(reading.Features.Characters - wantCharacters));
            }

            TestContext.WriteLine($"compared {compared} lines, unscored {unscored}");
            TestContext.WriteLine($"worst z deviation {worstZ:0.00000000e+00}  {worstZSentence}");
            TestContext.WriteLine($"worst coverage deviation {worstCoverage:0.00000000e+00}");
            TestContext.WriteLine($"worst characters deviation {worstCharacters:0.00000000e+00}");

            Assert.That(compared, Is.GreaterThan(3800), $"only {compared} of the catalogue's lines were scoreable");
            // The pins are two orders tighter than the one difference the lab itself reports: the
            // in-client dictionaries and the authors' own differ about a single word of one
            // non-English line, which moves that line's coverage by 5e-5 and its z by 5e-5. A
            // modelling mistake moves these figures in the hundredths.
            Assert.That(worstCharacters, Is.EqualTo(0).Within(1e-9), "the character count has to be the same figure the lab counted");
            Assert.That(worstCoverage, Is.LessThanOrEqualTo(2e-4), "coverage is 1 - the non-dictionary share");
            Assert.That(worstZ, Is.LessThanOrEqualTo(2e-4), "the z has to reproduce the lab's own score");
        }

        private static void assertWorst(Dictionary<string, (double Deviation, string Sentence)> worst, string name, double tolerance)
        {
            Assert.That(worst.TryGetValue(name, out (double Deviation, string Sentence) found), Is.True, $"no {name} was compared");
            Assert.That(found.Deviation, Is.LessThanOrEqualTo(tolerance),
                $"{name} is off by {found.Deviation:0.00000000e+00} on \"{found.Sentence}\"");
        }

        /// <summary>
        /// The affix engine answers the way the four dictionaries do, on a spread of forms that
        /// exercises each part of it: the stem list, an inflected form built through a suffix rule, a
        /// possessive, case handling, and two forms no English dictionary carries.
        /// </summary>
        [Test]
        public void TheDictionariesReadTheFormsTheyAlwaysHave()
        {
            var cases = new (string Word, bool Known)[]
            {
                ("word", true),
                ("words", true),
                ("walked", true),
                ("walking", true),
                ("happier", true),
                ("word's", true),
                ("don't", true),
                ("DON'T", true),
                ("Christmas", true),
                ("I'll", true),
                ("it's", true),
                ("gonna", true),
                ("r", true),
                ("hi", true),
                ("couch", true),
                ("colour", true),
                ("color", true),
                ("centre", true),
                ("realise", true),
                ("around-the-clock", true),
                ("oh-oh", true),
                ("e-e-e", true),
                ("2nd", true),
                ("21st", true),
                ("11th", true),
                ("4th", true),
                ("1.5", true),
                ("1,000", true),
                ("1999", true),
                ("ohhh", false),
                ("woooords", false),
                ("ppl", false),
                ("zzqx", false),
                ("zzqx-zzqx", false),
                ("around-zzqx", false),
                ("yyyy", false),
                ("aaaa", false),
                ("1th", false),
                ("21th", false),
                ("13st", false),
                ("24/7", false),
            };

            var failures = new List<string>();

            foreach ((string word, bool known) in cases)
            {
                if (TypabilityModel.anyDictionaryKnows(word) != known)
                    failures.Add(known ? $"{word}: expected the dictionaries to know it" : $"{word}: expected no dictionary to know it");
            }

            Assert.That(failures, Is.Empty, string.Join("\n", failures));

            // The cases above include hunspell's own NUMERAL grammar, which is what the dictionaries
            // reach through their compound rules: any run of digits is a word, a number carries only
            // the ordinal suffix English gives it ("21st" yes, "21th" no), and neither a slash nor a
            // stray letter is part of one.
        }

        /// <summary>
        /// The wiring: a line the shipped table carries is answered FROM the table, and a line it does
        /// not carry is answered by the computed index - which is what the game ships, so a map the
        /// catalogue never held is scored rather than read as z = 0.
        /// </summary>
        [Test]
        public void TheTableWinsAndTheComputedIndexAnswersTheRest()
        {
            Assert.That(TypabilityIndex.compute_unlisted_lines, Is.True, "the game scores a line the table does not carry");

            const string listed = "i was trying to find a way to kill time";

            Assert.That(TypabilityIndex.TryScore(listed, out TypabilityIndex.Score fromTable, computeUnlisted: false), Is.True);
            Assert.That(TypabilityIndex.TryScore(listed, out TypabilityIndex.Score withComputed, computeUnlisted: true), Is.True);
            Assert.That(withComputed.Z, Is.EqualTo(fromTable.Z), "a row the table carries has to win, bit for bit");
            Assert.That(withComputed.Coverage, Is.EqualTo(fromTable.Coverage));

            // Real English, so the index scores it, but nothing a lyric line in the bundle holds.
            const string unlisted = "The quick brown fox jumped over an idle dog.";

            Assert.That(TypabilityIndex.TryScore(unlisted, out _, computeUnlisted: false), Is.False, "the table cannot carry this one");
            Assert.That(TypabilityIndex.TryScore(unlisted, out TypabilityIndex.Score computed, computeUnlisted: true), Is.True, "the computed index has to answer it");
            Assert.That(TypabilityModel.TryScore(unlisted, out TypabilityModel.Reading reading), Is.True);
            Assert.That(computed.Z, Is.EqualTo(reading.Z), "the switch's answer is the model's own");
            Assert.That(computed.Coverage, Is.EqualTo(reading.Coverage));
        }
    }
}
