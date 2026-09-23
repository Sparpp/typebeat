// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// A PAUSE IS A DIVIDER FOR THE STAR RATING, the same way it is one for the engine. Subdividing a
    /// word raises the rating - the density premium prices how finely a map cuts its own timing, and
    /// every cut tightens the windows its presses are read against - and a word the mapper cut with a
    /// rest is sung, and typed, in exactly the separate stretches a subdivision would give it. A
    /// rating that spread such a word evenly over its whole span, rests included, would read the map
    /// as easier than it plays and would leave every pause a mapper inserted unpriced.
    ///
    /// <para>One word, over and over, so the rating moves for the structure and nothing else: the
    /// paused map is priced against the same map with the rests taken out, and against the same map
    /// with the pauses expressed as authored syllable subdivisions at the very same cuts.</para>
    /// </summary>
    public class PauseDifficultyTest
    {
        private const double word_span_ms = 700;
        private const double line_spacing_ms = 1000;
        private const int line_count = 24;

        // A word the syllabifier reads as ONE syllable, so every divider the mapper authored is a
        // window the rating did not already have: "more divisions, more rating" holds in the clear and
        // a pause's own cut can be priced against the subdivision that would carry the same one.
        private const string word = "strengths";
        private static readonly int[] cuts = { 4 };

        private static LyricLine line(int index, bool paused, bool subdivided)
        {
            double start = 1000 + index * line_spacing_ms;
            double end = start + word_span_ms;
            var unit = new TimedUnit { Text = word, StartTime = start, EndTime = end };

            if (paused)
            {
                // Each rest sits on its own cut and is carved OUT of the word's span, as the editor
                // writes one: the stretch before it ends, the rest runs, the next stretch begins.
                var pauses = new List<WordPause>();
                double stretch = word_span_ms / (cuts.Length + 1);
                double cursor = start;

                foreach (int cut in cuts)
                {
                    double restStart = cursor + stretch * 0.55;
                    double restEnd = restStart + stretch * 0.35;
                    pauses.Add(new WordPause(restStart, restEnd, cut));
                    cursor = restEnd;
                }

                unit = new TimedUnit { Text = word, StartTime = start, EndTime = end, Pauses = pauses };
            }
            else if (subdivided)
            {
                var boundaries = new List<double>();

                foreach (int cut in cuts)
                    boundaries.Add(start + word_span_ms * cut / word.Length);

                unit = new TimedUnit
                {
                    Text = word,
                    StartTime = start,
                    EndTime = end,
                    SyllableBoundaries = boundaries,
                    SyllableSplits = new List<int>(cuts),
                };
            }

            return new LyricLine
            {
                RawText = word,
                StartTime = start,
                EndTime = start + line_spacing_ms,
                SingEndTime = end,
                Units = new[] { unit },
            };
        }

        private static double stars(bool paused, bool subdivided)
        {
            var lines = new List<LyricLine>();

            for (int i = 0; i < line_count; i++)
                lines.Add(line(i, paused, subdivided));

            return LyricDifficulty.Compute(lines);
        }

        [Test]
        public void TestPausesArePricedLikeSubdivisions()
        {
            double plain = stars(paused: false, subdivided: false);
            double divided = stars(paused: false, subdivided: true);
            double rested = stars(paused: true, subdivided: false);

            Assert.Multiple(() =>
            {
                Assert.That(rested, Is.GreaterThan(plain),
                    $"a paused word must not price as one undivided span (plain {plain:F4}, subdivided {divided:F4}, paused {rested:F4})");

                Assert.That(rested, Is.GreaterThanOrEqualTo(divided),
                    $"a paused word prices the cuts a subdivision does, at least (plain {plain:F4}, subdivided {divided:F4}, paused {rested:F4})");
            });
        }

        /// <summary>
        /// The same fact one level down, at the shape the rating actually reads: a paused word is built
        /// with one group per STRETCH, and each group ends where its own rest begins rather than where
        /// the next group starts. That gap is the rest, and it is what keeps the model honest - the
        /// characters on either side are typed in separate bursts, exactly as an authored subdivision's
        /// are typed in separate syllables.
        /// </summary>
        [Test]
        public void TestAPausedWordIsBuiltFromItsStretches()
        {
            var lines = new List<LyricLine> { line(0, paused: true, subdivided: false) };
            LyricDifficulty.Word built = LyricDifficulty.BuildWords(lines, 1, false).Single(w => w.Token == word);

            Assert.Multiple(() =>
            {
                Assert.That(built.Groups, Is.Not.Null, "the word carries no groups at all: its stretches were not read");
                Assert.That(built.GroupEnds, Is.Not.Null, "the groups are not stretches: they have no ends of their own");
                Assert.That(built.Groups!.Length, Is.EqualTo(cuts.Length + 1), "one group per stretch");
                Assert.That(built.Groups[0], Is.EqualTo(built.Start).Within(1e-6), "the first stretch opens the word");
                Assert.That(built.GroupEnds![^1], Is.EqualTo(built.End).Within(1e-6), "the last stretch closes it");
                Assert.That(built.GroupEnds[0], Is.LessThan(built.Groups[1]),
                    "the groups meet, so the rest between them was folded into one of them");
            });
        }
    }
}
