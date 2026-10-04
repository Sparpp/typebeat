// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Beatmaps.ControlPoints;
using typebeat.Game.Rulesets.Judgements;
using typebeat.Game.Rulesets.Scoring;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using typebeat.Game.Rulesets.TypeBeat.Objects;
using typebeat.Game.Rulesets.TypeBeat.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// The two halves the hit error meter reads, pinned without a meter: a result's
    /// <see cref="JudgementResult.TimeOffset"/> (where its tick lands) and the hit windows (where the
    /// coloured bands are drawn). Both must describe what the engine judged, not the default ladder
    /// and the cell's point target.
    /// </summary>
    [TestFixture]
    public class HitErrorOffsetTest
    {
        private static JudgementResult resultAt(double startTime, double rawTime)
        {
            var charObject = new TypeBeatCharObject { StartTime = startTime };
            charObject.ApplyDefaults(new ControlPointInfo(), new BeatmapDifficulty());

            return new JudgementResult(charObject, charObject.CreateJudgement())
            {
                Type = HitResult.Great,
                RawTime = rawTime,
            };
        }

        [Test]
        public void WithoutAJudgedOffsetTheOffsetIsTheDistanceFromTheObject()
        {
            Assert.That(resultAt(1000, 1100).TimeOffset, Is.EqualTo(100));
        }

        [Test]
        public void AJudgedOffsetReplacesTheDistanceFromTheObject()
        {
            // A press 400 ms after the cell's point target that the engine graded dead on (inside the
            // sung span): the tick belongs at 0, where the Great it scored is.
            var result = resultAt(1000, 1400);
            result.JudgedTimeOffset = 0;

            Assert.That(result.TimeOffset, Is.EqualTo(0));
            Assert.That(result.TimeAbsolute, Is.EqualTo(1400), "the real judgement time is untouched");
        }

        [Test]
        public void AJudgedOffsetIsClampedLikeTheRawOne()
        {
            var result = resultAt(1000, 1000);
            result.JudgedTimeOffset = 5000;

            Assert.That(result.TimeOffset, Is.EqualTo(result.HitObject.MaximumJudgementOffset));
        }

        [Test]
        public void ResettingAResultClearsItsJudgedOffset()
        {
            var result = resultAt(1000, 1400);
            result.JudgedTimeOffset = -250;

            result.Reset();

            Assert.That(result.JudgedTimeOffset, Is.Null);
            Assert.That(result.TimeOffset, Is.EqualTo(0));
        }

        [Test]
        public void HitWindowsFollowTheirSource()
        {
            var windows = new TypeBeatHitWindows();

            Assert.That(windows.WindowFor(HitResult.Great), Is.EqualTo(SyncWindows.Default.GreatLate), "the default ladder with no source");

            var doubled = SyncWindows.Default.Scaled(2);
            windows.WindowsSource = () => doubled;

            Assert.That(windows.WindowFor(HitResult.Great), Is.EqualTo(doubled.GreatLate));
            Assert.That(windows.WindowFor(HitResult.Ok), Is.EqualTo(doubled.OkLate));
            Assert.That(windows.WindowFor(HitResult.Meh), Is.EqualTo(doubled.MehLate));
            Assert.That(windows.WindowFor(HitResult.Miss), Is.EqualTo(doubled.MehLate));
        }
    }
}
