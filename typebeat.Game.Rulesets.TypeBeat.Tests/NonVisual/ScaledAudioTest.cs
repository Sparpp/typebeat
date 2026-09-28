// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using ManagedBass;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.IO.Stores;
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
    ///
    /// <para>The stream is PULL-BASED (see <see cref="ScaledAudioStream"/>), so two further things are
    /// pinned here that a byte array never had to answer for: it must present the length it promises,
    /// seek to any point of it, and it must give its BASS handle back when it is disposed, because BASS
    /// handles are not garbage collected.</para>
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
            byte[] scaled = apply(2, out double sourcePeak, out double scaledPeak);

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
            byte[] scaled = apply(10, out _, out double scaledPeak);

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
            Assert.That(peakOf(apply(0.5, out _, out _)), Is.EqualTo(source_amplitude * 0.5).Within(0.02));
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
            Assert.That(ScaledAudio.Open(new MemoryStream(new byte[512]), 2), Is.Null);
            Assert.That(ScaledAudio.Open(new MemoryStream(Array.Empty<byte>()), 2), Is.Null);
        }

        // ---- the pull-based stream's own contracts ----

        [Test]
        public void TheStreamPresentsExactlyTheLengthItPromises()
        {
            // The framework sizes its own read buffer from Length and asserts that every Read fills what
            // it asked for (osu.Framework.IO.AsyncBufferStream), so both have to be true to the byte.
            using var stream = ScaledAudio.Open(new MemoryStream(tone()), 1);

            Assert.That(stream, Is.Not.Null);

            long declared = stream!.Length;
            var read = new MemoryStream();
            stream.CopyTo(read);

            Assert.Multiple(() =>
            {
                Assert.That(read.Length, Is.EqualTo(declared), "what comes out is exactly what the length advertised");
                Assert.That(declared, Is.EqualTo(ScaledAudioStream.HEADER_BYTES + 44100 * sizeof(short)), "a second of mono 16-bit at 44.1 kHz, behind a 44-byte header");
            });
        }

        [Test]
        public void EveryReadFillsWhatItAskedFor()
        {
            using var stream = ScaledAudio.Open(new MemoryStream(tone()), 1);

            var buffer = new byte[4096];
            long remaining = stream!.Length;

            while (remaining > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);

                Assert.That(stream.Read(buffer, 0, wanted), Is.EqualTo(wanted), "a short read is a failed assertion inside the framework's reader");
                remaining -= wanted;
            }

            Assert.That(stream.Read(buffer, 0, buffer.Length), Is.Zero, "and past the end there is nothing left to give");
        }

        [Test]
        public void SeekingLandsOnTheSameBytesAsReadingStraightThrough()
        {
            // The framework's reader seeks the stream to block boundaries and reads out of order, so a
            // seek has to put the decoder exactly where a straight read would have been.
            using var whole = ScaledAudio.Open(new MemoryStream(tone()), 1.5);
            var expected = new MemoryStream();
            whole!.CopyTo(expected);

            using var seeking = ScaledAudio.Open(new MemoryStream(tone()), 1.5);
            byte[] straight = expected.ToArray();

            foreach (long offset in new long[] { 32768, 1024, straight.Length - 2048, 44, 0 })
            {
                seeking!.Position = offset;

                var chunk = new byte[512];
                int read = seeking.Read(chunk, 0, chunk.Length);

                Assert.That(read, Is.EqualTo(chunk.Length), $"a full chunk at {offset}");
                Assert.That(chunk, Is.EqualTo(straight[(int)offset..((int)offset + read)]), $"the bytes at {offset}");
            }
        }

        [Test]
        public void DisposingTheStreamGivesTheBassHandleBack()
        {
            // BASS handles are not collected. A stream that kept its decode channel would leak one per
            // track load, which is exactly the shape of leak this whole feature had to have removed.
            var stream = ScaledAudio.Open(new MemoryStream(tone()), 2);

            int handle = stream!.DecodeHandle;

            Assert.That(handle, Is.Not.Zero);
            Assert.That(Bass.ChannelGetLength(handle), Is.GreaterThan(0), "the handle is live while the stream is");

            stream.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(stream.DecodeHandle, Is.Zero, "the stream lets go of the handle");
                Assert.That(Bass.ChannelGetLength(handle), Is.LessThanOrEqualTo(0), "and BASS no longer knows it, i.e. it was really freed");
            });
        }

        [Test]
        public void TheFrameworksOwnBlockWalkGetsTheSameBytes()
        {
            // Exactly how osu.Framework.IO.AsyncBufferStream reads a track's stream: a buffer sized from
            // Length, then, block by block, seek to block * 32768 and assert the read filled the block.
            // If this holds, the framework's reader holds.
            const int block_size = 32768;

            using var stream = ScaledAudio.Open(new MemoryStream(tone()), 1.5);

            var blockwise = new byte[stream!.Length];

            for (int offset = 0; offset < blockwise.Length; offset += block_size)
            {
                stream.Seek(offset, SeekOrigin.Begin);

                int wanted = Math.Min(blockwise.Length - offset, block_size);

                Assert.That(stream.Read(blockwise, offset, wanted), Is.EqualTo(wanted), $"the block at {offset}");
            }

            stream.Position = 0;

            Assert.That(readAll(stream), Is.EqualTo(blockwise), "reading in blocks out of order is the same audio as reading straight through");
        }

        // ---- the store the one track store is built on ----

        [Test]
        public void TheStoreCarriesTheGainInTheNameAndHoldsNothing()
        {
            // There is ONE gained track store for the whole session (AudioManager.GetTrackStore registers
            // what it is given and only releases it on disposal), so the gain cannot live on the store:
            // it travels in the name, and the store stays stateless.
            var files = new FakeFileStore(tone());
            var store = new ScaledAudioStore(files);

            using var quiet = store.GetStream(ScaledAudioStore.Key("audio.wav", 0.5));
            using var loud = store.GetStream(ScaledAudioStore.Key("audio.wav", 2));

            Assert.Multiple(() =>
            {
                Assert.That(peakOf(readAll(quiet)), Is.EqualTo(source_amplitude * 0.5).Within(0.02));
                Assert.That(peakOf(readAll(loud)), Is.EqualTo(source_amplitude * 2).Within(0.02));
                Assert.That(files.Requested, Is.EqualTo(new[] { "audio.wav", "audio.wav" }), "the file half of the key is what reaches the file store");
                Assert.That(store.GetStream("audio.wav"), Is.Null, "a name with no gain in it is not this store's to answer");
                Assert.That(store.GetStream(ScaledAudioStore.Key("missing.mp3", 2)), Is.Null, "and a file that is not there falls back rather than throwing");
            });
        }

        [Test]
        public void TheKeyRoundTripsAGainTheBarCanAskFor()
        {
            // The bar works in hundredths and the format keeps every digit of them, so the key has to as
            // well: two gains that printed the same would be two different tracks under one name.
            var files = new FakeFileStore(tone());
            var store = new ScaledAudioStore(files);

            foreach (double gain in new[] { 0.01, 1.25, 1.33, 3.99 })
            {
                using var stream = store.GetStream(ScaledAudioStore.Key("audio.wav", gain));
                Assert.That(stream, Is.Not.Null, gain.ToString());
            }

            Assert.That(ScaledAudioStore.Key("audio.wav", 1.33), Is.Not.EqualTo(ScaledAudioStore.Key("audio.wav", 1.3333)));
        }

        // ---- helpers ----

        /// <summary>The scaled audio a track would play, rendered in full, plus the peaks seen on the way.</summary>
        private static byte[] apply(double gain, out double sourcePeak, out double scaledPeak)
        {
            using var stream = ScaledAudio.Open(new MemoryStream(tone()), gain);

            if (stream == null)
            {
                sourcePeak = scaledPeak = 0;
                return Array.Empty<byte>();
            }

            byte[] rendered = readAll(stream);

            sourcePeak = stream.SourcePeak;
            scaledPeak = stream.ScaledPeak;

            return rendered;
        }

        private static byte[] readAll(Stream? stream)
        {
            if (stream == null)
                return Array.Empty<byte>();

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
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

        /// <summary>A beatmap file store holding one file, which records what was asked of it.</summary>
        private class FakeFileStore : IResourceStore<byte[]>
        {
            private readonly byte[] contents;

            public readonly List<string> Requested = new List<string>();

            public FakeFileStore(byte[] contents)
            {
                this.contents = contents;
            }

            public byte[] Get(string name) => name == "audio.wav" ? contents : null!;

            public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(Get(name));

            public Stream GetStream(string name)
            {
                Requested.Add(name);
                return name == "audio.wav" ? new MemoryStream(contents, writable: false) : null!;
            }

            public IEnumerable<string> GetAvailableResources() => new[] { "audio.wav" };

            public void Dispose()
            {
            }
        }
    }
}
