// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the replay frame format's legacy (.osr) mapping: a frame is (integral time, char code in
    /// MouseX, config flags in MouseY), and encode/decode must round-trip every value on the
    /// typeable surface (a-z / A-Z / 0-9 / space) plus all three sentinels (backspace, line skip,
    /// config) exactly.
    /// The decoder path being simulated is <c>LegacyScoreDecoder.convertFrame</c>: FromLegacy is
    /// called first, then Time is overwritten with the integral legacy time.
    /// </summary>
    [TestFixture]
    public class TypeBeatReplayFrameTest
    {
        private static readonly Beatmap dummy_beatmap = new Beatmap();

        /// <summary>Encode to legacy, then decode the way the score decoder does.</summary>
        private static TypeBeatReplayFrame roundTrip(TypeBeatReplayFrame original)
        {
            LegacyReplayFrame legacy = original.ToLegacy(dummy_beatmap);

            // The legacy format stores integral frame times; recorded frames are integral already.
            double storedTime = Math.Round(legacy.Time);

            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(new LegacyReplayFrame(storedTime, legacy.MouseX, legacy.MouseY, legacy.ButtonState), dummy_beatmap);
            decoded.Time = storedTime; // LegacyScoreDecoder.convertFrame overwrites Time after FromLegacy.

            return decoded;
        }

        [TestCase('a')]
        [TestCase('z')]
        [TestCase('A')] // Shift-cased capitals matter under the Literate mod.
        [TestCase('Z')]
        [TestCase('0')]
        [TestCase('9')]
        [TestCase(' ')]
        public void CharacterFramesRoundTrip(char c)
        {
            var frame = roundTrip(new TypeBeatReplayFrame(1234, c));

            Assert.AreEqual(1234, frame.Time);
            Assert.AreEqual(c, frame.Character);
            Assert.IsFalse(frame.IsBackspace);
            Assert.IsFalse(frame.IsEnter);
            Assert.IsFalse(frame.IsConfig);
        }

        [Test]
        public void BackspaceFrameRoundTrips()
        {
            var frame = roundTrip(new TypeBeatReplayFrame(56789, TypeBeatReplayFrame.BACKSPACE));

            Assert.AreEqual(56789, frame.Time);
            Assert.IsTrue(frame.IsBackspace);
            Assert.IsFalse(frame.IsEnter);
            Assert.IsFalse(frame.IsConfig);
        }

        /// <summary>
        /// The third sentinel (backlog 241): a LINE SKIP frame survives the legacy encoding exactly as
        /// the other two do, and carries no config flags of its own.
        /// </summary>
        [Test]
        public void EnterFrameRoundTrips()
        {
            var frame = roundTrip(new TypeBeatReplayFrame(56789, TypeBeatReplayFrame.ENTER));

            Assert.AreEqual(56789, frame.Time);
            Assert.IsTrue(frame.IsEnter);
            Assert.IsFalse(frame.IsBackspace);
            Assert.IsFalse(frame.IsConfig);
            Assert.AreEqual(0f, new TypeBeatReplayFrame(56789, TypeBeatReplayFrame.ENTER).ToLegacy(dummy_beatmap).MouseY);
        }

        /// <summary>
        /// The sentinels are DISTINCT and all three sit below the typeable surface, whose lowest code
        /// point is the space at 0x20. That is what lets the frame's character field carry both kinds
        /// of thing at once, and what lets the feeder tell an unknown future sentinel from a real
        /// keystroke by magnitude alone rather than by an era bit.
        /// </summary>
        [Test]
        public void TheThreeSentinelsAreDistinctAndBelowEveryTypeableCharacter()
        {
            char[] sentinels = { TypeBeatReplayFrame.CONFIG, TypeBeatReplayFrame.BACKSPACE, TypeBeatReplayFrame.ENTER };

            Assert.AreEqual(sentinels.Length, sentinels.Distinct().Count());

            foreach (char sentinel in sentinels)
                Assert.Less(sentinel, ' ', "a sentinel that reached the typeable surface would collide with a real keystroke");

            Assert.AreEqual(0x00, TypeBeatReplayFrame.CONFIG);
            Assert.AreEqual(0x08, TypeBeatReplayFrame.BACKSPACE);
            Assert.AreEqual(0x0A, TypeBeatReplayFrame.ENTER);
        }

        /// <summary>
        /// Every judgement-relevant setting travels in the one flags word, and all hundred and
        /// twenty-eight combinations must survive: bit 0 = allow-wrong-input, bit 1 =
        /// space-skips-word, bit 2 = syllable-span timing (backlog 179), bit 3 =
        /// wrong-input-on-word-gaps (backlog 181), bit 4 = strict spaces (backlog 184), bit 6 =
        /// char-timed stretches (backlog 209), bit 7 = the bounded rush (backlog 218). Bit 5, the
        /// flexible-lines era, travels with its own fixtures in <c>FletcherEngineTest</c>, which is
        /// where its two halves (the caret and the snap) can be told apart.
        /// </summary>
        [Test]
        public void ConfigFrameRoundTripsEverySettingBit(
            [Values] bool allowWrongInput,
            [Values] bool spaceSkipsWord,
            [Values] bool syllableTiming,
            [Values] bool wrongInputOnWordGaps,
            [Values] bool strictSpaces,
            [Values] bool charTimedStretch,
            [Values] bool boundedRush)
        {
            var frame = roundTrip(TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput, spaceSkipsWord, syllableTiming, wrongInputOnWordGaps, strictSpaces, charTimedStretch, boundedRush: boundedRush));

            Assert.AreEqual(500, frame.Time);
            Assert.IsTrue(frame.IsConfig);
            Assert.IsFalse(frame.IsBackspace);
            Assert.AreEqual(allowWrongInput, frame.AllowWrongInput);
            Assert.AreEqual(spaceSkipsWord, frame.SpaceSkipsWord);
            Assert.AreEqual(syllableTiming, frame.SyllableTiming);
            Assert.AreEqual(wrongInputOnWordGaps, frame.WrongInputOnWordGaps);
            Assert.AreEqual(strictSpaces, frame.StrictSpaces);
            Assert.AreEqual(charTimedStretch, frame.CharTimedStretch);
            Assert.AreEqual(boundedRush, frame.BoundedRush);
        }

        /// <summary>The bits are at the positions the format names, so the encoded word is readable
        /// as a number: a replay of live play today (wrong input allowed, no word skipping, syllable
        /// judgement, gap typos typed through, strict spaces, char-timed stretches, flexible lines
        /// and the bounded rush) is exactly 1 | 4 | 8 | 16 | 32 | 64 | 128 = 253, and every bit set
        /// is 255. Take bit 7 back off and it is the 125 a replay carried the day before backlog
        /// 218; take bit 5 with it and the 93 it carried before backlog 208; then the 29 of the day
        /// before backlog 209, and the 13 of the day before backlog 184.</summary>
        [Test]
        public void TheFlagsWordIsExactlyTheDocumentedBitPositions()
        {
            Assert.AreEqual(255f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(253f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true, boundedRush: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(125f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true, flexibleLines: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(93f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(95f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true, charTimedStretch: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(29f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(31f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true, wrongInputOnWordGaps: true, strictSpaces: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(13f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true, wrongInputOnWordGaps: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(15f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true, wrongInputOnWordGaps: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(5f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: false, syllableTiming: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(7f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: true, spaceSkipsWord: true, syllableTiming: true).ToLegacy(dummy_beatmap).MouseY);
            Assert.AreEqual(0f, TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput: false).ToLegacy(dummy_beatmap).MouseY);
        }

        /// <summary>
        /// Bit 0 keeps the exact meaning every replay already on disk was written with, so a stored
        /// flags word of 0 or 1 (the only two values that existed before space-skip) still decodes to
        /// the run it recorded, with the newer bit reading false.
        /// </summary>
        [TestCase(0, false)]
        [TestCase(1, true)]
        public void ReplaysRecordedBeforeSpaceSkipDecodeUnchanged(int storedFlags, bool expectedAllowWrongInput)
        {
            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(new LegacyReplayFrame(500, (float)TypeBeatReplayFrame.CONFIG, storedFlags, ReplayButtonState.None), dummy_beatmap);

            Assert.IsTrue(decoded.IsConfig);
            Assert.AreEqual(expectedAllowWrongInput, decoded.AllowWrongInput);
            Assert.IsFalse(decoded.SpaceSkipsWord, "a replay from before the setting existed was played without it");
        }

        /// <summary>
        /// The same guarantee one bit up, and the one that carries a JUDGEMENT ERA (backlog 179):
        /// 0..3 are the only flags words that existed before syllable-span timing, and every one of
        /// them must decode with bit 2 clear, i.e. to the classic point-target rule those runs were
        /// actually judged on. The two older bits must still read exactly what they always did.
        /// </summary>
        [TestCase(0, false, false)]
        [TestCase(1, true, false)]
        [TestCase(2, false, true)]
        [TestCase(3, true, true)]
        public void ReplaysRecordedBeforeSyllableTimingDecodeAsClassic(int storedFlags, bool expectedAllowWrongInput, bool expectedSpaceSkipsWord)
        {
            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(new LegacyReplayFrame(500, (float)TypeBeatReplayFrame.CONFIG, storedFlags, ReplayButtonState.None), dummy_beatmap);

            Assert.IsTrue(decoded.IsConfig);
            Assert.AreEqual(expectedAllowWrongInput, decoded.AllowWrongInput);
            Assert.AreEqual(expectedSpaceSkipsWord, decoded.SpaceSkipsWord);
            Assert.IsFalse(decoded.SyllableTiming, "a replay from before the rule existed was played on point targets");
        }

        /// <summary>
        /// The same guarantee for backlog 209's bit, and it carries a JUDGEMENT ERA too: 0..31 are
        /// the only flags words that existed before the stretch narrowing, so every one of them must
        /// decode with bit 6 clear, i.e. to the pure span rule those runs were judged on, mashed
        /// freestyle sections and all. The five older bits must still read what they always did.
        /// </summary>
        [TestCase(0)]
        [TestCase(13)]
        [TestCase(29)]
        [TestCase(31)]
        public void ReplaysRecordedBeforeCharTimedStretchDecodeOnPureSpans(int storedFlags)
        {
            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(new LegacyReplayFrame(500, (float)TypeBeatReplayFrame.CONFIG, storedFlags, ReplayButtonState.None), dummy_beatmap);

            Assert.IsTrue(decoded.IsConfig);
            Assert.AreEqual((storedFlags & 1) != 0, decoded.AllowWrongInput);
            Assert.AreEqual((storedFlags & 2) != 0, decoded.SpaceSkipsWord);
            Assert.AreEqual((storedFlags & 4) != 0, decoded.SyllableTiming);
            Assert.AreEqual((storedFlags & 8) != 0, decoded.WrongInputOnWordGaps);
            Assert.AreEqual((storedFlags & 16) != 0, decoded.StrictSpaces);
            Assert.IsFalse(decoded.CharTimedStretch, "a replay from before the rule existed was judged on whole syllable spans");
        }

        /// <summary>
        /// The same guarantee for backlog 218's bit, which carries a CARET era rather than a
        /// judgement one: 0..127 are the only flags words that existed before the rush bound, so
        /// every one of them must decode with bit 7 clear, i.e. to the unbounded roll those runs were
        /// played with (finish a line and you are on the next one, however far off its cue it is).
        /// The seven older bits must still read exactly what they always did.
        /// </summary>
        [TestCase(0)]
        [TestCase(29)]
        [TestCase(93)]
        [TestCase(125)]
        [TestCase(127)]
        public void ReplaysRecordedBeforeTheRushBoundDecodeUnbounded(int storedFlags)
        {
            var decoded = new TypeBeatReplayFrame();
            decoded.FromLegacy(new LegacyReplayFrame(500, (float)TypeBeatReplayFrame.CONFIG, storedFlags, ReplayButtonState.None), dummy_beatmap);

            Assert.IsTrue(decoded.IsConfig);
            Assert.AreEqual((storedFlags & 1) != 0, decoded.AllowWrongInput);
            Assert.AreEqual((storedFlags & 2) != 0, decoded.SpaceSkipsWord);
            Assert.AreEqual((storedFlags & 4) != 0, decoded.SyllableTiming);
            Assert.AreEqual((storedFlags & 8) != 0, decoded.WrongInputOnWordGaps);
            Assert.AreEqual((storedFlags & 16) != 0, decoded.StrictSpaces);
            Assert.AreEqual((storedFlags & 32) != 0, decoded.FlexibleLines);
            Assert.AreEqual((storedFlags & 64) != 0, decoded.CharTimedStretch);
            Assert.IsFalse(decoded.BoundedRush, "a replay from before the bound existed rushed as far ahead as it liked");
        }

        /// <summary>A non-CONFIG frame carries no flags: the character's own frame must not smuggle settings.</summary>
        [Test]
        public void CharacterFramesCarryNoConfigFlags()
        {
            var frame = new TypeBeatReplayFrame(1234, 'a') { AllowWrongInput = true, SpaceSkipsWord = true, SyllableTiming = true, WrongInputOnWordGaps = true, StrictSpaces = true, CharTimedStretch = true, BoundedRush = true };

            Assert.AreEqual(0f, frame.ToLegacy(dummy_beatmap).MouseY ?? -1f);
        }

        [Test]
        public void IntegralTimesSurviveTheLegacyDeltaFormat()
        {
            // The encoder emits integer frame DELTAS and the decoder re-accumulates them; verify a
            // realistic keystroke sequence survives the delta arithmetic unchanged.
            double[] times = { 0, 480, 481, 481, 1000, 60000 };
            int last = 0;

            foreach (double t in times)
            {
                // What LegacyScoreEncoder writes for this frame.
                int written = (int)Math.Round(t) - last;
                // What LegacyScoreDecoder accumulates back.
                last += written;

                Assert.AreEqual(t, last, "recorded (integral) frame times must be exact through the delta encoding");
            }
        }

        [Test]
        public void KeystrokeFramesNeverCollapse()
        {
            // The base recorder replaces the previous frame when IsEquivalentTo returns true. Two
            // real keypresses can share one rounded millisecond ('l' 'l' inside a single input
            // frame), so equivalence-collapse must be disabled outright.
            var a = new TypeBeatReplayFrame(1000, 'l');
            var b = new TypeBeatReplayFrame(1000, 'l');

            Assert.IsFalse(a.IsEquivalentTo(b));
            Assert.IsFalse(a.IsEquivalentTo(a));
        }
    }
}
