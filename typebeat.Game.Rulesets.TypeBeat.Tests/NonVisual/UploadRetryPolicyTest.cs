// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Online.API;
using typebeat.Game.Screens.Edit.Submission;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the retry decision the beatmap submission screen drives its package upload with.
    /// A valid 11.7MB package repeatedly died with an <see cref="HttpRequestException"/>
    /// ("Error while copying content to a stream"), an abort mid-body that never reached the origin,
    /// so that class of failure is repeated automatically while a server answer is not.
    /// </summary>
    [TestFixture]
    public class UploadRetryPolicyTest
    {
        [Test]
        public void TransportFailuresAreRetried()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsTransportFailure(new HttpRequestException("Error while copying content to a stream.")), Is.True);
                Assert.That(UploadRetryPolicy.IsTransportFailure(new IOException("The response ended prematurely.")), Is.True);

                // the shape .NET actually produces for the observed failure: the IO error wrapped by the send.
                Assert.That(UploadRetryPolicy.IsTransportFailure(
                    new HttpRequestException("Error while copying content to a stream.", new IOException("Unable to write data to the transport connection."))), Is.True);
            });
        }

        [Test]
        public void WrappedTransportFailuresAreRetried()
        {
            Assert.Multiple(() =>
            {
                // an `InvalidOperationException` wrapper also pins that the base type of `APIException`
                // is not itself treated as a server answer.
                Assert.That(UploadRetryPolicy.IsTransportFailure(new InvalidOperationException("wrapper", new HttpRequestException("aborted"))), Is.True);
                Assert.That(UploadRetryPolicy.IsTransportFailure(new AggregateException(new IOException("aborted"))), Is.True);

                // arbitrarily deep, since the wrapping depth is not something callers control.
                Assert.That(UploadRetryPolicy.IsTransportFailure(
                    new InvalidOperationException("outer", new AggregateException(new HttpRequestException("aborted")))), Is.True);
            });
        }

        [Test]
        public void CancellationIsNotRetried()
        {
            // `APIRequest.Cancel()` fails the request with exactly this and runs the same failure path a
            // real error does, so retrying it would resurrect a request the caller deliberately killed.
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsTransportFailure(new OperationCanceledException(@"Request cancelled")), Is.False);
                Assert.That(UploadRetryPolicy.IsTransportFailure(new TaskCanceledException()), Is.False);
                Assert.That(UploadRetryPolicy.IsTransportFailure(new OperationCanceledException("cancelled", new HttpRequestException("aborted"))), Is.False);
            });
        }

        [Test]
        public void ServerErrorsAreNotRetried()
        {
            // an `APIException` means a response body decoded into a displayable error, which proves the
            // request reached the origin. That holds even though its inner exception is a transport one.
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsTransportFailure(new APIException("beatmap is too large", null, HttpStatusCode.BadRequest)), Is.False);
                Assert.That(UploadRetryPolicy.IsTransportFailure(
                    new APIException("beatmap is too large", new HttpRequestException("Bad Request"), HttpStatusCode.BadRequest)), Is.False);
            });
        }

        [Test]
        public void TimeoutsAreNotRetried()
        {
            // `WebRequest.AllowRetryOnTimeout` is set false for every request in `APIRequest.Perform`;
            // retrying an idle timeout here would quietly undo that global decision.
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsTransportFailure(new WebException("Request timed out after 60 seconds idle", WebExceptionStatus.Timeout)), Is.False);
                Assert.That(UploadRetryPolicy.IsTransportFailure(new WebException(@"User not logged in")), Is.False);
            });
        }

        [Test]
        public void UnrelatedFailuresAreNotRetried()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsTransportFailure(null), Is.False);
                Assert.That(UploadRetryPolicy.IsTransportFailure(new NotSupportedException("Beatmap submission not supported in this configuration!")), Is.False);
                Assert.That(UploadRetryPolicy.IsTransportFailure(new InvalidOperationException("nope")), Is.False);
            });
        }

        [Test]
        public void AttemptScheduleBacksOff()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.MAX_ATTEMPTS, Is.EqualTo(3));
                Assert.That(UploadRetryPolicy.DelayBeforeAttempt(1), Is.EqualTo(0));
                Assert.That(UploadRetryPolicy.DelayBeforeAttempt(2), Is.EqualTo(2000));
                Assert.That(UploadRetryPolicy.DelayBeforeAttempt(3), Is.EqualTo(5000));

                // past the table the longest backoff is reused rather than throwing mid-submission.
                Assert.That(UploadRetryPolicy.DelayBeforeAttempt(4), Is.EqualTo(5000));
            });

            Assert.Throws<ArgumentOutOfRangeException>(() => UploadRetryPolicy.DelayBeforeAttempt(0));
        }

        [Test]
        public void RetriesAreExhaustedAfterMaxAttempts()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ShouldRetryAfter(1, new HttpRequestException("aborted")), Is.True);
                Assert.That(UploadRetryPolicy.ShouldRetryAfter(2, new HttpRequestException("aborted")), Is.True);
                Assert.That(UploadRetryPolicy.ShouldRetryAfter(3, new HttpRequestException("aborted")), Is.False);

                // a non-transport failure gives up immediately, with attempts still on the table.
                Assert.That(UploadRetryPolicy.ShouldRetryAfter(1, new APIException("beatmap is too large", null)), Is.False);
            });
        }

        /// <summary>
        /// Drives the same loop the submission screen does, to pin the whole sequence a persistent
        /// transport failure produces: three attempts total, separated by 2s then 5s.
        /// </summary>
        [Test]
        public void PersistentTransportFailureRunsThreeAttempts()
        {
            var delays = new List<double>();
            int attempts = 0;
            var failure = new HttpRequestException("Error while copying content to a stream.");

            while (true)
            {
                attempts++;

                if (!UploadRetryPolicy.ShouldRetryAfter(attempts, failure))
                    break;

                delays.Add(UploadRetryPolicy.DelayBeforeAttempt(attempts + 1));
            }

            Assert.Multiple(() =>
            {
                Assert.That(attempts, Is.EqualTo(3));
                Assert.That(delays, Is.EqualTo(new[] { 2000d, 5000d }));
            });
        }

        [Test]
        public void ChunkTransportFailuresAreRetried()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new HttpRequestException("Error while copying content to a stream.")), Is.True);
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new IOException("The response ended prematurely.")), Is.True);
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(
                    new HttpRequestException("aborted", new IOException("Unable to write data to the transport connection."))), Is.True);
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new AggregateException(new IOException("aborted"))), Is.True);
            });
        }

        [Test]
        public void ChunkTimeoutsAreRetriedUnlikeWholeUploads()
        {
            // this is the whole reason the chunk arm exists as a separate predicate: a black-holed request
            // surfaces as an idle timeout, and an 8KB idempotent chunk is worth repeating where a 600s
            // monolithic upload is not.
            var timeout = new WebException("Request timed out after 60 seconds idle", WebExceptionStatus.Timeout);

            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(timeout), Is.True);
                Assert.That(UploadRetryPolicy.IsTransportFailure(timeout), Is.False);

                // the logged-out queue failure lands here too. It just fails again instantly, which is harmless.
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new WebException(@"User not logged in")), Is.True);
                Assert.That(UploadRetryPolicy.IsTransportFailure(new WebException(@"User not logged in")), Is.False);
            });
        }

        [Test]
        public void ChunkCancellationAndServerErrorsAreNotRetried()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new OperationCanceledException(@"Request cancelled")), Is.False);
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new TaskCanceledException()), Is.False);

                // the cancel path wins over an inner transport failure, exactly as on the upload predicate.
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new OperationCanceledException("cancelled", new WebException("timed out"))), Is.False);

                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new APIException("chunk hash mismatch", null, HttpStatusCode.BadRequest)), Is.False);
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(
                    new APIException("upload session expired", new WebException("timed out"), HttpStatusCode.Gone)), Is.False);

                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(null), Is.False);
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(new InvalidOperationException("nope")), Is.False);
            });
        }

        [Test]
        public void ChunkAttemptScheduleBacksOffFaster()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.DelayBeforeChunkAttempt(1), Is.EqualTo(0));
                Assert.That(UploadRetryPolicy.DelayBeforeChunkAttempt(2), Is.EqualTo(1000));
                Assert.That(UploadRetryPolicy.DelayBeforeChunkAttempt(3), Is.EqualTo(3000));

                // clamped past the table, same as the upload ladder.
                Assert.That(UploadRetryPolicy.DelayBeforeChunkAttempt(4), Is.EqualTo(3000));

                // a chunk is 8KB, so its whole ladder has to be cheaper than the upload one.
                Assert.That(UploadRetryPolicy.DelayBeforeChunkAttempt(2), Is.LessThan(UploadRetryPolicy.DelayBeforeAttempt(2)));
                Assert.That(UploadRetryPolicy.DelayBeforeChunkAttempt(3), Is.LessThan(UploadRetryPolicy.DelayBeforeAttempt(3)));
            });

            Assert.Throws<ArgumentOutOfRangeException>(() => UploadRetryPolicy.DelayBeforeChunkAttempt(0));
        }

        [Test]
        public void ChunkRetriesAreExhaustedAfterMaxAttempts()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ShouldRetryChunkAfter(1, new WebException("timed out")), Is.True);
                Assert.That(UploadRetryPolicy.ShouldRetryChunkAfter(2, new WebException("timed out")), Is.True);
                Assert.That(UploadRetryPolicy.ShouldRetryChunkAfter(3, new WebException("timed out")), Is.False);

                Assert.That(UploadRetryPolicy.ShouldRetryChunkAfter(1, new HttpRequestException("aborted")), Is.True);
                Assert.That(UploadRetryPolicy.ShouldRetryChunkAfter(1, new APIException("chunk hash mismatch", null)), Is.False);
                Assert.That(UploadRetryPolicy.ShouldRetryChunkAfter(1, new OperationCanceledException()), Is.False);
            });
        }

        [Test]
        public void FlushedRequestsAreRetriedForChunksAndComplete()
        {
            // three consecutive network failures put the API into `Failing`, which empties the queue and
            // fails everything in it with this. The request never left the machine, so it is transport
            // class, and the completing request is the one most likely to be sitting in that queue.
            var flushed = new APIAccess.WebRequestFlushedException(APIState.Failing);

            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(flushed), Is.True);
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(flushed), Is.True);

                // wrapped just as deeply as any other transport failure is.
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(new AggregateException(flushed)), Is.True);

                // the direct single-request ladder is untouched: it retries on its own predicate, whose
                // set deliberately excludes anything the API queue itself did.
                Assert.That(UploadRetryPolicy.IsTransportFailure(flushed), Is.False);
            });
        }

        [Test]
        public void CompleteSharesTheChunkTransportSet()
        {
            // the completing request carries no body, so it cannot hit the byte ceiling, but it can be
            // black-holed on the way back and it can be flushed out of the queue. Both are worth repeating.
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(new WebException("Request timed out after 600 seconds idle", WebExceptionStatus.Timeout)), Is.True);
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(new HttpRequestException("Error while copying content to a stream.")), Is.True);
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(new IOException("The response ended prematurely.")), Is.True);

                // a server verdict on the assembled payload is final: repeating it cannot change it.
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(new APIException("beatmap is too large", null, HttpStatusCode.BadRequest)), Is.False);
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(new APIException("upload session expired", new WebException("timed out"), HttpStatusCode.Gone)), Is.False);

                // and the cancel path stays the cancel path.
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(new OperationCanceledException(@"Request cancelled")), Is.False);
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(null), Is.False);
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(new InvalidOperationException("nope")), Is.False);
            });
        }

        [Test]
        public void CompleteRetriesAreExhaustedAfterMaxAttempts()
        {
            var flushed = new APIAccess.WebRequestFlushedException(APIState.Failing);

            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ShouldRetryCompleteAfter(1, flushed), Is.True);
                Assert.That(UploadRetryPolicy.ShouldRetryCompleteAfter(2, flushed), Is.True);
                Assert.That(UploadRetryPolicy.ShouldRetryCompleteAfter(3, flushed), Is.False);

                Assert.That(UploadRetryPolicy.ShouldRetryCompleteAfter(1, new APIException("beatmap is too large", null)), Is.False);
                Assert.That(UploadRetryPolicy.ShouldRetryCompleteAfter(1, new OperationCanceledException()), Is.False);
            });
        }

        [Test]
        public void CompleteAttemptScheduleUsesTheChunkLadder()
        {
            // the body is empty, so the cost of repeating it is the server's ingest work, not a transfer.
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.DelayBeforeCompleteAttempt(1), Is.EqualTo(0));
                Assert.That(UploadRetryPolicy.DelayBeforeCompleteAttempt(2), Is.EqualTo(1000));
                Assert.That(UploadRetryPolicy.DelayBeforeCompleteAttempt(3), Is.EqualTo(3000));
                Assert.That(UploadRetryPolicy.DelayBeforeCompleteAttempt(4), Is.EqualTo(3000));
            });

            Assert.Throws<ArgumentOutOfRangeException>(() => UploadRetryPolicy.DelayBeforeCompleteAttempt(0));
        }

        /// <summary>
        /// Drives the completing request's loop the way <c>ChunkedPackageUpload</c> does, to pin what a
        /// session whose chunks all arrived actually costs before it gives up: three attempts, 1s then 3s.
        /// </summary>
        [Test]
        public void PersistentCompleteFailureRunsThreeAttempts()
        {
            var delays = new List<double>();
            int attempts = 0;
            var failure = new APIAccess.WebRequestFlushedException(APIState.Failing);

            while (true)
            {
                attempts++;

                if (!UploadRetryPolicy.ShouldRetryCompleteAfter(attempts, failure))
                    break;

                delays.Add(UploadRetryPolicy.DelayBeforeCompleteAttempt(attempts + 1));
            }

            Assert.Multiple(() =>
            {
                Assert.That(attempts, Is.EqualTo(3));
                Assert.That(delays, Is.EqualTo(new[] { 1000d, 3000d }));
            });
        }

        /// <summary>
        /// The two shapes a 502, 503 or 504 actually arrives in, built the way the real path builds them.
        /// osu.Framework's <c>WebRequest</c> raises a non-success status as
        /// <c>new WebException(response.StatusCode.ToString())</c>, with no <c>Response</c> attached, which
        /// is the same seam <c>ModelDownloader</c> reads a rate limit off. It only becomes an
        /// <see cref="APIException"/> when the body decodes, which a proxy's own error page does not do and
        /// an app emitting its own JSON 503 on the way down does.
        /// </summary>
        [Test]
        public void GatewayErrorsAreTransientInBothShapes()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new WebException(@"BadGateway")), Is.True);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new WebException(@"ServiceUnavailable")), Is.True);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new WebException(@"GatewayTimeout")), Is.True);

                // the decoded shape, which used to be terminal for a chunk and for the completing request.
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new APIException("Bad Gateway", null, HttpStatusCode.BadGateway)), Is.True);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new APIException("shutting down", new WebException(@"ServiceUnavailable"), HttpStatusCode.ServiceUnavailable)), Is.True);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new APIException("upstream timed out", null, HttpStatusCode.GatewayTimeout)), Is.True);

                // a message that is the number rather than the enum name, for anything not built by the framework.
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new WebException(@"502")), Is.True);

                // and wrapped, since the wrapping depth is not something callers control.
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new AggregateException(new WebException(@"BadGateway"))), Is.True);
            });
        }

        [Test]
        public void NonGatewayFailuresAreNotGatewayTransient()
        {
            Assert.Multiple(() =>
            {
                // a genuine verdict from the origin stays terminal, which is the whole point of separating
                // the two: 404 is the old-server create, 422 is a package the server refuses.
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new APIException("no such route", null, HttpStatusCode.NotFound)), Is.False);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new APIException("beatmap is too large", null, HttpStatusCode.UnprocessableEntity)), Is.False);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new WebException(@"NotFound")), Is.False);

                // an outer verdict wins over an inner gateway message, so a decoded 404 stays a 404.
                Assert.That(UploadRetryPolicy.IsGatewayTransient(
                    new APIException("no such route", new WebException(@"BadGateway"), HttpStatusCode.NotFound)), Is.False);

                // the failures the fast ladders exist for are not gateway failures.
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new WebException("Request timed out after 30 seconds idle", WebExceptionStatus.Timeout)), Is.False);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new HttpRequestException("Error while copying content to a stream.")), Is.False);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new IOException("The response ended prematurely.")), Is.False);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new APIAccess.WebRequestFlushedException(APIState.Failing)), Is.False);

                // and the cancel path wins over everything, as it does in every predicate here.
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new OperationCanceledException("cancelled", new WebException(@"BadGateway"))), Is.False);

                Assert.That(UploadRetryPolicy.IsGatewayTransient(null), Is.False);
                Assert.That(UploadRetryPolicy.IsGatewayTransient(new InvalidOperationException("nope")), Is.False);
            });
        }

        /// <summary>
        /// The ladder's job is to outlast a restart, so the number that matters is the span from the first
        /// 5xx to the last try: 5s + 15s + 45s + 45s = 110s. The deploy that motivated this answered 502
        /// and 504 for roughly 20 seconds.
        /// </summary>
        [Test]
        public void GatewayLadderOutlastsARestart()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.MAX_GATEWAY_ROUNDS, Is.EqualTo(5));

                Assert.That(UploadRetryPolicy.DelayBeforeGatewayRound(1), Is.EqualTo(0));
                Assert.That(UploadRetryPolicy.DelayBeforeGatewayRound(2), Is.EqualTo(5000));
                Assert.That(UploadRetryPolicy.DelayBeforeGatewayRound(3), Is.EqualTo(15000));
                Assert.That(UploadRetryPolicy.DelayBeforeGatewayRound(4), Is.EqualTo(45000));
                Assert.That(UploadRetryPolicy.DelayBeforeGatewayRound(5), Is.EqualTo(45000));

                // clamped past the table, the same way every other ladder here is.
                Assert.That(UploadRetryPolicy.DelayBeforeGatewayRound(6), Is.EqualTo(45000));

                double covered = 0;

                for (int round = 2; round <= UploadRetryPolicy.MAX_GATEWAY_ROUNDS; round++)
                    covered += UploadRetryPolicy.DelayBeforeGatewayRound(round);

                Assert.That(covered, Is.EqualTo(110000));

                // the observed restart window, with a wide margin over it.
                Assert.That(covered, Is.GreaterThan(5 * 20000));

                // and it has to be far slower than the transport ladders, which wait for a request rather
                // than for a process.
                Assert.That(UploadRetryPolicy.DelayBeforeGatewayRound(2), Is.GreaterThan(UploadRetryPolicy.DelayBeforeChunkAttempt(3)));
            });

            Assert.Throws<ArgumentOutOfRangeException>(() => UploadRetryPolicy.DelayBeforeGatewayRound(0));
        }

        /// <summary>
        /// Drives the loop <c>ChunkedPackageUpload.deferForGateway</c> runs, to pin the whole sequence a
        /// server that never comes back produces.
        /// </summary>
        [Test]
        public void PersistentGatewayOutageRunsFiveRounds()
        {
            var delays = new List<double>();
            int rounds = 0;
            var failure = new WebException(@"BadGateway");

            while (true)
            {
                rounds++;

                if (!UploadRetryPolicy.ShouldRetryGatewayAfter(rounds, failure))
                    break;

                delays.Add(UploadRetryPolicy.DelayBeforeGatewayRound(rounds + 1));
            }

            Assert.Multiple(() =>
            {
                Assert.That(rounds, Is.EqualTo(5));
                Assert.That(delays, Is.EqualTo(new[] { 5000d, 15000d, 45000d, 45000d }));

                // a non-gateway failure never enters the ladder at all.
                Assert.That(UploadRetryPolicy.ShouldRetryGatewayAfter(1, new HttpRequestException("aborted")), Is.False);
                Assert.That(UploadRetryPolicy.ShouldRetryGatewayAfter(1, new APIException("no such route", null, HttpStatusCode.NotFound)), Is.False);
            });
        }

        [Test]
        public void GatewayRoundHandsItsAttemptBack()
        {
            var gateway = new WebException(@"GatewayTimeout");
            var decodedGateway = new APIException("Bad Gateway", null, HttpStatusCode.BadGateway);
            var timeout = new WebException("Request timed out after 30 seconds idle", WebExceptionStatus.Timeout);

            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.AttemptsAfterGatewayRound(1, gateway), Is.EqualTo(0));
                Assert.That(UploadRetryPolicy.AttemptsAfterGatewayRound(3, gateway), Is.EqualTo(2));
                Assert.That(UploadRetryPolicy.AttemptsAfterGatewayRound(1, decodedGateway), Is.EqualTo(0));

                // never below zero, so a gateway answer to a request that was never counted is harmless.
                Assert.That(UploadRetryPolicy.AttemptsAfterGatewayRound(0, gateway), Is.EqualTo(0));

                // everything else keeps spending the cap exactly as it did.
                Assert.That(UploadRetryPolicy.AttemptsAfterGatewayRound(2, timeout), Is.EqualTo(2));
                Assert.That(UploadRetryPolicy.AttemptsAfterGatewayRound(2, new HttpRequestException("aborted")), Is.EqualTo(2));
                Assert.That(UploadRetryPolicy.AttemptsAfterGatewayRound(2, new APIException("chunk hash mismatch", null, HttpStatusCode.BadRequest)), Is.EqualTo(2));
                Assert.That(UploadRetryPolicy.AttemptsAfterGatewayRound(2, null), Is.EqualTo(2));
            });

            Assert.Throws<ArgumentOutOfRangeException>(() => UploadRetryPolicy.AttemptsAfterGatewayRound(-1, gateway));
        }

        /// <summary>
        /// The failure the field case actually produced: a deploy lands mid-session and every request in
        /// the window is answered 502. Drives the accounting <c>chunkFailed</c> does, to pin that no number
        /// of gateway rounds moves the per-index chunk cap, and that a real chunk failure afterwards still
        /// gets its full three attempts.
        /// </summary>
        [Test]
        public void GatewayRoundsDoNotSpendTheChunkCap()
        {
            var gateway = new WebException(@"BadGateway");
            int chunkAttempts = 0;

            for (int round = 0; round < 20; round++)
            {
                // uploadNextChunk() counts the attempt, then the failure hands it back.
                chunkAttempts++;
                chunkAttempts = UploadRetryPolicy.AttemptsAfterGatewayRound(chunkAttempts, gateway);
            }

            Assert.That(chunkAttempts, Is.EqualTo(0), "gateway rounds must not spend the per-index cap");

            var transport = new WebException("Request timed out after 30 seconds idle", WebExceptionStatus.Timeout);
            int transportAttempts = 0;

            while (true)
            {
                transportAttempts++;
                transportAttempts = UploadRetryPolicy.AttemptsAfterGatewayRound(transportAttempts, transport);

                if (!UploadRetryPolicy.ShouldRetryChunkAfter(transportAttempts, transport))
                    break;
            }

            Assert.That(transportAttempts, Is.EqualTo(UploadRetryPolicy.MAX_ATTEMPTS), "a real chunk failure still ends at the cap");
        }

        [Test]
        public void ChunkedResumeScheduleGapsTheLadders()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.MAX_CHUNKED_RESUMES, Is.EqualTo(3));

                Assert.That(UploadRetryPolicy.DelayBeforeChunkedResume(1), Is.EqualTo(5000));
                Assert.That(UploadRetryPolicy.DelayBeforeChunkedResume(2), Is.EqualTo(15000));
                Assert.That(UploadRetryPolicy.DelayBeforeChunkedResume(3), Is.EqualTo(30000));
                Assert.That(UploadRetryPolicy.DelayBeforeChunkedResume(4), Is.EqualTo(30000));

                // total outage a submission with chunks already in survives: one gateway ladder per session
                // attempt (the first plus three resumes) plus the gaps between them.
                double ladder = 0;

                for (int round = 2; round <= UploadRetryPolicy.MAX_GATEWAY_ROUNDS; round++)
                    ladder += UploadRetryPolicy.DelayBeforeGatewayRound(round);

                double gaps = 0;

                for (int resume = 1; resume <= UploadRetryPolicy.MAX_CHUNKED_RESUMES; resume++)
                    gaps += UploadRetryPolicy.DelayBeforeChunkedResume(resume);

                Assert.That(gaps, Is.EqualTo(50000));
                Assert.That((UploadRetryPolicy.MAX_CHUNKED_RESUMES + 1) * ladder + gaps, Is.EqualTo(490000));
            });

            Assert.Throws<ArgumentOutOfRangeException>(() => UploadRetryPolicy.DelayBeforeChunkedResume(0));
        }

        /// <summary>
        /// The decision the submission screen makes when a chunked session fails. The rule the field case
        /// broke on: a session holding chunks must never hand back to the direct upload, because the direct
        /// request is exactly what the chunked protocol exists to avoid sending.
        /// </summary>
        [Test]
        public void SessionHoldingChunksResumesRatherThanFallingBack()
        {
            var gateway = new WebException(@"BadGateway");

            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, true, 1, gateway), Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.ResumeChunked));
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(2, true, 1, gateway), Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.ResumeChunked));

                // bounded: after the last resume the chunked failure is what the user is shown.
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(3, true, 1, gateway), Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.GiveUp));

                // and it holds for a non-gateway failure too, because the direct request is no more
                // sendable for a black-holed connection than it is for a restarting server.
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, true, 1, new WebException("Request timed out after 30 seconds idle", WebExceptionStatus.Timeout)),
                    Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.ResumeChunked));
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, true, 1, new HttpRequestException("aborted")),
                    Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.ResumeChunked));

                // a session that got chunks in never reaches the direct ladder, whatever the attempt count.
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, true, 1, gateway), Is.Not.EqualTo(UploadRetryPolicy.ChunkedFailureAction.FallBackToDirect));
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(3, true, 1, gateway), Is.Not.EqualTo(UploadRetryPolicy.ChunkedFailureAction.FallBackToDirect));
            });
        }

        [Test]
        public void GatewayFailureResumesEvenWithNothingHeld()
        {
            // a create that 502s says nothing except that the server is not up yet, and the direct upload
            // would meet the same edge, so there is nothing to fall back to.
            var gateway = new APIException("Bad Gateway", null, HttpStatusCode.BadGateway);

            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, false, 1, gateway), Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.ResumeChunked));
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(3, false, 1, gateway), Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.GiveUp));
            });
        }

        /// <summary>
        /// The degradation backlog 195 shipped has to survive: a server predating the session routes 404s
        /// the create, nothing is held, and the submission ends up exactly where it would have without the
        /// chunked fallback existing at all.
        /// </summary>
        [Test]
        public void OldServerWithNoProgressStillFallsBackToDirect()
        {
            var createNotFound = new APIException("Not Found", new WebException(@"NotFound"), HttpStatusCode.NotFound);

            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, false, 1, createNotFound), Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.FallBackToDirect));

                // the resume count does not gate this arm: it is not a resume.
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(3, false, 1, createNotFound), Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.FallBackToDirect));

                // the direct ladder's own cap still ends it.
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, false, UploadRetryPolicy.MAX_ATTEMPTS, createNotFound), Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.GiveUp));

                // a slicing disagreement with no chunks in falls back the same way it always did.
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, false, 1, new InvalidOperationException("Upload session chunk count disagrees")),
                    Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.FallBackToDirect));
            });
        }

        [Test]
        public void ServerVerdictOnHeldChunksGivesUpImmediately()
        {
            // the origin answered about these exact bytes, and a resumed session asks the same question of
            // the same payload, so resuming would only delay the message by several minutes.
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, true, 1, new APIException("beatmap is too large", null, HttpStatusCode.UnprocessableEntity)),
                    Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.GiveUp));
                Assert.That(UploadRetryPolicy.ActionAfterChunkedFailure(0, true, 1, new APIException("chunk hash mismatch", null, HttpStatusCode.BadRequest)),
                    Is.EqualTo(UploadRetryPolicy.ChunkedFailureAction.GiveUp));
            });

            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => UploadRetryPolicy.ActionAfterChunkedFailure(-1, true, 1, null));
                Assert.Throws<ArgumentOutOfRangeException>(() => UploadRetryPolicy.ActionAfterChunkedFailure(0, true, -1, null));
            });
        }

        [Test]
        public void TransportFailureOnLaterAttemptStillStopsAtTheCap()
        {
            // a first attempt killed by a server error never reaches a retry, and a transport failure on
            // the last attempt is terminal, so the cap holds regardless of which failure lands where.
            var delays = new List<double>();
            int attempts = 0;
            var failures = new Exception[]
            {
                new HttpRequestException("aborted"),
                new IOException("The response ended prematurely."),
                new HttpRequestException("aborted"),
            };

            while (true)
            {
                var failure = failures[attempts];
                attempts++;

                if (!UploadRetryPolicy.ShouldRetryAfter(attempts, failure))
                    break;

                delays.Add(UploadRetryPolicy.DelayBeforeAttempt(attempts + 1));
            }

            Assert.Multiple(() =>
            {
                Assert.That(attempts, Is.EqualTo(3));
                Assert.That(delays, Has.Count.EqualTo(2));
            });
        }

        /// <summary>
        /// The entry-path arm of backlog 203: the FIRST direct attempt hands the submission to the
        /// chunked flow on a transport failure (the byte ceiling, the original trigger) and equally
        /// on a gateway 5xx in either shape, because a submission that BEGINS inside a deploy window
        /// must reach the machinery whose slow ladder can ride the restart out. A genuine verdict
        /// and the cancel path stay on the direct ladder's rules.
        /// </summary>
        [Test]
        public void FirstDirectFailureSwitchesToChunkedOnTransportOrGateway()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.SwitchesToChunked(new HttpRequestException("copying content to a stream")), Is.True);
                Assert.That(UploadRetryPolicy.SwitchesToChunked(new WebException(@"BadGateway")), Is.True);
                Assert.That(UploadRetryPolicy.SwitchesToChunked(new APIException("shutting down", null, HttpStatusCode.ServiceUnavailable)), Is.True);

                Assert.That(UploadRetryPolicy.SwitchesToChunked(new APIException("beatmap has no audio", null, HttpStatusCode.UnprocessableEntity)), Is.False);
                Assert.That(UploadRetryPolicy.SwitchesToChunked(new WebException(@"Request cancelled")), Is.False,
                    "an idle-timeout WebException is not retried on the 600s monolith, so it does not switch either; the ceiling surfaces as HttpRequestException there");
                Assert.That(UploadRetryPolicy.SwitchesToChunked(new OperationCanceledException()), Is.False);
                Assert.That(UploadRetryPolicy.SwitchesToChunked(null), Is.False);
            });
        }

        /// <summary>
        /// Backlog 206's window rule: a batch of chunk PUTs that failed together gets ONE answer. A
        /// gateway 5xx in the batch makes it a gateway round however many chunks it answered, because an
        /// outage answers everything in flight and spending a round each would burn the whole ladder
        /// inside a single deploy window.
        /// </summary>
        [Test]
        public void ConcurrentGatewayFailuresAreOneGatewayRound()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(failures(5, i => new ChunkUploadWindow.ChunkFailure(i, 0, new WebException(@"BadGateway")))),
                    Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.GatewayRound));

                // the shape an origin that is up enough to answer its own 503 produces.
                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(failures(5, i => new ChunkUploadWindow.ChunkFailure(i, 0, new APIException("shutting down", null, HttpStatusCode.ServiceUnavailable)))),
                    Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.GatewayRound));

                // one 502 among four ordinary transport failures is still the outage: they are the same
                // event seen five times.
                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(failures(5, i => new ChunkUploadWindow.ChunkFailure(i, 1,
                    i == 3 ? new WebException(@"GatewayTimeout") : new HttpRequestException("aborted")))), Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.GatewayRound));
            });
        }

        [Test]
        public void ADrainedWindowOfTransportFailuresRetries()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(failures(5, i => new ChunkUploadWindow.ChunkFailure(i, 1, new HttpRequestException("aborted")))),
                    Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.Retry));

                // an idle timeout is the black hole this whole protocol exists for, and a chunk is 8KB.
                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(failures(2, i => new ChunkUploadWindow.ChunkFailure(i, 2, new WebException(@"Timeout")))),
                    Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.Retry));

                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(Array.Empty<ChunkUploadWindow.ChunkFailure>()),
                    Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.Retry));
            });
        }

        [Test]
        public void OneExhaustedOrRefusedChunkEndsTheWindow()
        {
            Assert.Multiple(() =>
            {
                // a single chunk out of attempts ends the session even though its four neighbours are
                // still retryable: the per-index cap is what makes a genuinely broken chunk terminal.
                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(failures(5, i => new ChunkUploadWindow.ChunkFailure(i,
                    i == 2 ? UploadRetryPolicy.MAX_ATTEMPTS : 1, new HttpRequestException("aborted")))), Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.GiveUp));

                // and a decoded verdict on any one chunk ends it on its first attempt.
                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(failures(5, i => new ChunkUploadWindow.ChunkFailure(i, 1,
                    i == 4 ? new APIException("session not found", null, HttpStatusCode.NotFound) : new HttpRequestException("aborted")))),
                    Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.GiveUp));
            });
        }

        /// <summary>
        /// A gateway answer outranks a chunk that ran out of attempts in the same batch, matching the
        /// order the sequential pump used. The terminal chunk loses nothing: its count is preserved
        /// across the wait, so when it fails again against an origin that is actually answering, that
        /// round has no gateway in it and ends the session.
        /// </summary>
        [Test]
        public void AGatewayAnswerOutranksAnExhaustedChunk()
        {
            var mixed = new[]
            {
                new ChunkUploadWindow.ChunkFailure(0, UploadRetryPolicy.MAX_ATTEMPTS, new HttpRequestException("aborted")),
                new ChunkUploadWindow.ChunkFailure(1, 0, new WebException(@"BadGateway")),
            };

            Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(mixed), Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.GatewayRound));
        }

        private static IEnumerable<ChunkUploadWindow.ChunkFailure> failures(int count, Func<int, ChunkUploadWindow.ChunkFailure> build)
            => Enumerable.Range(0, count).Select(build).ToArray();
    }
}
