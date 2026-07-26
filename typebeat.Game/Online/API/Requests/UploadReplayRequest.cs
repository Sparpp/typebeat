// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Net.Http;
using osu.Framework.IO.Network;

namespace typebeat.Game.Online.API.Requests
{
    /// <summary>
    /// Uploads a legacy replay (.osr) for a score that has already been submitted online.
    /// </summary>
    /// <remarks>
    /// Wire contract, fixed against the server: <c>PUT /api/v2/scores/{scoreId}/replay</c>, bearer
    /// auth, owner only, the raw .osr bytes as <c>application/octet-stream</c>, a 5MB cap, 204 on
    /// success, idempotent overwrite. A server that predates the route answers 404; callers are
    /// expected to swallow that into a log line rather than surfacing it.
    /// </remarks>
    public class UploadReplayRequest : APIRequest
    {
        /// <summary>
        /// The largest payload the server accepts. Anything above this is rejected here so a doomed
        /// upload never leaves the machine.
        /// </summary>
        public const int MAX_REPLAY_BYTES = 5 * 1024 * 1024;

        /// <summary>
        /// The online (solo) score id the replay belongs to.
        /// </summary>
        public long ScoreId { get; }

        /// <summary>
        /// The raw .osr payload, exactly as <c>LegacyScoreEncoder</c> produced it.
        /// </summary>
        public byte[] ReplayData { get; }

        /// <summary>
        /// The API target path for a given score. Exposed so the wire shape can be pinned by tests
        /// without reaching through the protected <see cref="Target"/> member.
        /// </summary>
        public static string TargetFor(long scoreId) => $@"scores/{scoreId}/replay";

        public UploadReplayRequest(long scoreId, byte[] replayData)
        {
            ArgumentNullException.ThrowIfNull(replayData);

            if (scoreId <= 0)
                throw new ArgumentOutOfRangeException(nameof(scoreId), scoreId, @"An online score id is required to upload a replay.");

            if (replayData.Length == 0)
                throw new ArgumentException(@"Refusing to upload an empty replay.", nameof(replayData));

            if (replayData.Length > MAX_REPLAY_BYTES)
                throw new ArgumentException($@"Replay exceeds the {MAX_REPLAY_BYTES} byte server limit.", nameof(replayData));

            ScoreId = scoreId;
            ReplayData = replayData;
        }

        protected override string Target => TargetFor(ScoreId);

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();

            req.Method = HttpMethod.Put;
            req.ContentType = @"application/octet-stream";
            req.Timeout = 30000;
            req.AddRaw(ReplayData);

            return req;
        }
    }
}
