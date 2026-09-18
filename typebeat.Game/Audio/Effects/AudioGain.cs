// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using osu.Framework.Audio.Track;

namespace typebeat.Game.Audio.Effects
{
    /// <summary>
    /// A map's TRACK GAIN, as the editor talks about it: the value behind the audio gain bar, its
    /// conversion to decibels, and the clipping reading that goes with it.
    ///
    /// <para><b>WHERE THE AUDIO HALF OF THIS LIVES, since it is not here.</b> The gain is applied by
    /// handing the framework DIFFERENT AUDIO - the song decoded, scaled, clamped and presented as the
    /// WAV the track plays (see <see cref="ScaledAudio"/> and <see cref="ScaledAudioStream"/>), built
    /// wherever a working beatmap's track is built. There is no live effect object and no amplifier on
    /// a mixer, because the three routes that would have given one were each tried and each failed on
    /// this platform:</para>
    ///
    /// <list type="bullet">
    /// <item>VOLUME CANNOT: <c>AdjustableAudioComponent.Volume</c> and the aggregate of every volume
    /// adjustment on it are limited to 0..1 (measured, not assumed: setting an adjustment to 2 reads
    /// back as 1), so volume can only take a track DOWN from its own level, and a quietly mastered song
    /// has nothing left to give through it.</item>
    /// <item><c>AudioManager.TrackMixer</c> IS EMPTY HERE: the framework only routes audio through it
    /// when a GLOBAL mixer is in use, and its own documentation says that is "only the case on Windows
    /// when using shared mode WASAPI initialisation" - so on macOS that mixer holds no tracks and an
    /// effect added to it is silent however correct its parameters are. A mixer of our own
    /// (<c>CreateAudioMixer</c>) plus <c>Add(track)</c> fares no better: the work is accepted, BASS
    /// reports success, and nothing about what is heard changes.</item>
    /// <item>A BASS DSP ON THE TRACK'S OWN CHANNEL works - it is the one place that is unambiguously
    /// the samples being played, the framework keeps that channel private so it takes reflection to
    /// reach, and it scales correctly - but the framework SWAPS AND FREES that channel underneath a
    /// track whenever the track changes, which crashes the client from the audio thread. It was
    /// removed for that reason and must not come back in that shape.</item>
    /// </list>
    ///
    /// <para>What is left here is the ARITHMETIC the editor's controls need, and nothing that pretends
    /// to touch playback: a component that held a track and did nothing with it lived here for a while
    /// and was only ever read as a promise it could not keep.</para>
    /// </summary>
    public static class AudioGain
    {
        /// <summary>
        /// The largest gain a map may ask for, in dB (+12 dB is four times the amplitude, which is
        /// <see cref="Beatmaps.BeatmapMetadata.MAX_AUDIO_GAIN"/>).
        /// </summary>
        public const double MAX_GAIN_DB = 12;

        /// <summary>
        /// The quietest gain a map may ask for, in dB: -60 dB is a thousandth of the amplitude, which is
        /// silence in everything but the arithmetic.
        /// </summary>
        public const double MIN_GAIN_DB = -60;

        /// <summary>
        /// A linear multiplier in dB: 1 -> 0 dB, 2 -> +6.02 dB, 0.5 -> -6.02 dB. Zero or below (and
        /// anything non-finite) is silence, i.e. as far down as a gain goes.
        /// </summary>
        public static double multiplierToDb(double multiplier)
        {
            if (!double.IsFinite(multiplier) || multiplier <= 0)
                return MIN_GAIN_DB;

            return Math.Clamp(20 * Math.Log10(multiplier), MIN_GAIN_DB, MAX_GAIN_DB);
        }

        /// <summary>
        /// The inverse of <see cref="multiplierToDb"/>, for display and tests.
        /// </summary>
        public static double dbToMultiplier(double db) => Math.Pow(10, db / 20);

        /// <summary>
        /// The loudest sample in an analysed <paramref name="waveform"/>, across both channels: 0..1 on
        /// the same scale the gain multiplies against (1 = full scale). Null and unanalysed read as 0,
        /// i.e. "nothing known to be loud": the caller decides what to say about that, and this method
        /// deliberately does not pretend to have measured anything.
        ///
        /// <para>The peaks come from the framework's own waveform - the one the editor's timeline draws -
        /// so the warning is about the same analysis the mapper can see. It is a READING, and a
        /// conservative one: the waveform is sampled per bucket, so a single clipped sample between two
        /// buckets can hide from it.</para>
        /// </summary>
        public static double PeakOf(Waveform? waveform)
        {
            if (waveform == null)
                return 0;

            double peak = 0;

            foreach (var point in waveform.GetPoints())
                peak = Math.Max(peak, Math.Max(Math.Abs(point.AmplitudeLeft), Math.Abs(point.AmplitudeRight)));

            return peak;
        }

        /// <summary>
        /// The same reading as <see cref="PeakOf"/>, for callers that cannot afford to block the update
        /// thread on a song's first analysis - which is every caller that is not the editor's own
        /// timeline, since getting the points means decoding the track.
        ///
        /// <para>IT MUST BE THIS ONE FROM A TASK. The synchronous <see cref="PeakOf"/> waits for the
        /// framework's analysis internally, and the framework refuses that from inside an asynchronous
        /// operation ("Can't use GetResultSafely from inside an async operation"): a worker calling it
        /// throws every time, which is exactly how the editor's clipping indicator came to sit on
        /// "reading" forever. The async accessor is the framework's own answer to the same problem.</para>
        /// </summary>
        public static async Task<double> PeakOfAsync(Waveform? waveform)
        {
            if (waveform == null)
                return 0;

            double peak = 0;

            foreach (var point in await waveform.GetPointsAsync().ConfigureAwait(false))
                peak = Math.Max(peak, Math.Max(Math.Abs(point.AmplitudeLeft), Math.Abs(point.AmplitudeRight)));

            return peak;
        }

        /// <summary>
        /// Whether a gain of <paramref name="multiplier"/> on a track whose loudest sample is
        /// <paramref name="peak"/> pushes some of it past full scale. That is what the editor's
        /// indicator turns red for: not "this is loud", but "this is louder than the signal has room
        /// for, so the surplus would be cut off".
        /// </summary>
        public static bool WouldClip(double peak, double multiplier) => peak * multiplier > 1;
    }
}
