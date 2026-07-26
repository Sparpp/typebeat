// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using osu.Framework.Logging;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.Multiplayer;
using typebeat.Game.Scoring;

namespace typebeat.Game.Online
{
    /// <summary>
    /// Pushes local replays up to the server so online scores can be watched back by anyone.
    /// </summary>
    /// <remarks>
    /// Everything here is fire and forget. A replay upload is a nice-to-have that must never disturb
    /// the play or results flow, so failures (including the 404 an old server returns for the route)
    /// are logged and dropped, never surfaced as notifications and never awaited by a caller.
    /// </remarks>
    public class ReplayUploader
    {
        /// <summary>
        /// How many retroactive uploads a single leaderboard fetch may kick off.
        /// </summary>
        /// <remarks>
        /// Backfill is opportunistic healing, not a sync job. Capping it keeps a leaderboard that is
        /// full of the local user's own replay-less scores from turning one screen open into fifty
        /// realm reads and fifty uploads.
        /// </remarks>
        public const int MAX_BACKFILLS_PER_FETCH = 3;

        private readonly IAPIProvider api;
        private readonly ScoreManager scores;

        /// <summary>
        /// Online score ids already attempted this session, successfully or not. Guards against a
        /// leaderboard that is refetched on every filter change re-uploading the same replays.
        /// </summary>
        private readonly HashSet<long> attempted = new HashSet<long>();

        public ReplayUploader(IAPIProvider api, ScoreManager scores)
        {
            this.api = api;
            this.scores = scores;
        }

        /// <summary>
        /// Upload the replay that was just recorded for a score that has just been submitted.
        /// </summary>
        /// <param name="onlineScoreId">The online score id the submission returned.</param>
        /// <param name="replayData">The encoded .osr payload.</param>
        public void Upload(long onlineScoreId, byte[] replayData) => queue(onlineScoreId, replayData, @"freshly recorded");

        /// <summary>
        /// Given a batch of online score rows, retroactively upload the local replay for any of the
        /// local user's own scores the server does not hold a replay for.
        /// </summary>
        /// <remarks>
        /// Detection is limited to whatever rows the caller hands over, which in practice means the
        /// leaderboard currently being viewed. There is no background sweep of every past score; old
        /// scores heal as their leaderboards get looked at.
        /// </remarks>
        public void Backfill(IEnumerable<ScoreInfo> onlineScores)
        {
            if (!api.IsLoggedIn)
                return;

            long localUserId = api.LocalUser.Value.OnlineID;

            // ids at or below 1 are the guest/system placeholders; there is no "own score" to speak of.
            if (localUserId <= 1)
                return;

            var candidates = new List<ScoreInfo>();

            lock (attempted)
            {
                foreach (var score in onlineScores)
                {
                    if (!ReplayAvailabilityResolver.ShouldBackfill(score.UserID == localUserId, score.OnlineID, score.HasOnlineReplay, attempted.Contains(score.OnlineID)))
                        continue;

                    attempted.Add(score.OnlineID);
                    candidates.Add(score);

                    if (candidates.Count >= MAX_BACKFILLS_PER_FETCH)
                        break;
                }
            }

            if (candidates.Count == 0)
                return;

            Task.Run(() =>
            {
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var local = scores.FindLocalScoreWithReplay(candidate);

                        if (local == null)
                            continue;

                        byte[]? replayData = scores.GetRawReplayBytes(local);

                        if (replayData == null)
                            continue;

                        queue(candidate.OnlineID, replayData, @"backfilled from the local score store");
                    }
                    catch (Exception e)
                    {
                        Logger.Log($@"Replay backfill for score {candidate.OnlineID} could not be prepared ({e.Message}).", LoggingTarget.Network);
                    }
                }
            }).FireAndForget();
        }

        private void queue(long onlineScoreId, byte[] replayData, string origin)
        {
            if (onlineScoreId <= 0 || replayData.Length == 0)
                return;

            if (!api.IsLoggedIn)
                return;

            if (replayData.Length > UploadReplayRequest.MAX_REPLAY_BYTES)
            {
                Logger.Log($@"Replay for score {onlineScoreId} is {replayData.Length} bytes, above the server limit; not uploading.", LoggingTarget.Network);
                return;
            }

            lock (attempted)
                attempted.Add(onlineScoreId);

            var request = new UploadReplayRequest(onlineScoreId, replayData);

            request.Success += () => Logger.Log($@"Replay uploaded for score {onlineScoreId} ({origin}, {replayData.Length} bytes).", LoggingTarget.Network);

            // Deliberately not Logger.Error: an old server answers 404 here, and no upload failure is
            // worth interrupting the user over.
            request.Failure += e => Logger.Log($@"Replay upload for score {onlineScoreId} failed ({e.Message}); the score itself is unaffected.", LoggingTarget.Network);

            api.Queue(request);
        }
    }
}
