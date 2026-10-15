// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class PunctuationPaceTest
    {
        private static LyricLine line(string text) => new LyricLine
        {
            RawText = text,
            StartTime = 0,
            EndTime = text.Split(' ').Length * 2000,
            SingEndTime = text.Split(' ').Length * 2000,
            Units = text.Split(' ').Select((token, i) => new TimedUnit
            {
                Text = token,
                StartTime = i * 2000,
                EndTime = (i + 1) * 2000,
            }).ToArray(),
        };

        [TestCase("W-O-R-D", 7)]
        [TestCase("well---known", 10)]
        [TestCase("-word-", 4)]
        [TestCase("well- known", 10)]
        [TestCase("well - known", 10)]
        [TestCase("well- -known", 10)]
        [TestCase("a - ! - b", 3)]
        [TestCase("a ! b", 4)]
        [TestCase("a,b", 2)]
        [TestCase("W-O-R-D W-O-R-D W-O-R-D", 23)]
        public void DefaultDifficultyCountsTheSameCellsAsGameplay(string text, int expected)
        {
            var lyrics = new[] { line(text) };
            var gameplay = TypingLine.FromLyricLine(lyrics[0]);
            var words = LyricDifficulty.BuildWords(lyrics, 1, false);

            Assert.That(gameplay.Cells.Count(c => c.IsTypeable), Is.EqualTo(expected));
            Assert.That(words.Sum(w => w.Cells), Is.EqualTo(expected));
            Assert.That(LyricPaceStatistics.Compute(lyrics).TypeableCellCount, Is.EqualTo(expected));
        }

        [TestCase("W-O-R-D")]
        [TestCase("well---known")]
        [TestCase("-word-")]
        [TestCase("well- known")]
        [TestCase("well - known")]
        [TestCase("a - ! - b")]
        [TestCase("a ! b")]
        [TestCase("W-O-R-D W-O-R-D W-O-R-D")]
        public void CurveSpacesKeepTheirGameplayTargets(string text)
        {
            var lyrics = line(text);
            var gameplay = TypingLine.FromLyricLine(lyrics);
            double[] expected = gameplay.Cells.Where(c => c.IsTypeable).Select(c => c.TargetTime).ToArray();

            Assert.That(LyricWpmCurve.DefaultTargets(lyrics), Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BothStreamsCountSevenCellsForTheSpelledWord(bool literate)
        {
            var lyrics = new[] { line("W-O-R-D") };
            Assert.That(LyricDifficulty.BuildWords(lyrics, 1, literate).Single().Cells, Is.EqualTo(7));
        }

        [Test]
        public void SpaceCountingPreservesUnitTimingsAndSubdivisions()
        {
            var lyrics = new LyricLine
            {
                RawText = "W-O-R-D next",
                StartTime = 0,
                EndTime = 5000,
                SingEndTime = 5000,
                Units = new[]
                {
                    new TimedUnit { Text = "W-O-R-D", StartTime = 100, EndTime = 2100, SyllableBoundaries = new[] { 1100.0 } },
                    new TimedUnit { Text = "next", StartTime = 4000, EndTime = 5000 },
                },
            };
            var words = LyricDifficulty.BuildWords(new[] { lyrics }, 2, false);

            Assert.That(words.Count, Is.EqualTo(2));
            Assert.That(words[0].Cells, Is.EqualTo(8), "seven cells and the authored gap after this unit");
            Assert.That(words[0].Start, Is.EqualTo(50));
            Assert.That(words[0].Span, Is.EqualTo(1000));
            Assert.That(words[0].Groups, Is.EqualTo(new[] { 50.0, 550.0 }));
            Assert.That(words[1].Start, Is.EqualTo(2000));
            Assert.That(words[1].Span, Is.EqualTo(500));
        }

        [Test]
        public void FreestyleDiscountStillAppliesAlongsideGeneratedSpaces()
        {
            var words = LyricDifficulty.BuildWords(new[] { line("a-&-&-b") }, 1, false);
            Assert.That(words.Single().Cells, Is.EqualTo(5.5), "two letters, three spaces and two quarter-weight freestyle cells");
        }

        [Test]
        public void GeneratedSpacesRaiseRawTargetAndStarRating()
        {
            var spelled = new[] { line(string.Join(' ', Enumerable.Repeat("w-o-r-d", 12))) };
            var plain = new[] { line(string.Join(' ', Enumerable.Repeat("word", 12))) };
            var spelledEnvelope = LyricDifficulty.ComputeDetail(spelled, enduranceAxis: LyricDifficulty.EnduranceAxis.Envelope);
            var plainEnvelope = LyricDifficulty.ComputeDetail(plain, enduranceAxis: LyricDifficulty.EnduranceAxis.Envelope);
            var spelledRating = LyricDifficulty.RateChunked(spelled, 1, false, LyricDifficulty.NoScores, ChunkedEndurance.Live);
            var plainRating = LyricDifficulty.RateChunked(plain, 1, false, LyricDifficulty.NoScores, ChunkedEndurance.Live);

            Assert.That(spelledEnvelope.Cells, Is.EqualTo(95).Within(1e-9));
            Assert.That(plainEnvelope.Cells, Is.EqualTo(59).Within(1e-9));
            Assert.That(spelledEnvelope.TargetWpm, Is.GreaterThan(plainEnvelope.TargetWpm));
            Assert.That(spelledRating.Report.Stars, Is.GreaterThan(plainRating.Report.Stars));
            Assert.That(spelledRating.Report.CountedCharacters, Is.GreaterThan(plainRating.Report.CountedCharacters));
        }

        [TestCase(0.75)]
        [TestCase(1)]
        [TestCase(1.5)]
        public void GeneratedSpacesQualifyForPeakAndMatchThePlayedTimeline(double rate)
        {
            var lyrics = line("W-O-R-D W-O-R-D W-O-R-D");
            var cells = TypingLine.FromLyricLine(lyrics).Cells.Where(c => c.IsTypeable).ToArray();
            // Separate one-character lines reproduce those exact point targets without generating
            // another space between them. The reference curve therefore sees every played press.
            var reference = cells.Select(c => new LyricLine
            {
                RawText = "a", StartTime = c.TargetTime, EndTime = c.TargetTime + 1, SingEndTime = c.TargetTime + 1,
                Units = new[] { new TimedUnit { Text = "a", StartTime = c.TargetTime, EndTime = c.TargetTime + 1 } },
            }).ToArray();
            var actual = LyricWpmCurve.Compute(new[] { lyrics }, rate: rate);
            var expected = LyricWpmCurve.Compute(reference, rate: rate);

            Assert.That(cells.Length, Is.EqualTo(23));
            Assert.That(actual.IsEmpty, Is.False, "the 12 letters alone previously fell below the 16-cell floor");
            Assert.That(actual.PeakWpm, Is.EqualTo(expected.PeakWpm));
            Assert.That(actual.Curve, Is.EqualTo(expected.Curve));
        }
    }
}
