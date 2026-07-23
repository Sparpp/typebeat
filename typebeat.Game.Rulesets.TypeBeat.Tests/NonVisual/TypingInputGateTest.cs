// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// <see cref="TypingEngine.LineIsActive"/> is the seam the playfield's raw key handler gates on:
    /// a typeable key (Space included; it is both a character and the default skip binding) is
    /// consumed for typing only while a line is active, and otherwise falls through to global
    /// bindings so the skip overlay can act. This pins the predicate across the pre-roll, an active
    /// line, a mid-song dead zone and re-activation: the transitions the key handler depends on.
    /// </summary>
    [TestFixture]
    public class TypingInputGateTest
    {
        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static LyricBeatmap map(params LyricLine[] lines) => new LyricBeatmap
        {
            Metadata = new LyricBeatmapMetadata { Artist = "T", Title = "S", FolderPath = @"X:\n", AudioFileName = "a.mp3" },
            Lines = lines,
            Granularity = TimingGranularity.Word,
        };

        [Test]
        public void LineIsActiveMirrorsActiveLineIndex()
        {
            // Line 0 active [1000, 2000); line 1's first word is at 12000, so it self-activates at
            // 10500, a >8s dead zone in between where nothing is active.
            var l0 = line("ab", 1000, 2000, 2000, unit("ab", 1000, 2000));
            var l1 = line("cd", 2000, 13000, 13000, unit("cd", 12000, 13000));
            var engine = new TypingEngine(map(l0, l1));

            // Pre-roll: before the first line's cue nothing is active; Space must NOT be consumed.
            engine.Update(0);
            Assert.AreEqual(-1, engine.ActiveLineIndex);
            Assert.AreEqual(false, engine.LineIsActive, "pre-roll: key falls through to global bindings");

            // Line 0 active: every typeable key (Space included) is consumed for typing.
            engine.Update(1500);
            Assert.AreEqual(0, engine.ActiveLineIndex);
            Assert.AreEqual(true, engine.LineIsActive, "active line: typing consumed");

            // Dead zone after line 0 seals, before line 1's cue: fall through so Space can skip.
            engine.Update(4000);
            Assert.AreEqual(-1, engine.ActiveLineIndex, "dead zone: nothing active");
            Assert.AreEqual(false, engine.LineIsActive, "dead zone: key falls through so Space can skip");

            // Line 1 activates at its cue lead: typing consumption resumes exactly here.
            engine.Update(engine.Lines[1].ActivationTime);
            Assert.AreEqual(1, engine.ActiveLineIndex);
            Assert.AreEqual(true, engine.LineIsActive, "line 1 active: typing consumed again");
        }

        [Test]
        public void LineIsActiveIsFalseOnceEveryLineIsSealed()
        {
            var engine = new TypingEngine(map(line("ab", 1000, 2000, 2000, unit("ab", 1000, 2000))));

            engine.Update(1500);
            Assert.AreEqual(true, engine.LineIsActive);

            // Past the only line's seal: finished, nothing active, keys fall through.
            engine.Update(10000);
            Assert.IsTrue(engine.IsFinished);
            Assert.AreEqual(false, engine.LineIsActive, "after the final line: key falls through");
        }
    }
}
