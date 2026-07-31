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
            AudioFile = AudioFile,
            BackgroundFile = BackgroundFile
        };
    }
}
