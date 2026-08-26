// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using typebeat.Game.Online.API;

namespace typebeat.Game.Screens.Edit.Submission
{
    /// <summary>
    /// Decides whether a failed beatmap package upload is worth retrying, and how long to wait first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because a valid package upload can die purely in transport: the send is aborted
    /// part way through the body (a dropped connection, an edge terminating the request), so the
    /// request never reaches the origin intact and nothing about the payload is at fault. Repeating
    /// the exact same upload is then the correct response, and the observed failure mode was five
    /// consecutive manual retries of an 11.7MB package.
    /// </para>
    /// <para>
    /// Only the two package-upload steps use this. The set create/update step is deliberately not
    /// retried: it is not a large body, and a repeat of it can create redundant sets server-side.
    /// </para>
    /// <para>
    /// This type is pure and holds no state, so the screen's retry loop is a thin driver over it.
    /// </para>
    /// </remarks>
    public static class UploadRetryPolicy
    {
        /// <summary>
        /// Total number of attempts an upload gets, the first one included.
        /// </summary>
        public const int MAX_ATTEMPTS = 3;

        /// <summary>
        /// Delay before each attempt after the first, in milliseconds.
        /// Indexed by attempt number minus two (so the first entry precedes attempt 2).
        /// </summary>
        private static readonly double[] delays_before_attempt = { 2000, 5000 };

        /// <summary>
        /// Delay before each chunk attempt after the first, in milliseconds.
        /// Indexed the same way as <see cref="delays_before_attempt"/>, but shorter: a chunk is 8KB, so
        /// waiting seconds to repeat one costs more than the repeat itself.
        /// </summary>
        private static readonly double[] delays_before_chunk_attempt = { 1000, 3000 };

        /// <summary>
        /// Total number of tries a run of gateway 5xx answers gets before the flow gives up on the
        /// server coming back, the first one included.
        /// </summary>
        /// <remarks>
        /// See <see cref="DelayBeforeGatewayRound"/> for the arithmetic behind the number.
        /// </remarks>
        public const int MAX_GATEWAY_ROUNDS = 5;

        /// <summary>
        /// Delay before each gateway round after the first, in milliseconds.
        /// Indexed by round number minus two (so the first entry precedes round 2).
        /// </summary>
        private static readonly double[] delays_before_gateway_round = { 5000, 15000, 45000, 45000 };

        /// <summary>
        /// Number of times a chunked upload session that made progress is started again from scratch
        /// before the flow gives up. The session reattaches on the payload's content-derived SHA-256, so
        /// a fresh start keeps every chunk the server already holds and re-sends only the missing ones.
        /// </summary>
        public const int MAX_CHUNKED_RESUMES = 3;

        /// <summary>
        /// Delay before each chunked resume, in milliseconds. Indexed by resume number minus one.
        /// </summary>
        private static readonly double[] delays_before_chunked_resume = { 5000, 15000, 30000 };

        /// <summary>
        /// The status codes an edge answers with when it cannot reach the origin, or reached it and gave
        /// up waiting. None of them is a verdict on the request: the origin never answered.
        /// </summary>
        private static bool isGatewayStatus(HttpStatusCode statusCode)
            => statusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

        /// <summary>
        /// How long to wait before starting <paramref name="attemptNumber"/> (1-based).
        /// The first attempt starts immediately; later attempts back off.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="attemptNumber"/> is below 1.</exception>
        public static double DelayBeforeAttempt(int attemptNumber)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);

            if (attemptNumber == 1)
                return 0;

            // clamped rather than thrown on, so a caller that grows MAX_ATTEMPTS without extending
            // the table degrades to the longest backoff instead of crashing mid-submission.
            int index = Math.Min(attemptNumber - 2, delays_before_attempt.Length - 1);
            return delays_before_attempt[index];
        }

        /// <summary>
        /// How long to wait before starting <paramref name="attemptNumber"/> (1-based) of a single
        /// upload-session chunk. Same shape as <see cref="DelayBeforeAttempt"/> on a shorter table.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="attemptNumber"/> is below 1.</exception>
        public static double DelayBeforeChunkAttempt(int attemptNumber)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);

            if (attemptNumber == 1)
                return 0;

            int index = Math.Min(attemptNumber - 2, delays_before_chunk_attempt.Length - 1);
            return delays_before_chunk_attempt[index];
        }

        /// <summary>
        /// How long to wait before starting <paramref name="attemptNumber"/> (1-based) of the request
        /// that closes an upload session.
        /// </summary>
        /// <remarks>
        /// The chunk ladder rather than the upload one: the body is empty, so the cost of repeating it is
        /// the server's ingest work and not a transfer, and the user is already several minutes into a
        /// submission by the time it runs.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="attemptNumber"/> is below 1.</exception>
        public static double DelayBeforeCompleteAttempt(int attemptNumber) => DelayBeforeChunkAttempt(attemptNumber);

        /// <summary>
        /// How long to wait before <paramref name="roundNumber"/> (1-based) of a run of gateway 5xx
        /// answers. The first round is the request that discovered the outage, so it waits nothing.
        /// </summary>
        /// <remarks>
        /// This ladder is deliberately an order of magnitude slower than
        /// <see cref="DelayBeforeChunkAttempt"/>, because it is waiting for a DIFFERENT thing. A chunk
        /// retry is waiting for one request to go through; a gateway retry is waiting for a process to
        /// come back up, and no number of requests sent meanwhile changes when that happens.
        ///
        /// The arithmetic, with <see cref="MAX_GATEWAY_ROUNDS"/> at 5: rounds 2 to 5 wait
        /// 5s + 15s + 45s + 45s, so the last try lands 110 seconds after the first 5xx. The deploy that
        /// motivated this (2026-08-26 21:29Z, a set-57 session holding 82 of 905 chunks) answered 502 and
        /// 504 for roughly 20 seconds, which 110s covers more than five times over, with room for a slow
        /// migration or a container that has to be pulled first. The old fast ladder covered 4 seconds of
        /// it and then declared the whole session path dead.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="roundNumber"/> is below 1.</exception>
        public static double DelayBeforeGatewayRound(int roundNumber)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(roundNumber, 1);

            if (roundNumber == 1)
                return 0;

            int index = Math.Min(roundNumber - 2, delays_before_gateway_round.Length - 1);
            return delays_before_gateway_round[index];
        }

        /// <summary>
        /// How long to wait before resume <paramref name="resumeNumber"/> (1-based) of a chunked upload
        /// session that failed with chunks already held server-side.
        /// </summary>
        /// <remarks>
        /// A resume only happens after a gateway ladder has already run its 110 seconds, so this is a
        /// gap between ladders rather than a backoff of its own. Across
        /// <see cref="MAX_CHUNKED_RESUMES"/> resumes the flow spends 4 x 110s of ladder plus
        /// 5s + 15s + 30s of gap, so roughly 8 minutes of unreachable server before a submission that got
        /// chunks in is finally given up on.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="resumeNumber"/> is below 1.</exception>
        public static double DelayBeforeChunkedResume(int resumeNumber)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(resumeNumber, 1);

            int index = Math.Min(resumeNumber - 1, delays_before_chunked_resume.Length - 1);
            return delays_before_chunked_resume[index];
        }

        /// <summary>
        /// Whether an upload that has already made <paramref name="attemptsMade"/> attempts and failed
        /// with <paramref name="exception"/> should be attempted again.
        /// </summary>
        public static bool ShouldRetryAfter(int attemptsMade, Exception? exception)
            => attemptsMade < MAX_ATTEMPTS && IsTransportFailure(exception);

        /// <summary>
        /// Whether a single upload-session chunk that has already made <paramref name="attemptsMade"/>
        /// attempts and failed with <paramref name="exception"/> should be sent again.
        /// </summary>
        public static bool ShouldRetryChunkAfter(int attemptsMade, Exception? exception)
            => attemptsMade < MAX_ATTEMPTS && IsChunkTransportFailure(exception);

        /// <summary>
        /// Whether the request that closes an upload session, having already made
        /// <paramref name="attemptsMade"/> attempts and failed with <paramref name="exception"/>,
        /// should be sent again.
        /// </summary>
        public static bool ShouldRetryCompleteAfter(int attemptsMade, Exception? exception)
            => attemptsMade < MAX_ATTEMPTS && IsCompleteTransportFailure(exception);

        /// <summary>
        /// Whether a run of gateway 5xx answers that has already spent <paramref name="roundsMade"/>
        /// rounds, the last of which failed with <paramref name="exception"/>, should wait and try again.
        /// </summary>
        public static bool ShouldRetryGatewayAfter(int roundsMade, Exception? exception)
            => roundsMade < MAX_GATEWAY_ROUNDS && IsGatewayTransient(exception);

        /// <summary>
        /// Whether <paramref name="exception"/> is an edge answering for an origin that did not, meaning
        /// 502, 503 or 504.
        /// </summary>
        /// <remarks>
        /// <para>
        /// How this actually surfaces, because there are two shapes and only one of them was handled.
        /// osu.Framework's <c>WebRequest</c> raises a non-success status as
        /// <c>new WebException(response.StatusCode.ToString())</c>: no <see cref="WebException.Response"/>
        /// is attached and <see cref="WebException.Status"/> stays
        /// <see cref="WebExceptionStatus.UnknownError"/>, so the enum NAME in the message is the only
        /// carrier of the code. <c>ModelDownloader</c> reads a rate limit off exactly that seam
        /// (<c>webException.Message == "TooManyRequests"</c>). <see cref="APIRequest.Fail"/> then wraps
        /// that in an <see cref="APIException"/> carrying <c>WebRequest.ResponseStatusCode</c>, but ONLY
        /// if the response body contains <c>"error"</c>, which a proxy's own 502 page does not: caddy
        /// serves an empty body or an HTML page, neither of which decodes, so a gateway failure normally
        /// arrives as the bare <see cref="WebException"/>. It arrives as an
        /// <see cref="APIException"/> when the origin is up enough to emit its own JSON error with a 503,
        /// which is what an app shutting down behind a healthy proxy does.
        /// </para>
        /// <para>
        /// Both shapes are recognised here, because the two are the same event. That matters most for the
        /// <see cref="APIException"/> one: <see cref="IsChunkTransportFailure"/> refuses every
        /// <see cref="APIException"/>, so a 502 in that shape used to be TERMINAL for a chunk and for the
        /// completing request, while the same 502 in the <see cref="WebException"/> shape burned the fast
        /// 1s/3s ladder and then became terminal four seconds later. Neither outcome is right for a
        /// server that is merely restarting.
        /// </para>
        /// <para>
        /// The walk terminates at the first exception that carries a status either way, so a decoded 404
        /// wrapping anything at all stays a genuine server verdict. The cancel path wins over everything,
        /// as it does in every predicate here.
        /// </para>
        /// </remarks>
        public static bool IsGatewayTransient(Exception? exception)
        {
            for (var candidate = exception; candidate != null; candidate = candidate.InnerException)
            {
                switch (candidate)
                {
                    case OperationCanceledException:
                        return false;

                    // a decoded verdict carries the ORIGIN's status code, so it is the authority when it
                    // has one. A null status means the request was aborted before the response landed,
                    // in which case the inner exception is still worth looking at.
                    case APIException { StatusCode: not null } api:
                        return isGatewayStatus(api.StatusCode.Value);

                    case WebException web:
                        return isGatewayWebException(web);
                }
            }

            return false;
        }

        /// <summary>
        /// The per-index or per-request attempt count to carry forward after an attempt failed with
        /// <paramref name="exception"/>, given <paramref name="attemptsMade"/> attempts had been made.
        /// </summary>
        /// <remarks>
        /// A gateway round hands its attempt back. The attempt caps exist to make a genuinely broken
        /// chunk terminal, and a 502 says nothing about the chunk: the origin never saw it. Without this
        /// a restart spends the whole per-index cap on requests that could not have landed, and the
        /// upload dies for a reason that has already stopped being true.
        /// </remarks>
        public static int AttemptsAfterGatewayRound(int attemptsMade, Exception? exception)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(attemptsMade);

            return IsGatewayTransient(exception) ? Math.Max(0, attemptsMade - 1) : attemptsMade;
        }

        private static bool isGatewayWebException(WebException exception)
        {
            // checked first for the shapes that do carry a response, even though the framework's own
            // status failure does not.
            if ((exception.Response as HttpWebResponse)?.StatusCode is HttpStatusCode fromResponse)
                return isGatewayStatus(fromResponse);

            // otherwise the message is the status enum's name, or its number if some other caller built it.
            return Enum.TryParse(exception.Message, out HttpStatusCode fromMessage) && isGatewayStatus(fromMessage);
        }

        /// <summary>
        /// Whether <paramref name="exception"/> represents a transport failure worth repeating the upload for,
        /// meaning the request did not arrive intact rather than the server rejecting what it received.
        /// </summary>
        /// <remarks>
        /// The chain is walked outermost first (a wrapping exception exposes its cause through
        /// <see cref="Exception.InnerException"/>), so the outermost decision wins. That ordering is what
        /// makes an <see cref="APIException"/> wrapping a transport exception count as a server answer.
        ///
        /// Retried:
        /// <list type="bullet">
        /// <item><see cref="HttpRequestException"/>: the send itself failed, which is the observed
        /// "Error while copying content to a stream" abort mid-body.</item>
        /// <item><see cref="IOException"/>: the same class of failure surfacing from the stream copy,
        /// with or without an <see cref="HttpRequestException"/> wrapped around it.</item>
        /// </list>
        ///
        /// Not retried:
        /// <list type="bullet">
        /// <item><see cref="OperationCanceledException"/>: this is the cancel path.
        /// <see cref="APIRequest.Cancel"/> fails the request with exactly this, running the same
        /// failure path a real error does, so retrying it would resurrect a request the caller killed.
        /// <see cref="System.Threading.Tasks.TaskCanceledException"/> derives from it and is covered too.</item>
        /// <item><see cref="APIException"/>: the server answered, and the answer decoded into a
        /// displayable error message. Its inner exception may well be a transport exception, but the
        /// response body proves the request reached the origin, so the payload is what is at fault.</item>
        /// <item><see cref="WebException"/>: the framework raises this for an idle timeout, and
        /// <c>WebRequest.AllowRetryOnTimeout</c> is deliberately false globally
        /// (set for every request in <see cref="APIRequest.Perform"/>). Retrying a timeout here would
        /// quietly reintroduce what that global switches off. It is also what a request queued while
        /// logged out fails with, which no amount of retrying fixes.</item>
        /// </list>
        /// </remarks>
        public static bool IsTransportFailure(Exception? exception)
        {
            for (var candidate = exception; candidate != null; candidate = candidate.InnerException)
            {
                switch (candidate)
                {
                    case OperationCanceledException:
                    case APIException:
                    case WebException:
                        return false;

                    case HttpRequestException:
                    case IOException:
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="exception"/> is worth repeating a single upload-session chunk for.
        /// </summary>
        /// <remarks>
        /// Identical to <see cref="IsTransportFailure"/> except that <see cref="WebException"/> is
        /// RETRIED here. The failure this whole fallback exists for is a middlebox black-holing a
        /// request once its body passes roughly 20KB: the send neither completes nor errors, so it
        /// surfaces client-side as an idle timeout, which is a <see cref="WebException"/>. A chunk is
        /// 8KB and the PUT that carries it is idempotent, so repeating one is cheap and correct.
        /// That is not true of the monolithic upload, whose 600s timeout retry
        /// <c>WebRequest.AllowRetryOnTimeout = false</c> deliberately disables, which is why
        /// <see cref="IsTransportFailure"/> keeps refusing it.
        ///
        /// The offline-queue "User not logged in" failure is also a <see cref="WebException"/> and so
        /// lands in the retried set here. That is harmless: it fails again immediately, without a
        /// request leaving the machine, and the chunk attempt cap ends it after three tries.
        ///
        /// <see cref="APIAccess.WebRequestFlushedException"/> is retried for the same reason: it means
        /// the API queue was emptied out from under a request that had not been sent yet (three
        /// consecutive network failures put the API into <see cref="APIState.Failing"/>, which flushes),
        /// so nothing was rejected and nothing left the machine. Since the black-hole this fallback
        /// exists for produces exactly those consecutive failures, a flush is a NORMAL event here rather
        /// than an exotic one, and refusing to retry it strands the flow.
        ///
        /// <see cref="OperationCanceledException"/> (the cancel path) and <see cref="APIException"/>
        /// (a decoded server verdict) stay refused for exactly the reasons they are refused above.
        /// </remarks>
        public static bool IsChunkTransportFailure(Exception? exception)
        {
            for (var candidate = exception; candidate != null; candidate = candidate.InnerException)
            {
                switch (candidate)
                {
                    case OperationCanceledException:
                    case APIException:
                        return false;

                    case WebException:
                    case HttpRequestException:
                    case IOException:
                    case APIAccess.WebRequestFlushedException:
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="exception"/> is worth repeating the request that closes an upload
        /// session for.
        /// </summary>
        /// <remarks>
        /// The same set as <see cref="IsChunkTransportFailure"/>, named separately because the decision
        /// it encodes is a different one: the completing request carries no body, so it cannot hit the
        /// byte ceiling, but it is queued behind the chunks and is therefore the request most likely to
        /// be sitting in the queue when a run of chunk failures flushes it. That flush, and a response
        /// black-holed on the way back, are what strand an upload whose chunks all arrived.
        ///
        /// The ambiguity this accepts, deliberately: if the server DID process a complete and only its
        /// response was lost, the retry finds the session gone (it is deleted on consumption) and the
        /// flow fails with the server's own message. That is a worse error text than it could be, and it
        /// is strictly better than the alternative, which is a submission that silently never lands.
        /// It cannot double-submit, because the session is consumed exactly once.
        ///
        /// An <see cref="APIException"/> stays refused, as everywhere else here: the server answered
        /// with a verdict on the assembled payload, and repeating the request cannot change it.
        /// </remarks>
        public static bool IsCompleteTransportFailure(Exception? exception) => IsChunkTransportFailure(exception);

        /// <summary>
        /// What the submission screen does when a chunked upload session ends in failure.
        /// </summary>
        public enum ChunkedFailureAction
        {
            /// <summary>
            /// Show the chunked failure and stop.
            /// </summary>
            GiveUp,

            /// <summary>
            /// Start a fresh chunked session after <see cref="DelayBeforeChunkedResume"/>. Session
            /// creation reattaches on the payload's SHA-256, so held chunks survive and only the missing
            /// ones are re-sent.
            /// </summary>
            ResumeChunked,

            /// <summary>
            /// Hand back to the single-request upload ladder, the pre-chunking behaviour.
            /// </summary>
            FallBackToDirect,
        }

        /// <summary>
        /// Decides what follows a failed chunked upload session.
        /// </summary>
        /// <param name="resumesMade">Chunked sessions already restarted for this submission.</param>
        /// <param name="hadProgress">Whether the server confirmed it holds at least one chunk.</param>
        /// <param name="directAttemptsMade">Single-request upload attempts already spent.</param>
        /// <param name="exception">The failure that ended the session.</param>
        /// <remarks>
        /// <para>
        /// The rule that matters: a session holding chunks NEVER hands back to the direct upload. The
        /// direct request is the thing the whole chunked protocol exists to avoid, so for the user class
        /// this fallback was built for it cannot succeed, and re-sending it throws away everything the
        /// session already got in. It resumes instead, which is nearly free because the server keeps what
        /// it holds.
        /// </para>
        /// <para>
        /// A gateway 5xx resumes even with NO progress, because a create that 502s says nothing about the
        /// server other than that it is not up yet, and the direct upload would meet the same edge.
        /// </para>
        /// <para>
        /// Everything else with no progress falls back exactly as it did before this existed, which is
        /// what keeps the old-server degradation intact: a server predating the session routes 404s the
        /// create, decodes into an <see cref="APIException"/>, and lands here with nothing held.
        /// </para>
        /// <para>
        /// The one case where progress does not buy a resume is a decoded non-gateway
        /// <see cref="APIException"/>: the origin rendered a verdict on these exact bytes, and a resumed
        /// session asks the same question of the same payload, so it would only delay the message the
        /// user needs to see by several minutes.
        /// </para>
        /// </remarks>
        public static ChunkedFailureAction ActionAfterChunkedFailure(int resumesMade, bool hadProgress, int directAttemptsMade, Exception? exception)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(resumesMade);
            ArgumentOutOfRangeException.ThrowIfNegative(directAttemptsMade);

            bool gateway = IsGatewayTransient(exception);

            // a decoded error that is not a gateway page is the origin's answer about these exact bytes.
            bool verdict = !gateway && isServerVerdict(exception);

            if (!verdict && (hadProgress || gateway))
                return resumesMade < MAX_CHUNKED_RESUMES ? ChunkedFailureAction.ResumeChunked : ChunkedFailureAction.GiveUp;

            // a verdict on a payload the server already holds part of: the direct upload sends the very
            // same bytes, so there is nothing left to try.
            if (hadProgress)
                return ChunkedFailureAction.GiveUp;

            return directAttemptsMade < MAX_ATTEMPTS ? ChunkedFailureAction.FallBackToDirect : ChunkedFailureAction.GiveUp;
        }

        /// <summary>
        /// Whether the origin answered with a body that decoded into a displayable error, anywhere in
        /// <paramref name="exception"/>'s chain.
        /// </summary>
        private static bool isServerVerdict(Exception? exception)
        {
            for (var candidate = exception; candidate != null; candidate = candidate.InnerException)
            {
                if (candidate is APIException)
                    return true;
            }

            return false;
        }
    }
}
