// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Linq;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Beatmaps
{
    /// <summary>Applies website corrections to display rows without rewriting the map or its identity.</summary>
    internal static class OnlineBeatmapDisplayMetadata
    {
        public static bool HasSameDisplayState(BeatmapInfo current, BeatmapInfo persisted)
            => current.OnlineID == persisted.OnlineID
               && current.Status == persisted.Status
               && current.MD5Hash == persisted.MD5Hash
               && current.OnlineMD5Hash == persisted.OnlineMD5Hash
               && current.LastOnlineUpdate == persisted.LastOnlineUpdate
               && current.LastLocalUpdate == persisted.LastLocalUpdate
               && current.DifficultyName == persisted.DifficultyName
               && current.Metadata.Title == persisted.Metadata.Title
               && current.Metadata.TitleUnicode == persisted.Metadata.TitleUnicode
               && current.Metadata.Artist == persisted.Metadata.Artist
               && current.Metadata.ArtistUnicode == persisted.Metadata.ArtistUnicode
               && current.Metadata.Source == persisted.Metadata.Source
               && current.Metadata.Tags == persisted.Metadata.Tags
               && current.Metadata.UserTags.SequenceEqual(persisted.Metadata.UserTags)
               && current.BeatmapSet?.OnlineID == persisted.BeatmapSet?.OnlineID
               && current.BeatmapSet?.Status == persisted.BeatmapSet?.Status
               && current.BeatmapSet?.AllBeatmapsUpToDate == persisted.BeatmapSet?.AllBeatmapsUpToDate;

        public static void Apply(BeatmapInfo local, APIBeatmapSet set, APIBeatmap online)
        {
            if (local.Status == BeatmapOnlineStatus.LocallyModified || local.MD5Hash != online.MD5Hash)
                return;

            var metadata = local.Metadata;
            // Realm observes even redundant writes, so guard every assignment.
            if (metadata.Title != set.Title) metadata.Title = set.Title;
            if (metadata.TitleUnicode != set.TitleUnicode) metadata.TitleUnicode = set.TitleUnicode;
            if (metadata.Artist != set.Artist) metadata.Artist = set.Artist;
            if (metadata.ArtistUnicode != set.ArtistUnicode) metadata.ArtistUnicode = set.ArtistUnicode;
            if (metadata.Source != set.Source) metadata.Source = set.Source;
            if (metadata.Tags != set.Tags) metadata.Tags = set.Tags;
            if (local.DifficultyName != online.DifficultyName) local.DifficultyName = online.DifficultyName;
        }
    }
}
