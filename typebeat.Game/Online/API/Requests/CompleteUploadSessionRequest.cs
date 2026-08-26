// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Net.Http;
using osu.Framework.IO.Network;

namespace typebeat.Game.Online.API.Requests
{
    /// <summary>
    /// Closes a chunked upload session, which is what actually runs the package ingest.
    /// </summary>
    /// <remarks>
    /// Wire contract, fixed against the server: <c>POST {bss}/upload-sessions/{sessionId}/complete</c>
    /// with an empty body, 204 on success, a <c>WireJson</c> error envelope otherwise (which decodes
    /// into an <see cref="APIException"/> like any other beatmap submission error).
    ///
    /// The timeout matches the single-request uploads rather than the chunk requests: the request
    /// body is empty, but the server parses, validates and ingests the whole assembled package inside
    /// it, which is the same work the monolithic upload did after its body finished arriving.
    /// </remarks>
    public class CompleteUploadSessionRequest : APIRequest
    {
        protected override string Uri
        {
            get
            {
                if (API!.Endpoints.BeatmapSubmissionServiceUrl == null)
                    throw new NotSupportedException("Beatmap submission not supported in this configuration!");

                return $@"{API!.Endpoints.BeatmapSubmissionServiceUrl}/upload-sessions/{SessionId}/complete";
            }
        }

        protected override string Target => throw new NotSupportedException();

        public string SessionId { get; }

        public CompleteUploadSessionRequest(string sessionId)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);

            SessionId = sessionId;
        }

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();

            req.Method = HttpMethod.Post;
            req.Timeout = 600_000;

            // an empty raw body rather than no body at all. Without raw content the framework attaches no
            // content whatsoever: its multipart fallback only builds a form when there are parameters or
            // files to put in one, and there are neither here. This keeps a definite `Content-Length: 0`,
            // which is what the contract asks for and what a proxy in the middle is least surprised by.
            req.AddRaw(Array.Empty<byte>());

            return req;
        }
    }
}
