// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Net.Http;
using osu.Framework.IO.Network;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API.Requests
{
    /// <summary>
    /// Asks the server what a chunked upload session currently holds.
    /// </summary>
    /// <remarks>
    /// Wire contract, fixed against the server: <c>GET {bss}/upload-sessions/{sessionId}</c>, answering
    /// with the same <see cref="UploadSessionResponse"/> creation does.
    ///
    /// This exists because a chunk PUT can be stored by the server and still fail on the client: a
    /// middlebox black-holes the RESPONSE, so the server logs 204 and the client sees an idle timeout.
    /// Repeating that chunk blindly then burns the attempt cap on work that was already done, and three
    /// such failures in a row also trip the API's own failure counter, which flushes everything still
    /// queued. Asking what the server actually has turns both of those into a no-op.
    ///
    /// The timeout is short for the same reason the chunk request's is: the answer is a few dozen bytes,
    /// so a slow one is a dead one. A server that predates the route answers 404, which decodes into an
    /// <see cref="APIException"/>, and the caller falls back to retrying the chunk blindly.
    /// </remarks>
    public class GetUploadSessionRequest : APIRequest<UploadSessionResponse>
    {
        protected override string Uri
        {
            get
            {
                if (API!.Endpoints.BeatmapSubmissionServiceUrl == null)
                    throw new NotSupportedException("Beatmap submission not supported in this configuration!");

                return $@"{API!.Endpoints.BeatmapSubmissionServiceUrl}/upload-sessions/{SessionId}";
            }
        }

        protected override string Target => throw new NotSupportedException();

        public string SessionId { get; }

        public GetUploadSessionRequest(string sessionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);

            SessionId = sessionId;
        }

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();

            req.Method = HttpMethod.Get;
            req.Timeout = 30_000;
            // one fresh connection per session request; see UploadSessionChunkRequest (backlog 201).
            req.AddHeader(@"Connection", @"close");

            return req;
        }
    }
}
