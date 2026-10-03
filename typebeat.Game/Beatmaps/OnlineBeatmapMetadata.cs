// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// This structure contains parts of beatmap metadata which are involved with the online parts
    /// of the game, and therefore must be treated with particular care.
    /// This data is retrieved from trusted sources (such as osu-web API, or a locally downloaded sqlite snapshot
    /// of osu-web metadata).
    /// </summary>
    public class OnlineBeatmapMetadata
    {
        /// <summary>
        /// The online ID of the beatmap.
        /// </summary>
        public int BeatmapID { get; init; }

        /// <summary>
        /// The online ID of the beatmap set.
        /// </summary>
        public int BeatmapSetID { get; init; }

        /// <summary>
        /// The online ID of the author.
        /// </summary>
        public int AuthorID { get; init; }

        /// <summary>
        /// The online status of the beatmap.
        /// </summary>
        public BeatmapOnlineStatus BeatmapStatus { get; init; }

        /// <summary>
        /// The online status of the associated beatmap set.
        /// </summary>
        public BeatmapOnlineStatus? BeatmapSetStatus { get; init; }

        /// <summary>
        /// The rank date of the beatmap, if applicable and available.
        /// </summary>
        public DateTimeOffset? DateRanked { get; init; }

        /// <summary>
        /// The submission date of the beatmap, if available.
        /// </summary>
        public DateTimeOffset? DateSubmitted { get; init; }

        /// <summary>
        /// The MD5 hash of the beatmap. Used to verify integrity.
        /// </summary>
        public string MD5Hash { get; init; } = string.Empty;

        /// <summary>
        /// The date when this metadata was last updated.
        /// </summary>
        public DateTimeOffset LastUpdated { get; init; }

        /// <summary>
        /// The list of tags that users have assigned to this beatmap.
        /// </summary>
        public List<string> UserTags { get; } = [];

        /// <summary>
        /// The song language the server stores for the beatmap's set, or <see cref="BeatmapLanguage.Unspecified"/>
        /// when it has none or the source does not carry one (the local metadata cache never does).
        /// </summary>
        /// <remarks>
        /// Only ever fills a gap: <see cref="BeatmapUpdaterMetadataLookup"/> applies it to a row whose language is
        /// still unspecified, which is what a file uploaded before the <c>[Metadata] Language:</c> line existed
        /// decodes to. A language the file itself states always wins.
        /// </remarks>
        public BeatmapLanguage Language { get; init; }

        /// <summary>
        /// Whether the online set's CURRENT version carries an isolated vocals stem (backlog 396), the
        /// server's <c>has_vocals_stem</c>. Set-level: the server reports the same value on every
        /// difficulty of the set. False from a source that does not carry it (the local metadata cache,
        /// or an older server), which reads as "nothing to offer".
        /// </summary>
        /// <remarks>
        /// The update offer compares this against the LOCAL copy's own stem presence
        /// (<see cref="VocalsStem.FilenameIn"/>): online has a stem and local has none means the local copy
        /// is missing a file the online version offers, even though its <c>.osu</c> MD5s are unchanged.
        /// </remarks>
        public bool HasVocalsStem { get; init; }
    }
}
