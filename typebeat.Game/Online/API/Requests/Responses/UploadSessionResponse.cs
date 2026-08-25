// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Newtonsoft.Json;

namespace typebeat.Game.Online.API.Requests.Responses
{
    /// <summary>
    /// The server's answer to creating (or re-creating) a package upload session.
    /// </summary>
    /// <remarks>
    /// Creation is idempotent on (kind, sha256, total_bytes), so re-creating an existing session hands
    /// back that same session with its current <see cref="Received"/> list. Resuming is therefore just
    /// "create, then send whatever is not in <see cref="Received"/>".
    /// </remarks>
    public class UploadSessionResponse
    {
        [JsonProperty("session_id")]
        public string SessionId { get; set; } = string.Empty;

        /// <summary>
        /// The size of every chunk but the last, chosen by the server.
        /// </summary>
        [JsonProperty("chunk_bytes")]
        public int ChunkBytes { get; set; }

        /// <summary>
        /// How many chunks the payload splits into. Cross-checked client-side against the same
        /// computation over the local payload, because a disagreement means one side is slicing
        /// differently and the assembled bytes would be wrong.
        /// </summary>
        [JsonProperty("total_chunks")]
        public int TotalChunks { get; set; }

        /// <summary>
        /// The chunk indices already stored, ascending.
        /// </summary>
        [JsonProperty("received")]
        public int[] Received { get; set; } = Array.Empty<int>();

        [JsonProperty("expires_at")]
        public string ExpiresAt { get; set; } = string.Empty;
    }
}
