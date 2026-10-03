// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using typebeat.Game.Database;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// A map may carry an ISOLATED VOCALS STEM beside its audio: a mono render of just the sung
    /// voice, produced by the local aligner's Demucs separation and kept in the map folder so the
    /// editor can draw a waveform of the vocals alone and see where they actually start.
    ///
    /// <para>The file is named <c>vocals.ogg</c> when the producing machine had a Vorbis encoder and
    /// <c>vocals.wav</c> when it did not (16 kHz mono either way), so both names are accepted here:
    /// the name is the whole contract between the importer that writes it and the waveform loader
    /// that reads it, and a machine without libvorbis must not produce a map whose stem the player's
    /// editor then silently ignores.</para>
    ///
    /// <para>The stem is NOT the audio the map plays. Nothing about playback, timing, judgement or
    /// save reads it; it is a reading surface for the mapper, and a map that carries none simply has
    /// one fewer editor feature (see <see cref="WorkingBeatmap.VocalsStemWaveform"/>).</para>
    /// </summary>
    public static class VocalsStem
    {
        /// <summary>The stem's file name base; the extension is chosen by the producer.</summary>
        public const string BASE_NAME = "vocals";

        /// <summary>The preferred file name: Vorbis, a fraction of the wav's size.</summary>
        public const string OGG_FILENAME = BASE_NAME + ".ogg";

        /// <summary>The fallback file name, used when the producer had no Vorbis encoder.</summary>
        public const string WAV_FILENAME = BASE_NAME + ".wav";

        /// <summary>Every file name the loader accepts, preferred first.</summary>
        public static readonly string[] Filenames = { OGG_FILENAME, WAV_FILENAME };

        /// <summary>
        /// The stem file name <paramref name="set"/> carries, or null when it has none. A realm
        /// lookup, so it costs nothing and opens no audio file.
        /// </summary>
        public static string? FilenameIn(IHasRealmFiles set)
        {
            foreach (string name in Filenames)
            {
                if (set.GetFile(name) != null)
                    return name;
            }

            return null;
        }

        /// <summary>
        /// Whether <paramref name="fileName"/> is one of the accepted stem names (case-insensitive),
        /// so an importer can tell a stem it just produced from an unrelated file.
        /// </summary>
        public static bool IsStemFileName(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            foreach (string name in Filenames)
            {
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
