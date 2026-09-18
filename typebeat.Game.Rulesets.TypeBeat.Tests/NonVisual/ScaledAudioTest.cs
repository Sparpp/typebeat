// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using ManagedBass;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Audio;
using typebeat.Game.Audio.Effects;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// THE RUNTIME GAIN ITSELF: the map's audio decoded, every sample scaled, clamped at full scale, and
    /// handed back as the WAV its track plays (see <see cref="ScaledAudio"/>). These are the assertions
    /// that say the gain is real - the three live-audio routes that do not work are recorded on
    /// <c>AudioGain</c>, and this is the one that does, so it is measured rather than inspected: a tone
    /// goes in at a known amplitude, and the amplitude of the audio that comes out is read back.
    /// </summary>
    [TestFixture]
    public class ScaledAudioTest
    {
        /// <summary>The amplitude of the tone these tests feed in, well inside full scale.</summary>
        private const double source_amplitude = 0.2;

        [OneTimeSetUp]
        public void SetUp()
        {
            // The no-sound device: this decodes audio without a speaker, which is exactly how the game
            // applies a map's gain too.
            Assume.That(Bass.Init(0), Is.True, "BASS could not start a decoding device");
        }

        [OneTimeTearDown]
        public void TearDown() => Bass.Free();

        [Test]
        public void TheGainIsReallyAppliedToTheSamples()
        {
            byte[] scaled = ScaledAudio.Apply(new MemoryStream(tone()), 2, out double sourcePeak, out double scaledPeak) ?? Array.Empty<byte>();

            Assert.That(scaled, Is.Not.Empty, "the tone must decode");

            double played = peakOf(scaled);

            Assert.Multiple(() =>
            {
                Assert.That(sourcePeak, Is.EqualTo(source_amplitude).Within(0.01), "the source is read at the amplitude it was written with");
                Assert.That(scaledPeak, Is.EqualTo(source_amplitude * 2).Within(0.02), "and the gain is applied to every sample");
                Assert.That(played, Is.EqualTo(source_amplitude * 2).Within(0.02), "the audio the track would play is the scaled one - this is the whole feature");
            });
        }

        [Test]
        public void PastFullScaleTheSurplusIsCutOffRatherThanWrapped()
        {
            byte[] scaled = ScaledAudio.Apply(new MemoryStream(tone()), 10, out _, out double scaledPeak) ?? Array.Empty<byte>();

            double played = peakOf(scaled);

            Assert.Multiple(() =>
            {
                Assert.That(scaledPeak, Is.EqualTo(2.0).Within(0.05), "the reading reports what the gain asked for, past full scale");
                Assert.That(played, Is.EqualTo(1.0).Within(0.01), "while what plays is clamped: gain cannot create headroom");
                Assert.That(played, Is.GreaterThan(0), "clamped, NOT wrapped: a gain must never invert a peak");
            });
        }

        [Test]
        public void QuieteningIsTheSameArithmetic()
        {
            byte[] scaled = ScaledAudio.Apply(new MemoryStream(tone()), 0.5, out _, out _) ?? Array.Empty<byte>();

            Assert.That(peakOf(scaled), Is.EqualTo(source_amplitude * 0.5).Within(0.02));
        }

        [Test]
        public async Task ThePeakReadingIsTakenThroughTheAsyncAnalyser()
        {
            // The clipping indicator's reading, taken the way the editor takes it: from a task, through
            // the framework's ASYNC analyser. The synchronous equivalent waits inside the framework and
            // the framework refuses that from inside a task ("Can't use GetResultSafely from inside an
            // async operation") - which is what left the editor stuck on "Reading the map's peaks...".
            using var waveform = new osu.Framework.Audio.Track.Waveform(new MemoryStream(tone()));

            Assert.That(await AudioGain.PeakOfAsync(waveform), Is.EqualTo(source_amplitude).Within(0.02),
                "the tone's own amplitude, read back through the waveform the indicator reads");

            Assert.That(await AudioGain.PeakOfAsync(null), Is.Zero, "and a map with no waveform has nothing to read");
        }

        [Test]
        public void UndecodableAudioIsReportedRatherThanGuessedAt()
        {
            // A map whose audio cannot be read must fall back to playing the file as imported (see
            // WorkingBeatmapCache.getGainedTrack), so this has to be null rather than a silent tone.
            Assert.That(ScaledAudio.Apply(new MemoryStream(new byte[512]), 2, out _, out _), Is.Null);
        }

        /// <summary>The loudest sample in a WAV, read back through the same decoder the game uses.</summary>
        private static double peakOf(byte[] wav)
        {
            int handle = Bass.CreateStream(wav, 0, wav.Length, BassFlags.Decode | BassFlags.Float);
            Assert.That(handle, Is.Not.Zero, "the scaled audio must itself be playable");

            try
            {
                var block = new float[16384];
                int read = Bass.ChannelGetData(handle, block, block.Length * sizeof(float));
                double peak = 0;

                for (int i = 0; i < read / sizeof(float); i++)
                    peak = Math.Max(peak, Math.Abs(block[i]));

                return peak;
            }
            finally
            {
                Bass.StreamFree(handle);
            }
        }

        /// <summary>One second of a 440 Hz tone as a 16-bit PCM WAV, at a known amplitude.</summary>
        private static byte[] tone()
        {
            const int rate = 44100;
            int frames = rate;
            var pcm = new byte[frames * 2];

            for (int i = 0; i < frames; i++)
            {
                short value = (short)Math.Round(Math.Sin(2 * Math.PI * 440 * i / rate) * source_amplitude * short.MaxValue);
                pcm[i * 2] = (byte)(value & 0xff);
                pcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }

            var wav = new MemoryStream();
            var writer = new BinaryWriter(wav);

            writer.Write("RIFF".ToCharArray());
            writer.Write(36 + pcm.Length);
            writer.Write("WAVE".ToCharArray());
            writer.Write("fmt ".ToCharArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data".ToCharArray());
            writer.Write(pcm.Length);
            writer.Write(pcm);
            writer.Flush();

            wav.Position = 0;
            return wav.ToArray();
        }
    }
}
