// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// WHERE A LINE STARTS IS NOT A DIFFICULTY INPUT. A line's own start and end are the game's
    /// BOOKKEEPING - which row a word is on, when the boundary cue fires, when the line may be taken -
    /// and the rating is built from the WORDS: their own sung spans, their syllabification and their
    /// rests, their spellings, and which line each belongs to. So a mapper who drags a line boundary
    /// around while leaving the words where they are moves nothing in the star rating, which is what
    /// this pins.
    /// </summary>
    public class LineBoundaryDifficultyTest
    {
        private static readonly string[] lines = { "alpha beta", "gamma delta", "epsilon zeta" };

        // Each line runs a second and a half, its words sung inside it, with a clear stretch of nothing
        // at the end of every line. Only the MIDDLE line's boundary moves, and its words never do.
        private static List<LyricLine> map(int boundaryShiftMs)
        {
            var result = new List<LyricLine>();

            for (int i = 0; i < lines.Length; i++)
            {
                string[] tokens = lines[i].Split(' ');
                var units = new List<TimedUnit>();

                for (int w = 0; w < tokens.Length; w++)
                {
                    double wordStart = 1000 + i * 1500 + w * 500;

                    units.Add(new TimedUnit
                    {
                        Text = tokens[w],
                        StartTime = wordStart,
                        EndTime = wordStart + 400,
                    });
                }

                result.Add(new LyricLine
                {
                    RawText = lines[i],
                    StartTime = 1000 + i * 1500 + (i == 1 ? boundaryShiftMs : 0),
                    EndTime = 1000 + (i + 1) * 1500 + (i <= 1 ? boundaryShiftMs : 0),
                    SingEndTime = 1000 + i * 1500 + 900,
                    Units = units,
                });
            }

            return result;
        }

        /// <summary>
        /// The same map with one line's words sung several times FASTER, which is the change the rating
        /// IS supposed to see: the guard against the test above passing vacuously.
        ///
        /// <para>Deliberately a change of PACE rather than a shift: sliding a whole line of words later
        /// is also invisible to the rating, because the silence BETWEEN two lines is deleted from the
        /// press stream (the next line cues its own first word), whereas the same words spaced tighter
        /// is a genuinely faster thing to type.</para>
        /// </summary>
        private static List<LyricLine> withMovedWord()
        {
            var shifted = map(0);
            var line = shifted[1];

            shifted[1] = new LyricLine
            {
                RawText = line.RawText,
                StartTime = line.StartTime,
                EndTime = line.EndTime,
                SingEndTime = line.SingEndTime,
                Units = new[]
                {
                    new TimedUnit { Text = "gamma", StartTime = 2500, EndTime = 2560 },
                    new TimedUnit { Text = "delta", StartTime = 2580, EndTime = 2640 },
                },
            };

            return shifted;
        }

        [Test]
        public void MovingALineBoundaryDoesNotMoveTheStarRating()
        {
            double reference = LyricDifficulty.Compute(map(0));

            Assert.Multiple(() =>
            {
                Assert.That(reference, Is.GreaterThan(0), "not vacuous: the fixture has to be worth something");
                Assert.That(LyricDifficulty.Compute(map(-400)), Is.EqualTo(reference), "a boundary dragged 400 ms earlier");
                Assert.That(LyricDifficulty.Compute(map(600)), Is.EqualTo(reference), "and one dragged 600 ms later");
            });
        }

        [Test]
        public void MovingAWordDoesMoveIt()
        {
            // The other half of the claim: the rating is reading the words, so a word that really moves
            // is a different map and prices differently.
            Assert.That(LyricDifficulty.Compute(withMovedWord()), Is.Not.EqualTo(LyricDifficulty.Compute(map(0))));
        }
    }
}
