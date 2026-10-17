// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class WholeMapTypabilityBonusTest
    {
        [TestCase(-1.9, 7.133)]
        [TestCase(-5, 7.35)]
        [TestCase(0, 7)]
        [TestCase(1.9, 7)]
        public void NegativeZAddsOnePercentPerUnitToFinalStars(double z, double stars)
            => Assert.That(7 * LyricDifficulty.WholeMapTypabilityMultiplier(z), Is.EqualTo(stars).Within(1e-12));

        [TestCase(false, 0.75)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(true, 1.5)]
        public void BonusIsAppliedOnceAfterLengthAndRhythm(bool literate, double rate)
        {
            bool Scores(string text, out double z)
            {
                z = -1.9;
                return true;
            }

            var settings = ChunkedEndurance.Active;
            var rating = LyricDifficulty.RateChunked(new[] { line("Typing difficult words.", 0) }, rate, literate, Scores, settings);
            var report = rating.Report;
            Assert.Multiple(() =>
            {
                Assert.That(rating.TypabilityMeanZ, Is.EqualTo(-1.9).Within(1e-12));
                Assert.That(report.WholeMapTypabilityMultiplier, Is.EqualTo(1.019).Within(1e-12));
                Assert.That(report.Stars, Is.EqualTo(settings.anchor * report.Score * (1 + report.LengthBonusApplied) * 1.019).Within(1e-12));
            });
        }

        [Test]
        public void UsesTheWholeMapMeanInsteadOfTheHardestLine()
        {
            bool Scores(string text, out double z)
            {
                z = text == "bad" ? -3 : 1;
                return true;
            }

            // Three characters at -3 and five at +1 give a character-weighted mean of -0.5.
            var rating = LyricDifficulty.RateChunked(new[] { line("bad", 0), line("words", 5000) }, 1, false, Scores, ChunkedEndurance.Active);
            Assert.That(rating.TypabilityMeanZ, Is.EqualTo(-0.5).Within(1e-12));
            Assert.That(rating.Report.WholeMapTypabilityMultiplier, Is.EqualTo(1.005).Within(1e-12));
        }

        [Test]
        public void UnscoredAndInsufficientlyScoredMapsDoNotGainABonus()
        {
            bool PartialScores(string text, out double z)
            {
                z = text == "bad" ? -3 : 0;
                return text == "bad";
            }

            var lines = new[] { line("bad", 0), line("longer", 5000) };
            foreach (var scores in new LyricDifficulty.LyricScoreSource[] { LyricDifficulty.NoScores, PartialScores })
            {
                var rating = LyricDifficulty.RateChunked(lines, 1, false, scores, ChunkedEndurance.Active);
                Assert.That(rating.TypabilityApplied, Is.False);
                Assert.That(rating.Report.WholeMapTypabilityMultiplier, Is.EqualTo(1));
            }
        }

        [Test]
        public void LiterateUsesItsOwnAuthoredTypability()
        {
            var lines = new[] { line("F. (F) B. (B) I. (I)", 0) };
            foreach (bool literate in new[] { false, true })
            {
                string played = literate ? lines[0].RawText : Typeability.ToDefaultStream(lines[0].RawText);
                Assert.That(TypabilityIndex.TryScore(played, out var score), Is.True);
                double expected = 1 + Math.Max(0, -TypabilityIndex.ClampZ(score.Z)) / 100;
                var rating = LyricDifficulty.ComputeDetail(lines, literate: literate);
                var report = LyricDifficulty.RateChunked(lines, 1, literate, LyricDifficulty.ShippedScores, ChunkedEndurance.Active).Report;
                Assert.That(report.WholeMapTypabilityMultiplier, Is.EqualTo(expected).Within(1e-12));
                Assert.That(rating.Stars, Is.EqualTo(ChunkedEndurance.Active.anchor * report.Score * (1 + report.LengthBonusApplied) * expected).Within(1e-12));
            }
        }

        private static LyricLine line(string text, double start) => new LyricLine
        {
            RawText = text,
            StartTime = start,
            EndTime = start + 4000,
            SingEndTime = start + 4000,
            Units = text.Split(' ').Select((token, i) => new TimedUnit
            {
                Text = token,
                StartTime = start + i * 4000.0 / text.Split(' ').Length,
                EndTime = start + (i + 1) * 4000.0 / text.Split(' ').Length,
            }).ToArray(),
        };
    }
}
