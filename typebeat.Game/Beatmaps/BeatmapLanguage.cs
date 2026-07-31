// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Localisation;
using typebeat.Game.Resources.Localisation.Web;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// The language a beatmap's song is sung in, chosen by the mapper in the editor's song setup
    /// and required before the map can be submitted (see <c>Editor.submitBeatmap</c>).
    /// </summary>
    /// <remarks>
    /// This is deliberately NOT <see cref="Overlays.BeatmapListing.SearchLanguage"/>, which it
    /// otherwise mirrors member for member:
    /// <list type="bullet">
    /// <item><description><see cref="Unspecified"/> must be the ZERO value. This enum is persisted
    /// to realm through an int backing field, and realm fills a newly added column with 0, so any
    /// other ordering would silently declare every pre-existing beatmap to be in whatever language
    /// happened to sit at 0.</description></item>
    /// <item><description><c>SearchLanguage</c> carries an <c>Any</c> sentinel, which is a query
    /// concept ("do not filter"), not something a song can be. A map must never be able to hold
    /// it.</description></item>
    /// </list>
    /// The lowercased member names ARE the strings that travel on the wire (the
    /// <c>[Metadata] Language:</c> line of the .osu, see
    /// <c>typebeat.Game.Rulesets.TypeBeat/Beatmaps/LyricOsuFormat.cs</c>) and that the website
    /// stores in <c>beatmapsets.language</c>. Keep this list in lockstep with the server's
    /// <c>Typebeat.Web/Packages/BeatmapLanguages.cs</c>; <c>tests/Typebeat.WireCompat</c> pins the
    /// two against each other. Declaration order is the editor dropdown's display order.
    /// </remarks>
    public enum BeatmapLanguage
    {
        /// <summary>Not chosen yet. Never stored by the website and never submittable.</summary>
        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageUnspecified))]
        Unspecified = 0,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageEnglish))]
        English,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageJapanese))]
        Japanese,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageChinese))]
        Chinese,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageKorean))]
        Korean,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageFrench))]
        French,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageGerman))]
        German,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageSpanish))]
        Spanish,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageItalian))]
        Italian,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageRussian))]
        Russian,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguagePolish))]
        Polish,

        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageSwedish))]
        Swedish,

        /// <summary>The song has no sung words (the map types something else, e.g. onomatopoeia).</summary>
        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageInstrumental))]
        Instrumental,

        /// <summary>A real language that is not on this list.</summary>
        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageOther))]
        Other,
    }

    public static class BeatmapLanguageExtensions
    {
        /// <summary>
        /// The lowercase name this language travels as in the .osu <c>[Metadata] Language:</c>
        /// line and in the website's <c>beatmapsets.language</c> column.
        /// <see cref="BeatmapLanguage.Unspecified"/> maps to an empty string, which is the signal
        /// to write NO line at all (and, server-side, to leave whatever is stored untouched).
        /// </summary>
        public static string ToCanonicalName(this BeatmapLanguage language)
            => language == BeatmapLanguage.Unspecified ? string.Empty : language.ToString().ToLowerInvariant();

        /// <summary>
        /// Inverse of <see cref="ToCanonicalName"/>, case-insensitive and total: anything blank or
        /// unrecognised (a file written by a future client with a language this build has never
        /// heard of) decodes to <see cref="BeatmapLanguage.Unspecified"/> rather than throwing, so
        /// an unknown value can never make a beatmap fail to load.
        /// </summary>
        public static BeatmapLanguage FromCanonicalName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BeatmapLanguage.Unspecified;

            string trimmed = name.Trim();

            // Enum.TryParse also accepts the NUMERIC form ("2" would become Japanese), and the
            // underlying values are an implementation detail of realm storage that must never leak
            // into the file format. Names only.
            if (!char.IsLetter(trimmed[0]))
                return BeatmapLanguage.Unspecified;

            return Enum.TryParse(trimmed, ignoreCase: true, out BeatmapLanguage parsed) && Enum.IsDefined(parsed)
                ? parsed
                : BeatmapLanguage.Unspecified;
        }
    }
}
