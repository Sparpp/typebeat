// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
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
        /// The language the song is sung in. Chosen in the editor's song setup, written into the
        /// beatmap's <c>[Metadata] Language:</c> line, and required before the map can be
        /// submitted (see <c>Editor.submitBeatmap</c>).
        /// </summary>
        /// <remarks>
        /// Stored through <see cref="LanguageInt"/> the same way <see cref="BeatmapInfo.Status"/>
        /// is: realm persists the int, code sees the enum. <see cref="BeatmapLanguage.Unspecified"/>
        /// is 0 precisely so that realm's default for the newly added column means "not chosen".
        /// Deliberately NOT added to <see cref="IBeatmapMetadataInfo"/>: that interface is the
        /// display/search contract shared with the API response models, none of which carry a
        /// per-difficulty language, and widening its default equality would change what counts as
        /// a metadata difference across the whole game.
        /// </remarks>
        [Ignored]
        public BeatmapLanguage Language
        {
            get => (BeatmapLanguage)LanguageInt;
            set => LanguageInt = (int)value;
        }

        [MapTo(nameof(Language))]
        public int LanguageInt { get; set; } = (int)BeatmapLanguage.Unspecified;

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

        public string AudioFile { get; set; } = string.Empty;
        public string BackgroundFile { get; set; } = string.Empty;

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
            Language = Language,
            PreviewTime = PreviewTime,
            AudioGain = AudioGain,
            AudioFile = AudioFile,
            BackgroundFile = BackgroundFile
        };
    }
}
