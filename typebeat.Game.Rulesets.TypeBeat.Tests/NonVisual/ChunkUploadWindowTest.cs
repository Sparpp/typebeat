// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using NUnit.Framework;
using typebeat.Game.Online.API;
using typebeat.Game.Screens.Edit.Submission;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the window the chunked upload pumps chunks through. The driver around it is API-bound, so
    /// the arithmetic that a wide pump gets wrong lives here instead: how many chunks start at once, what
    /// a failure does to the ones already flying, what an attempt count means when failures arrive out of
    /// order, and when the session is actually finished.
    /// </summary>
    [TestFixture]
    public class ChunkUploadWindowTest
    {
        private static Exception transportFailure() => new HttpRequestException("Error while copying content to a stream.");

        private static Exception gatewayFailure() => new WebException("BadGateway");

        private static Exception serverVerdict() => new APIException("beatmap is too large", null, HttpStatusCode.BadRequest);

        private static ChunkUploadWindow windowOf(int totalChunks, int maxInFlight = ChunkUploadWindow.MAX_IN_FLIGHT)
        {
            var window = new ChunkUploadWindow(maxInFlight);
            window.Reset(Enumerable.Range(0, totalChunks));
            return window;
        }

        [Test]
        public void WindowStartsSeveralChunksAtOnce()
        {
            var window = windowOf(20);

            var batch = window.TakeNextBatch();

            Assert.Multiple(() =>
            {
                // ascending, so the payload still goes up roughly front to back.
                Assert.That(batch, Is.EqualTo(new[] { 0, 1, 2, 3, 4 }));
                Assert.That(window.InFlightCount, Is.EqualTo(ChunkUploadWindow.MAX_IN_FLIGHT));
                Assert.That(window.PendingCount, Is.EqualTo(15));

                // and the window is full, so asking again starts nothing.
                Assert.That(window.TakeNextBatch(), Is.Empty);
            });
        }

        [Test]
        public void SuccessRefillsTheWindowOneForOne()
        {
            var window = windowOf(20);
            window.TakeNextBatch();

            window.MarkSucceeded(2);

            Assert.Multiple(() =>
            {
                Assert.That(window.TakeNextBatch(), Is.EqualTo(new[] { 5 }));
                Assert.That(window.InFlightCount, Is.EqualTo(ChunkUploadWindow.MAX_IN_FLIGHT));
                Assert.That(window.PendingCount, Is.EqualTo(14));
            });
        }

        [Test]
        public void CompletionWaitsForTheWindowToDrain()
        {
            var window = windowOf(3);

            var batch = window.TakeNextBatch();

            Assert.Multiple(() =>
            {
                // the whole payload is out, but the server has confirmed none of it: completing here
                // would ask it to assemble a package out of chunks it may not hold.
                Assert.That(batch, Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(window.PendingCount, Is.Zero);
                Assert.That(window.IsComplete, Is.False);
            });

            window.MarkSucceeded(0);
            window.MarkSucceeded(2);

            Assert.That(window.IsComplete, Is.False);

            window.MarkSucceeded(1);

            Assert.That(window.IsComplete, Is.True);
        }

        [Test]
        public void AFailureStopsTheWindowRefilling()
        {
            var window = windowOf(20);
            window.TakeNextBatch();

            window.MarkFailed(1, transportFailure());

            Assert.Multiple(() =>
            {
                Assert.That(window.IsDraining, Is.True);

                // nothing new starts, even though there is now room and plenty owed: the batch in flight
                // has to finish arriving before one decision can be taken for all of it.
                Assert.That(window.TakeNextBatch(), Is.Empty);
                Assert.That(window.InFlightCount, Is.EqualTo(4));
                Assert.That(window.IsDrained, Is.False);
            });

            window.MarkSucceeded(0);
            window.MarkSucceeded(2);
            window.MarkSucceeded(3);

            Assert.That(window.IsDrained, Is.False, "a window with a chunk still in flight has not drained");

            window.MarkSucceeded(4);

            Assert.That(window.IsDrained, Is.True);
        }

        [Test]
        public void ADrainedWindowTakesExactlyOneDecision()
        {
            // the collapse rule, as the driver sees it: an outage answers every request in flight, so a
            // whole batch of 502s is one event. Deciding per failed chunk would spend the entire gateway
            // ladder inside a single deploy window and give up on a server that came back seconds later.
            var window = windowOf(20);
            var batch = window.TakeNextBatch();

            int decisions = 0;
            var actions = new List<UploadRetryPolicy.DrainedWindowAction>();

            foreach (int index in batch)
            {
                window.MarkFailed(index, gatewayFailure());

                if (!window.IsDrained)
                    continue;

                decisions++;
                actions.Add(UploadRetryPolicy.ActionAfterDrainedWindow(window.Failures));
            }

            Assert.Multiple(() =>
            {
                Assert.That(window.Failures, Has.Count.EqualTo(5));
                Assert.That(decisions, Is.EqualTo(1));
                Assert.That(actions, Is.EqualTo(new[] { UploadRetryPolicy.DrainedWindowAction.GatewayRound }));
            });
        }

        [Test]
        public void FailedChunksReturnToPendingInOrder()
        {
            var window = windowOf(20);
            window.TakeNextBatch();

            window.MarkFailed(3, transportFailure());
            window.MarkFailed(1, transportFailure());
            window.MarkSucceeded(0);
            window.MarkSucceeded(2);
            window.MarkSucceeded(4);

            window.ResumeWithoutReconcile();

            Assert.Multiple(() =>
            {
                // ascending again regardless of the order they failed in, so a blind retry re-sends the
                // missing chunks before moving on into the payload.
                Assert.That(window.TakeNextBatch(), Is.EqualTo(new[] { 1, 3, 5, 6, 7 }));
                Assert.That(window.IsDraining, Is.False);
                Assert.That(window.Failures, Is.Empty);
            });
        }

        [Test]
        public void AttemptsAreCountedPerIndex()
        {
            // what the sequential pump could not do: it kept ONE counter, for the head of the queue.
            var window = windowOf(20);

            window.TakeNextBatch();
            window.MarkFailed(1, transportFailure());
            window.MarkSucceeded(0);
            window.MarkSucceeded(2);
            window.MarkSucceeded(3);
            window.MarkSucceeded(4);
            window.ResumeWithoutReconcile();

            window.TakeNextBatch();
            window.MarkFailed(1, transportFailure());

            Assert.Multiple(() =>
            {
                Assert.That(window.AttemptsFor(1), Is.EqualTo(2));

                // a neighbour that flew in the same window is on its first attempt, not its second.
                Assert.That(window.AttemptsFor(5), Is.EqualTo(1));

                // and a chunk that succeeded costs nothing at all afterwards.
                Assert.That(window.AttemptsFor(0), Is.Zero);
                Assert.That(window.WorstAttempts, Is.EqualTo(2));
            });
        }

        [Test]
        public void GatewayFailuresHandTheirAttemptBack()
        {
            // the origin never saw the request, so it says nothing about the chunk, and the per-index cap
            // exists to make a genuinely broken chunk terminal rather than to time an outage out.
            var window = windowOf(20);
            window.TakeNextBatch();

            window.MarkFailed(0, gatewayFailure());
            window.MarkFailed(1, transportFailure());

            Assert.Multiple(() =>
            {
                Assert.That(window.AttemptsFor(0), Is.Zero);
                Assert.That(window.AttemptsFor(1), Is.EqualTo(1));
                Assert.That(window.GatewayFailure, Is.Not.Null);
            });
        }

        [Test]
        public void ReconcileClearsAttemptsOnlyForChunksThatLanded()
        {
            var window = windowOf(20);
            window.TakeNextBatch();

            foreach (int index in new[] { 0, 1, 2, 3, 4 })
                window.MarkFailed(index, transportFailure());

            // the server stored 0 and answered into a black hole; 1 genuinely never arrived.
            window.Reconcile(new[] { 1, 2, 3, 4 }.Concat(Enumerable.Range(5, 15)));

            Assert.Multiple(() =>
            {
                Assert.That(window.AttemptsFor(0), Is.Zero, "a chunk the server confirms is not still on its first strike");
                Assert.That(window.AttemptsFor(1), Is.EqualTo(1));
                Assert.That(window.PendingCount, Is.EqualTo(19));
                Assert.That(window.IsDraining, Is.False);
                Assert.That(window.Failures, Is.Empty);
                Assert.That(window.ConfirmedCount(20), Is.EqualTo(1));
            });
        }

        [Test]
        public void ReconcileRefusesWhileChunksAreInFlight()
        {
            // reconciling mid-flight would rebuild the pending set from a list that cannot yet mention
            // the chunks still on the wire, so it would re-send them alongside themselves.
            var window = windowOf(20);
            window.TakeNextBatch();

            Assert.Throws<InvalidOperationException>(() => window.Reconcile(Enumerable.Range(0, 20)));
        }

        [Test]
        public void ConfirmedCountIgnoresChunksInFlight()
        {
            // a sent chunk is not a held chunk: the failure this protocol exists for is a request that
            // was neither answered nor refused, and the count decides between resuming and falling back.
            var window = windowOf(20);
            window.Reset(Enumerable.Range(5, 15));

            Assert.That(window.ConfirmedCount(20), Is.EqualTo(5), "chunks the session already held count from the start");

            window.TakeNextBatch();

            Assert.That(window.ConfirmedCount(20), Is.EqualTo(5));

            window.MarkSucceeded(6);

            Assert.That(window.ConfirmedCount(20), Is.EqualTo(6));

            // a chunk that failed goes back to owed, so the count does not move either way.
            window.MarkFailed(5, transportFailure());

            Assert.That(window.ConfirmedCount(20), Is.EqualTo(6));
        }

        [Test]
        public void ReportedFailurePrefersTheOneNoRetryCanFix()
        {
            var window = windowOf(20);
            window.TakeNextBatch();

            window.MarkFailed(0, transportFailure());
            window.MarkFailed(1, serverVerdict());
            window.MarkFailed(2, transportFailure());
            window.MarkFailed(3, transportFailure());
            window.MarkFailed(4, transportFailure());

            Assert.Multiple(() =>
            {
                // the transport failures arrived first and will be retried; the verdict is the thing the
                // user needs to read, so it is what ends the session.
                Assert.That(window.FailureToReport(), Is.TypeOf<APIException>());
                Assert.That(UploadRetryPolicy.ActionAfterDrainedWindow(window.Failures), Is.EqualTo(UploadRetryPolicy.DrainedWindowAction.GiveUp));
            });
        }

        [Test]
        public void ChunksNotInFlightCannotBeCompleted()
        {
            // the driver's identity gate is what keeps this unreachable, and a silent no-op here would
            // let a stale callback take a chunk out of the window twice.
            var window = windowOf(20);
            window.TakeNextBatch();

            Assert.Multiple(() =>
            {
                Assert.Throws<InvalidOperationException>(() => window.MarkSucceeded(9));
                Assert.Throws<InvalidOperationException>(() => window.MarkFailed(9, transportFailure()));

                window.MarkSucceeded(0);
                Assert.Throws<InvalidOperationException>(() => window.MarkSucceeded(0));
            });
        }

        [Test]
        public void ProgressNotesMoveInPercentSteps()
        {
            // 905 chunks, the upload this was built for: 1% is 9 chunks, so five in flight against a
            // 0.4s round trip writes a note roughly twice a second instead of a dozen times.
            const int total = 905;

            Assert.Multiple(() =>
            {
                Assert.That(ChunkUploadWindow.ShouldReportProgress(100, 101, total), Is.False);
                Assert.That(ChunkUploadWindow.ShouldReportProgress(100, 108, total), Is.False);
                Assert.That(ChunkUploadWindow.ShouldReportProgress(100, 109, total), Is.True);

                // never repeats a count, and never runs backwards, which a reconcile could otherwise
                // make it do after a partial batch.
                Assert.That(ChunkUploadWindow.ShouldReportProgress(100, 100, total), Is.False);
                Assert.That(ChunkUploadWindow.ShouldReportProgress(100, 40, total), Is.False);
            });
        }

        [Test]
        public void ProgressNoteLandsOnTheFirstAndTheLastCount()
        {
            const int total = 905;

            Assert.Multiple(() =>
            {
                // the first one says how big the upload is, which is the thing the bar cannot.
                Assert.That(ChunkUploadWindow.ShouldReportProgress(-1, 0, total), Is.True);
                Assert.That(ChunkUploadWindow.ShouldReportProgress(-1, 82, total), Is.True);

                // and the last one is never rounded away by the step.
                Assert.That(ChunkUploadWindow.ShouldReportProgress(900, 905, total), Is.True);
            });
        }

        [Test]
        public void ProgressNoteIsSilentForShortUploads()
        {
            // over before a count could be read, and the progress bar already carries them.
            Assert.Multiple(() =>
            {
                Assert.That(ChunkUploadWindow.ShouldReportProgress(-1, 0, 5), Is.False);
                Assert.That(ChunkUploadWindow.ShouldReportProgress(3, 5, 5), Is.False);
                Assert.That(ChunkUploadWindow.ShouldReportProgress(-1, 0, ChunkUploadWindow.MIN_CHUNKS_FOR_PROGRESS_NOTE), Is.True);
            });
        }
    }
}
