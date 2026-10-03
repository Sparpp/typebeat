// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using typebeat.Game.Beatmaps;
using typebeat.Game.Database;
using typebeat.Game.Extensions;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using Realms;

namespace typebeat.Game.Screens.Select
{
    /// <summary>
    /// This component is designed to perform lookups of online data
    /// and store portions of it for later local use to the realm database.
    /// </summary>
    /// <example>
    /// This component is designed to locally persist potentially-volatile online information such as:
    /// <list type="bullet">
    /// <item>user tags assigned to difficulties of a beatmap,</item>
    /// <item>the beatmap's <see cref="BeatmapInfo.Status"/>,</item>
    /// <item>guest mappers assigned to difficulties of a beatmap,</item>
    /// <item>the local user's best score on a given beatmap.</item>
    /// </list>
    /// </example>
    public partial class RealmPopulatingOnlineLookupSource : Component
    {
        [Resolved]
        private IAPIProvider api { get; set; } = null!;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        public Task<APIBeatmapSet?> GetBeatmapSetAsync(int id, CancellationToken token = default)
        {
            var request = new GetBeatmapSetRequest(id);
            var tcs = new TaskCompletionSource<APIBeatmapSet?>();

            token.Register(request.Cancel);

            // async request success callback is a bit of a dangerous game, but there's some reasoning for it.
            // - don't really want to use `IAPIAccess.PerformAsync()` because we still want to respect request queueing & online status checks
            // - we want the realm write here to be async because it is known to be slow for some users with large beatmap collections
            // - at the time of writing `RealmAccess.WriteAsync()` can only be safely called from update thread,
            //   and API request completion callbacks are automatically marshaled onto update thread scheduler,
            //   so calling `WriteAsync()` within the callback is a somewhat "nice" way of guaranteeing that the call is safe
            //   (rather than having to enforce that `GetBeatmapSetAsync()` can only be called from update thread, or locally scheduling)
            request.Success += async onlineBeatmapSet =>
            {
                if (token.IsCancellationRequested)
                {
                    tcs.SetCanceled(token);
                    return;
                }

                await realm.WriteAsync(r => updateRealmBeatmapSet(r, onlineBeatmapSet)).ConfigureAwait(true);
                tcs.SetResult(onlineBeatmapSet);
            };
            request.Failure += tcs.SetException;
            api.Queue(request);
            return tcs.Task;
        }

        private static void updateRealmBeatmapSet(Realm r, APIBeatmapSet onlineBeatmapSet)
        {
            var onlineBeatmaps = onlineBeatmapSet.Beatmaps.ToDictionary(b => b.OnlineID);

            var dbBeatmapSets = r.All<BeatmapSetInfo>().Where(b => b.OnlineID == onlineBeatmapSet.OnlineID);

            foreach (var dbBeatmapSet in dbBeatmapSets)
            {
                // note that every single write to realm models is preceded by a guard, even if it technically would write the same value back.
                // the reason this matters is that doing so avoids triggering realm subscription callbacks.
                // unfortunately in terms of subscriptions realm treats *every* write to any realm object as a modification,
                // even if the write was redundant and had no observable effect.

                // notably, `LocallyModified` status is preserved on the set until the user performs an explicit action to get rid of it
                // (be it updating the set or deciding to discard their changes, removing the set and re-downloading it, etc.)
                if (dbBeatmapSet.Status != onlineBeatmapSet.Status && dbBeatmapSet.Status != BeatmapOnlineStatus.LocallyModified)
                    dbBeatmapSet.Status = onlineBeatmapSet.Status;

                // The set's update identity, exactly as BeatmapUpdaterMetadataLookup records it. The set fetch
                // carries the version on the set itself; adopting it clears "behind" once the local copy is at
                // the online version without manufacturing an offer for a set never versioned before.
                dbBeatmapSet.AdoptOnlineVersion(onlineBeatmapSet.LastUpdated);

                // The online version's stem presence (backlog 396), the same flag the metadata lookup
                // records. Song select's set fetch is the other refresh point, so the UPDATE offer is
                // correct whichever lookup ran last.
                dbBeatmapSet.RecordOnlineVocalsStem(onlineBeatmapSet.HasVocalsStem);

                foreach (var dbBeatmap in dbBeatmapSet.Beatmaps)
                {
                    if (onlineBeatmaps.TryGetValue(dbBeatmap.OnlineID, out var onlineBeatmap))
                    {
                        // compare `BeatmapUpdaterMetadataLookup`
                        if (dbBeatmap.OnlineMD5Hash != onlineBeatmap.MD5Hash)
                            dbBeatmap.OnlineMD5Hash = onlineBeatmap.MD5Hash;

                        if (dbBeatmap.LastOnlineUpdate != onlineBeatmap.LastUpdated)
                            dbBeatmap.LastOnlineUpdate = onlineBeatmap.LastUpdated;

                        if (dbBeatmap.MatchesOnlineVersion && dbBeatmap.Status != onlineBeatmap.Status)
                            dbBeatmap.Status = onlineBeatmap.Status;

                        onlineBeatmap.BeatmapSet = onlineBeatmapSet;
                        HashSet<string> userTags = onlineBeatmap.GetTopUserTags(confirmedOnly: true)
                                                                .Select(t => t.Tag.Name)
                                                                .ToHashSet();

                        if (!userTags.SetEquals(dbBeatmap.Metadata.UserTags))
                        {
                            dbBeatmap.Metadata.UserTags.Clear();
                            dbBeatmap.Metadata.UserTags.AddRange(userTags);
                        }
                    }
                }
            }
        }
    }
}
