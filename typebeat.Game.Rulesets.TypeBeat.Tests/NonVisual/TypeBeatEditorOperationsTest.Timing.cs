// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Screens.Edit;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    public partial class TypeBeatEditorOperationsTest
    {
        private static EditorBeatmap createDenseTimingBeatmap(TimingGranularity granularity)
        {
            var beatmap = createBeatmap();
            string text = string.Join(' ', Enumerable.Repeat("a", 9).Append("aaaaaaaaaaaaaaaaaaaa"));

            foreach (var hit in TypeBeatEditorOperations.OrderedLines(beatmap))
            {
                var line = hit.Line;
                string rawText = hit.LineIndex == 0 ? text : line.RawText;
                double singEnd = hit.LineIndex == 0 ? 2000 : line.SingEndTime;
                var units = LrcParser.InterpolateUnits(rawText, line.StartTime, singEnd);
                hit.Granularity = granularity;
                hit.Line = new LyricLine
                {
                    RawText = rawText,
                    StartTime = line.StartTime,
                    EndTime = line.EndTime,
                    SingEndTime = singEnd,
                    Units = granularity == TimingGranularity.Line ? units : units.Select(u => new TimedUnit
                    {
                        Text = u.Text, StartTime = u.StartTime, EndTime = u.EndTime, Source = TimingSource.Explicit,
                    }).ToArray(),
                };
            }

            return beatmap;
        }

        [TestCase(TimingGranularity.Line)]
        [TestCase(TimingGranularity.Word)]
        public void SharedLineBoundaryReservesEveryWordsMinimum(TimingGranularity granularity)
        {
            var beatmap = createDenseTimingBeatmap(granularity);
            var first = lineAt(beatmap, 0);
            var second = lineAt(beatmap, 1);

            TypeBeatEditorOperations.SetLineStart(beatmap, second, 1030);
            Assert.That(first.Line.Units.Count, Is.EqualTo(10));
            Assert.That(first.Line.Units.All(u => u.EndTime - u.StartTime >= 5 - 1e-8), Is.True);
            Assert.That(first.Line.Units[^1].EndTime, Is.LessThanOrEqualTo(second.Line.StartTime));
            assertReloadStable(beatmap);

            TypeBeatEditorOperations.SetLineStart(beatmap, first, first.Line.EndTime - 1);
            Assert.That(first.Line.Units.All(u => u.EndTime - u.StartTime >= 5 - 1e-8), Is.True);
            Assert.That(first.Line.Units[0].StartTime, Is.GreaterThanOrEqualTo(first.Line.StartTime));
            assertReloadStable(beatmap);
        }

        [TestCase(TimingGranularity.Line)]
        [TestCase(TimingGranularity.Word)]
        public void SungEndEditLeavesEveryWordAtLeastFiveMilliseconds(TimingGranularity granularity)
        {
            var beatmap = createDenseTimingBeatmap(granularity);
            var line = lineAt(beatmap, 0);

            TypeBeatEditorOperations.SetSingEnd(beatmap, line, 1001);
            Assert.That(line.Line.SingEndTime - line.Line.StartTime, Is.GreaterThanOrEqualTo(50));
            Assert.That(line.Line.Units.All(u => u.EndTime - u.StartTime >= 5 - 1e-8), Is.True);
            assertReloadStable(beatmap);
        }

        [TestCase(TimingGranularity.Line)]
        [TestCase(TimingGranularity.Word)]
        public void SungEndEditRefusesAWindowTooShortForItsWords(TimingGranularity granularity)
        {
            var beatmap = createDenseTimingBeatmap(granularity);
            var line = lineAt(beatmap, 0);
            line.Line = new LyricLine
            {
                RawText = line.Line.RawText,
                StartTime = 1000,
                EndTime = 1030,
                SingEndTime = 1030,
                Units = LrcParser.InterpolateUnits(line.Line.RawText, 1000, 1030),
            };
            var before = line.Line;

            Assert.DoesNotThrow(() => TypeBeatEditorOperations.SetSingEnd(beatmap, line, 1005));
            Assert.That(line.Line, Is.SameAs(before), "do not create new tiny blocks when the existing window cannot hold them");
        }

        [TestCase(TimingGranularity.Line)]
        [TestCase(TimingGranularity.Word)]
        public void SharedLineBoundaryRefusesTwoWindowsWithoutEnoughWordSpace(TimingGranularity granularity)
        {
            var beatmap = createDenseTimingBeatmap(granularity);
            var first = lineAt(beatmap, 0);
            var second = lineAt(beatmap, 1);
            string text = first.Line.RawText;
            first.Line = new LyricLine
            {
                RawText = text, StartTime = 1000, EndTime = 1030, SingEndTime = 1030,
                Units = LrcParser.InterpolateUnits(text, 1000, 1030),
            };
            second.Line = new LyricLine
            {
                RawText = text, StartTime = 1030, EndTime = 1060, SingEndTime = 1060,
                Units = LrcParser.InterpolateUnits(text, 1030, 1060),
            };
            var firstBefore = first.Line;
            var secondBefore = second.Line;

            Assert.DoesNotThrow(() => TypeBeatEditorOperations.SetLineStart(beatmap, second, 1010));
            Assert.That(first.Line, Is.SameAs(firstBefore));
            Assert.That(second.Line, Is.SameAs(secondBefore));
        }

        [Test]
        public void WordResizeStopsAtFiveMillisecondsAndRetainsLegalInteriorTiming()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.AddSyllableBoundary(beatmap, line, 0, 1400);

            TypeBeatEditorOperations.SetUnitTiming(beatmap, line, 0, 1700, 1700);
            var unit = line.Line.Units[0];
            Assert.That(unit.EndTime - unit.StartTime, Is.EqualTo(5));
            Assert.That(unit.SyllableBoundaries, Is.Empty);
            Assert.That(unit.SyllableSplits, Is.Empty);

            TypeBeatEditorOperations.SetUnitTiming(beatmap, line, 0, 1000, 1006);
            Assert.That(line.Line.Units[0].EndTime - line.Line.Units[0].StartTime, Is.EqualTo(6),
                "valid short words should not be forced back to the former 30ms floor");
            assertReloadStable(beatmap);
        }

        [TestCase(TypeBeatEditorOperations.UnitGroupEdit.ResizeStart, 99999)]
        [TestCase(TypeBeatEditorOperations.UnitGroupEdit.ResizeEnd, -99999)]
        public void GroupResizeStopsTheSmallestWordAtFiveMilliseconds(TypeBeatEditorOperations.UnitGroupEdit mode, double delta)
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 1);
            var starts = line.Line.Units.Select(u => u.StartTime).ToArray();
            var ends = line.Line.Units.Select(u => u.EndTime).ToArray();

            TypeBeatEditorOperations.EditUnitGroup(beatmap, line, new[] { 0, 1 }, starts, ends, delta, mode);
            Assert.That(line.Line.Units.Min(u => u.EndTime - u.StartTime), Is.EqualTo(5));
            Assert.That(line.Line.Units[0].EndTime, Is.LessThanOrEqualTo(line.Line.Units[1].StartTime));
            assertReloadStable(beatmap);
        }

        [Test]
        public void MovingAFiveMillisecondWordPreservesItsSize()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            TypeBeatEditorOperations.SetUnitTiming(beatmap, line, 0, 1000, 1000);
            TypeBeatEditorOperations.MoveUnit(beatmap, line, 0, 99999);
            Assert.That(line.Line.Units[0].StartTime, Is.EqualTo(1895));
            Assert.That(line.Line.Units[0].EndTime, Is.EqualTo(1900));
            assertReloadStable(beatmap);
        }

        [Test]
        public void TimingPasteCannotCrushAnIndividualWordBelowTheMinimum()
        {
            var beatmap = createBeatmap();
            var line = lineAt(beatmap, 0);
            var payload = new LyricTimingClipboard.UnitTimingsPayload();
            payload.Units.Add(new LyricTimingClipboard.UnitSpan { Start = 0, End = 1 });
            payload.Units.Add(new LyricTimingClipboard.UnitSpan { Start = 1, End = 1800 });

            Assert.That(TypeBeatEditorOperations.PasteUnitTimings(beatmap, line, 0, payload), Is.True);
            Assert.That(line.Line.Units.All(u => u.EndTime - u.StartTime >= 5), Is.True);
            Assert.That(line.Line.Units[0].EndTime, Is.LessThanOrEqualTo(line.Line.Units[1].StartTime));
            assertReloadStable(beatmap);
        }
    }
}
