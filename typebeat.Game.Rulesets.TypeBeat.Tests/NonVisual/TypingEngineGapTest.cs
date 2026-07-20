// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins activation when a line's vocals sit LATE in its window — the shape the editor
    /// produces when the previous line's vocals end early (shared boundaries: the next window
    /// starts at the early boundary, its vocals stay put). The line must self-activate at
    /// first target - CUE_LEAD_MS with nobody active before it. Also pins the contrasting
    /// non-shared GAP shape, where activation clamps to the line's own StartTime.
    /// </summary>
    [TestFixture]
    public class TypingEngineGapTest
    {
        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata
            {
                Artist = "Test",
                Title = "Gap",
                FolderPath = @"X:\nowhere",
                AudioFileName = "a.mp3",
            },
            Lines = lines,
            Granularity = TimingGranularity.Line,
        };

        [Test]
        public void LateVocalsAfterSharedBoundaryActivateAtCueLead()
        {
            // Shared boundary at 2000 (line 0's vocals ended early); line 1's vocals start 3000ms
            // into its own window. Activation = 5000 - CUE_LEAD (3500), NOT the boundary.
            var engine = new TypingEngine(map(
                line("ab", 1000, 2000, 2000, unit("ab", 1000, 2000)),
                line("cd", 2000, 6200, 6200, unit("cd", 5000, 6200))));

            Assert.AreEqual(5000, engine.Lines[1].Cells[0].TargetTime, "first typeable target = vocal start");
            Assert.AreEqual(5000 - TypingEngine.CUE_LEAD_MS, engine.Lines[1].ActivationTime, "activation = first target - cue lead");

            for (double t = 0; t <= 7000; t += 100)
            {
                engine.Update(t);

                if (t < 1000)
                    Assert.AreEqual(-1, engine.ActiveLineIndex, $"t={t}: pre-roll");
                else if (t < 2000)
                    Assert.AreEqual(0, engine.ActiveLineIndex, $"t={t}: line 0 active");
                else if (t >= 2100 && t < 3500)
                    Assert.AreEqual(-1, engine.ActiveLineIndex, $"t={t}: dead zone after early boundary");
                else if (t >= 3500 && t < 6200)
                    Assert.AreEqual(1, engine.ActiveLineIndex, $"t={t}: line 1 self-activated at its cue lead");
            }

            Assert.IsTrue(engine.IsFinished, "engine finished after both lines sealed");
        }

        [Test]
        public void NonSharedWindowGapClampsActivationToStartTime()
        {
            // A true window gap (2000..5000 belongs to nobody): activation clamps to StartTime.
            var engine = new TypingEngine(map(
                line("ab", 1000, 2000, 2000, unit("ab", 1000, 2000)),
                line("cd", 5000, 6200, 6200, unit("cd", 5000, 6200))));

            Assert.AreEqual(5000, engine.Lines[1].ActivationTime, "activation clamped to the line's own StartTime");

            engine.Update(4900);
            Assert.AreEqual(-1, engine.ActiveLineIndex, "nobody active during the window gap");
            engine.Update(5000);
            Assert.AreEqual(1, engine.ActiveLineIndex, "line 1 active at its StartTime");
        }
    }
}
