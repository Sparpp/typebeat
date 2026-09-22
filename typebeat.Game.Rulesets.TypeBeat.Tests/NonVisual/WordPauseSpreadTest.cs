// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// THE AUTHORED PAUSE (the Map Editor's Insert Pause) and what it does to the ENGINE: it splits one
// word's char-to-time spread into two halves, so no cell has a target inside the rest and the cells
// after it are timed from its END.
//
// The feature exists for the multisyllabic words where a singer breathes between syllables: the
// mapper parks the playhead in the breath and the word keeps its authored span, with the rest
// reserved inside it. Two rules the fixture matrix below exists to pin, because both are easy to get
// wrong and neither is visible from a single case:
//
//   THE HALVES ARE UNITS IN THEIR OWN RIGHT. Each one is ramped by the same piecewise arithmetic a
//   whole word uses, with ITS OWN boundaries and ITS OWN char cut, so "ap|ple" either side of a
//   breath stays "ap" + "ple" rather than becoming one six-character ramp.
//
//   A BOUNDARY PULLED INTO THE REST ATTACHES TO THE NEARER HALF and is rescaled into its span (the
//   house rule for a boundary that survives a retimed word). Its authored CHAR cut cannot follow it,
//   so the halves fall back to the derived even split - the same "a stale split re-derives" contract
//   a whole word already follows.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class WordPauseSpreadTest
    {
        private const double tolerance = 1e-9;

        #region Fixture builders

        private static TimedUnit plainUnit(string text, double start, double end) => new TimedUnit
        {
            Text = text,
            StartTime = start,
            EndTime = end,
        };

        private static LyricLine lineOf(params TimedUnit[] units) => new LyricLine
        {
            RawText = string.Join(' ', units.Select(u => u.Text)),
            StartTime = units[0].StartTime,
            EndTime = units[^1].EndTime,
            SingEndTime = units[^1].EndTime,
            Units = units,
        };

        /// <summary>The cell targets of a paused word, in display order.</summary>
        private static double[] targets(params TimedUnit[] units)
            => TypingLine.FromLyricLine(lineOf(units)).Cells.Select(c => c.TargetTime).ToArray();

        private static void assertTargets(double[] expected, double[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length));

            for (int i = 0; i < expected.Length; i++)
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(tolerance), $"cell {i}");
        }

        #endregion

        #region Pause alone

        /// <summary>
        /// A word that is NOT subdivided, paused between "ple" and "ase": three cells are ramped over
        /// [1000, 1450] and three over [1600, 2000], so no cell's target falls inside (1450, 1600) and
        /// the first post-pause cell lands exactly on the rest's end. The no-pause row is the same word
        /// flat, which is what tells the two ramps apart at all.
        /// </summary>
        [Test]
        public void APauseSplitsTheSpreadAndLeavesTheHonouredRestEmpty()
        {
            var unpaused = plainUnit("please", 1000, 2000);

            var paused = new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(1450, 1600, 3) },
            };

            assertTargets(new[] { 1000d, 1000 + 1000d / 6, 1000 + 2000d / 6, 1500, 1000 + 4000d / 6, 1000 + 5000d / 6 },
                targets(unpaused));

            var actual = targets(paused);
            assertTargets(new[] { 1000d, 1150, 1300, 1600, 1600 + 400d / 3, 1600 + 800d / 3 }, actual);

            Assert.IsFalse(actual.Any(t => t > 1450 + tolerance && t < 1600 - tolerance),
                "no cell has a target strictly inside the rest");
            Assert.That(actual[3], Is.EqualTo(1600).Within(tolerance), "the first post-pause cell is timed FROM the rest's end");
            Assert.IsTrue(actual.SequenceEqual(actual.OrderBy(t => t)), "targets stay non-decreasing");
        }

        /// <summary>
        /// The caret's own reading across the rest: it hovers strictly BETWEEN the last pre-pause cell
        /// and the first post-pause one for the whole rest, which is the "the caret waits where it is"
        /// behaviour - no engine state, just no anchor inside the gap.
        /// </summary>
        [Test]
        public void TheSungCaretHoversBetweenTheTwoCellsAcrossTheRest()
        {
            var typeline = TypingLine.FromLyricLine(lineOf(new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(1450, 1600, 3) },
            }));

            Assert.That(typeline.SungPositionAt(1300), Is.EqualTo(2).Within(tolerance), "at the last pre-pause target the caret is on that cell");
            Assert.That(typeline.SungPositionAt(1450), Is.EqualTo(2.5).Within(tolerance), "mid-rest the caret sits between the two cells");
            Assert.That(typeline.SungPositionAt(1599.9), Is.LessThan(3), "the caret never reaches the first post-pause cell before the rest ends");
            Assert.That(typeline.SungPositionAt(1600), Is.EqualTo(3).Within(tolerance), "it arrives exactly at the rest's end");
        }

        #endregion

        #region Both: each half is a unit in its own right

        /// <summary>
        /// "remember" subdivided once (boundary 1300) with the authored cut "re|member", paused after
        /// its fifth character. The first half keeps the mapper's boundary AND cut (three cells over
        /// [1000, 1300], two over [1300, 1600]) while the second half - which carries neither - is a
        /// plain three-cell ramp over [1800, 2000]. A single flat ramp over the whole word would put
        /// every one of these somewhere else, so the row pins that the halves are ramped separately.
        /// </summary>
        [Test]
        public void EachHalfKeepsItsOwnBoundariesAndCut()
        {
            var paused = new TimedUnit
            {
                Text = "remember",
                StartTime = 1000,
                EndTime = 2000,
                SyllableBoundaries = new[] { 1300.0 },
                SyllableSplits = new[] { 2 },
                Pauses = new[] { new WordPause(1600, 1800, 5) },
            };

            assertTargets(new[]
            {
                1000d, 1150, 1300, 1400, 1500, // "remem": two cells to the 1300 boundary, three after
                1800, 1800 + 200d / 3, 1800 + 400d / 3, // "ber": flat across the rest of the word
            }, targets(paused));
        }

        #endregion

        #region A boundary inside the rest

        /// <summary>
        /// One subdivision, pulled INSIDE the rest and nearer its END: it goes to the SECOND half and
        /// is rescaled there (0.75 of the way into [1600, 1800] lands 0.75 into [1800, 2000], i.e.
        /// 1950). The first half is left its plain five-cell flat ramp with no boundary at all, which
        /// is what proves the boundary did not simply stay where it was.
        /// </summary>
        [Test]
        public void ABoundaryInsideTheRestGoesToTheNearerHalf()
        {
            var paused = new TimedUnit
            {
                Text = "remember",
                StartTime = 1000,
                EndTime = 2000,
                SyllableBoundaries = new[] { 1750.0 },
                Pauses = new[] { new WordPause(1600, 1800, 5) },
            };

            // First half: five cells flat over [1000, 1600] - the boundary did NOT come here.
            // Second half: three cells over [1800, 2000] cut at the rescaled 1950.
            assertTargets(new[]
            {
                1000d, 1120, 1240, 1360, 1480,
                1800, 1900, 1950 + 50d / 3,
            }, targets(paused));
        }

        /// <summary>
        /// The mirror: the same subdivision nearer the rest's START goes to the FIRST half and is
        /// rescaled into [1000, 1600] (0.25 of the way in - 1150), leaving the second half flat.
        /// </summary>
        [Test]
        public void ABoundaryCloserToTheRestStartGoesToTheFirstHalf()
        {
            var paused = new TimedUnit
            {
                Text = "remember",
                StartTime = 1000,
                EndTime = 2000,
                SyllableBoundaries = new[] { 1650.0 },
                Pauses = new[] { new WordPause(1600, 1800, 5) },
            };

            assertTargets(new[]
            {
                1000d, 1060, 1120, 1240, 1420,
                1800, 1800 + 200d / 3, 1800 + 400d / 3,
            }, targets(paused));
        }

        /// <summary>
        /// A boundary pulled into the rest takes the mapper's CHAR CUT out of play for the whole word:
        /// the cut no longer sits in the half its time does, so both halves re-derive evenly rather
        /// than author a cut against the wrong segment. The first half here is the EVEN cut of five
        /// cells over its one boundary (three cells then two, 1000, 1120, 1240 | 1360, 1480), not the
        /// authored "rem|em" one (1000, 1100, 1200 | 1300, 1450) the same boundary would have produced
        /// in place - the two disagree on three of the five cells.
        /// </summary>
        [Test]
        public void APulledBoundaryKeepsTheAuthoredCutOfTheStretchItLandsIn()
        {
            var paused = new TimedUnit
            {
                Text = "remember",
                StartTime = 1000,
                EndTime = 2000,
                SyllableBoundaries = new[] { 1700.0 },
                SyllableSplits = new[] { 3 },
                Pauses = new[] { new WordPause(1600, 1800, 5) },
            };

            assertTargets(new[]
            {
                1000d, 1100, 1200, 1300, 1450,
                1800, 1800 + 200d / 3, 1800 + 400d / 3,
            }, targets(paused));
        }

        #endregion

        #region Degenerate pauses ramp exactly as if there were none

        /// <summary>
        /// A pause that cannot cut this word is IGNORED rather than half-applied: a split that leaves
        /// every cell on one side of it (here after the last character), and edges that have left the
        /// unit's own span. Each row must read exactly as the same word with no pause at all, which is
        /// the conservative fallback the loader also takes.
        /// </summary>
        [TestCase(6, 1450, 1600, TestName = "APauseAfterTheLastCharacterIsInert")]
        [TestCase(0, 1450, 1600, TestName = "APauseBeforeTheFirstCharacterIsInert")]
        [TestCase(3, 900, 1600, TestName = "APauseStartingBeforeTheWordIsInert")]
        [TestCase(3, 1450, 2100, TestName = "APauseEndingAfterTheWordIsInert")]
        [TestCase(3, 1600, 1450, TestName = "AnInvertedPauseIsInert")]
        public void APauseThatCannotCutTheWordChangesNothing(int split, double start, double end)
        {
            var reference = targets(plainUnit("please", 1000, 2000));

            var paused = new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(start, end, split) },
            };

            assertTargets(reference, targets(paused));
        }

        #endregion

        #region The pace figures do not see the rest

        /// <summary>
        /// The rest is a BREATH, not a break: a word carrying one is still one uninterrupted sung
        /// span, so the mapper-facing pace figures (CPM/WPM) are untouched. They are walked over WORD
        /// spans, which the pause sits strictly inside, so the only way this could regress is if
        /// someone started deriving them from cell targets.
        /// </summary>
        [Test]
        public void ThePaceFiguresAreUnchangedByAnAuthoredPause()
        {
            var plain = lineOf(plainUnit("please", 1000, 2000), plainUnit("go", 2200, 2600));

            var paused = lineOf(
                new TimedUnit { Text = "please", StartTime = 1000, EndTime = 2000, Pauses = new[] { new WordPause(1450, 1600, 3) } },
                plainUnit("go", 2200, 2600));

            var a = LyricPaceStatistics.Compute(new[] { plain });
            var b = LyricPaceStatistics.Compute(new[] { paused });

            Assert.That(b.AverageCpm, Is.EqualTo(a.AverageCpm).Within(tolerance));
            Assert.That(b.AverageWpm, Is.EqualTo(a.AverageWpm).Within(tolerance));
            Assert.That(b.LineAverageCpm, Is.EqualTo(a.LineAverageCpm).Within(tolerance));
            Assert.That(b.TargetWpm, Is.EqualTo(a.TargetWpm).Within(tolerance));
        }

        #endregion

        #region The rest as a subdivider: groups, timing window and the gameplay mark

        /// <summary>
        /// A word may take SEVERAL rests, one per breath. "please" with a rest after "pl" and another after
        /// "ple" is sung in THREE stretches: each is timed over its own span, no cell has a target inside
        /// either rest, and each stretch's first cell lands exactly on the rest's end before it.
        /// </summary>
        [Test]
        public void SeveralRestsMakeSeveralStretches()
        {
            var typeline = TypingLine.FromLyricLine(lineOf(new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(1200, 1300, 2), new WordPause(1600, 1700, 4) },
            }));

            assertTargets(new[]
            {
                1000d, 1100, // "pl" over [1000, 1200]
                1300, 1450,  // "ea" over [1300, 1600]
                1700, 1850,  // "se" over [1700, 2000]
            }, typeline.Cells.Select(c => c.TargetTime).ToArray());

            Assert.IsFalse(typeline.Cells.Any(c => (c.TargetTime > 1200 && c.TargetTime < 1300) || (c.TargetTime > 1600 && c.TargetTime < 1700)),
                "no cell has a target inside either rest");
        }

        /// <summary>
        /// And the judgement groups part at EVERY one of them: three groups, the gap of a rest between the
        /// first and second and between the second and third, with the gameplay mark at both cuts - so a
        /// word with two breaths reads and judges exactly like one with two subdividers.
        /// </summary>
        [Test]
        public void SeveralRestsPartTheGroupsAndMarkEachCut()
        {
            var typeline = TypingLine.FromLyricLine(lineOf(new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(1200, 1300, 2), new WordPause(1600, 1700, 4) },
            }));

            Assert.That(typeline.Syllables, Is.EqualTo(new[]
            {
                new SyllableGroup(0, 2, 1000, 1200),
                new SyllableGroup(2, 4, 1300, 1600),
                new SyllableGroup(4, 6, 1700, 2000),
            }));

            Assert.That(typeline.SyllableMarkerCells, Is.EqualTo(new[] { 2, 4 }), "a mark at each rest's cut");
        }

        /// <summary>
        /// A rest is a SUBDIVIDER, so the judgement groups part at it: the characters before it are
        /// sung over [1000, 1450] and the ones after over [1500, 2000], with the rest standing as a gap
        /// no group covers - the same shape a syllable boundary leaves. Those two spans are the timing
        /// window each side is judged against, and the second group's first cell is what the gameplay
        /// subdivision mark is drawn at, so a breath reads on the play exactly like a mapper-drawn
        /// divider.
        /// </summary>
        [Test]
        public void ARestPartsTheJudgementGroupsAndMarksTheCut()
        {
            var typeline = TypingLine.FromLyricLine(lineOf(new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(1450, 1500, 3) },
            }));

            Assert.That(typeline.Syllables.Count, Is.EqualTo(2), "one group either side of the rest");
            Assert.That(typeline.Syllables[0], Is.EqualTo(new SyllableGroup(0, 3, 1000, 1450)));
            Assert.That(typeline.Syllables[1], Is.EqualTo(new SyllableGroup(3, 6, 1500, 2000)));

            Assert.That(typeline.SyllableIndexOf(2), Is.EqualTo(0), "the characters before the rest judge in the first group");
            Assert.That(typeline.SyllableIndexOf(3), Is.EqualTo(1), "and the ones after it in the second");

            Assert.That(typeline.SyllableMarkerCells, Is.EqualTo(new[] { 3 }),
                "the far side of the rest is marked the way a subdivision's cut is");
        }

        /// <summary>
        /// A rest among a word's own subdivisions parts the groups on the same terms, and keeps them:
        /// "re" / "mem" / "ber" with the rest's gap between the last two, each side's characters judged
        /// over its own span, and a mark at both interior cuts.
        /// </summary>
        [Test]
        public void ARestAmongSubdivisionsKeepsEveryGroupAndMark()
        {
            var typeline = TypingLine.FromLyricLine(lineOf(new TimedUnit
            {
                Text = "remember",
                StartTime = 1000,
                EndTime = 2000,
                SyllableBoundaries = new[] { 1300.0 },
                SyllableSplits = new[] { 2 },
                Pauses = new[] { new WordPause(1600, 1800, 5) },
            }));

            Assert.That(typeline.Syllables, Is.EqualTo(new[]
            {
                new SyllableGroup(0, 2, 1000, 1300),
                new SyllableGroup(2, 5, 1300, 1600),
                new SyllableGroup(5, 8, 1800, 2000),
            }));

            Assert.That(typeline.SyllableMarkerCells, Is.EqualTo(new[] { 2, 5 }));
        }

        /// <summary>
        /// A word with no rest keeps the groups it has always had - one natural group per syllable of
        /// the spelling, read off the targets with no gap between them - so nothing about the play of an
        /// existing map moves.
        /// </summary>
        [Test]
        public void AWordWithoutARestKeepsItsNaturalGroups()
        {
            var typeline = TypingLine.FromLyricLine(lineOf(plainUnit("please", 1000, 2000)));

            Assert.That(typeline.Syllables.Count, Is.EqualTo(1), "\"please\" is one syllable");
            Assert.That(typeline.Syllables[0], Is.EqualTo(new SyllableGroup(0, 6, 1000, 2000)));
            Assert.That(typeline.SyllableMarkerCells, Is.Empty, "nothing was authored, so nothing is marked");
        }

        /// <summary>
        /// A rest the engine cannot honour adds no group and no mark, exactly as it moves no target.
        /// </summary>
        [Test]
        public void ARestThatCannotCutTheWordAddsNoGroupOrMark()
        {
            var typeline = TypingLine.FromLyricLine(lineOf(new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(1900, 2100, 3) },
            }));

            Assert.That(typeline.Syllables.Count, Is.EqualTo(1));
            Assert.That(typeline.SyllableMarkerCells, Is.Empty);
        }

        #endregion

        #region What the editor's strip draws

        /// <summary>
        /// The runs the strip lays a word's characters out in, as (text, start, end) triples.
        /// </summary>
        private static (string Text, double Start, double End)[] runs(TimedUnit unit)
            => PausedWord.DisplayRuns(unit.Text, unit, unit.StartTime, unit.EndTime)
                        .Select(r => (r.Text, r.StartTime, r.EndTime)).ToArray();

        private static void assertRuns((string Text, double Start, double End)[] expected, (string Text, double Start, double End)[] actual)
        {
            Assert.That(actual.Length, Is.EqualTo(expected.Length), "run count");

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(actual[i].Text, Is.EqualTo(expected[i].Text), $"run {i} text");
                Assert.That(actual[i].Start, Is.EqualTo(expected[i].Start).Within(tolerance), $"run {i} start");
                Assert.That(actual[i].End, Is.EqualTo(expected[i].End).Within(tolerance), $"run {i} end");
            }
        }

        /// <summary>
        /// A rest parts the word's text and MOVES it: "please" paused after "ple" draws the first
        /// three characters over [1000, 1450] and the last three over [1500, 2000], so the greyed band
        /// the strip draws between them covers no letters at all. The split lands on the character the
        /// engine times first after the rest, so what the mapper reads is what the play judges.
        /// </summary>
        [Test]
        public void ARestPartsTheWordsTextAroundItself()
        {
            var paused = new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(1450, 1500, 3) },
            };

            assertRuns(new[]
            {
                ("ple", 1000d, 1450d),
                ("ase", 1500d, 2000d),
            }, runs(paused));
        }

        /// <summary>
        /// A rest does to a SUBDIVIDED word what it does to any other: each half keeps its own
        /// boundaries, so "re|member" cut after its fifth character reads "re" / "mem" / "ber" with the
        /// rest between the last two - the very spans the engine times those characters over.
        /// </summary>
        [Test]
        public void EachHalfOfASubdividedPausedWordKeepsItsOwnBoundaries()
        {
            var paused = new TimedUnit
            {
                Text = "remember",
                StartTime = 1000,
                EndTime = 2000,
                SyllableBoundaries = new[] { 1300.0 },
                SyllableSplits = new[] { 2 },
                Pauses = new[] { new WordPause(1600, 1800, 5) },
            };

            assertRuns(new[]
            {
                ("re", 1000d, 1300d),
                ("mem", 1300d, 1600d),
                ("ber", 1800d, 2000d),
            }, runs(paused));
        }

        /// <summary>
        /// A word with no rest draws exactly as it always has - the syllable segments between the
        /// word's own boundary times - which is what keeps every existing map's strip layout still.
        /// </summary>
        [Test]
        public void AWordWithoutARestIsLaidOutExactlyAsBefore()
        {
            var subdivided = new TimedUnit
            {
                Text = "apple",
                StartTime = 1000,
                EndTime = 2000,
                SyllableBoundaries = new[] { 1300.0 },
                SyllableSplits = new[] { 2 },
            };

            assertRuns(new[]
            {
                ("ap", 1000d, 1300d),
                ("ple", 1300d, 2000d),
            }, runs(subdivided));

            assertRuns(new[] { ("please", 1000d, 2000d) }, runs(plainUnit("please", 1000, 2000)));
        }

        /// <summary>
        /// A rest the engine cannot honour is not drawn as one either: the strip lays the word out as if
        /// it carried none, exactly as the loader and the ramp both ignore it. The rows are the same
        /// degenerate shapes the spread matrix uses.
        /// </summary>
        [TestCase(6, 1450, 1600, TestName = "ARestAfterTheLastCharacterLaysOutAsIfAbsent")]
        [TestCase(3, 900, 1600, TestName = "ARestStartingBeforeTheWordLaysOutAsIfAbsent")]
        [TestCase(3, 1450, 2100, TestName = "ARestEndingAfterTheWordLaysOutAsIfAbsent")]
        public void ARestThatCannotCutTheWordIsNotDrawnAsOne(int split, double start, double end)
        {
            var paused = new TimedUnit
            {
                Text = "please",
                StartTime = 1000,
                EndTime = 2000,
                Pauses = new[] { new WordPause(start, end, split) },
            };

            assertRuns(new[] { ("please", 1000d, 2000d) }, runs(paused));
        }

        /// <summary>
        /// A boundary the mapper has dragged INTO a rest is the one shape whose text and time disagree
        /// (its cut names a place that is not in the half its time went to). The layout must still draw
        /// the word forwards: every character appears exactly once, the runs never start before the
        /// word or end after it, and nothing runs backwards.
        /// </summary>
        [Test]
        public void ARestOverABoundaryStillDrawsForwards()
        {
            var paused = new TimedUnit
            {
                Text = "remember",
                StartTime = 1000,
                EndTime = 2000,
                SyllableBoundaries = new[] { 1700.0 },
                Pauses = new[] { new WordPause(1600, 1800, 5) },
            };

            var actual = runs(paused);

            Assert.That(string.Concat(actual.Select(r => r.Text)), Is.EqualTo("remember"), "every character is drawn exactly once");
            Assert.That(actual[0].Start, Is.EqualTo(1000).Within(tolerance), "the word still starts at its own start");
            Assert.That(actual[^1].End, Is.EqualTo(2000).Within(tolerance), "and ends at its own end");
            Assert.IsTrue(actual.All(r => r.End >= r.Start), "no run is inverted");

            for (int i = 1; i < actual.Length; i++)
                Assert.That(actual[i].Start, Is.GreaterThanOrEqualTo(actual[i - 1].End - tolerance), $"run {i} does not overlap its predecessor");

            Assert.IsTrue(actual.Any(r => Math.Abs(r.End - 1600) < tolerance), "the rest's start still cuts the text");
            Assert.IsTrue(actual.Any(r => Math.Abs(r.Start - 1800) < tolerance), "and its end still opens the far side");
        }

        #endregion
    }
}
