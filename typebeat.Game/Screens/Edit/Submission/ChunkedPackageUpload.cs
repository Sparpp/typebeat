// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
    /// headers, and each goes up on its own connection: every session request carries
    /// <c>Connection: close</c> (backlog 201), so the connection churn is expected rather than
    /// accidental. It has to be the client asking: the origin's own close on chunk responses only
    /// bounded the proxy-to-origin hop, and pooled client connections were observed accumulating
    /// create + chunks until they crossed the ceiling mid-chunk. A chunk that dies
    /// is repeated on the spot: 8KB and a second of backoff, against the 153s a single failed
    /// monolithic attempt costs today.
    /// </para>
    /// <para>
    /// Several are in flight at once (backlog 206), which is what makes a large package practical: with
    /// a connection per chunk the cost is a handshake and a round trip, ~0.4s measured against ~0.0s of
    /// server-side processing, so a sequential pump spends about six minutes of pure latency on a 905
    /// chunk upload and leaves the wire idle throughout. <see cref="ChunkUploadWindow"/> owns that
    /// window, the bookkeeping it forces, and the reasons it is safe against this server.
    /// </para>
    /// <para>
    /// Ordering matters as much as the mechanism. The single-request upload stays the DEFAULT and this
    /// only starts after it has failed in transport once, so users behind a network that is not doing
    /// this pay nothing for it. Session creation against a server that predates the routes 404s, which
    /// decodes into an <see cref="APIException"/> and fails this flow, and the caller then resumes the
    /// ordinary retry ladder: the fallback degrades to exactly the pre-existing behaviour.
    /// </para>
    /// <para>
    /// Resume across RUNS needs no separate route, because session creation is idempotent on
    /// (kind, sha256, total_bytes) and reports what it already holds, so the flow is always
    /// "create, upload the missing indices, complete". That only works because the payload bytes are
    /// reproducible: the multipart boundary is derived from the content rather than randomised, or the
    /// same archive would hash differently on every launch and never match a session.
    /// </para>
    /// <para>
    /// Resume WITHIN a run needs one, and that is what <see cref="GetUploadSessionRequest"/> is for. The
    /// same middlebox that black-holes a request can black-hole a response, so a chunk the server stored
    /// and answered 204 to can still fail on the client. Every chunk retry therefore asks what the
    /// session holds before re-sending anything, which is what stops a lost response from burning the
    /// attempt cap and, with it, the whole upload.
    /// </para>
    /// <para>
    /// Resume across a DEPLOY is a third case, and the one backlog 203 fixes. A trickle-class upload runs
    /// for hours, this server ships several times a day, and every request in the window between the old
    /// process going away and the new one being ready is answered 502 or 504 by the edge. That is not a
    /// transport failure and it is not a verdict: nothing is wrong except the timing, so it gets its own
    /// slow ladder (<see cref="UploadRetryPolicy.DelayBeforeGatewayRound"/>), it does not spend an attempt
    /// from any cap, and it applies to every request the flow makes. Before that, the fast 1s/3s chunk
    /// ladder covered four seconds of a twenty second restart and then declared the session path dead.
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

            using var content = new MultipartFormDataContent(boundaryFor(new[] { package }));

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

            // materialised because both the boundary and the body are built from them, and an enumerable
            // that yields differently on a second pass would produce a boundary the body does not use.
            var changed = filesChanged.ToList();
            var deleted = filesDeleted.ToList();

            var pieces = new List<byte[]>();

            foreach ((string filename, byte[] contents) in changed)
            {
                pieces.Add(Encoding.UTF8.GetBytes(filename));
                pieces.Add(contents);
            }

            foreach (string filename in deleted)
                pieces.Add(Encoding.UTF8.GetBytes(filename));

            using var content = new MultipartFormDataContent(boundaryFor(pieces));

            foreach ((string filename, byte[] contents) in changed)
                content.Add(filePart(contents), @"filesChanged", filename);

            foreach (string filename in deleted)
                content.Add(new StringContent(filename), @"filesDeleted");

            return await payloadFrom(CreateUploadSessionRequest.KIND_PATCH, content).ConfigureAwait(false);
        }

        /// <summary>
        /// How many hex characters of the content digest go into the multipart boundary.
        /// </summary>
        private const int boundary_hex_chars = 40;

        /// <summary>
        /// Derives the multipart boundary from the content the payload will carry, so that identical
        /// content assembles into byte-identical payload bytes on every run.
        /// </summary>
        /// <remarks>
        /// This matters because session creation is idempotent on the payload's SHA-256. The BCL's
        /// default boundary is a fresh GUID per payload, so the same archive hashed differently on every
        /// run, and a session left half-uploaded by a dead network could never be resumed by relaunching
        /// and submitting again: the second run always looked like a brand new payload.
        ///
        /// A boundary must be at most 70 characters from a restricted set (RFC 2046), which lowercase
        /// hex with an ASCII prefix satisfies. 40 hex characters is 160 bits: a collision would need two
        /// different payloads whose digests share that prefix, and the only consequence would be a
        /// multipart body whose separator appears inside a part, which is exactly the risk the BCL's own
        /// random boundary takes with fewer bits behind it.
        /// </remarks>
        private static string boundaryFor(IEnumerable<byte[]> pieces)
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            Span<byte> length = stackalloc byte[sizeof(int)];

            foreach (byte[] piece in pieces)
            {
                // length-prefixed, so that two different splits of the same concatenated bytes (a rename
                // that moves a character from one filename into the next) cannot digest identically.
                BinaryPrimitives.WriteInt32LittleEndian(length, piece.Length);
                digest.AppendData(length);
                digest.AppendData(piece);
            }

            return @"typebeat-" + Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant()[..boundary_hex_chars];
        }

        private static ByteArrayContent filePart(byte[] contents)
        {
            var part = new ByteArrayContent(contents);
            part.Headers.ContentType = new MediaTypeHeaderValue(@"application/octet-stream");
            return part;
        }

        private static async Task<UploadPayload> payloadFrom(string kind, MultipartFormDataContent content)
        {
            // the boundary lives in the content type, which is why the content type has to be handed to
            // the server rather than assumed by it.
            string contentType = content.Headers.ContentType!.ToString();
            byte[] bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);

            return new UploadPayload(kind, contentType, bytes);
        }

        /// <summary>
        /// The chunk indices of a <paramref name="totalChunks"/> chunk payload that the server does not
        /// already hold, ascending.
        /// </summary>
        /// <remarks>
        /// Indices outside <c>[0, totalChunks)</c> in <paramref name="received"/> are ignored rather than
        /// trusted: they cannot be answered by the local payload, and a server that reports one is
        /// talking about a different slicing than the one this flow verified at session creation.
        /// </remarks>
        public static List<int> RemainingChunks(int totalChunks, IEnumerable<int>? received)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(totalChunks);

            var held = received?.ToHashSet() ?? new HashSet<int>();
            var remaining = new List<int>(totalChunks);

            for (int i = 0; i < totalChunks; i++)
            {
                if (!held.Contains(i))
                    remaining.Add(i);
            }

            return remaining;
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

        /// <summary>
        /// The highest number of chunks the server has confirmed it holds, over the life of this flow.
        /// </summary>
        /// <remarks>
        /// Server-confirmed rather than optimistic: it counts the chunks session creation or a status
        /// fetch reported, plus the chunks whose own PUT was answered. The caller reads it at failure
        /// time to decide between resuming and falling back, so it must not include anything merely sent.
        /// </remarks>
        public int HeldChunks { get; private set; }

        /// <summary>
        /// Whether the server holds any of this payload. A flow that failed with this false never got a
        /// byte in, which is what an old server without the session routes looks like.
        /// </summary>
        public bool HadProgress => HeldChunks > 0;

        /// <summary>
        /// How many chunks the payload was sliced into, or 0 before the session is open.
        /// </summary>
        public int TotalChunkCount => totalChunks;

        /// <summary>
        /// The single-request step in flight (session create, status fetch or complete), if any. The
        /// chunk PUTs do not go through here, because there are several of them at once.
        /// </summary>
        private APIRequest? activeRequest;

        /// <summary>
        /// The chunk PUTs in flight, by index. The window's identity gate: a chunk callback counts only
        /// while this still names that exact request for its index.
        /// </summary>
        private readonly Dictionary<int, UploadSessionChunkRequest> inFlightRequests = new Dictionary<int, UploadSessionChunkRequest>();

        /// <summary>
        /// Which chunks are owed, which are outstanding, and what each has cost. See
        /// <see cref="ChunkUploadWindow"/> for why the pump is a window rather than a queue.
        /// </summary>
        private readonly ChunkUploadWindow window = new ChunkUploadWindow();

        private string sessionId = string.Empty;
        private int chunkBytes;
        private int totalChunks;

        private int completeAttempts;

        /// <summary>
        /// Gateway 5xx answers seen since the last time the server took a chunk. Reset by forward
        /// progress rather than by any single successful request, so a server that flaps without ever
        /// accepting bytes still runs out of rounds.
        /// </summary>
        private int gatewayRounds;

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

                // an edge answering for a server that is restarting is not an old server without the
                // session routes, and telling them apart here is what keeps a deploy from pushing the
                // whole submission back onto the direct upload.
                if (deferForGateway(exception, @"upload session create", Start))
                    return;

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

            // every chunk in flight, not just one: with a window there is no single active request, and
            // a chunk left running would keep a socket open and call back into a dead flow.
            var chunks = inFlightRequests.Values.ToArray();
            inFlightRequests.Clear();

            request?.Cancel();

            foreach (var chunk in chunks)
                chunk.Cancel();
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

            window.Reset(RemainingChunks(totalChunks, response.Received));

            log($"Upload session {sessionId} open: {totalChunks} chunks of {chunkBytes} bytes, {window.ConfirmedCount(totalChunks)} already held");

            reportProgress();
            pump();
        }

        /// <summary>
        /// Fills the window, starting chunk PUTs until <see cref="ChunkUploadWindow.MAX_IN_FLIGHT"/> are
        /// outstanding, and closes the session once nothing is owed and nothing is outstanding.
        /// </summary>
        private void pump()
        {
            if (canceled || finished)
                return;

            foreach (int index in window.TakeNextBatch())
                sendChunk(index);

            // both halves of the condition matter: a pending list that has just emptied still has up to
            // MAX_IN_FLIGHT chunks the server has not confirmed, and completing then would ask it to
            // assemble a package with holes in it.
            if (window.IsComplete)
                complete();
        }

        /// <summary>
        /// Starts one chunk PUT, off the API queue.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Off the queue deliberately (backlog 206). <see cref="APIAccess"/> drains its queue on one
        /// background thread, one request at a time (<c>run</c> to <c>processQueuedRequests</c> to
        /// <c>handleRequest</c>, which performs the request synchronously), so queued chunks would go up
        /// sequentially however wide this window is. <see cref="IAPIProvider.PerformAsync"/> performs the
        /// request on its own thread instead, which is the only path in the fork that runs API requests
        /// concurrently.
        /// </para>
        /// <para>
        /// Thread safety is unchanged by that, because the callbacks are not what moves:
        /// <see cref="APIRequest.TriggerSuccess"/> and <see cref="APIRequest.TriggerFailure"/> both hand
        /// off through <c>IAPIProvider.Schedule</c>, which is the update thread's scheduler, exactly as
        /// they do for a queued request. So every line of the bookkeeping below still runs on the update
        /// thread, one callback at a time, and the window needs no locking. What does run on the worker
        /// thread is <see cref="APIRequest.Perform"/> itself, whose two pieces of shared state are the
        /// OAuth token (retrieved under a lock) and a bindable read of the local user.
        /// </para>
        /// <para>
        /// One side effect worth naming: a chunk performed this way no longer feeds <c>APIAccess</c>'s
        /// consecutive-failure counter, so a run of black-holed chunks can no longer put the API into
        /// <see cref="APIState.Failing"/> and flush the queue out from under the completing request. The
        /// status fetch and the complete stay queued, so they keep the queue's ordering and its
        /// logged-out check.
        /// </para>
        /// </remarks>
        private void sendChunk(int index)
        {
            byte[] slice = new byte[ChunkLength(index, payload.Bytes.Length, chunkBytes)];
            Array.Copy(payload.Bytes, ChunkOffset(index, chunkBytes), slice, 0, slice.Length);

            var request = new UploadSessionChunkRequest(sessionId, index, slice);
            inFlightRequests[index] = request;

            request.Success += () =>
            {
                if (!isCurrent(request))
                    return;

                inFlightRequests.Remove(index);
                chunkSucceeded(index);
            };
            request.Failure += exception =>
            {
                if (!isCurrent(request))
                    return;

                inFlightRequests.Remove(index);
                chunkFailed(index, exception);
            };

            // not awaited: everything this flow needs arrives through the callbacks above, and
            // APIAccess.Perform turns any exception into request.Fail rather than faulting the task.
            _ = api.PerformAsync(request);
        }

        private void chunkSucceeded(int index)
        {
            window.MarkSucceeded(index);
            reportProgress();

            // a success can be what finishes draining a window that a sibling chunk already failed, in
            // which case the batch's one decision is owed now.
            if (window.IsDrained)
            {
                windowDrained();
                return;
            }

            pump();
        }

        private void chunkFailed(int index, Exception exception)
        {
            window.MarkFailed(index, exception);

            log($"Chunk {index} attempt {window.AttemptsFor(index)}/{UploadRetryPolicy.MAX_ATTEMPTS} failed: {exception}");

            // the rest of the window is still coming home, and whatever failed this one is likely to
            // have failed them too, so the decision waits until there is one batch to take it for.
            if (window.IsDrained)
                windowDrained();
        }

        /// <summary>
        /// Decides what a window that has finished draining after a failure does next, once, for the
        /// whole batch.
        /// </summary>
        /// <remarks>
        /// The decision itself is <see cref="UploadRetryPolicy.ActionAfterDrainedWindow"/>, which is pure
        /// and pinned; this carries it out. The reconcile is one status GET for the batch rather than one
        /// per failed chunk, which is the same trade the window makes everywhere else: five chunks that
        /// died together died of one thing.
        /// </remarks>
        private void windowDrained()
        {
            if (canceled || finished)
                return;

            string what = $"chunks {string.Join(", ", window.Failures.Select(f => f.Index))}";

            switch (UploadRetryPolicy.ActionAfterDrainedWindow(window.Failures))
            {
                case UploadRetryPolicy.DrainedWindowAction.GatewayRound:
                    // ONE round for the whole batch: an outage answers every request in flight, so five
                    // concurrent 502s are one event and spending a round on each would burn the entire
                    // ladder inside a single deploy window.
                    beginGatewayRound(window.GatewayFailure ?? window.FailureToReport(), what, reconcileThenPump);
                    return;

                case UploadRetryPolicy.DrainedWindowAction.GiveUp:
                    fail(window.FailureToReport());
                    return;

                default:
                {
                    // indexed on the worst chunk in the batch, so one chunk failing repeatedly backs the
                    // round off even while its neighbours are on their first try.
                    double delay = UploadRetryPolicy.DelayBeforeChunkAttempt(window.WorstAttempts + 1);

                    log($"{what} failed, reconciling and retrying in {delay / 1000:0.#}s");

                    schedule(() =>
                    {
                        if (canceled)
                            return;

                        reconcileThenPump();
                    }, delay);
                    return;
                }
            }
        }

        /// <summary>
        /// Asks the server what the session holds before re-sending the chunks that failed, once for the
        /// whole drained batch.
        /// </summary>
        /// <remarks>
        /// A chunk PUT can be STORED and still fail on the client, because the failure this whole
        /// fallback exists for can black-hole the response instead of the request: the server logs its
        /// 204 and the client sits out its idle timeout. Re-sending that chunk blindly then spends the
        /// per-index attempt cap on work already done. Reconciling first turns a lost response into a
        /// no-op.
        ///
        /// One GET per drained window rather than one per failed chunk is the same trade the window makes
        /// everywhere: the answer names every index the server holds, so a second fetch for a second
        /// failed chunk of the same batch could only repeat it.
        /// </remarks>
        private void reconcileThenPump()
        {
            if (canceled || finished)
                return;

            var request = new GetUploadSessionRequest(sessionId);
            activeRequest = request;

            request.Success += response =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                sessionReconciled(response);
            };
            request.Failure += exception =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;

                // a gateway 5xx here is the same outage that just failed the chunks, so it waits on the
                // gateway ladder instead of falling through to a blind retry that cannot land either.
                if (deferForGateway(exception, @"upload session status", reconcileThenPump))
                    return;

                // any other failure falls through to the blind retry, including a decoded 404: a server
                // that predates this route and a session that has genuinely gone away answer the same
                // way, and only the chunk PUT itself can tell them apart. The attempt accounting is
                // untouched, so this costs nothing beyond the request.
                log($"Upload session status fetch failed, retrying the missing chunks blindly: {exception}");
                window.ResumeWithoutReconcile();
                pump();
            };

            api.Queue(request);
        }

        private void sessionReconciled(UploadSessionResponse response)
        {
            if (response.TotalChunks != totalChunks)
            {
                // the session answered for a different slicing than the one creation agreed on, so its
                // received list cannot be mapped onto the local payload. Retry blindly rather than
                // rebuild the pending set from something this flow does not understand.
                log($"Upload session status reports {response.TotalChunks} chunks, expected {totalChunks}; retrying the missing chunks blindly");
                window.ResumeWithoutReconcile();
                pump();
                return;
            }

            window.Reconcile(RemainingChunks(totalChunks, response.Received));

            log($"Upload session {sessionId} holds {window.ConfirmedCount(totalChunks)}/{totalChunks} chunks");

            reportProgress();
            pump();
        }

        private void complete()
        {
            completeAttempts++;

            log($"Completing upload session {sessionId} (attempt {completeAttempts}/{UploadRetryPolicy.MAX_ATTEMPTS})");

            var request = new CompleteUploadSessionRequest(sessionId);
            activeRequest = request;

            request.Success += () =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                succeed();
            };
            request.Failure += exception =>
            {
                if (!isCurrent(request))
                    return;

                activeRequest = null;
                completeFailed(exception);
            };

            api.Queue(request);
        }

        /// <summary>
        /// Decides whether the request that closes the session is worth sending again.
        /// </summary>
        /// <remarks>
        /// An <see cref="APIException"/> is the server's verdict on the assembled payload and ends the
        /// flow, as it always did. A transport-class failure does not: every chunk is already stored, so
        /// giving up here throws away the entire upload over a request with an empty body. The one this
        /// is most often is the queue flush that follows a run of chunk failures, where the request never
        /// left the machine at all.
        ///
        /// The ambiguity accepted here: if the server DID run the ingest and only its response was lost,
        /// the retry finds the session gone (it is consumed on complete) and the flow fails with the
        /// server's message instead of this one. It cannot submit twice.
        /// </remarks>
        private void completeFailed(Exception exception)
        {
            log($"Complete attempt {completeAttempts}/{UploadRetryPolicy.MAX_ATTEMPTS} failed: {exception}");

            completeAttempts = UploadRetryPolicy.AttemptsAfterGatewayRound(completeAttempts, exception);

            if (deferForGateway(exception, @"upload session complete", complete))
                return;

            if (!UploadRetryPolicy.ShouldRetryCompleteAfter(completeAttempts, exception))
            {
                fail(exception);
                return;
            }

            schedule(() =>
            {
                if (canceled)
                    return;

                complete();
            }, UploadRetryPolicy.DelayBeforeCompleteAttempt(completeAttempts + 1));
        }

        /// <summary>
        /// Takes ownership of <paramref name="exception"/> if it is an edge answering 502, 503 or 504,
        /// by scheduling <paramref name="resume"/> on the gateway ladder. Returns whether it did.
        /// </summary>
        /// <remarks>
        /// Every step of the flow routes its failure through here first, because the thing being waited
        /// out is the same one whichever request happened to be in flight when the origin went away: a
        /// deploy, a reload, a reboot. The ladder is separate from the per-request transport retries
        /// (which stay fast, because they are waiting for one request rather than for a process) and
        /// separate from the attempt caps, which a gateway round hands its attempt back to.
        /// </remarks>
        private bool deferForGateway(Exception exception, string what, Action resume)
        {
            if (!UploadRetryPolicy.IsGatewayTransient(exception))
                return false;

            beginGatewayRound(exception, what, resume);
            return true;
        }

        /// <summary>
        /// Spends one gateway round waiting for the origin to come back, then runs
        /// <paramref name="resume"/>, or ends the flow if the ladder is out of rounds.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="deferForGateway"/> because the window has a caller that has already
        /// decided a round is owed: a drained batch of concurrent failures is ONE outage, so it spends
        /// one round between all of them rather than one each.
        /// </remarks>
        private void beginGatewayRound(Exception exception, string what, Action resume)
        {
            gatewayRounds++;

            if (!UploadRetryPolicy.ShouldRetryGatewayAfter(gatewayRounds, exception))
            {
                log($"{what}: the server is still unreachable after {gatewayRounds} gateway rounds, giving up on this session");
                fail(exception);
                return;
            }

            double delay = UploadRetryPolicy.DelayBeforeGatewayRound(gatewayRounds + 1);

            log($"{what}: gateway error, round {gatewayRounds}/{UploadRetryPolicy.MAX_GATEWAY_ROUNDS}, waiting {delay / 1000:0.#}s ({HeldChunks}/{totalChunks} chunks held)");

            schedule(() =>
            {
                if (canceled)
                    return;

                resume();
            }, delay);
        }

        private void reportProgress()
        {
            int done = window.ConfirmedCount(totalChunks);

            if (done > HeldChunks)
            {
                HeldChunks = done;

                // the origin is demonstrably back and taking bytes, so the gateway ladder starts over.
                // Tied to progress rather than to any successful request, because a status GET can
                // succeed against a server that is up but refusing writes.
                gatewayRounds = 0;
            }

            OnProgress?.Invoke(done, totalChunks);
        }

        /// <summary>
        /// Whether <paramref name="request"/>'s callbacks still speak for this flow.
        /// </summary>
        /// <remarks>
        /// <see cref="APIRequest.Cancel"/> runs the same failure path a 404 does, so identity is what
        /// separates a live request from one this flow has already walked away from. With a window there
        /// is no single active request, so a chunk is current exactly while
        /// <see cref="inFlightRequests"/> still names that instance for its own index.
        /// </remarks>
        private bool isCurrent(APIRequest request)
        {
            if (canceled)
                return false;

            if (ReferenceEquals(activeRequest, request))
                return true;

            return request is UploadSessionChunkRequest chunk
                   && inFlightRequests.TryGetValue(chunk.ChunkIndex, out var inFlight)
                   && ReferenceEquals(inFlight, request);
        }

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
