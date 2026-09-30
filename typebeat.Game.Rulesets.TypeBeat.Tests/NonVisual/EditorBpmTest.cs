// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Beatmaps.Timing;
using typebeat.Game.IO;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Screens.Edit;
using typebeat.Game.Screens.Edit.Timing;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class EditorBpmTest
    {
        [SetUp]
        public void SetUp() => LyricBeatmapDecoder.Register();

        [TestCase(1, 500)]
        [TestCase(2, 250)]
        [TestCase(4, 125)]
        [TestCase(8, 62.5)]
        [TestCase(16, 31.25)]
        [TestCase(3, 500.0 / 3)]
        public void GridAndSnapAgreeAtFractionalOffset(int divisor, double step)
        {
            var points = new ControlPointInfo();
            points.Add(123.456, new TimingControlPoint { BeatLength = 500 });
            var settings = new EditorTimingSettings();
            settings.SnapToGrid.Value = true;
            var ticks = EditorBeatGrid.Ticks(points, divisor, 123.456, 1123.456).ToArray();
            Assert.That(ticks[1].Time - ticks[0].Time, Is.EqualTo(step).Within(1e-9));
            foreach (var tick in ticks)
                Assert.That(settings.Snap(tick.Time + step * 0.2, points, divisor), Is.EqualTo(tick.Time).Within(1e-9));
        }

        [TestCase(2)]
        [TestCase(4)]
        public void HalfAndWholeNotesMatchGrid(int multiplier)
        {
            var points = new ControlPointInfo();
            points.Add(10.25, new TimingControlPoint { BeatLength = 500 });
            var settings = new EditorTimingSettings();
            settings.SnapToGrid.Value = true;
            settings.BeatMultiplier.Value = multiplier;
            var ticks = EditorBeatGrid.Ticks(points, 1, 10.25, 5010.25, beatMultiplier: multiplier).ToArray();
            Assert.That(ticks[1].Time, Is.EqualTo(10.25 + 500 * multiplier));
            Assert.That(settings.Snap(ticks[1].Time + 100, points, 1), Is.EqualTo(ticks[1].Time));
        }

        [Test]
        public void TimingChangeStartsANewGridAndSnapUsesItsBoundary()
        {
            var points = new ControlPointInfo();
            points.Add(0, new TimingControlPoint { BeatLength = 500 });
            points.Add(1070.5, new TimingControlPoint { BeatLength = 400, TimeSignature = new TimeSignature(3) });
            var ticks = EditorBeatGrid.Ticks(points, 4, 900, 2400).ToArray();
            Assert.That(ticks.Select(t => t.Time), Does.Contain(1070.5));
            Assert.That(ticks.Select(t => t.Time), Does.Not.Contain(1125));
            Assert.That(ticks.First(t => t.Time == 1070.5).IsBar, Is.True);
            Assert.That(ticks.First(t => Math.Abs(t.Time - 2270.5) < 1e-9).IsBar, Is.True);
            var settings = new EditorTimingSettings();
            settings.SnapToGrid.Value = true;
            Assert.That(settings.Snap(1060, points, 4), Is.EqualTo(1070.5));
        }

        [Test]
        public void NoTimingAndDisabledSnappingLeaveInputUntouched()
        {
            var settings = new EditorTimingSettings();
            var points = new ControlPointInfo();
            settings.SnapToGrid.Value = true;
            Assert.That(settings.Snap(234.567, points, 4), Is.EqualTo(234.567));
            Assert.That(EditorBeatGrid.Ticks(points, 4, 0, 1000), Is.Empty);
            points.Add(0, new TimingControlPoint { BeatLength = 500 });
            settings.SnapToGrid.Value = false;
            settings.ShowGrid.Value = true;
            Assert.That(settings.Snap(234.567, points, 4), Is.EqualTo(234.567));
        }

        [Test]
        public void NegativeOffsetAndOmittedBarAreHandled()
        {
            var points = new ControlPointInfo();
            points.Add(-100.25, new TimingControlPoint { BeatLength = 500, OmitFirstBarLine = true });
            var ticks = EditorBeatGrid.Ticks(points, 4, 0, 1000).ToArray();
            Assert.That(ticks[0].Time, Is.EqualTo(24.75));
            Assert.That(ticks.All(t => t.Time >= 0), Is.True);
            points.Clear();
            points.Add(100.25, new TimingControlPoint { BeatLength = 500, OmitFirstBarLine = true });
            Assert.That(EditorBeatGrid.Ticks(points, 4, 100.25, 600.25).First().IsBar, Is.False);
        }

        [Test]
        public void HighBpmGridDrawingIsBounded()
        {
            var points = new ControlPointInfo();
            points.Add(0, new TimingControlPoint { BeatLength = 6 });
            var ticks = EditorBeatGrid.Ticks(points, 64, 0, 120000, minimumSpacing: 400).ToArray();
            Assert.That(ticks.Length, Is.LessThanOrEqualTo(301));
        }

        [Test]
        public void SaveReloadPreservesBpmPrecisionMeterAndEffectsInAnyCulture()
        {
            using var beatmap = createBeatmap();
            TimingPointOperations.Set(beatmap, null, -123.456789, 137.123456789, 3);
            var second = TimingPointOperations.Set(beatmap, null, 8012.345678, 89.987654321, 7);
            second.OmitFirstBarLine = true;
            beatmap.ControlPointInfo.Add(9000.25, new EffectControlPoint { KiaiMode = true });
            beatmap.BeatmapInfo.BeatDivisor = 8;
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                using var writer = new StringWriter();
                TypeBeatBeatmapEncoder.Encode(beatmap, writer);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(writer.ToString()));
                using var reader = new LineBufferedReader(stream);
                var decoded = Game.Beatmaps.Formats.Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
                Assert.That(decoded.ControlPointInfo.TimingPoints, Is.EqualTo(beatmap.ControlPointInfo.TimingPoints));
                Assert.That(decoded.ControlPointInfo.EffectPointAt(9000.25).KiaiMode, Is.True);
                Assert.That(decoded.BeatmapInfo.BeatDivisor, Is.EqualTo(8));
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        [Test]
        public void AddEditDeleteUndoRedoRestoresTimingAndSharedCollection()
        {
            using var beatmap = createBeatmap();
            var collection = beatmap.ControlPointInfo;
            using var handler = new RulesetBeatmapChangeHandler(beatmap, new TypeBeatRuleset());
            var point = TimingPointOperations.Set(beatmap, null, 123.456, 120, 4);
            point = TimingPointOperations.Set(beatmap, point, 234.567, 137.5, 3);
            handler.RestoreState(-1);
            Assert.That(beatmap.ControlPointInfo, Is.SameAs(collection));
            Assert.That(collection.TimingPoints.Single().Time, Is.EqualTo(123.456));
            Assert.That(collection.TimingPoints.Single().BPM, Is.EqualTo(120));
            handler.RestoreState(1);
            Assert.That(collection.TimingPoints.Single().Time, Is.EqualTo(234.567));
            Assert.That(collection.TimingPoints.Single().BPM, Is.EqualTo(137.5));
            TimingPointOperations.Delete(beatmap, collection.TimingPoints.Single());
            Assert.That(collection.TimingPoints, Is.Empty);
            handler.RestoreState(-1);
            Assert.That(collection.TimingPoints.Single().TimeSignature.Numerator, Is.EqualTo(3));
            handler.RestoreState(1);
            Assert.That(collection.TimingPoints, Is.Empty);
        }

        [TestCase(double.NaN, 120, 4)]
        [TestCase(0, double.PositiveInfinity, 4)]
        [TestCase(0, 0, 4)]
        [TestCase(0, 120, 0)]
        public void InvalidTimingDoesNotMutateMap(double offset, double bpm, int meter)
        {
            using var beatmap = createBeatmap();
            Assert.Throws<ArgumentOutOfRangeException>(() => TimingPointOperations.Set(beatmap, null, offset, bpm, meter));
            Assert.That(beatmap.ControlPointInfo.TimingPoints, Is.Empty);
            Assert.That(beatmap.TransactionActive, Is.False);
        }

        private static EditorBeatmap createBeatmap()
        {
            var beatmap = new Beatmap();
            beatmap.BeatmapInfo.Ruleset = new TypeBeatRuleset().RulesetInfo;
            return new EditorBeatmap(beatmap);
        }
    }
}
