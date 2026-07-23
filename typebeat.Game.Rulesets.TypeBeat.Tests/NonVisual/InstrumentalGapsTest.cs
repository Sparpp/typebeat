// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

// Headless coverage of InstrumentalGaps: which purely-instrumental stretches between lyric lines
// qualify for a mid-song skip, and where the skip lands.
//
// IMPORTANT: every fixture line is built through TimingJsonLoader.BuildLines, the PRODUCTION
// decode path, never hand-assembled LyricLines. Two prior fixes to the skip passed on hand-made
// lines with a timeline hole between one line's EndTime and the next line's StartTime, a shape
// the real decoder never produces (BuildLines makes windows contiguous: a non-last line's EndTime
// IS the next line's StartMs), and both fixes were dead on arrival on real maps. Building through
// BuildLines pins the tests to the real data shape.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Rulesets.TypeBeat.Beatmaps;
using typebeat.Game.Rulesets.TypeBeat.Gameplay;
using Assert = NUnit.Framework.Legacy.ClassicAssert;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    [TestFixture]
    public class InstrumentalGapsTest
    {
        private static TimingJsonLoader.RawLine raw(string text, double startMs, double endMs, params (string text, double start, double end)[] words)
            => new TimingJsonLoader.RawLine(
                text, startMs, endMs, false,
                words.Select(w => (w.text, w.start, w.end, 1.0, new List<double>())).ToList());

        /// <summary>Runs the production decode resolution (BuildLines) then the engine flattening.</summary>
        private static IReadOnlyList<TypingLine> lines(double? songEndMs, params TimingJsonLoader.RawLine[] raws)
            => TimingJsonLoader.BuildLines(raws, songEndMs)
                               .Select(l => TypingLine.FromLyricLine(l, TimingGranularity.Word))
                               .ToList();

        [Test]
        public void RealDecodeShapeIsContiguousAndStillQualifies()
        {
            // The real-map shape verbatim: line A sings 1000-2000, line B starts (and sings from)
            // 12000. The decoder runs A's window all the way to B's start; there is NO timeline
            // hole, no dead zone, and the old "seal -> activation" mechanical window is exactly
            // zero. The 10s instrumental exists only as the perceived stretch.
            var l = lines(60000,
                raw("ab", 1000, 2000, ("ab", 1000, 1800)),
                raw("cd", 12000, 13000, ("cd", 12000, 13000)));

            Assert.AreEqual(12000, l[0].EndTime, "decoder invariant: A's window runs to B's start");
            Assert.AreEqual(12000, l[1].ActivationTime, "B activates at its own start (vocals at start)");
            Assert.AreEqual(0, l[1].ActivationTime - (l[0].EndTime + l[0].SealGraceMs), "mechanical window is zero on the real shape");

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(1, gaps.Count, "exactly-10s perceived gap qualifies");
            // Skip period opens after A's vocals (SingEnd 2000) + settle.
            Assert.AreEqual(2000 + InstrumentalGaps.GAP_START_SETTLE_MS, gaps[0].GapStartTime);
            Assert.AreEqual(12000, gaps[0].ActivationTime);
            // Skip lands 3000 ms before activation (matches the intro skip's object-minus-3000 landing).
            Assert.AreEqual(9000, gaps[0].SkipTarget);
        }

        [Test]
        public void PerceivedGapJustUnder10sDoesNotQualify()
        {
            // Vocals end 2000, next vocals 11990 -> perceived 9990 < 10000 -> no skip.
            var gaps = InstrumentalGaps.Compute(lines(60000,
                raw("ab", 1000, 2000, ("ab", 1000, 1800)),
                raw("cd", 11990, 13000, ("cd", 11990, 13000))));

            Assert.AreEqual(0, gaps.Count, "9.99s perceived gap does not qualify");
        }

        [Test]
        public void LateNextVocalsExtendTheGapAndSkipTargetTracksActivation()
        {
            // Line B's window opens right after A's vocals (2000) but its vocals do not start until
            // 20000, so it activates at 20000 - 1500 = 18500. Perceived stretch = 20000 - 2000 =
            // 18000. Skip target = 18500 - 3000 = 15500, preserving the full cue lead before the
            // next word even though the boundary sits way back at 2000.
            var l = lines(60000,
                raw("ab", 1000, 2000, ("ab", 1000, 1800)),
                raw("cd", 2000, 21000, ("cd", 20000, 21000)));

            Assert.AreEqual(18500, l[1].ActivationTime, "activation = first target - cue lead");

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(1, gaps.Count);
            Assert.AreEqual(2000 + InstrumentalGaps.GAP_START_SETTLE_MS, gaps[0].GapStartTime);
            Assert.AreEqual(18500, gaps[0].ActivationTime);
            Assert.AreEqual(15500, gaps[0].SkipTarget);
        }

        [Test]
        public void VocalsRunningDeepIntoTheGapDropTheSkip()
        {
            // Line A's REPORTED end is 2000, but its actual last word is sung 14900-15000 (a held
            // note running almost to line B at 15000). The perceived stretch measured from SingEnd
            // (2000) would qualify, but the last typeable target sits at 14900; the player is
            // still typing there, so the usable skip period (15900 -> 12000) is negative and the
            // gap is dropped rather than flashing an overlay over live typing.
            var l = lines(60000,
                raw("a b", 1000, 2000, ("a", 1000, 2000), ("b", 14900, 15000)),
                raw("cd", 15000, 16000, ("cd", 15000, 16000)));

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(0, gaps.Count, "no usable period between the sung tail and the skip target");
        }

        [Test]
        public void ConsecutiveGapsAreBothDetected()
        {
            // Three lines, two long instrumental stretches (A->B and B->C), both qualify.
            var l = lines(60000,
                raw("ab", 1000, 2000, ("ab", 1000, 1800)),
                raw("cd", 15000, 15800, ("cd", 15000, 15800)),
                raw("ef", 30000, 31000, ("ef", 30000, 31000)));

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(2, gaps.Count, "both instrumental stretches detected");

            Assert.AreEqual(2000 + InstrumentalGaps.GAP_START_SETTLE_MS, gaps[0].GapStartTime);
            Assert.AreEqual(15000, gaps[0].ActivationTime);
            Assert.AreEqual(12000, gaps[0].SkipTarget);

            Assert.AreEqual(15800 + InstrumentalGaps.GAP_START_SETTLE_MS, gaps[1].GapStartTime);
            Assert.AreEqual(30000, gaps[1].ActivationTime);
            Assert.AreEqual(27000, gaps[1].SkipTarget);
        }

        [Test]
        public void LastLineOutroIsNotAGap()
        {
            // Line B is the last line; whatever instrumental follows it is the outro, handled by
            // the outro flow, never by a mid-song gap; Compute only looks between lines.
            var l = lines(60000,
                raw("ab", 1000, 2000, ("ab", 1000, 1800)),
                raw("cd", 12000, 12800, ("cd", 12000, 12800)));

            var gaps = InstrumentalGaps.Compute(l);

            Assert.AreEqual(1, gaps.Count, "only the A->B gap, never a post-last-line gap");
            Assert.AreEqual(12000, gaps[0].ActivationTime);
        }

        [Test]
        public void FirstLineLeadInIsNotAGap()
        {
            // A long lead-in before the first line (first line starts at 30000). The intro is
            // handled by the existing intro SkipOverlay, never by an instrumental gap.
            var gaps = InstrumentalGaps.Compute(lines(60000,
                raw("ab", 30000, 31000, ("ab", 30000, 31000)),
                raw("cd", 31000, 32000, ("cd", 31000, 32000))));

            Assert.AreEqual(0, gaps.Count, "the pre-first-line lead-in is not an instrumental gap");
        }

        [Test]
        public void ShortGapsBetweenNormalLinesDoNotQualify()
        {
            // Densely packed lines: every inter-line stretch is well under 10s.
            var gaps = InstrumentalGaps.Compute(lines(60000,
                raw("ab", 1000, 2000, ("ab", 1000, 2000)),
                raw("cd", 3000, 4000, ("cd", 3000, 4000)),
                raw("ef", 5000, 6000, ("ef", 5000, 6000))));

            Assert.AreEqual(0, gaps.Count, "no long instrumental stretches");
        }

        [Test]
        public void EmptyOrSingleLineHasNoGaps()
        {
            Assert.AreEqual(0, InstrumentalGaps.Compute(new List<TypingLine>()).Count, "no lines");
            Assert.AreEqual(0, InstrumentalGaps.Compute(lines(60000, raw("ab", 1000, 2000, ("ab", 1000, 2000)))).Count, "single line");
        }
    }
}
