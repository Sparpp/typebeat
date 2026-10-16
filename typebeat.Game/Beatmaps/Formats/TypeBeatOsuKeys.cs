// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace typebeat.Game.Beatmaps.Formats
{
    /// <summary>
    /// The type!beat additions to the .osu key set, spelled once for every client writer and reader:
    /// the ruleset's native writer (<c>LyricOsuFormat</c>, behind imports and editor saves, so nearly
    /// every stored map), <see cref="LegacyBeatmapEncoder"/> (the .osz export) and
    /// <see cref="LegacyBeatmapDecoder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WRITERS emit only the lines below. The spelling is the native writer's, because that is what the
    /// stored maps already carry: <c>Language:</c> and <c>AudioGain:</c> in <c>[Metadata]</c> with no
    /// space after the colon, <c>LyricFont: </c> and <c>LyricFontFile: </c> in <c>[General]</c> with
    /// one, and the font file value as stored. Each line is conditional, so a map that never set the
    /// value encodes exactly as it did before the key existed.
    /// </para>
    /// <para>
    /// READERS keep accepting both spellings: the decoder splits a line on its first colon and trims
    /// both halves, and so does the server's package parser, so a line the legacy encoder wrote with a
    /// space after the colon (<c>Language: russian</c>, before these keys were shared) decodes and
    /// parses exactly as the native spelling does.
    /// </para>
    /// </remarks>
    public static class TypeBeatOsuKeys
    {
        /// <summary>The <c>[Metadata]</c> song language key.</summary>
        public const string LANGUAGE = "Language";

        /// <summary>The <c>[Metadata]</c> track gain key.</summary>
        public const string AUDIO_GAIN = "AudioGain";

        /// <summary>The <c>[General]</c> typing font family key.</summary>
        public const string LYRIC_FONT = "LyricFont";

        /// <summary>The <c>[General]</c> bundled font file key.</summary>
        public const string LYRIC_FONT_FILE = "LyricFontFile";

        /// <summary>
        /// The language line for a canonical language name (<see cref="BeatmapLanguageExtensions.ToCanonicalName"/>),
        /// or null when no language is stated.
        /// </summary>
        public static string? LanguageLine(string? canonicalName)
            => string.IsNullOrWhiteSpace(canonicalName) ? null : $"{LANGUAGE}:{canonicalName.Trim()}";

        /// <summary>
        /// The gain line, or null for the default gain. The value goes through
        /// <see cref="BeatmapMetadata.EncodeAudioGain"/>, so it round-trips exactly.
        /// </summary>
        public static string? AudioGainLine(double gain)
            => gain == BeatmapMetadata.DEFAULT_AUDIO_GAIN ? null : $"{AUDIO_GAIN}:{BeatmapMetadata.EncodeAudioGain(gain)}";

        /// <summary>The font family line, or null when no font is chosen.</summary>
        public static string? LyricFontLine(string? family)
            => string.IsNullOrEmpty(family) ? null : $"{LYRIC_FONT}: {family}";

        /// <summary>The bundled font file line, or null when no file is bundled.</summary>
        public static string? LyricFontFileLine(string? filename)
            => string.IsNullOrEmpty(filename) ? null : $"{LYRIC_FONT_FILE}: {filename}";
    }
}
