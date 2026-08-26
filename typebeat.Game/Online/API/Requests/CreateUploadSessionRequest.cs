// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Net.Http;
using Newtonsoft.Json;
using osu.Framework.IO.Network;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Online.API.Requests
{
    /// <summary>
    /// Opens (or re-opens) a chunked upload session for one beatmap package payload.
    /// </summary>
    /// <remarks>
    /// Wire contract, fixed against the server:
    /// <c>POST {bss}/beatmapsets/{setId}/upload-sessions</c> with a JSON body of
    /// <c>kind</c> / <c>content_type</c> / <c>total_bytes</c> / <c>sha256</c>, answering with an
    /// <see cref="UploadSessionResponse"/>.
    ///
    /// Creation is IDEMPOTENT on (kind, sha256, total_bytes): repeating it returns the same session
    /// along with the chunks already received, which is what makes resume work without any separate
    /// status route. A server that predates the route answers 404, which decodes into an
    /// <see cref="APIException"/> and lets the caller fall back to the single-request upload.
    /// </remarks>
    public class CreateUploadSessionRequest : APIRequest<UploadSessionResponse>
    {
        /// <summary>
        /// The <c>kind</c> value for a full package replace.
        /// </summary>
        public const string KIND_FULL = @"full";

        /// <summary>
        /// The <c>kind</c> value for a partial (changed and deleted files) update.
        /// </summary>
        public const string KIND_PATCH = @"patch";

        protected override string Uri
        {
            get
            {
                if (API!.Endpoints.BeatmapSubmissionServiceUrl == null)
                    throw new NotSupportedException("Beatmap submission not supported in this configuration!");

                return $@"{API!.Endpoints.BeatmapSubmissionServiceUrl}/beatmapsets/{BeatmapSetID}/upload-sessions";
            }
        }

        protected override string Target => throw new NotSupportedException();

        public uint BeatmapSetID { get; }

        /// <summary>
        /// <see cref="KIND_FULL"/> or <see cref="KIND_PATCH"/>, sent as <c>kind</c>.
        /// </summary>
        public string Kind { get; }

        /// <summary>
        /// The content type of the assembled payload, including its multipart boundary.
        /// Sent as <c>content_type</c>.
        /// </summary>
        public string ContentType { get; }

        /// <summary>
        /// The assembled payload's length, sent as <c>total_bytes</c>.
        /// </summary>
        public int TotalBytes { get; }

        /// <summary>
        /// Lowercase hex SHA-256 of the assembled payload, which the server verifies on complete.
        /// Sent as <c>sha256</c>.
        /// </summary>
        public string Sha256 { get; }

        public CreateUploadSessionRequest(uint beatmapSetId, string kind, string contentType, int totalBytes, string sha256)
        {
            ArgumentException.ThrowIfNullOrEmpty(kind);
            ArgumentException.ThrowIfNullOrEmpty(contentType);
            ArgumentException.ThrowIfNullOrEmpty(sha256);
            ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);

            BeatmapSetID = beatmapSetId;
            Kind = kind;
            ContentType = contentType;
            TotalBytes = totalBytes;
            Sha256 = sha256;
        }

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();
            req.Method = HttpMethod.Post;
            req.ContentType = @"application/json";
            // one fresh connection per session request, so the per-connection byte ceiling this
            // protocol exists for is budgeted per request; see UploadSessionChunkRequest (backlog 201).
            req.AddHeader(@"Connection", @"close");

            // serialised from an explicit shape rather than from `this`, so the body carries exactly the
            // four contracted members and nothing the base request happens to expose publicly.
            req.AddRaw(JsonConvert.SerializeObject(new
            {
                kind = Kind,
                content_type = ContentType,
                total_bytes = TotalBytes,
                sha256 = Sha256,
            }));

            return req;
        }
    }
}
