// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using ManagedBass;
using osu.Framework.Logging;

namespace typebeat.Game.Audio
{
    /// <summary>
    /// A map's gain, BAKED INTO THE BYTES ITS TRACK PLAYS.
    ///
    /// <para>This is the route that works. The three that do not are recorded on
    /// <see cref="Effects.AudioGain"/>: the volume path is clamped to its own level, the framework's
    /// mixers are not in the audio path on this platform, and a BASS DSP on the track's channel crashes
    /// the client when the framework recycles that channel. What is left is to hand the framework
    /// different audio: this decodes the song, scales every sample, clamps anything that would pass full
    /// scale, and writes the result as a 16-bit PCM WAV for the track to play.</para>
    ///
    /// <para>WHAT IT COSTS, exactly, since that was the question: NOTHING ON DISK. The file a mapper
    /// imported is untouched - no re-encode, no quality loss, no second copy, no size change - and the
    /// scaled audio exists only in memory for as long as the map's track does, at about 10 MB per minute
    /// of stereo audio (the same 16-bit rate the game plays). Encoding a scaled file back out would
    /// need an encoder this client does not ship, and a lossless one would be several times the size of
    /// the mp3 it came from; this needs neither.</para>
    ///
    /// <para>Clamping rather than wrapping is the contract: a gain cannot create headroom, so a sample
    /// pushed past full scale is cut off, which is what the editor's clipping indicator warns about
    /// before anyone plays the map.</para>
    /// </summary>
    internal static class ScaledAudio
    {
        /// <summary>Samples decoded per pass: big enough to be cheap, small enough to stay off the heap.</summary>
        private const int block_samples = 16384;

        /// <summary>
        /// Decodes <paramref name="source"/> and returns it as a WAV with every sample multiplied by
        /// <paramref name="gain"/>, or null when the audio could not be decoded (in which case the caller
        /// should play the file as it is rather than fail the map).
        /// </summary>
        /// <param name="source">The audio file's bytes, as stored in the beatmap set.</param>
        /// <param name="gain">The linear multiplier to apply; 1 is a no-op and never reaches here.</param>
        /// <param name="sourcePeak">The loudest sample in the file, for the caller's log.</param>
        /// <param name="scaledPeak">The loudest sample after the gain, before clamping bites.</param>
        public static byte[]? Apply(Stream source, double gain, out double sourcePeak, out double scaledPeak)
        {
            sourcePeak = 0;
            scaledPeak = 0;

            byte[] encoded = new byte[source.Length];

            int read = 0;

            while (read < encoded.Length)
            {
                int justRead = source.Read(encoded, read, encoded.Length - read);

                if (justRead <= 0)
                    break;

                read += justRead;
            }

            if (read == 0)
                return null;

            // Decoded, not played: no device, no callbacks, nothing that can race the framework.
            int decode = Bass.CreateStream(encoded, 0, read, BassFlags.Decode | BassFlags.Float);

            if (decode == 0)
            {
                Logger.Log($@"The map's audio could not be decoded to apply its gain (bass error {Bass.LastError}). It will play at its own level.", level: LogLevel.Error);
                return null;
            }

            try
            {
                ChannelInfo info = Bass.ChannelGetInfo(decode);
                int channels = Math.Max(1, info.Channels);
                int rate = info.Frequency > 0 ? info.Frequency : 44100;

                using var output = new MemoryStream();

                // The header is patched at the end, when the real length is known.
                output.Write(new byte[44], 0, 44);

                var block = new float[block_samples];
                var pcm = new byte[block_samples * sizeof(short)];
                long dataBytes = 0;

                while (true)
                {
                    int bytes = Bass.ChannelGetData(decode, block, block.Length * sizeof(float));

                    if (bytes <= 0)
                        break;

                    int samples = bytes / sizeof(float);

                    for (int i = 0; i < samples; i++)
                    {
                        double sample = block[i];
                        sourcePeak = Math.Max(sourcePeak, Math.Abs(sample));

                        double scaled = sample * gain;
                        scaledPeak = Math.Max(scaledPeak, Math.Abs(scaled));

                        short value = (short)Math.Round(Math.Clamp(scaled, -1, 1) * short.MaxValue);
                        pcm[i * 2] = (byte)(value & 0xff);
                        pcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
                    }

                    output.Write(pcm, 0, samples * sizeof(short));
                    dataBytes += samples * sizeof(short);
                }

                writeHeader(output, channels, rate, dataBytes);
                return output.ToArray();
            }
            finally
            {
                Bass.StreamFree(decode);
            }
        }

        /// <summary>
        /// Writes the 44-byte canonical PCM WAV header over the placeholder at the start of
        /// <paramref name="stream"/>, which the decoder - and BASS's own WAV reader on the other side -
        /// both expect in full.
        /// </summary>
        private static void writeHeader(MemoryStream stream, int channels, int rate, long dataBytes)
        {
            int blockAlign = channels * sizeof(short);
            int byteRate = rate * blockAlign;

            stream.Position = 0;

            void ascii(string text)
            {
                foreach (char c in text)
                    stream.WriteByte((byte)c);
            }

            void int32(int value)
            {
                stream.WriteByte((byte)(value & 0xff));
                stream.WriteByte((byte)((value >> 8) & 0xff));
                stream.WriteByte((byte)((value >> 16) & 0xff));
                stream.WriteByte((byte)((value >> 24) & 0xff));
            }

            void int16(int value)
            {
                stream.WriteByte((byte)(value & 0xff));
                stream.WriteByte((byte)((value >> 8) & 0xff));
            }

            ascii("RIFF");
            int32((int)Math.Min(int.MaxValue, 36 + dataBytes));
            ascii("WAVE");
            ascii("fmt ");
            int32(16);
            int16(1);
            int16(channels);
            int32(rate);
            int32(byteRate);
            int16(blockAlign);
            int16(sizeof(short) * 8);
            ascii("data");
            int32((int)Math.Min(int.MaxValue, dataBytes));

            stream.Position = stream.Length;
        }
    }
}
