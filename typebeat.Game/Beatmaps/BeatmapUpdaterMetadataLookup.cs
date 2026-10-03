// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using osu.Framework.Platform;
using typebeat.Game.Extensions;
using typebeat.Game.Online.API;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// A component which handles population of online IDs for beatmaps using a two part lookup procedure.
    /// </summary>
    public class BeatmapUpdaterMetadataLookup : IDisposable
    {
        private readonly IOnlineBeatmapMetadataSource apiMetadataSource;
        private readonly IOnlineBeatmapMetadataSource localCachedMetadataSource;

        public BeatmapUpdaterMetadataLookup(IAPIProvider api, Storage storage)
            : this(new APIBeatmapMetadataSource(api), new LocalCachedBeatmapMetadataSource(storage))
        {
        }

        internal BeatmapUpdaterMetadataLookup(IOnlineBeatmapMetadataSource apiMetadataSource, IOnlineBeatmapMetadataSource localCachedMetadataSource)
        {
            this.apiMetadataSource = apiMetadataSource;
            this.localCachedMetadataSource = localCachedMetadataSource;
        }

        /// <summary>
        /// Queue an update for a beatmap set.
        /// </summary>
        /// <remarks>
        /// This may happen during initial import, or at a later stage in response to a user action or server event.
        /// </remarks>
        /// <param name="beatmapSet">The beatmap set to update. Updates will be applied directly (so a transaction should be started if this instance is managed).</param>
        /// <param name="preferOnlineFetch">Whether metadata from an online source should be preferred. If <c>true</c>, the local cache will be skipped to ensure the freshest data state possible.</param>
        public void Update(BeatmapSetInfo beatmapSet, bool preferOnlineFetch)
        {
            var lookupResults = new List<OnlineBeatmapMetadata?>();

            foreach (var beatmapInfo in beatmapSet.Beatmaps)
            {
                // note that these lookups DO NOT ACTUALLY FULLY GUARANTEE that the beatmap is what it claims it is,
                // i.e. the correctness of this lookup should be treated as APPROXIMATE AT WORST.
                // this is because the beatmap filename is used as a fallback in some scenarios where the MD5 of the beatmap may mismatch.
                // this is considered to be an acceptable casualty so that things can continue to work as expected for users in some rare scenarios
                // (stale beatmap files in beatmap packs, beatmap mirror desyncs).
                // however, all this means that other places such as score submission ARE EXPECTED TO VERIFY THE MD5 OF THE BEATMAP AGAINST THE ONLINE ONE EXPLICITLY AGAIN.
                //
                // additionally note that the online ID stored to the map is EXPLICITLY NOT USED because some users in a silly attempt to "fix" things for themselves on stable
                // would reuse online IDs of already submitted beatmaps, which means that information is pretty much expected to be bogus in a nonzero number of beatmapsets.
                if (!tryLookup(beatmapInfo, preferOnlineFetch, out var res))
                    continue;

                if (res == null)
                {
                    beatmapInfo.ResetOnlineInfo();
                    lookupResults.Add(null); // mark lookup failure
                    continue;
                }

                lookupResults.Add(res);

                beatmapInfo.OnlineID = res.BeatmapID;
                beatmapInfo.OnlineMD5Hash = res.MD5Hash;
                beatmapInfo.LastOnlineUpdate = res.LastUpdated;

                Debug.Assert(beatmapInfo.BeatmapSet != null);
                beatmapInfo.BeatmapSet.OnlineID = res.BeatmapSetID;

                // Some metadata should only be applied if there's no local changes.
                if (beatmapInfo.MatchesOnlineVersion)
                {
                    beatmapInfo.Status = res.BeatmapStatus;
                    beatmapInfo.Metadata.Author.OnlineID = res.AuthorID;
                    beatmapInfo.Metadata.UserTags.Clear();
                    beatmapInfo.Metadata.UserTags.AddRange(res.UserTags);

                    // The oldest server sets were uploaded before the [Metadata] Language: line existed, so their
                    // files decode to Unspecified while the server knows the language. Fill that gap only: a row
                    // that already holds a language got it from its own file (or an earlier lookup), and the file
                    // is the authority. The file stamp in BeatmapUpdater.Process runs after this and overwrites
                    // with whatever the file states, so the order of the two never lets the server win.
                    if (beatmapInfo.Metadata.Language == BeatmapLanguage.Unspecified && res.Language != BeatmapLanguage.Unspecified)
                        beatmapInfo.Metadata.Language = res.Language;
                }
            }

            // The set's own update identity (see BeatmapSetInfo.OnlineVersionLastUpdated). The server reports
            // the set version on every difficulty, so any successful result carries it; adopting it here is
            // what clears the "behind" state once the local copy is genuinely at the online version, and what
            // a first-ever lookup of an unversioned set records without manufacturing an offer.
            DateTimeOffset? onlineVersion = lookupResults.Where(r => r != null).Select(r => (DateTimeOffset?)r!.LastUpdated).Max();
            beatmapSet.AdoptOnlineVersion(onlineVersion);

            // What the online version offers about the stem (backlog 396), reported on every difficulty of
            // the set. Only recorded when a lookup actually succeeded: a failed or absent one must not blank a
            // flag that is holding the UPDATE offer open. Same source as the version baseline, so a fresh
            // import (which runs this lookup) seeds it too, and a plain lookup of an already-installed set
            // refreshes it on first sight.
            var successful = lookupResults.Where(r => r != null).ToList();

            if (successful.Count > 0)
                beatmapSet.RecordOnlineVocalsStem(successful.Any(r => r!.HasVocalsStem));

            if (beatmapSet.Beatmaps.All(b => b.MatchesOnlineVersion)
                && lookupResults.All(r => r != null)
                && lookupResults.Select(r => r!.BeatmapSetID).Distinct().Count() == 1)
            {
                var representative = lookupResults.First()!;

                beatmapSet.Status = representative.BeatmapSetStatus ?? BeatmapOnlineStatus.None;
                beatmapSet.DateRanked = representative.DateRanked;
                beatmapSet.DateSubmitted = representative.DateSubmitted;
            }
        }

        /// <summary>
        /// Attempts to retrieve the <see cref="OnlineBeatmapMetadata"/> for the given <paramref name="beatmapInfo"/>.
        /// </summary>
        /// <param name="beatmapInfo">The beatmap to perform the online lookup for.</param>
        /// <param name="preferOnlineFetch">Whether online sources should be preferred for the lookup.</param>
        /// <param name="result">The result of the lookup. Can be <see langword="null"/> if no matching beatmap was found (or the lookup failed).</param>
        /// <returns>
        /// <see langword="true"/> if any of the metadata sources were available and returned a valid <paramref name="result"/>.
        /// <see langword="false"/> if none of the metadata sources were available, or if there was insufficient data to return a valid <paramref name="result"/>.
        /// </returns>
        /// <remarks>
        /// There are two cases wherein this method will return <see langword="false"/>:
        /// <list type="bullet">
        /// <item>If neither the local cache or the API are available to query.</item>
        /// <item>If the API is not available to query, and a positive match was not made in the local cache.</item>
        /// </list>
        /// In either case, the online ID read from the .osu file will be preserved, which may not necessarily be what we want.
        /// TODO: reconsider this if/when a better flow for queueing online retrieval is implemented.
        /// </remarks>
        private bool tryLookup(BeatmapInfo beatmapInfo, bool preferOnlineFetch, out OnlineBeatmapMetadata? result)
        {
            bool useLocalCache = !apiMetadataSource.Available || !preferOnlineFetch;
            if (useLocalCache && localCachedMetadataSource.TryLookup(beatmapInfo, out result))
                return true;

            if (apiMetadataSource.TryLookup(beatmapInfo, out result))
                return true;

            result = null;
            return false;
        }

        public void Dispose()
        {
            apiMetadataSource.Dispose();
            localCachedMetadataSource.Dispose();
        }
    }
}
