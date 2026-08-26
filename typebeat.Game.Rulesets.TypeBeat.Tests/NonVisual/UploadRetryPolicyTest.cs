// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
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
    }
}
