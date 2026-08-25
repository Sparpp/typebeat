// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Net.Http;
using osu.Framework.Extensions;
using osu.Framework.IO.Network;

namespace typebeat.Game.Online.API.Requests
{
    /// <summary>
    /// Sends one chunk of a chunked package upload session.
    /// </summary>
    /// <remarks>
    /// Wire contract, fixed against the server:
    /// <c>PUT {bss}/upload-sessions/{sessionId}/chunks/{index}</c>, the raw slice as
    /// <c>application/octet-stream</c>, an <c>X-Chunk-Sha256</c> header carrying lowercase hex
    /// SHA-256 of exactly those bytes, 204 on stored. Re-sending a chunk already stored is fine.
    ///
    /// The server answers with <c>Connection: close</c> because the ceiling that forces this whole
    /// mechanism is per connection, so one connection per chunk is the point rather than a cost.
    /// The timeout is short (30s for 8KB), because a black-holed chunk should be given up on and
    /// repeated quickly rather than sat on.
    /// </remarks>
    public class UploadSessionChunkRequest : APIRequest
    {
        protected override string Uri
        {
            get
            {
                if (API!.Endpoints.BeatmapSubmissionServiceUrl == null)
                    throw new NotSupportedException("Beatmap submission not supported in this configuration!");

                return $@"{API!.Endpoints.BeatmapSubmissionServiceUrl}/upload-sessions/{SessionId}/chunks/{ChunkIndex}";
            }
        }

        protected override string Target => throw new NotSupportedException();

        public string SessionId { get; }

        public int ChunkIndex { get; }

        /// <summary>
        /// The raw slice of the assembled payload this chunk covers.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Lowercase hex SHA-256 of <see cref="Data"/>, sent as <c>X-Chunk-Sha256</c>.
        /// </summary>
        public string ChunkSha256 { get; }

        public UploadSessionChunkRequest(string sessionId, int chunkIndex, byte[] data)
        {
            ArgumentException.ThrowIfNullOrEmpty(sessionId);
            ArgumentOutOfRangeException.ThrowIfNegative(chunkIndex);
            ArgumentNullException.ThrowIfNull(data);

            if (data.Length == 0)
                throw new ArgumentException(@"Refusing to upload an empty chunk.", nameof(data));

            SessionId = sessionId;
            ChunkIndex = chunkIndex;
            Data = data;

            using (var stream = new MemoryStream(data))
                ChunkSha256 = stream.ComputeSHA2Hash();
        }

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();

            req.Method = HttpMethod.Put;
            req.ContentType = @"application/octet-stream";
            req.Timeout = 30_000;
            req.AddHeader(@"X-Chunk-Sha256", ChunkSha256);
            req.AddRaw(Data);

            return req;
        }
    }
}
