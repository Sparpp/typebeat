// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Net;
using typebeat.Game.Database;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;

namespace typebeat.Game.Beatmaps
{
    /// <summary>
    /// Performs online metadata lookups using the osu-web API.
    /// </summary>
    public class APIBeatmapMetadataSource : IOnlineBeatmapMetadataSource
    {
        private readonly IAPIProvider api;

        public APIBeatmapMetadataSource(IAPIProvider api)
        {
            this.api = api;
        }

        /// <summary>
        /// Online, or a guest (<see cref="APIState.Offline"/>: no login at all), since a lookup is a public read
        /// (<see cref="GetBeatmapRequest.AllowsAnonymous"/>) and a guest's imports must learn their online id and
        /// ranked status too. Not while <see cref="APIState.Connecting"/> or <see cref="APIState.Failing"/>, as before.
        /// </summary>
        public bool Available => IsAvailableIn(api.State.Value);

        internal static bool IsAvailableIn(APIState state) => state == APIState.Online || state == APIState.Offline;

        /// <summary>
        /// Whether a failed lookup is the server's verdict that the map is not online, which is what clears a
        /// map's online state, rather than a lookup that never got an answer.
        /// </summary>
        /// <remarks>
        /// A signed-in lookup has always treated every failure as the verdict, and still does: an
        /// <see cref="APIState.Online"/> session has just reached the server. A GUEST lookup must not. A guest is
        /// <see cref="APIState.Offline"/> whether or not there is any network, so the cases a session's own
        /// state used to screen out (no connection at all, a server that refuses anonymous lookups) all arrive
        /// here, and reading them as "not online" would wipe the online id of every map a guest imports with
        /// the network down, which no later lookup recovers (the reprocess pass only revisits maps that still
        /// HAVE an online id). Only a 404 is a verdict; anything else reports the source unavailable, so the
        /// lookup leaves the map exactly as its file states.
        /// </remarks>
        internal static bool IsNotFoundVerdict(APIState stateAtLookup, Exception? failure)
        {
            if (stateAtLookup != APIState.Offline)
                return true;

            return failure is APIException { StatusCode: HttpStatusCode.NotFound }
                   || (failure is WebException webException && webException.Message == nameof(HttpStatusCode.NotFound));
        }

        public bool TryLookup(BeatmapInfo beatmapInfo, out OnlineBeatmapMetadata? onlineMetadata)
        {
            var stateAtLookup = api.State.Value;

            if (!IsAvailableIn(stateAtLookup))
            {
                onlineMetadata = null;
                return false;
            }

            Debug.Assert(beatmapInfo.BeatmapSet != null);

            var req = new GetBeatmapRequest(md5Hash: beatmapInfo.MD5Hash, filename: beatmapInfo.Path);

            try
            {
                // intentionally blocking to limit web request concurrency
                api.Perform(req);

                if (req.CompletionState == APIRequestCompletionState.Failed)
                {
                    logForModel(beatmapInfo.BeatmapSet, $@"Online retrieval failed for {beatmapInfo}");
                    onlineMetadata = null;
                    return IsNotFoundVerdict(stateAtLookup, req.FailureException);
                }

                var res = req.Response;

                if (res != null)
                {
                    logForModel(beatmapInfo.BeatmapSet, $@"Online retrieval mapped {beatmapInfo} to {res.OnlineBeatmapSetID} / {res.OnlineID}.");

                    onlineMetadata = new OnlineBeatmapMetadata
                    {
                        BeatmapID = res.OnlineID,
                        BeatmapSetID = res.OnlineBeatmapSetID,
                        AuthorID = res.AuthorID,
                        BeatmapStatus = res.Status,
                        BeatmapSetStatus = res.BeatmapSet?.Status,
                        DateRanked = res.BeatmapSet?.Ranked,
                        DateSubmitted = res.BeatmapSet?.Submitted,
                        MD5Hash = res.MD5Hash,
                        LastUpdated = res.LastUpdated,
                        // type!beat's own song language, not osu-web's `language` object (see APIBeatmapSet.SongLanguage).
                        // Total: an older server that omits it, or a name this build does not know, reads as Unspecified.
                        Language = BeatmapLanguageExtensions.FromCanonicalName(res.BeatmapSet?.SongLanguage),
                        // Whether the current online version carries a vocals stem (backlog 396). An older
                        // server omits it, which reads as false ("nothing to offer").
                        HasVocalsStem = res.BeatmapSet?.HasVocalsStem ?? false,
                        // Tags are not populated because the response does not contain tag data.
                        // TODO: consider web change to include the tag data? or a second web request for the set to retrieve tags?
                    };
                    return true;
                }
            }
            catch (Exception e)
            {
                logForModel(beatmapInfo.BeatmapSet, $@"Online retrieval failed for {beatmapInfo} ({e.Message})");
                onlineMetadata = null;
                return false;
            }

            onlineMetadata = null;
            return false;
        }

        private void logForModel(BeatmapSetInfo set, string message) =>
            RealmArchiveModelImporter<BeatmapSetInfo>.LogForModel(set, $@"[{nameof(APIBeatmapMetadataSource)}] {message}");

        public void Dispose()
        {
        }
    }
}
