// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Extensions;
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
    /// two against each other. Dropdown display order is separate from these persisted IDs.
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

        // Append new persisted IDs; never renumber the original language choices.
        Afrikaans = 14,

        Bavarian = 15,

        Danish = 16,

        Dutch = 17,

        [System.ComponentModel.Description("Low German")]
        LowGerman = 18,

        Norwegian = 19,

        [System.ComponentModel.Description("Swiss German")]
        SwissGerman = 20,

        Belarusian = 21,

        Bulgarian = 22,

        Croatian = 23,

        Czech = 24,

        Serbian = 25,

        Slovak = 26,

        Ukrainian = 27,

        Finnish = 28,

        Hungarian = 29,

        Catalan = 30,

        Neapolitan = 31,

        Portuguese = 32,

        Romanian = 33,

        /// <summary>The song has no sung words (the map types something else, e.g. onomatopoeia).</summary>
        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageInstrumental))]
        Instrumental = 12,

        /// <summary>A real language that is not on this list.</summary>
        [LocalisableDescription(typeof(BeatmapsStrings), nameof(BeatmapsStrings.LanguageOther))]
        Other = 13,
    }

    public static class BeatmapLanguageExtensions
    {
        /// <summary>
        /// Shared order for the editor and lyric import language dropdowns.
        /// Prioritise common choices, then sort the remaining languages alphabetically with Instrumental and Other last.
        /// </summary>
        public static IReadOnlyList<BeatmapLanguage> DisplayOrder { get; } = Array.AsReadOnly(
            Enum.GetValues<BeatmapLanguage>()
                .Where(language => language is not (BeatmapLanguage.Unspecified or BeatmapLanguage.LowGerman or BeatmapLanguage.SwissGerman))
                .OrderBy(language => language switch
                {
                    BeatmapLanguage.English => 0,
                    BeatmapLanguage.Japanese => 1,
                    BeatmapLanguage.Korean => 2,
                    BeatmapLanguage.Russian => 3,
                    BeatmapLanguage.French => 4,
                    BeatmapLanguage.Spanish => 5,
                    BeatmapLanguage.Instrumental => 7,
                    BeatmapLanguage.Other => 8,
                    _ => 6,
                })
                .ThenBy(language => language.ToString(), StringComparer.Ordinal)
                .ToArray());

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
            // into the file format. Names only. Letters only, too: Enum.TryParse reads a
            // comma-separated value as a FLAGS combination, so "japanese,english" would otherwise
            // OR the two ids together (2 | 1 = 3) and decode as Chinese. A list goes through
            // FromCanonicalList, which splits it first.
            foreach (char c in trimmed)
            {
                if (!char.IsLetter(c))
                    return BeatmapLanguage.Unspecified;
            }

            return Enum.TryParse(trimmed, ignoreCase: true, out BeatmapLanguage parsed) && Enum.IsDefined(parsed)
                ? parsed
                : BeatmapLanguage.Unspecified;
        }

        /// <summary>
        /// The separator between languages in a language LIST, both in the .osu <c>[Metadata] Languages:</c>
        /// line (as canonical names) and in realm's stored <see cref="BeatmapMetadata.LanguageIDs"/> (as
        /// persisted ids). A comma because no canonical name can contain one (they are letters only, see
        /// <see cref="FromCanonicalName"/>), and the server splits the line on the same character.
        /// </summary>
        public const char LIST_SEPARATOR = ',';

        /// <summary>
        /// A map's languages in their one valid shape: <see cref="BeatmapLanguage.Unspecified"/> and anything
        /// undefined dropped, duplicates dropped (the first occurrence keeps its place), and
        /// <see cref="BeatmapLanguage.Instrumental"/> exclusive.
        /// </summary>
        /// <remarks>
        /// Instrumental is exclusive because it is a claim that the song has NO sung language, which no other
        /// entry can sit beside. <see cref="BeatmapLanguage.Other"/> is not: it names a real language that is
        /// not on the list, and a song can mix it with listed ones. When a list still holds Instrumental beside
        /// a real language (a hand-edited file), the FIRST entry decides: a list that leads with Instrumental is
        /// just Instrumental, and any other loses its Instrumental. The editor's toggle
        /// (<see cref="Toggle"/>) never produces such a list in the first place. The server folds lists the same
        /// way (<c>BeatmapLanguages.NormalizeList</c> in typebeat-web, pinned by WireCompat).
        /// </remarks>
        public static IReadOnlyList<BeatmapLanguage> Normalise(IEnumerable<BeatmapLanguage> languages)
        {
            var result = new List<BeatmapLanguage>();

            foreach (var language in languages)
            {
                if (language == BeatmapLanguage.Unspecified || !Enum.IsDefined(language) || result.Contains(language))
                    continue;

                result.Add(language);
            }

            if (result.Count > 1)
            {
                if (result[0] == BeatmapLanguage.Instrumental)
                    return new[] { BeatmapLanguage.Instrumental };

                result.Remove(BeatmapLanguage.Instrumental);
            }

            return result;
        }

        /// <summary>
        /// The editor's multi-select toggle: a selected <paramref name="toggled"/> is removed, an unselected one is
        /// appended. The NEWLY picked choice wins an exclusivity clash: picking Instrumental clears every language,
        /// and picking a language clears Instrumental, so the mapper never sees a click silently refused.
        /// </summary>
        public static IReadOnlyList<BeatmapLanguage> Toggle(IReadOnlyList<BeatmapLanguage> current, BeatmapLanguage toggled)
        {
            if (toggled == BeatmapLanguage.Unspecified)
                return Normalise(current);

            if (current.Contains(toggled))
                return Normalise(current.Where(l => l != toggled));

            if (toggled == BeatmapLanguage.Instrumental)
                return new[] { BeatmapLanguage.Instrumental };

            return Normalise(current.Where(l => l != BeatmapLanguage.Instrumental).Append(toggled));
        }

        /// <summary>
        /// The ONE language that speaks for a multi-language map wherever a single language is still read: the
        /// first entry that is not <see cref="BeatmapLanguage.English"/>, or English when it is the only entry
        /// (<see cref="BeatmapLanguage.Unspecified"/> for an empty list).
        /// </summary>
        /// <remarks>
        /// Every consumer of the single language (romanisation, original-script lines, Japanese input, the
        /// Polyglot rating rules, the legacy <c>Language:</c> line old clients and servers read) is asking about
        /// the language that is NOT plain English, because English is what the game types natively and needs no
        /// special handling. An English and Japanese song therefore has Japanese as its primary however the
        /// mapper ordered the picks, so a mixed map romanises and rates exactly as the Japanese map it mostly
        /// behaves like. Between two non-English languages the mapper's first pick wins. A single-language list
        /// is its own primary, which is what keeps every existing map's encoding and behaviour unchanged. The
        /// server derives the same primary (<c>BeatmapLanguages.Primary</c>), pinned by WireCompat.
        /// </remarks>
        public static BeatmapLanguage PrimaryOf(IEnumerable<BeatmapLanguage> languages)
        {
            var first = BeatmapLanguage.Unspecified;

            foreach (var language in languages)
            {
                if (language == BeatmapLanguage.Unspecified)
                    continue;

                if (language != BeatmapLanguage.English)
                    return language;

                if (first == BeatmapLanguage.Unspecified)
                    first = language;
            }

            return first;
        }

        /// <summary>
        /// The <c>[Metadata] Languages:</c> value for a list: the normalised canonical names joined by
        /// <see cref="LIST_SEPARATOR"/> (<c>japanese,english</c>), or empty for an empty list.
        /// </summary>
        public static string ToCanonicalList(this IEnumerable<BeatmapLanguage> languages)
            => string.Join(LIST_SEPARATOR, Normalise(languages).Select(l => l.ToCanonicalName()));

        /// <summary>
        /// Inverse of <see cref="ToCanonicalList"/>, total like <see cref="FromCanonicalName"/>: every entry it
        /// does not recognise (a language a newer client added) is dropped rather than failing the decode, and the
        /// result is normalised. A single name with no separator reads as a one-entry list, so this also reads an
        /// old <c>Language:</c> line.
        /// </summary>
        public static IReadOnlyList<BeatmapLanguage> FromCanonicalList(string? names)
        {
            if (string.IsNullOrWhiteSpace(names))
                return Array.Empty<BeatmapLanguage>();

            return Normalise(names.Split(LIST_SEPARATOR).Select(FromCanonicalName));
        }

        /// <summary>
        /// The display text for a list: the languages' descriptions in list order, comma separated, or the
        /// description of <see cref="BeatmapLanguage.Unspecified"/> for an empty list.
        /// </summary>
        public static LocalisableString ToDisplayText(this IReadOnlyList<BeatmapLanguage> languages)
        {
            switch (languages.Count)
            {
                case 0:
                    return BeatmapLanguage.Unspecified.GetLocalisableDescription();

                case 1:
                    return languages[0].GetLocalisableDescription();

                default:
                    // Formatted over each language's localisable description ("{0}, {1}"), so a localised build
                    // still names every language in its own words.
                    string format = string.Join(", ", Enumerable.Range(0, languages.Count).Select(i => $"{{{i}}}"));
                    return LocalisableString.Format(format, languages.Select(l => (object?)l.GetLocalisableDescription()).ToArray());
            }
        }
    }
}
