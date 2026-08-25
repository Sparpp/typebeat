// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using osu.Framework.Extensions;
using osu.Framework.Logging;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;

namespace typebeat.Game.Screens.Edit.Submission
{
    /// <summary>
    /// Uploads a beatmap package payload as a sequence of small chunks through a server-side upload
    /// session, as a FALLBACK for the single-request upload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists: some middleboxes black-hole a single request once its body passes roughly
    /// 20KB. The observed case died at exactly 20099 bytes of a 37714 byte PATCH, identically across
    /// six or more attempts, and never reached the origin. Nothing about the payload is at fault and
    /// repeating the same request cannot help, because the ceiling is per connection, not per
    /// session: the only way through is to keep every individual request small.
    /// </para>
    /// <para>
    /// So chunks are 8KB, comfortably under the observed ceiling with room for the request line and
    /// headers, and they go up one at a time on their own connection (the server answers each with
    /// <c>Connection: close</c>, so the churn is expected rather than accidental). A chunk that dies
    /// is repeated on the spot: 8KB and a second of backoff, against the 153s a single failed
    /// monolithic attempt costs today.
    /// </para>
    /// <para>
    /// Ordering matters as much as the mechanism. The single-request upload stays the DEFAULT and this
    /// only starts after it has failed in transport once, so users behind a network that is not doing
    /// this pay nothing for it. Session creation against a server that predates the routes 404s, which
    /// decodes into an <see cref="APIException"/> and fails this flow, and the caller then resumes the
    /// ordinary retry ladder: the fallback degrades to exactly the pre-existing behaviour.
    /// </para>
    /// <para>
    /// Resume needs no separate route because session creation is idempotent on
    /// (kind, sha256, total_bytes) and reports what it already holds, so the flow is always
    /// "create, upload the missing indices, complete".
    /// </para>
    /// </remarks>
    public class ChunkedPackageUpload
    {
        /// <summary>
        /// Splits <paramref name="totalBytes"/> into chunks of at most <paramref name="chunkBytes"/>.
        /// </summary>
        public static int TotalChunks(int totalBytes, int chunkBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);

            return (totalBytes + chunkBytes - 1) / chunkBytes;
        }

        /// <summary>
        /// The offset into the payload at which chunk <paramref name="index"/> starts.
        /// </summary>
        public static int ChunkOffset(int index, int chunkBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);

            return index * chunkBytes;
        }

        /// <summary>
        /// How many bytes chunk <paramref name="index"/> covers. Every chunk but the last is
        /// <paramref name="chunkBytes"/> long; the last one is the remainder.
        /// </summary>
        public static int ChunkLength(int index, int totalBytes, int chunkBytes)
        {
            int offset = ChunkOffset(index, chunkBytes);
            ArgumentOutOfRangeException.ThrowIfNegative(totalBytes);

            if (offset >= totalBytes)
                throw new ArgumentOutOfRangeException(nameof(index), index, @"Chunk index is past the end of the payload.");

            return Math.Min(chunkBytes, totalBytes - offset);
        }

        /// <summary>
        /// Builds the payload the full-package path would have sent as one request: a single
        /// <c>beatmapArchive</c> file part carrying the package zip.
        /// </summary>
        public static async Task<UploadPayload> BuildFullPayloadAsync(byte[] package)
        {
            ArgumentNullException.ThrowIfNull(package);

            using var content = new MultipartFormDataContent();

            content.Add(filePart(package), @"beatmapArchive", @"package.osz");

            return await payloadFrom(CreateUploadSessionRequest.KIND_FULL, content).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds the payload the patch path would have sent as one request: a <c>filesChanged</c> file
        /// part per changed file (the multipart filename being the archive path) plus a
        /// <c>filesDeleted</c> string field per removed file.
        /// </summary>
        public static async Task<UploadPayload> BuildPatchPayloadAsync(IEnumerable<KeyValuePair<string, byte[]>> filesChanged, IEnumerable<string> filesDeleted)
        {
            ArgumentNullException.ThrowIfNull(filesChanged);
            ArgumentNullException.ThrowIfNull(filesDeleted);

            using var content = new MultipartFormDataContent();

            foreach ((string filename, byte[] contents) in filesChanged)
                content.Add(filePart(contents), @"filesChanged", filename);

            foreach (string filename in filesDeleted)
                content.Add(new StringContent(filename), @"filesDeleted");

            return await payloadFrom(CreateUploadSessionRequest.KIND_PATCH, content).ConfigureAwait(false);
        }

        private static ByteArrayContent filePart(byte[] contents)
        {
            var part = new ByteArrayContent(contents);
            part.Headers.ContentType = new MediaTypeHeaderValue(@"application/octet-stream");
            return part;
        }

        private static async Task<UploadPayload> payloadFrom(string kind, MultipartFormDataContent content)
        {
            // the boundary is generated by the BCL and lives in the content type, which is why the
            // content type has to be handed to the server rather than assumed by it.
            string contentType = content.Headers.ContentType!.ToString();
            byte[] bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);

            return new UploadPayload(kind, contentType, bytes);
        }

        private readonly uint beatmapSetId;
        private readonly UploadPayload payload;
        private readonly IAPIProvider api;
        private readonly Action<Action, double> schedule;

        /// <summary>
        /// Reports (chunks uploaded, chunks in total) as the flow advances. Chunks the server already
        /// held are counted as done from the start.
        /// </summary>
        public Action<int, int>? OnProgress { get; init; }

        /// <summary>
        /// Fired once, after the completing request confirms the ingest.
        /// </summary>
        public Action? OnSucceeded { get; init; }

        /// <summary>
        /// Fired once, with the failure that ended the flow. The caller decides what to do next, since
        /// only it knows what the fallback ordering is.
        /// </summary>
        public Action<Exception>? OnFailed { get; init; }

        private APIRequest? activeRequest;
        private string sessionId = string.Empty;
        private int chunkBytes;
        private int totalChunks;

        /// <summary>
        /// The chunk indices still to send, in ascending order.
        /// </summary>
        private readonly List<int> pending = new List<int>();

        private int pendingCursor;
        private int chunkAttempts;
        private bool canceled;
        private bool finished;

        /// <param name="beatmapSetId">The set being uploaded to.</param>
        /// <param name="payload">The assembled multipart payload, built once per submission.</param>
        /// <param name="api">Used to queue every request in the flow.</param>
        /// <param name="schedule">
        /// Runs a callback after a delay in milliseconds, on the update thread. Taken as a delegate so
        /// the flow does not have to be a drawable, and so its backoff is the caller's to cancel.
        /// </param>
        public ChunkedPackageUpload(uint beatmapSetId, UploadPayload payload, IAPIProvider api, Action<Action, double> schedule)
        {
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(schedule);

            this.beatmapSetId = beatmapSetId;
            this.payload = payload;
            this.api = api;
            this.schedule = schedule;
        }

        /// <summary>
        /// Starts the flow. Exactly one of <see cref="OnSucceeded"/> or <see cref="OnFailed"/> follows,
        /// unless <see cref="Cancel"/> intervenes, after which neither fires.
        /// </summary>
        public void Start()
        {
            log($"Creating {payload.Kind} upload session ({payload.Bytes.Length} bytes, sha256:{payload.Sha256})");

            var request = new CreateUploadSessionRequest(beatmapSetId, payload.Kind, payload.ContentType, payload.Bytes.Length, payload.Sha256);
            activeRequest = request;

            request.Success += response =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                sessionCreated(response);
            };
            request.Failure += exception =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                fail(exception);
            };

            api.Queue(request);
        }

        /// <summary>
        /// Abandons the flow, cancelling whatever request is in flight. No callback fires afterwards.
        /// </summary>
        public void Cancel()
        {
            if (canceled)
                return;

            canceled = true;

            var request = activeRequest;
            activeRequest = null;
            request?.Cancel();
        }

        private void sessionCreated(UploadSessionResponse response)
        {
            if (string.IsNullOrEmpty(response.SessionId))
            {
                fail(new InvalidOperationException(@"The server opened an upload session without an id."));
                return;
            }

            if (response.ChunkBytes < 1)
            {
                fail(new InvalidOperationException($@"The server chose an unusable chunk size ({response.ChunkBytes})."));
                return;
            }

            int expectedChunks = TotalChunks(payload.Bytes.Length, response.ChunkBytes);

            if (response.TotalChunks != expectedChunks)
            {
                // the two sides are slicing the same payload differently, so anything assembled from
                // what follows would be wrong. Guessing which side is right is not worth a corrupt package.
                fail(new InvalidOperationException($@"Upload session chunk count disagrees (server:{response.TotalChunks} local:{expectedChunks})."));
                return;
            }

            sessionId = response.SessionId;
            chunkBytes = response.ChunkBytes;
            totalChunks = expectedChunks;

            var received = response.Received.ToHashSet();

            pending.Clear();
            pendingCursor = 0;
            chunkAttempts = 0;

            for (int i = 0; i < totalChunks; i++)
            {
                if (!received.Contains(i))
                    pending.Add(i);
            }

            log($"Upload session {sessionId} open: {totalChunks} chunks of {chunkBytes} bytes, {totalChunks - pending.Count} already held");

            reportProgress();
            uploadNextChunk();
        }

        private void uploadNextChunk()
        {
            if (canceled)
                return;

            if (pendingCursor >= pending.Count)
            {
                complete();
                return;
            }

            int index = pending[pendingCursor];
            chunkAttempts++;

            byte[] slice = new byte[ChunkLength(index, payload.Bytes.Length, chunkBytes)];
            Array.Copy(payload.Bytes, ChunkOffset(index, chunkBytes), slice, 0, slice.Length);

            var request = new UploadSessionChunkRequest(sessionId, index, slice);
            activeRequest = request;

            request.Success += () =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                pendingCursor++;
                chunkAttempts = 0;
                reportProgress();
                uploadNextChunk();
            };
            request.Failure += exception =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                chunkFailed(index, exception);
            };

            api.Queue(request);
        }

        private void chunkFailed(int index, Exception exception)
        {
            log($"Chunk {index} attempt {chunkAttempts}/{UploadRetryPolicy.MAX_ATTEMPTS} failed: {exception}");

            if (!UploadRetryPolicy.ShouldRetryChunkAfter(chunkAttempts, exception))
            {
                fail(exception);
                return;
            }

            schedule(() =>
            {
                if (canceled)
                    return;

                uploadNextChunk();
            }, UploadRetryPolicy.DelayBeforeChunkAttempt(chunkAttempts + 1));
        }

        private void complete()
        {
            log($"Completing upload session {sessionId}");

            var request = new CompleteUploadSessionRequest(sessionId);
            activeRequest = request;

            request.Success += () =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                succeed();
            };
            // deliberately not retried here: an `APIException` from this is the server's verdict on the
            // assembled payload, and a transport failure on an empty-bodied request is not the ceiling
            // this class exists for. Either way the caller's fallback ordering decides what happens next.
            request.Failure += exception =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                fail(exception);
            };

            api.Queue(request);
        }

        private void reportProgress()
        {
            int done = totalChunks - pending.Count + pendingCursor;
            OnProgress?.Invoke(done, totalChunks);
        }

        private bool isCurrent(APIRequest request) => !canceled && ReferenceEquals(activeRequest, request);

        private void succeed()
        {
            if (finished || canceled)
                return;

            finished = true;
            OnSucceeded?.Invoke();
        }

        private void fail(Exception exception)
        {
            if (finished || canceled)
                return;

            finished = true;
            OnFailed?.Invoke(exception);
        }

        private static void log(string message)
            => Logger.Log($@"[{nameof(ChunkedPackageUpload)}] {message}", LoggingTarget.Database);

        /// <summary>
        /// One assembled upload payload: the exact bytes the single-request path would have sent, plus
        /// the two things the server needs to accept them back in pieces.
        /// </summary>
        public class UploadPayload
        {
            /// <summary>
            /// <see cref="CreateUploadSessionRequest.KIND_FULL"/> or
            /// <see cref="CreateUploadSessionRequest.KIND_PATCH"/>.
            /// </summary>
            public string Kind { get; }

            /// <summary>
            /// The multipart content type, boundary included.
            /// </summary>
            public string ContentType { get; }

            public byte[] Bytes { get; }

            /// <summary>
            /// Lowercase hex SHA-256 of <see cref="Bytes"/>. Also the session's identity, since session
            /// creation is idempotent on it.
            /// </summary>
            public string Sha256 { get; }

            public UploadPayload(string kind, string contentType, byte[] bytes)
            {
                ArgumentException.ThrowIfNullOrEmpty(kind);
                ArgumentException.ThrowIfNullOrEmpty(contentType);
                ArgumentNullException.ThrowIfNull(bytes);

                Kind = kind;
                ContentType = contentType;
                Bytes = bytes;

                using (var stream = new MemoryStream(bytes))
                    Sha256 = stream.ComputeSHA2Hash();
            }
        }
    }
}
