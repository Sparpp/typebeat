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

        /// <summary>
        /// The <c>[Metadata]</c> song language LIST key (backlog 472): every language of a map that declares more
        /// than one, as canonical names joined by commas (<c>Languages:japanese,english</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A SEPARATE key beside <see cref="LANGUAGE"/>, not a list inside it, because that is the shape old readers
        /// degrade on gracefully. Every client and server that predates the list splits a <c>[Metadata]</c> line on
        /// its first colon and switches on the key, so an unknown <c>Languages</c> key is skipped without a word,
        /// and the <c>Language:</c> line beside it still carries the map's primary language
        /// (<see cref="BeatmapLanguageExtensions.PrimaryOf"/>), which is exactly the one language an old reader can
        /// hold. A list inside <c>Language:</c> would have been worse than ignored: an old client's
        /// <c>Enum.TryParse</c> reads <c>japanese,english</c> as a flags combination and decodes Chinese, and an old
        /// server folds the whole value to "unset".
        /// </para>
        /// <para>
        /// Written ONLY for a map with more than one language, so a single-language map encodes byte for byte as it
        /// did before the key existed. When both lines are present the list wins; a reader that sees only
        /// <c>Language:</c> reads a one-entry list.
        /// </para>
        /// </remarks>
        public const string LANGUAGES = "Languages";

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
        /// The language list line for a canonical list (<see cref="BeatmapLanguageExtensions.ToCanonicalList"/>), or
        /// null unless it names more than one language (see <see cref="LANGUAGES"/>).
        /// </summary>
        public static string? LanguagesLine(string? canonicalList)
        {
            if (string.IsNullOrWhiteSpace(canonicalList) || !canonicalList.Contains(BeatmapLanguageExtensions.LIST_SEPARATOR))
                return null;

            return $"{LANGUAGES}:{canonicalList.Trim()}";
        }

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
