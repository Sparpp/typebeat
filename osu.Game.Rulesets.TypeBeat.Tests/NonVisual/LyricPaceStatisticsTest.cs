// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Game.Rulesets.TypeBeat.Beatmaps;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace osu.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class LyricPaceStatisticsTest
    {
        [Test]
        public void ComputesPerfectPlayPace()
        {
            // "ab cd": 5 typeable cells (a, b, space, c, d). Targets: a=1000, b=1500,
            // space=2000, c=2000, d=2500 — perfect-play active window = 2500 - 1000 = 1500 ms.
            // CPM = 5 / (1500ms / 60000) = 200; WPM = 200 / 5 = 40.
            var line = new LyricLine
            {
                RawText = "ab cd",
                StartTime = 1000,
                EndTime = 4000,
                SingEndTime = 3000,
                Units = new[]
                {
                    new TimedUnit { Text = "ab", StartTime = 1000, EndTime = 2000 },
                    new TimedUnit { Text = "cd", StartTime = 2000, EndTime = 3000 },
                },
            };

            var pace = LyricPaceStatistics.Compute(new[] { line }, TimingGranularity.Word);

            Assert.AreEqual(5, pace.TypeableCellCount);
            Assert.AreEqual(40.0, pace.AverageWpm, 1e-9);
            Assert.AreEqual(200.0, pace.AverageCpm, 1e-9);
        }

        [Test]
        public void EmptyMapIsZero()
        {
            var pace = LyricPaceStatistics.Compute(Array.Empty<LyricLine>(), TimingGranularity.Line);

            Assert.AreEqual(0, pace.TypeableCellCount);
            Assert.AreEqual(0, pace.AverageWpm);
        }
    }
}
