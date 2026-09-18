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
    /// <para>This is the route that works, and the three that do not are recorded on
    /// <see cref="Effects.AudioGain"/>: the volume path is clamped to its own level, the framework's
    /// mixers are not in the audio path on this platform, and a BASS DSP on the track's channel crashes
    /// the client when the framework recycles that channel. What is left is to hand the framework
    /// different audio: the song is decoded, every sample scaled, anything that would pass full scale
    /// clamped, and the result presented as a 16-bit PCM WAV for the track to play.</para>
    ///
    /// <para>WHAT IT COSTS, exactly, since that was the question: NOTHING ON DISK. The file a mapper
    /// imported is untouched - no re-encode, no quality loss, no second copy, no size change - and the
    /// scaled audio is produced ON DEMAND, as the framework's own background reader pulls it (see
    /// <see cref="ScaledAudioStream"/>). Nothing about the song is decoded on the thread that asked for
    /// the track, and the only whole-song buffer in play is the one the framework allocates for ANY
    /// track. Encoding a scaled file back out would need an encoder this client does not ship, and a
    /// lossless one would be several times the size of the mp3 it came from; this needs neither.</para>
    ///
    /// <para>Clamping rather than wrapping is the contract: a gain cannot create headroom, so a sample
    /// pushed past full scale is cut off, which is what the editor's clipping indicator warns about
    /// before anyone plays the map.</para>
    /// </summary>
    internal static class ScaledAudio
    {
        /// <summary>
        /// Opens <paramref name="source"/> as the audio a track should play with <paramref name="gain"/>
        /// applied to it, or null when the audio could not be decoded (in which case the caller should
        /// play the file as it is rather than fail the map).
        ///
        /// <para>Only the container is read here: the encoded bytes are pulled into memory and handed to
        /// BASS as a DECODE channel, which costs a file read and a prescan rather than a decode. Every
        /// sample of the song is produced later, by whoever reads the returned stream.</para>
        /// </summary>
        /// <param name="source">The audio file's bytes, as stored in the beatmap set.</param>
        /// <param name="gain">The linear multiplier to apply; 1 is a no-op and never reaches here.</param>
        public static ScaledAudioStream? Open(Stream source, double gain)
        {
            ArgumentNullException.ThrowIfNull(source);

            byte[]? encoded = readAll(source);

            if (encoded == null)
                return null;

            // Decoded, not played: no device, no callbacks, nothing that can race the framework. Prescan
            // is what makes ChannelGetLength EXACT rather than an estimate on a VBR mp3, which the stream
            // below depends on (the framework sizes its read buffer from the length we declare); it is
            // the same flag the framework's own track stream is created with.
            int decode = Bass.CreateStream(encoded, 0, encoded.Length, BassFlags.Decode | BassFlags.Float | BassFlags.Prescan);

            if (decode == 0)
            {
                Logger.Log($@"The map's audio could not be decoded to apply its gain (bass error {Bass.LastError}). It will play at its own level.", level: LogLevel.Error);
                return null;
            }

            long length = Bass.ChannelGetLength(decode);

            if (length <= 0)
            {
                Logger.Log($@"The map's audio reported no length, so its gain cannot be applied (bass error {Bass.LastError}). It will play at its own level.", level: LogLevel.Error);
                Bass.StreamFree(decode);
                return null;
            }

            var info = Bass.ChannelGetInfo(decode);

            return new ScaledAudioStream(decode, gain, info.Channels, info.Frequency, length);
        }

        /// <summary>
        /// Reads <paramref name="source"/> in full, or null when it is empty (an audio file that is not
        /// there, which is the caller's cue to fall back rather than to fail).
        /// </summary>
        private static byte[]? readAll(Stream source)
        {
            if (source.CanSeek && source.Length == 0)
                return null;

            using var buffer = new MemoryStream();
            source.CopyTo(buffer);

            return buffer.Length == 0 ? null : buffer.ToArray();
        }
    }
}
