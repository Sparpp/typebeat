// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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

        // Keep a small bounded cache: revisiting a set still revalidates, but unchanged sets need
        // only the tiny state response. Cache entries carry revisions from the full response,
        // never the earlier check, so an upload racing the two requests cannot mark old data fresh.
        private readonly Dictionary<int, APIBeatmapSet> cachedSets = new Dictionary<int, APIBeatmapSet>();
        private readonly Queue<int> cacheOrder = new Queue<int>();
        private int? cachedUserId;
        private double cacheExpiresAt;

        public void InvalidateCache()
        {
            cachedSets.Clear();
            cacheOrder.Clear();
            // Revisions do not cover play counts, ratings, or other mutable statistics.
            cacheExpiresAt = Time.Current + 60000;
        }

        public Task<APIBeatmapSet?> GetBeatmapSetAsync(int id, CancellationToken token = default)
        {
            int userId = api.LocalUser.Value.Id;
            if (cachedUserId != userId || Time.Current >= cacheExpiresAt)
            {
                InvalidateCache();
                cachedUserId = userId;
            }
            var tcs = new TaskCompletionSource<APIBeatmapSet?>();
            APIRequest? activeRequest = null;
            var registration = token.Register(() =>
            {
                activeRequest?.Cancel();
                tcs.TrySetCanceled(token);
            });
            _ = tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

            // API callbacks run on the update thread, as required by RealmAccess.WriteAsync.
            async void finish(APIBeatmapSet set)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (api.LocalUser.Value.Id != userId)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }
                    await realm.WriteAsync(r => updateRealmBeatmapSet(r, set)).ConfigureAwait(true);
                    token.ThrowIfCancellationRequested();
                    if (api.LocalUser.Value.Id != userId)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }
                    if (!cachedSets.ContainsKey(id))
                    {
                        if (cachedSets.Count >= 16)
                            cachedSets.Remove(cacheOrder.Dequeue());
                        cacheOrder.Enqueue(id);
                    }
                    cachedSets[id] = set;
                    tcs.TrySetResult(set);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(token);
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            }

            void fetchDetails()
            {
                if (token.IsCancellationRequested) return;
                if (api.LocalUser.Value.Id != userId)
                {
                    tcs.TrySetCanceled();
                    return;
                }
                var request = new GetBeatmapSetRequest(id);
                activeRequest = request;
                request.Success += finish;
                request.Failure += e => tcs.TrySetException(e);
                api.Queue(request);
            }

            if (!cachedSets.ContainsKey(id))
            {
                fetchDetails();
                return tcs.Task;
            }

            var stateRequest = new GetBeatmapSetStateRequest(id);
            activeRequest = stateRequest;
            stateRequest.Success += state =>
            {
                if (token.IsCancellationRequested) return;
                if (cachedSets.TryGetValue(id, out var cached) && state.Matches(cached))
                    finish(cached);
                else
                    fetchDetails();
            };
            stateRequest.Failure += e =>
            {
                // Server-first deployment is preferred; older servers have no state route.
                if (stateRequest.ResponseStatusCode == HttpStatusCode.NotFound || e is APIException { StatusCode: HttpStatusCode.NotFound })
                    fetchDetails();
                else
                    tcs.TrySetException(e);
            };
            if (!token.IsCancellationRequested)
                api.Queue(stateRequest);
            return tcs.Task;
        }

        private static void updateRealmBeatmapSet(Realm r, APIBeatmapSet onlineBeatmapSet)
        {
            var onlineBeatmaps = onlineBeatmapSet.Beatmaps.ToDictionary(b => b.OnlineID);

            foreach (var dbBeatmapSet in r.All<BeatmapSetInfo>().Where(b => b.OnlineID == onlineBeatmapSet.OnlineID))
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

                // The online version's tracked-file manifest (backlog 396), the same value the metadata
                // lookup records. Song select's set fetch is the other refresh point, so the UPDATE offer
                // is correct whichever lookup ran last.
                dbBeatmapSet.RecordOnlineFileManifest(SetFileManifest.Online(onlineBeatmapSet.FileManifest, onlineBeatmapSet.HasVocalsStem));

                foreach (var dbBeatmap in dbBeatmapSet.Beatmaps)
                {
                    if (onlineBeatmaps.TryGetValue(dbBeatmap.OnlineID, out var onlineBeatmap))
                    {
                        OnlineBeatmapDisplayMetadata.Apply(dbBeatmap, onlineBeatmapSet, onlineBeatmap);
                        // compare `BeatmapUpdaterMetadataLookup`
                        if (dbBeatmap.OnlineMD5Hash != onlineBeatmap.MD5Hash)
                            dbBeatmap.OnlineMD5Hash = onlineBeatmap.MD5Hash;

                        if (dbBeatmap.LastOnlineUpdate != onlineBeatmap.LastUpdated)
                            dbBeatmap.LastOnlineUpdate = onlineBeatmap.LastUpdated;

                        if (dbBeatmap.MatchesOnlineVersion && dbBeatmap.Status != BeatmapOnlineStatus.LocallyModified && dbBeatmap.Status != onlineBeatmap.Status)
                            dbBeatmap.Status = onlineBeatmap.Status;

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
