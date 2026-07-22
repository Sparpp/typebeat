// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Headless coverage of InstrumentalGaps: which purely-instrumental stretches between lyric lines
// qualify for a mid-song skip, and where the skip lands. Times are hand-computed round numbers.
// TypingLine.FromLyricLine is the same flattening the gameplay engine uses, so ActivationTime and
// SealGraceMs here match live gameplay exactly.

using System.Collections.Generic;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class InstrumentalGapsTest
    {
        private static TimedUnit unit(string text, double start, double end)
            => new TimedUnit { Text = text, StartTime = start, EndTime = end };

        private static LyricLine line(string text, double start, double end, double singEnd, params TimedUnit[] units)
            => new LyricLine { RawText = text, StartTime = start, EndTime = end, SingEndTime = singEnd, Units = units };

        private static IReadOnlyList<TypingLine> lines(params LyricLine[] source)
        {
            var list = new List<TypingLine>(source.Length);

            foreach (var l in source)
                list.Add(TypingLine.FromLyricLine(l, TimingGranularity.Line));

            return list;
        }

        // A short vocal line ending well before its EndTime, with no boundary grace (its last target
        // sits far from EndTime). Seal == EndTime.
        private static LyricLine plainLine(string text, double start, double end)
            => line(text, start, end, start + 400, unit(text, start, start + 400));

        [Test]
        public void GapExactly10sQualifies()
        {
            // Line A seals at 2000 (grace 0). Line B activates at its StartTime 12000
            // (first target 12000, cue lead would be 10500, clamped up to StartTime).
            // Gap = 12000 - 2000 = 10000 == MIN_GAP_MS -> qualifies.
            var l = lines(
                line("ab", 1000, 2000, 1800, unit("ab", 1000, 1800)),
                line("cd", 12000, 13000, 13000, unit("cd", 12000, 13000)));

            Assert.AreEqual(2000, l[0].EndTime + l[0].SealGraceMs, "line A seal");
            Assert.AreEqual(12000, l[1].ActivationTime, "line B activation");

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(1, gaps.Count, "exactly-10s gap qualifies");
            Assert.AreEqual(2000, gaps[0].SealTime);
            Assert.AreEqual(12000, gaps[0].ActivationTime);
            Assert.AreEqual(10000, gaps[0].Duration);
            // Skip lands 3000 ms before activation (matches the intro skip's object-minus-3000 landing).
            Assert.AreEqual(9000, gaps[0].SkipTarget);
        }

        [Test]
        public void GapJustUnder10sDoesNotQualify()
        {
            // Line B activates at 11990 -> gap = 9990 < 10000 -> no skip.
            var gaps = InstrumentalGaps.Compute(lines(
                line("ab", 1000, 2000, 1800, unit("ab", 1000, 1800)),
                line("cd", 11990, 13000, 13000, unit("cd", 11990, 13000))));

            Assert.AreEqual(0, gaps.Count, "9.99s gap does not qualify");
        }

        [Test]
        public void SealGraceIsCountedIntoTheGapStart()
        {
            // Line A's single target sits ON its EndTime boundary -> min boundary grace 250 ms.
            // Seal = 2000 + 250 = 2250. Line B activates at 12000. Gap = 9750 < 10000 -> no skip,
            // whereas measuring from EndTime (2000) would have been exactly 10000. Proves the seal
            // grace (the last typeable instant) is included in the gap start.
            var l = lines(
                line("a", 1000, 2000, 2000, unit("a", 2000, 2000)),
                line("cd", 12000, 13000, 13000, unit("cd", 12000, 13000)));

            Assert.AreEqual(250, l[0].SealGraceMs, "boundary-pinned last target grants 250 ms grace");

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(0, gaps.Count, "grace pushes the seal past the 10s threshold");
        }

        [Test]
        public void LateNextVocalsExtendTheGapAndSkipTargetTracksActivation()
        {
            // Shared boundary at 2000 (line A's window runs to 2000, its vocals end early). Line B's
            // window opens at 2000 but its vocals start at 20000, so it activates at 20000 - 1500 =
            // 18500. Instrumental gap = 18500 - 2000 = 16500 (>= 10s). Skip target = 18500 - 3000 =
            // 15500, preserving the full cue lead before the next word.
            var l = lines(
                line("ab", 1000, 2000, 1800, unit("ab", 1000, 1800)),
                line("cd", 2000, 21000, 21000, unit("cd", 20000, 21000)));

            Assert.AreEqual(18500, l[1].ActivationTime, "activation = first target - cue lead");

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(1, gaps.Count);
            Assert.AreEqual(2000, gaps[0].SealTime);
            Assert.AreEqual(18500, gaps[0].ActivationTime);
            Assert.AreEqual(15500, gaps[0].SkipTarget);
        }

        [Test]
        public void ConsecutiveGapsAreBothDetected()
        {
            // Three lines, two long instrumental stretches (A->B and B->C), both qualify.
            var l = lines(
                line("ab", 1000, 2000, 1800, unit("ab", 1000, 1800)),
                line("cd", 15000, 16000, 15800, unit("cd", 15000, 15800)),
                line("ef", 30000, 31000, 31000, unit("ef", 30000, 31000)));

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(2, gaps.Count, "both instrumental stretches detected");

            Assert.AreEqual(2000, gaps[0].SealTime);
            Assert.AreEqual(15000, gaps[0].ActivationTime);
            Assert.AreEqual(12000, gaps[0].SkipTarget);

            Assert.AreEqual(16000, gaps[1].SealTime); // line B seal (grace 0)
            Assert.AreEqual(30000, gaps[1].ActivationTime);
            Assert.AreEqual(27000, gaps[1].SkipTarget);
        }

        [Test]
        public void LastLineOutroIsNotAGap()
        {
            // Line B is the last line and has a very long tail (EndTime far past its vocals). Because
            // there is no next line, the trailing instrumental is never a skippable gap.
            var l = lines(
                plainLine("ab", 1000, 2000),
                line("cd", 12000, 40000, 12800, unit("cd", 12000, 12800)));

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(1, gaps.Count, "only the A->B gap, never a post-last-line gap");
            Assert.AreEqual(12000, gaps[0].ActivationTime);

            foreach (var g in gaps)
                Assert.Less(g.SealTime, l[1].EndTime, "no gap begins at or after the final line's end");
        }

        [Test]
        public void FirstLineLeadInIsNotAGap()
        {
            // A long lead-in before the first line (first line starts at 30000). The intro is handled
            // by the existing intro SkipOverlay, never by an instrumental gap — Compute only looks
            // between lines, so nothing here qualifies.
            var gaps = InstrumentalGaps.Compute(lines(
                line("ab", 30000, 31000, 31000, unit("ab", 30000, 31000)),
                plainLine("cd", 31000, 32000)));

            Assert.AreEqual(0, gaps.Count, "the pre-first-line lead-in is not an instrumental gap");
        }

        [Test]
        public void ShortGapsBetweenNormalLinesDoNotQualify()
        {
            // Densely packed lines: every inter-line stretch is well under 10s.
            var gaps = InstrumentalGaps.Compute(lines(
                plainLine("ab", 1000, 2000),
                plainLine("cd", 3000, 4000),
                plainLine("ef", 5000, 6000)));

            Assert.AreEqual(0, gaps.Count, "no long instrumental stretches");
        }

        [Test]
        public void EmptyOrSingleLineHasNoGaps()
        {
            Assert.AreEqual(0, InstrumentalGaps.Compute(lines()).Count, "no lines");
            Assert.AreEqual(0, InstrumentalGaps.Compute(lines(plainLine("ab", 1000, 2000))).Count, "single line");
        }
    }
}
