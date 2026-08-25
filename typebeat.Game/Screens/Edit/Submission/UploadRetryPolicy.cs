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
                        return true;
                }
            }

            return false;
        }
    }
}
