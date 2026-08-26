// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace typebeat.Game.Screens.Edit.Submission
{
    /// <summary>
    /// The scheduling half of <see cref="ChunkedPackageUpload"/>: which chunk indices are owed, which are
    /// in flight, and how many attempts each one has cost. Holds no API types, so every rule it encodes
    /// can be pinned without a server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a window at all (backlog 206). Since backlog 201 every session request carries
    /// <c>Connection: close</c>, so each chunk pays a fresh connection: measured at roughly 0.4s of round
    /// trip against 0.0s of server-side processing. Sent one at a time, a 905 chunk upload is therefore
    /// about six minutes of pure waiting with an idle wire for nearly all of it. Sending several at once
    /// costs the server nothing extra and divides that wait by the window.
    /// </para>
    /// <para>
    /// Why that is SAFE, verified against the server's <c>UploadSessionStore</c> rather than assumed: a
    /// chunk PUT writes one file per chunk (a temp file moved over the target), it never writes the
    /// session manifest, and the received list is a directory enumeration. Concurrent PUTs of distinct
    /// indices touch distinct files, a same-index race resolves to one of two byte-identical writes,
    /// chunks are idempotent, and the per-user rate limiter is not on the chunk route.
    /// </para>
    /// <para>
    /// What concurrency costs on the client is bookkeeping, which is what this type is. The sequential
    /// pump could keep one attempt counter for the head of the queue, and could assume while reconciling
    /// that everything before the failed index had landed. Neither holds with five in flight: failures
    /// arrive interleaved and out of order, so attempts are counted PER INDEX, and the first failure
    /// DRAINS the window (nothing new is started) so that the batch is reconciled once and waits out at
    /// most one gateway round, rather than once per failed chunk.
    /// </para>
    /// </remarks>
    public class ChunkUploadWindow
    {
        /// <summary>
        /// How many chunk PUTs may be in flight at once.
        /// </summary>
        /// <remarks>
        /// Five. The failure this whole protocol exists for is a per-CONNECTION byte ceiling of roughly
        /// 20KB, and <c>Connection: close</c> guarantees each of these gets its own connection carrying
        /// one 8KB body, so a window multiplies connections rather than bytes per connection and cannot
        /// walk into the ceiling.
        ///
        /// The number is bounded from above by what a handshake-dominated protocol still gains: at ~0.4s
        /// of round trip and ~0s of server work, five in flight already takes the 905 chunk upload this
        /// was built for from about 362s to about 72s, and each further chunk of parallelism buys single
        /// digit seconds while multiplying the connection churn a nervous middlebox sees. It is bounded
        /// from below by wanting more than a token factor. Anything in four to six behaves the same, and
        /// five is the middle of that.
        /// </remarks>
        public const int MAX_IN_FLIGHT = 5;

        /// <summary>
        /// Below this many chunks, the flow reports no progress note at all: the upload is over before a
        /// count could be read, and the progress bar already carries it.
        /// </summary>
        public const int MIN_CHUNKS_FOR_PROGRESS_NOTE = 20;

        /// <summary>
        /// One chunk PUT that failed, as the round that drained the window saw it.
        /// </summary>
        public readonly struct ChunkFailure
        {
            /// <summary>
            /// The chunk index that failed.
            /// </summary>
            public int Index { get; }

            /// <summary>
            /// Attempts spent on <see cref="Index"/> including this one, after any gateway hand-back.
            /// </summary>
            public int Attempts { get; }

            /// <summary>
            /// What it failed with.
            /// </summary>
            public Exception Exception { get; }

            public ChunkFailure(int index, int attempts, Exception exception)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfNegative(attempts);
                ArgumentNullException.ThrowIfNull(exception);

                Index = index;
                Attempts = attempts;
                Exception = exception;
            }
        }

        private readonly int maxInFlight;

        /// <summary>
        /// Chunk indices still owed, ascending, so the payload still goes up roughly front to back.
        /// </summary>
        private readonly SortedSet<int> pending = new SortedSet<int>();

        private readonly HashSet<int> inFlight = new HashSet<int>();

        /// <summary>
        /// Attempts spent per chunk index. An index only appears once it has been sent, and drops out
        /// again the moment the server confirms it.
        /// </summary>
        private readonly Dictionary<int, int> attempts = new Dictionary<int, int>();

        private readonly List<ChunkFailure> failures = new List<ChunkFailure>();

        public ChunkUploadWindow(int maxInFlight = MAX_IN_FLIGHT)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxInFlight, 1);

            this.maxInFlight = maxInFlight;
        }

        public int PendingCount => pending.Count;

        public int InFlightCount => inFlight.Count;

        /// <summary>
        /// Whether the whole payload is accounted for: nothing owed and nothing outstanding.
        /// </summary>
        /// <remarks>
        /// Both halves matter. A pending list that has just emptied still has up to
        /// <see cref="MAX_IN_FLIGHT"/> chunks the server has not confirmed, and completing the session
        /// then asks it to assemble a package with holes in it.
        /// </remarks>
        public bool IsComplete => pending.Count == 0 && inFlight.Count == 0;

        /// <summary>
        /// Whether a failure has stopped the window refilling. Cleared by <see cref="Reconcile"/> or
        /// <see cref="ResumeWithoutReconcile"/>, which is to say by the flow deciding what to do next.
        /// </summary>
        public bool IsDraining { get; private set; }

        /// <summary>
        /// Whether a draining window has finished draining, meaning the whole batch has come home and the
        /// one decision owed to it can be taken.
        /// </summary>
        public bool IsDrained => IsDraining && inFlight.Count == 0;

        /// <summary>
        /// The failures collected since the window started draining.
        /// </summary>
        public IReadOnlyList<ChunkFailure> Failures => failures;

        /// <summary>
        /// The most attempts any chunk in the current drain round has cost, which is what the retry
        /// backoff is indexed on: a chunk failing repeatedly should back off even while its neighbours
        /// are on their first try.
        /// </summary>
        public int WorstAttempts => failures.Count == 0 ? 0 : failures.Max(f => f.Attempts);

        /// <summary>
        /// The gateway 5xx answer in the current drain round, if any. One of them stands for all of them.
        /// </summary>
        public Exception? GatewayFailure
            => failures.FirstOrDefault(f => UploadRetryPolicy.IsGatewayTransient(f.Exception)).Exception;

        public int AttemptsFor(int index) => attempts.GetValueOrDefault(index);

        /// <summary>
        /// How many of <paramref name="totalChunks"/> the server is known to hold: everything neither
        /// owed nor in flight.
        /// </summary>
        /// <remarks>
        /// In-flight chunks are deliberately not counted. They have been sent and not answered, and a
        /// request that is neither is exactly the failure this protocol exists for, so counting them
        /// would report an upload as further along than the server could assemble a package from.
        /// <see cref="ChunkedPackageUpload.HeldChunks"/> is also what the submission screen decides
        /// between resuming and falling back on, so it has to mean server-confirmed.
        /// </remarks>
        public int ConfirmedCount(int totalChunks)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(totalChunks);

            return totalChunks - pending.Count - inFlight.Count;
        }

        /// <summary>
        /// Points the window at a freshly opened session holding <paramref name="remaining"/>, discarding
        /// everything a previous session taught it.
        /// </summary>
        public void Reset(IEnumerable<int> remaining)
        {
            ArgumentNullException.ThrowIfNull(remaining);

            pending.Clear();
            inFlight.Clear();
            attempts.Clear();
            failures.Clear();
            IsDraining = false;

            foreach (int index in remaining)
                pending.Add(index);
        }

        /// <summary>
        /// Takes the chunk indices that should start now, moving them into the in-flight set and spending
        /// an attempt on each. Empty while the window is draining or full.
        /// </summary>
        public IReadOnlyList<int> TakeNextBatch()
        {
            var batch = new List<int>();

            while (!IsDraining && inFlight.Count < maxInFlight && pending.Count > 0)
            {
                int index = pending.Min;

                pending.Remove(index);
                inFlight.Add(index);
                attempts[index] = AttemptsFor(index) + 1;
                batch.Add(index);
            }

            return batch;
        }

        /// <summary>
        /// The server confirmed it holds <paramref name="index"/>.
        /// </summary>
        public void MarkSucceeded(int index)
        {
            if (!inFlight.Remove(index))
                throw new InvalidOperationException($@"Chunk {index} was confirmed while it was not in flight.");

            attempts.Remove(index);
        }

        /// <summary>
        /// The PUT of <paramref name="index"/> failed. The chunk goes back into the pending set and the
        /// window stops refilling until the flow decides what the batch does next.
        /// </summary>
        public void MarkFailed(int index, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);

            if (!inFlight.Remove(index))
                throw new InvalidOperationException($@"Chunk {index} failed while it was not in flight.");

            // a gateway 5xx hands its attempt back before anything reads the count: the per-index cap is
            // there to make a genuinely broken chunk terminal, and the origin never saw this one.
            int carried = UploadRetryPolicy.AttemptsAfterGatewayRound(AttemptsFor(index), exception);

            attempts[index] = carried;
            pending.Add(index);
            failures.Add(new ChunkFailure(index, carried, exception));
            IsDraining = true;
        }

        /// <summary>
        /// Rebuilds the pending set from what a status fetch says the server holds, and reopens the
        /// window.
        /// </summary>
        /// <remarks>
        /// The per-index cap is what makes a genuinely broken chunk terminal, so a counter only clears
        /// when the server confirms that index landed, which is exactly it dropping out of
        /// <paramref name="remaining"/>. That generalises the sequential pump's rule, which could only
        /// ask the question about the one chunk at the head of the queue.
        /// </remarks>
        public void Reconcile(IEnumerable<int> remaining)
        {
            ArgumentNullException.ThrowIfNull(remaining);

            if (inFlight.Count > 0)
                throw new InvalidOperationException(@"The upload window was reconciled while chunks were still in flight.");

            pending.Clear();

            foreach (int index in remaining)
                pending.Add(index);

            foreach (int index in attempts.Keys.ToList())
            {
                if (!pending.Contains(index))
                    attempts.Remove(index);
            }

            failures.Clear();
            IsDraining = false;
        }

        /// <summary>
        /// Reopens the window without asking the server anything, which is what a failed status fetch
        /// falls through to. Attempt counters are untouched, so a blind retry still costs what it costs.
        /// </summary>
        public void ResumeWithoutReconcile()
        {
            failures.Clear();
            IsDraining = false;
        }

        /// <summary>
        /// The failure the current drain round should be reported as: one that no retry could fix, if
        /// there is one, else the first that arrived.
        /// </summary>
        public Exception FailureToReport()
        {
            if (failures.Count == 0)
                throw new InvalidOperationException(@"The upload window has no failure to report.");

            foreach (var failure in failures)
            {
                if (!UploadRetryPolicy.IsGatewayTransient(failure.Exception) && !UploadRetryPolicy.ShouldRetryChunkAfter(failure.Attempts, failure.Exception))
                    return failure.Exception;
            }

            return failures[0].Exception;
        }

        /// <summary>
        /// Whether a progress note naming <paramref name="confirmed"/> of <paramref name="totalChunks"/>
        /// is worth writing, given <paramref name="lastReported"/> was the last count written (negative
        /// for none yet this session).
        /// </summary>
        /// <remarks>
        /// The window is what makes this a question. Five chunks in flight against a 0.4s round trip
        /// confirm a dozen a second, and the note is a text rebuild each time, so the first and the last
        /// always land and the ones between move in whole percent steps: roughly two a second on the
        /// upload this was built for, which is as fast as anything on screen can be read anyway.
        /// </remarks>
        public static bool ShouldReportProgress(int lastReported, int confirmed, int totalChunks)
        {
            if (totalChunks < MIN_CHUNKS_FOR_PROGRESS_NOTE)
                return false;

            // never repeats a count and never goes backwards, which a reconcile arriving after a partial
            // batch could otherwise make it do.
            if (confirmed <= lastReported)
                return false;

            if (lastReported < 0 || confirmed >= totalChunks)
                return true;

            return confirmed - lastReported >= Math.Max(1, totalChunks / 100);
        }
    }
}
