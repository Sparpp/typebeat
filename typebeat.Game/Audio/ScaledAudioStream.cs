// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using ManagedBass;

namespace typebeat.Game.Audio
{
    /// <summary>
    /// A map's audio, WITH ITS GAIN ALREADY IN IT, presented as a seekable 16-bit PCM WAV that decodes
    /// ON DEMAND (see <see cref="ScaledAudio"/> for why the gain is baked into the bytes at all).
    ///
    /// <para>Nothing is decoded when this is constructed: the header and the length come from a BASS
    /// DECODE channel over the imported file, and source samples are pulled, scaled and clamped inside
    /// <see cref="Read"/> as whoever is reading asks for them. That is the whole point of the class. The
    /// framework hands a track's stream to a background reader (<c>TrackBass.prepareStream</c> wraps any
    /// non-memory stream in <c>AsyncBufferStream</c>, whose loader runs on its own long-running task),
    /// so the song's decode happens THERE rather than on the update thread that asked for the track.</para>
    ///
    /// <para>TWO CONTRACTS THE FRAMEWORK'S READER IMPOSES, and neither is optional:</para>
    /// <list type="bullet">
    /// <item><see cref="Length"/> is taken as gospel: <c>AsyncBufferStream</c> allocates its buffer from
    /// it up front. It has to be exact, which is why the decode channel is created with
    /// <see cref="BassFlags.Prescan"/> (the same flag the framework's own track stream uses) so a VBR
    /// mp3 reports its real length rather than an estimate.</item>
    /// <item>A <see cref="Read"/> must FILL what it was asked for: that reader asserts it got every byte
    /// it requested. So a short decode is padded with silence up to <see cref="Length"/> rather than
    /// returned short, and a decode that runs long is truncated at it.</item>
    /// </list>
    ///
    /// <para>The BASS handle is NOT garbage collected. It is freed in <see cref="Dispose(bool)"/>, which
    /// the framework reaches: <c>TrackBass.Dispose</c> disposes the stream it was built from, and
    /// <c>AsyncBufferStream</c> closes the stream underneath it both when it finishes buffering and when
    /// it is closed early.</para>
    /// </summary>
    internal sealed class ScaledAudioStream : Stream
    {
        /// <summary>Size of the canonical PCM WAV header this stream presents in front of its samples.</summary>
        public const int HEADER_BYTES = 44;

        /// <summary>Source samples pulled per decode pass: big enough to be cheap, small enough to stay off the heap.</summary>
        private const int decode_block_samples = 16384;

        private readonly double gain;
        private readonly int channels;
        private readonly byte[] header;

        /// <summary>The 16-bit PCM payload's length in bytes, i.e. everything after <see cref="HEADER_BYTES"/>.</summary>
        private readonly long dataBytes;

        /// <summary>Guards the BASS handle and the decode cursor against a close arriving from another thread.</summary>
        private readonly object decodeLock = new object();

        private int decodeHandle;

        private float[]? sourceBlock;
        private byte[]? stagedPcm;
        private int stagedStart;
        private int stagedEnd;

        /// <summary>Where the reader is in the WAV this stream presents.</summary>
        private long position;

        /// <summary>
        /// Where the decode pipeline (staged bytes first, then the channel) will produce from next, as an
        /// offset into the PCM payload. Kept alongside <see cref="position"/> so a seek only has to touch
        /// BASS when the reader actually moved.
        /// </summary>
        private long decodeCursor;

        /// <summary>The loudest source sample seen so far, 0..1. Only complete once the stream has been read through.</summary>
        public double SourcePeak { get; private set; }

        /// <summary>The loudest sample seen so far AFTER the gain and BEFORE clamping, so it may exceed 1.</summary>
        public double ScaledPeak { get; private set; }

        /// <summary>The BASS decode handle, or 0 once freed. Exposed so tests can prove the handle is released.</summary>
        internal int DecodeHandle
        {
            get
            {
                lock (decodeLock)
                    return decodeHandle;
            }
        }

        internal ScaledAudioStream(int decodeHandle, double gain, int channels, int rate, long sourceBytes)
        {
            this.decodeHandle = decodeHandle;
            this.gain = gain;
            this.channels = Math.Max(1, channels);

            // The decode channel is float, the WAV is 16-bit: one source sample of four bytes becomes two.
            long samples = Math.Max(0, sourceBytes) / sizeof(float);
            dataBytes = samples * sizeof(short);

            header = buildHeader(this.channels, rate > 0 ? rate : 44100, dataBytes);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;

        public override long Length => HEADER_BYTES + dataBytes;

        public override long Position
        {
            get
            {
                lock (decodeLock)
                    return position;
            }
            set
            {
                lock (decodeLock)
                    position = Math.Clamp(value, 0, Length);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            lock (decodeLock)
            {
                int total = 0;

                // The header first, byte by byte: it is 44 bytes once per stream and never the hot path.
                while (total < count && position < HEADER_BYTES)
                {
                    buffer[offset + total] = header[position];
                    position++;
                    total++;
                }

                while (total < count && position < Length)
                {
                    int produced = readData(buffer, offset + total, (int)Math.Min(count - total, Length - position));

                    if (produced <= 0)
                        break;

                    total += produced;
                }

                return total;
            }
        }

        /// <summary>
        /// Hands out <paramref name="count"/> bytes of the scaled PCM payload, decoding more of the source
        /// when the staged bytes run out and PADDING WITH SILENCE when the source runs out before
        /// <see cref="Length"/> does. The padding is not defensive dressing: the framework's reader treats
        /// a short read as a failed assertion, and a length that is one frame optimistic must not take the
        /// track down with it.
        /// </summary>
        private int readData(byte[] buffer, int offset, int count)
        {
            long dataPosition = position - HEADER_BYTES;

            if (dataPosition != decodeCursor)
                seekDecodeTo(dataPosition);

            if (stagedStart == stagedEnd && !decodeMore())
            {
                // Nothing left in the source: the declared length is what the reader is entitled to.
                Array.Clear(buffer, offset, count);
                position += count;
                decodeCursor += count;
                return count;
            }

            int taken = Math.Min(count, stagedEnd - stagedStart);

            Buffer.BlockCopy(stagedPcm!, stagedStart, buffer, offset, taken);

            stagedStart += taken;
            position += taken;
            decodeCursor += taken;

            return taken;
        }

        /// <summary>
        /// Pulls one block from the decode channel, scales every sample by the gain, clamps anything past
        /// full scale and stages it as 16-bit PCM. False once the source has no more to give.
        /// </summary>
        private bool decodeMore()
        {
            if (decodeHandle == 0)
                return false;

            sourceBlock ??= new float[decode_block_samples];
            stagedPcm ??= new byte[decode_block_samples * sizeof(short)];

            int bytes = Bass.ChannelGetData(decodeHandle, sourceBlock, sourceBlock.Length * sizeof(float));

            if (bytes <= 0)
                return false;

            int samples = bytes / sizeof(float);

            for (int i = 0; i < samples; i++)
            {
                double sample = sourceBlock[i];
                SourcePeak = Math.Max(SourcePeak, Math.Abs(sample));

                double scaled = sample * gain;
                ScaledPeak = Math.Max(ScaledPeak, Math.Abs(scaled));

                short value = (short)Math.Round(Math.Clamp(scaled, -1, 1) * short.MaxValue);
                stagedPcm[i * 2] = (byte)(value & 0xff);
                stagedPcm[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }

            stagedStart = 0;
            stagedEnd = samples * sizeof(short);

            return stagedEnd > 0;
        }

        /// <summary>
        /// Moves the decode channel so the next sample it produces is the one at
        /// <paramref name="dataPosition"/> in the PCM payload. BASS seeks in whole FRAMES of the source's
        /// own float format, so anything finer (which only a channel count whose frame size does not
        /// divide the 44-byte header can produce) is decoded and thrown away.
        /// </summary>
        private void seekDecodeTo(long dataPosition)
        {
            stagedStart = stagedEnd = 0;

            long frameBytes = (long)channels * sizeof(short);
            long frame = dataPosition / frameBytes;
            int withinFrame = (int)(dataPosition % frameBytes);

            if (decodeHandle != 0)
                Bass.ChannelSetPosition(decodeHandle, frame * channels * sizeof(float));

            decodeCursor = frame * frameBytes;

            while (withinFrame > 0)
            {
                if (stagedStart == stagedEnd && !decodeMore())
                    break;

                int skipped = Math.Min(withinFrame, stagedEnd - stagedStart);
                stagedStart += skipped;
                decodeCursor += skipped;
                withinFrame -= skipped;
            }

            decodeCursor = dataPosition;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (decodeLock)
            {
                long target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => position + offset,
                    SeekOrigin.End => Length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };

                position = Math.Clamp(target, 0, Length);
                return position;
            }
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            lock (decodeLock)
            {
                if (decodeHandle != 0)
                {
                    Bass.StreamFree(decodeHandle);
                    decodeHandle = 0;
                }

                stagedStart = stagedEnd = 0;
            }

            base.Dispose(disposing);
        }

        ~ScaledAudioStream()
        {
            // BASS handles are not collected, so a stream nobody disposed still has to give its back.
            Dispose(false);
        }

        /// <summary>
        /// The 44-byte canonical PCM WAV header, which the decoder on the other side expects in full.
        /// </summary>
        private static byte[] buildHeader(int channels, int rate, long dataBytes)
        {
            int blockAlign = channels * sizeof(short);
            int byteRate = rate * blockAlign;

            using var stream = new MemoryStream(HEADER_BYTES);

            void ascii(string text)
            {
                foreach (char c in text)
                    stream.WriteByte((byte)c);
            }

            void int32(long value)
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
            int32(Math.Min(int.MaxValue, 36 + dataBytes));
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
            int32(Math.Min(int.MaxValue, dataBytes));

            return stream.ToArray();
        }
    }
}
