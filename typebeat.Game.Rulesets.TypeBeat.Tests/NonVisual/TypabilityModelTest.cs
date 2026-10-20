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
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class TypabilityModelTest
    {
        [TestCase(-9, -5)]
        [TestCase(-5, -5)]
        [TestCase(-4.49, -4.49)]
        [TestCase(-3, -3)]
        [TestCase(0, 0)]
        [TestCase(3, 3)]
        [TestCase(9, 3)]
        public void TypabilityContributionKeepsTheUpperBoundAndExtendsTheLowerBound(double raw, double expected)
        {
            Assert.That(TypabilityIndex.ClampZ(raw), Is.EqualTo(expected));
        }

        [TestCase("F. B. I.", "fbi")]
        [TestCase("F. (F) B. (B) I. (I)", "ffbbii")]
        [TestCase("C. (C) I. (I) A. (A)", "cciiaa")]
        [TestCase("K. (K) G. (G) B. (B)", "kkggbb")]
        public void PunctuationSeparatedLettersKeepTheirAuthoredDifficulty(string authored, string underlying)
        {
            Assert.That(TypabilityModel.TryScore(authored, out TypabilityModel.Reading reading), Is.True);
            Assert.That(TypabilityModel.TryScore(underlying, out TypabilityModel.Reading letters), Is.True);
            Assert.That(reading.Features.BiFreqMean, Is.EqualTo(letters.Features.BiFreqMean));
            Assert.That(reading.Features.LowercasePropNonSpace, Is.Zero, "capitalisation still comes from the authored text");
            Assert.That(reading.Features.SymbolsPropNonSpace, Is.GreaterThan(0), "punctuation still contributes to difficulty");
            Assert.That(reading.Z, Is.LessThan(letters.Z), "the capitals and marks must not be priced as plain letters");
            Assert.That(TypabilityIndex.TryScore(authored, out _), Is.True, "the live index must accept the estimate");
        }

        [TestCase("() [] !!!")]
        [TestCase("(F)")]
        [TestCase("123 456")]
        [TestCase("zzqx wvvt")]
        public void TheFallbackDoesNotInventScoresWithoutUsableText(string text)
        {
            Assert.That(TypabilityIndex.TryScore(text, out _), Is.False);
        }

        [Test]
        public void LiterateAcronymLinesContributeTypabilityToTheRating()
        {
            const string text = "F. (F) B. (B) I. (I)";
            var line = new LyricLine
            {
                RawText = text,
                StartTime = 0,
                EndTime = 4500,
                SingEndTime = 4250,
                Units = text.Split(' ').Select((token, i) => new TimedUnit
                {
                    Text = token,
                    StartTime = i * 750,
                    EndTime = i * 750 + 500,
                }).ToArray(),
            };

            Assert.That(TypabilityIndex.TryScore(text, out TypabilityIndex.Score score), Is.True);
            Assert.That(score.Z, Is.InRange(-5, -3), "this acronym must exercise the extended contribution range");
            var rating = LyricDifficulty.ComputeDetail(new[] { line }, literate: true);

            Assert.That(rating.TypabilityApplied, Is.True);
            Assert.That(rating.TypabilityMeanZ, Is.EqualTo(TypabilityIndex.ClampZ(score.Z)).Within(1e-9));
            Assert.That(rating.TypabilityMeanZ, Is.EqualTo(score.Z).Within(1e-9), "the previous -3 floor must no longer truncate this score");
            Assert.That(rating.Cells, Is.EqualTo(text.Length).Within(1e-9), "every authored keypress still counts");
            Assert.That(rating.Stars, Is.GreaterThan(0));
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
