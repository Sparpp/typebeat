// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JetBrains.Annotations;
using Newtonsoft.Json;
using typebeat.Game.Models;
using typebeat.Game.Screens.Select;
using typebeat.Game.Users;
using typebeat.Game.Utils;
using Realms;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// A realm model containing metadata for a beatmap.
    /// </summary>
    /// <remarks>
    /// An instance of this object is stored against each beatmap difficulty.
    /// It is also provided via <see cref="BeatmapSetInfo"/> for convenience and historical purposes.
    /// Note that accessing the metadata via <see cref="BeatmapSetInfo"/> may result in indeterminate results
    /// as metadata can meaningfully differ per beatmap in a set.
    ///
    /// Note that difficulty name is not stored in this metadata but in <see cref="BeatmapInfo"/>.
    /// </remarks>
    [Serializable]
    [MapTo("BeatmapMetadata")]
    public class BeatmapMetadata : RealmObject, IBeatmapMetadataInfo, IDeepCloneable<BeatmapMetadata>
    {
        public string Title { get; set; } = string.Empty;

        [JsonProperty("title_unicode")]
        public string TitleUnicode { get; set; } = string.Empty;

        public string Artist { get; set; } = string.Empty;

        [JsonProperty("artist_unicode")]
        public string ArtistUnicode { get; set; } = string.Empty;

        public RealmUser Author { get; set; } = null!;

        public string Source { get; set; } = string.Empty;

        [JsonProperty(@"tags")]
        public string Tags { get; set; } = string.Empty;

        /// <summary>
        /// The languages the song is sung in (backlog 472: a map may declare several), in the mapper's order.
        /// Chosen in the editor's song setup, written into the beatmap's <c>[Metadata] Language:</c> and
        /// <c>Languages:</c> lines, and at least one is required before the map can be submitted (see
        /// <c>Editor.submitBeatmap</c>). Empty means "not chosen". Always normalised on write
        /// (<see cref="BeatmapLanguageExtensions.Normalise"/>): no duplicates, no Unspecified, and
        /// Instrumental only ever alone.
        /// </summary>
        /// <remarks>
        /// Stored through <see cref="LanguageIDs"/>: realm persists the ids as text, code sees the list.
        /// Deliberately NOT added to <see cref="IBeatmapMetadataInfo"/>: that interface is the
        /// display/search contract shared with the API response models, none of which carry a
        /// per-difficulty language, and widening its default equality would change what counts as
        /// a metadata difference across the whole game.
        /// </remarks>
        [Ignored]
        [JsonIgnore]
        public IReadOnlyList<BeatmapLanguage> Languages
        {
            get => FromLanguageIDs(LanguageIDs);
            set => LanguageIDs = ToLanguageIDs(value);
        }

        /// <summary>
        /// The ONE language that speaks for the map wherever a single language is read (romanisation,
        /// original-script lines, Japanese input, the Polyglot rating rules, the legacy <c>Language:</c>
        /// line): <see cref="BeatmapLanguageExtensions.PrimaryOf"/> over <see cref="Languages"/>, or
        /// <see cref="BeatmapLanguage.Unspecified"/> when none is chosen. Setting it replaces the WHOLE list
        /// with that one language (Unspecified clears it), which is what every single-language writer means.
        /// </summary>
        /// <remarks>
        /// Read without allocating, straight off <see cref="LanguageIDs"/>: the editor and the lyric timeline
        /// read this every frame, and the editor's idle-allocation test holds them to zero.
        /// </remarks>
        [Ignored]
        [JsonIgnore]
        public BeatmapLanguage Language
        {
            get => PrimaryOfLanguageIDs(LanguageIDs);
            set => Languages = value == BeatmapLanguage.Unspecified ? Array.Empty<BeatmapLanguage>() : new[] { value };
        }

        /// <summary>
        /// Realm's storage for <see cref="Languages"/>: the languages' persisted ids (the enum's values, which
        /// <see cref="BeatmapLanguage"/> never renumbers) joined by <see cref="BeatmapLanguageExtensions.LIST_SEPARATOR"/>,
        /// e.g. <c>2,1</c> for Japanese then English, or empty for none. A string rather than a realm list so that
        /// it is a plain settable scalar: the editor's write mapper, the detach mapper and
        /// <see cref="DeepClone"/> all copy it with no special casing, and an editor save writes it back like any
        /// other field. Replaced the single int column <c>Language</c> in realm schema 68, whose migration
        /// carries every existing single value over.
        /// </summary>
        [MapTo("Languages")]
        public string LanguageIDs { get; set; } = string.Empty;

        /// <summary>Parses <see cref="LanguageIDs"/>. Total: anything it does not recognise is dropped.</summary>
        public static IReadOnlyList<BeatmapLanguage> FromLanguageIDs(string? ids)
        {
            if (string.IsNullOrEmpty(ids))
                return Array.Empty<BeatmapLanguage>();

            var parsed = new List<BeatmapLanguage>();

            foreach (string id in ids.Split(BeatmapLanguageExtensions.LIST_SEPARATOR))
            {
                if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                    parsed.Add((BeatmapLanguage)value);
            }

            return BeatmapLanguageExtensions.Normalise(parsed);
        }

        /// <summary>The <see cref="LanguageIDs"/> text for a list, normalised first.</summary>
        public static string ToLanguageIDs(IEnumerable<BeatmapLanguage> languages)
            => string.Join(BeatmapLanguageExtensions.LIST_SEPARATOR,
                BeatmapLanguageExtensions.Normalise(languages).Select(l => ((int)l).ToString(CultureInfo.InvariantCulture)));

        /// <summary>
        /// <see cref="BeatmapLanguageExtensions.PrimaryOf"/> read straight off stored ids without allocating:
        /// the first defined id that is not English, else English if present.
        /// </summary>
        public static BeatmapLanguage PrimaryOfLanguageIDs(string? ids)
        {
            if (string.IsNullOrEmpty(ids))
                return BeatmapLanguage.Unspecified;

            var first = BeatmapLanguage.Unspecified;
            ReadOnlySpan<char> remaining = ids;

            while (remaining.Length > 0)
            {
                int separator = remaining.IndexOf(BeatmapLanguageExtensions.LIST_SEPARATOR);
                var token = separator < 0 ? remaining : remaining[..separator];
                remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[(separator + 1)..];

                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                    continue;

                var language = (BeatmapLanguage)value;

                if (language == BeatmapLanguage.Unspecified || !Enum.IsDefined(language))
                    continue;

                if (language != BeatmapLanguage.English)
                    return language;

                first = language;
            }

            return first;
        }

        /// <summary>
        /// The list of user-voted tags applicable to this beatmap.
        /// This information is populated from online sources (<see cref="RealmPopulatingOnlineLookupSource"/>)
        /// and can meaningfully differ between beatmaps of a single set.
        /// </summary>
        public IList<string> UserTags { get; } = null!;

        /// <summary>
        /// The time in milliseconds to begin playing the track for preview purposes.
        /// If -1, the track should begin playing at 40% of its length.
        /// </summary>
        public int PreviewTime { get; set; } = -1;

        /// <summary>
        /// No gain change: the song exactly as the mapper imported it.
        /// </summary>
        public const double DEFAULT_AUDIO_GAIN = 1;

        /// <summary>
        /// The most gain a map may ask for, as a linear multiplier (4 = +12 dB).
        /// </summary>
        public const double MAX_AUDIO_GAIN = 4;

        /// <summary>
        /// The map's own TRACK GAIN, as a linear multiplier: <see cref="DEFAULT_AUDIO_GAIN"/> is the
        /// song exactly as it was imported, 2 is twice the amplitude (+6 dB), 0.5 half of it (-6 dB).
        /// Chosen with the bar under the audio track picker in the editor's setup screen, and applied
        /// wherever the map's track plays.
        /// </summary>
        /// <remarks>
        /// A GAIN and not a volume, and the difference is the whole reason the field exists: the
        /// audio stack's volume is capped at full scale (<c>AdjustableAudioComponent.Volume</c> and
        /// its aggregate are both limited to 0..1), so a quietly mastered song cannot be turned up by
        /// volume at all - it is already played at 1. A value above the default is applied as a real
        /// amplifier on the track mixer instead (<see cref="Audio.Effects.AudioGain"/>), which is the
        /// one place the signal may be scaled past its own level; the cost is that a map boosted past
        /// what its samples can hold clips, which is the mapper's call and why the bar is a choice and
        /// not an automatic normalisation.
        ///
        /// <para>Stored as <c>[Metadata] AudioGain:</c> and only written when it is not the default, so
        /// every map that never touches the bar still encodes byte for byte as it did. Values are
        /// clamped to [0, <see cref="MAX_AUDIO_GAIN"/>] on read (see <c>LegacyBeatmapDecoder</c>), so a
        /// hand-edited file cannot ask for an amplifier that eats the mix.</para>
        ///
        /// <para>Deliberately not on <see cref="IBeatmapMetadataInfo"/> for the reason
        /// <see cref="Language"/> is not: that interface is the display/search contract shared with the
        /// API response models, and widening its default equality would change what counts as a
        /// metadata difference across the whole game.</para>
        /// </remarks>
        public double AudioGain { get; set; } = DEFAULT_AUDIO_GAIN;

        /// <summary>
        /// THE one on-disk spelling of <see cref="AudioGain"/>, shared by the ruleset's native encoder
        /// and the legacy <c>.osu</c> encoder the <c>.osz</c> exporter uses.
        ///
        /// <para>Shared because it has to be: the two encoders write the same map, and a map exported
        /// through one and re-imported through the other must come back at the gain it left with. A
        /// rounded format ("0.####") on one side of that pair is what walked an exported 1.33333 back to
        /// 1.3333. "R" is round-trippable by definition, which is exactly the property wanted.</para>
        /// </summary>
        public static string EncodeAudioGain(double gain) => gain.ToString("R", CultureInfo.InvariantCulture);

        public string AudioFile { get; set; } = string.Empty;
        public string BackgroundFile { get; set; } = string.Empty;

        /// <summary>
        /// The mapper-chosen font family for the gameplay typing surface (backlog 291), or empty for
        /// the game's built-in lyric font. Stored as <c>[General] LyricFont:</c> and only written when
        /// a mapper has chosen one, so every existing map still encodes byte for byte as it did.
        /// </summary>
        /// <remarks>
        /// A THIRD source for the one family string the lyric stack renders in, not a new rendering
        /// path: the player's own <c>TypeBeatRulesetSetting.LyricFont</c> pick (when not Default)
        /// always wins, then this, then the built-in font. A family the player's machine cannot
        /// resolve falls back rather than failing the play. Deliberately not on
        /// <see cref="IBeatmapMetadataInfo"/> for the reason <see cref="Language"/> and
        /// <see cref="AudioGain"/> are not: that interface is the display/search contract shared with
        /// the API response models.
        /// </remarks>
        public string LyricFont { get; set; } = string.Empty;

        /// <summary>
        /// Optional filename (inside the beatmap set, like <see cref="BackgroundFile"/>) of a bundled
        /// font file for <see cref="LyricFont"/>, so players without the family installed still see
        /// the mapper's font. Stored as <c>[General] LyricFontFile:</c>, written only when set. A
        /// missing or corrupt file falls back to resolving <see cref="LyricFont"/> by name, then to
        /// the built-in font; it never fails the play.
        /// </summary>
        public string LyricFontFile { get; set; } = string.Empty;

        public BeatmapMetadata(RealmUser? user = null)
        {
            Author = user ?? new RealmUser();
        }

        [UsedImplicitly] // Realm
        private BeatmapMetadata()
        {
        }

        IUser IBeatmapMetadataInfo.Author => Author;

        public override string ToString() => this.GetDisplayTitle();

        public BeatmapMetadata DeepClone() => new BeatmapMetadata(Author.DeepClone())
        {
            Title = Title,
            TitleUnicode = TitleUnicode,
            Artist = Artist,
            ArtistUnicode = ArtistUnicode,
            Source = Source,
            Tags = Tags,
            LanguageIDs = LanguageIDs,
            PreviewTime = PreviewTime,
            AudioGain = AudioGain,
            AudioFile = AudioFile,
            BackgroundFile = BackgroundFile,
            LyricFont = LyricFont,
            LyricFontFile = LyricFontFile
        };
    }
}
