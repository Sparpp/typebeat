// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Replays.Legacy;
using typebeat.Game.Rulesets.TypeBeat.Replays;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the replay frame format's legacy (.osr) mapping: a frame is (integral time, char code in
    /// MouseX, config flags in MouseY), and encode/decode must round-trip every value on the
    /// typeable surface (a-z / A-Z / 0-9 / space) plus both sentinels (backspace, config) exactly.
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
            Assert.IsFalse(frame.IsConfig);
        }

        [Test]
        public void BackspaceFrameRoundTrips()
        {
            var frame = roundTrip(new TypeBeatReplayFrame(56789, TypeBeatReplayFrame.BACKSPACE));

            Assert.AreEqual(56789, frame.Time);
            Assert.IsTrue(frame.IsBackspace);
            Assert.IsFalse(frame.IsConfig);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ConfigFrameRoundTripsAllowWrongInput(bool allowWrongInput)
        {
            var frame = roundTrip(TypeBeatReplayFrame.CreateConfigFrame(500, allowWrongInput));

            Assert.AreEqual(500, frame.Time);
            Assert.IsTrue(frame.IsConfig);
            Assert.IsFalse(frame.IsBackspace);
            Assert.AreEqual(allowWrongInput, frame.AllowWrongInput);
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
