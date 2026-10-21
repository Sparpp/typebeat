// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// The press-now highlight follows the judge. LyricLineDisplay lights a cell from the engine's own
// TypingEngine.JudgedSpanOf and WindowsFor, so a cell is fully lit exactly while a press on it
// would be graded Great. The cross-check here is against REAL presses: a fresh engine types the
// line up to the cell at the sampled time and reports the tier the cell was judged, under every
// rule that moves the span (Easy's word shelter, FirstCharTiming, stretch runs, freestyle windows,
// the grouping and target eras) and every window scale (Easy, a rate mod, Hard Rock in both
// window eras).

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.UI;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class JudgedSpanHighlightTest
    {
        /// <summary>
        /// Every shape the span rule treats differently, one word each: a subtimed two-syllable word,
        /// a word the mapper left whole (one group when only authored syllables count, the
        /// syllabifier's cut otherwise), a stretched run followed by a span-timed suffix inside one
        /// syllable ("y|eeeah"), a freestyle section (doubled windows, stretch cells), an
        /// unsyllabifiable token (ungrouped, point-timed) and a short closing word.
        /// </summary>
        private static LyricBeatmap fixture() => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Song",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = new[]
            {
                new LyricLine
                {
                    RawText = "hello yellow yeeeah &&& mmm go",
                    StartTime = 0,
                    EndTime = 30000,
                    SingEndTime = 13000,
                    Units = new[]
                    {
                        new TimedUnit { Text = "hello", StartTime = 1000, EndTime = 3000, SyllableBoundaries = new[] { 2000.0 }, SyllableSplits = new[] { 3 } },
                        new TimedUnit { Text = "yellow", StartTime = 3500, EndTime = 5000 },
                        new TimedUnit { Text = "yeeeah", StartTime = 5500, EndTime = 8500, SyllableBoundaries = new[] { 6000.0 }, SyllableSplits = new[] { 1 } },
                        new TimedUnit { Text = "&&&", StartTime = 9000, EndTime = 10500 },
                        new TimedUnit { Text = "mmm", StartTime = 11000, EndTime = 12000 },
                        new TimedUnit { Text = "go", StartTime = 12500, EndTime = 13000 },
                    },
                },
            },
            Granularity = TimingGranularity.Syllable,
        };

        /// <summary>The flags each named run judges with, as the live factory, Easy, the rate mods and the replay eras set them.</summary>
        private static TypingEngine engine(string era, bool firstCharTiming)
        {
            var built = new TypingEngine(fixture())
            {
                SyllableTiming = true,
                CharTimedStretch = true,
                FirstCharTiming = firstCharTiming,
                AuthoredSyllablesOnly = true,
                AlignSubdivisionTargets = true,
                UnhalvedHardRockWindows = true,
            };

            switch (era)
            {
                case "none":
                    break;

                case "easy":
                    built.WindowScale *= 2;
                    built.WordShelter = true;
                    break;

                case "rate":
                    built.WindowScale *= 1.5;
                    break;

                case "hr":
                    built.SyllableTiming = false;
                    built.HardRockFromMod = true;
                    break;

                case "hr-halved":
                    built.SyllableTiming = false;
                    built.HardRockFromMod = true;
                    built.UnhalvedHardRockWindows = false;
                    break;

                case "old-replay":
                    built.CharTimedStretch = false;
                    built.AuthoredSyllablesOnly = false;
                    built.AlignSubdivisionTargets = false;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(era), era, null);
            }

            return built;
        }

        /// <summary>The tier a press on cell <paramref name="cellIndex"/> at <paramref name="time"/> is graded, typing everything before it at the same instant.</summary>
        private static JudgementType gradeOf(string era, bool firstCharTiming, int cellIndex, double time)
        {
            var typist = engine(era, firstCharTiming);
            var cells = typist.Lines[0].Cells;
            CharJudgement? judged = null;
            typist.CharJudged += j =>
            {
                if (j.CellIndex == cellIndex)
                    judged = j;
            };

            typist.Update(time);

            for (int i = 0; i <= cellIndex; i++)
                Assert.IsTrue(typist.ProcessKey(cells[i].IsFreestyle ? 'x' : cells[i].Expected, time), $"press {i} at {time}");

            Assert.IsNotNull(judged, $"cell {cellIndex} at {time} was not judged");
            return judged!.Value.Type;
        }

        /// <summary>
        /// The instants worth sampling for one cell: every edge the line's own data offers inside the
        /// cell's word (its word's span, its syllables' spans, every target), each shifted by every
        /// window half the fixture can produce and one millisecond either side of it, plus a coarse
        /// grid. Derived from the line, not from the span under test.
        /// </summary>
        private static IEnumerable<double> samplesFor(TypingEngine judge, int cellIndex)
        {
            var line = judge.Lines[0];
            var edges = new List<double> { line.Cells[cellIndex].TargetTime };
            int word = line.WordIndexOf(cellIndex);

            if (word >= 0)
            {
                var group = line.Words[word];
                edges.Add(group.StartTime);
                edges.Add(group.EndTime);

                for (int i = group.StartCell; i < group.EndCellExclusive; i++)
                    edges.Add(line.Cells[i].TargetTime);
            }

            foreach (var grouping in new[] { line.GroupingFor(true), line.GroupingFor(false) })
            {
                int syllable = grouping.IndexOf(cellIndex);

                if (syllable < 0)
                    continue;

                edges.Add(grouping.Groups[syllable].StartTime);
                edges.Add(grouping.Groups[syllable].EndTime);
            }

            var times = new SortedSet<double>();

            foreach (double edge in edges)
            {
                foreach (double half in new[] { 0, 75, 150, 225, 300, 450, 600 })
                {
                    foreach (double nudge in new[] { -1, 0, 1 })
                    {
                        times.Add(edge - half + nudge);
                        times.Add(edge + half + nudge);
                    }
                }
            }

            for (double t = edges.Min() - 700; t <= edges.Max() + 700; t += 50)
                times.Add(t);

            return times;
        }

        [TestCase("none", true)]
        [TestCase("none", false)]
        [TestCase("easy", true)]
        [TestCase("easy", false)]
        [TestCase("rate", true)]
        [TestCase("hr", true)]
        [TestCase("hr-halved", true)]
        [TestCase("old-replay", false)]
        public void ACellIsFullyLitExactlyWhileAPressOnItWouldBeGreat(string era, bool firstCharTiming)
        {
            var judge = engine(era, firstCharTiming);
            var line = judge.Lines[0];
            var display = new LyricLineDisplay(line, judge);
            int checkedCells = 0;

            for (int cell = 0; cell < line.Cells.Count; cell++)
            {
                if (!line.Cells[cell].IsCountable)
                    continue;

                checkedCells++;

                foreach (double time in samplesFor(judge, cell))
                {
                    display.SetSungWindow(time);

                    bool lit = display.CellSungHighlightAmount(cell) == 1;
                    bool great = gradeOf(era, firstCharTiming, cell, time) == JudgementType.Great;

                    Assert.AreEqual(great, lit, $"{era}: cell {cell} '{line.Cells[cell].Expected}' at {time}");
                }
            }

            Assert.AreEqual(line.Cells.Count(c => c.IsCountable), checkedCells);
        }

        /// <summary>
        /// The eras a replay header sets flip on the engine the display was built with, mid-run. The
        /// highlight reads the engine on every feed, so the next feed already lights the new span:
        /// here the first cell of "hello", whose span is [1000, 1000] under FirstCharTiming and the
        /// whole syllable without it, and the cells of "yellow", which share one group only while
        /// authored syllables are the grouping.
        /// </summary>
        [Test]
        public void TheHighlightFollowsErasFlippedOnTheLiveEngine()
        {
            var judge = engine("none", firstCharTiming: true);
            var line = judge.Lines[0];
            var display = new LyricLineDisplay(line, judge);
            double greatLate = judge.Windows.GreatLate;

            display.SetSungWindow(1000 + greatLate + 1);
            Assert.Less(display.CellSungHighlightAmount(0), 1f, "the first character has left its window");
            Assert.AreEqual(1f, display.CellSungHighlightAmount(1), "the rest of the syllable is still being sung");

            judge.FirstCharTiming = false;
            display.SetSungWindow(1000 + greatLate + 1);
            Assert.AreEqual(1f, display.CellSungHighlightAmount(0), "without the era the first character shares the span");

            int yellow = line.WordIndexOf(6);
            var word = line.Words[yellow];
            int last = word.EndCellExclusive - 1;

            judge.AuthoredSyllablesOnly = false;
            Assert.AreNotEqual(judge.GroupingOf(line).IndexOf(word.StartCell), judge.GroupingOf(line).IndexOf(last), "the natural cut splits the word");
            display.SetSungWindow(word.StartTime - judge.Windows.GreatEarly);
            Assert.AreEqual(1f, display.CellSungHighlightAmount(word.StartCell));
            Assert.AreEqual(0f, display.CellSungHighlightAmount(last), "the last syllable is not open yet");

            judge.AuthoredSyllablesOnly = true;
            display.SetSungWindow(word.StartTime - judge.Windows.GreatEarly);
            Assert.AreEqual(1f, display.CellSungHighlightAmount(last), "one authored group lights together");
        }
    }
}
