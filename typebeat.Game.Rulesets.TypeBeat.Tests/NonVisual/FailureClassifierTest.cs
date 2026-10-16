// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using NUnit.Framework;
using typebeat.Game.Online;
using typebeat.Game.Online.API;
using typebeat.Game.Screens.Edit.Submission;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins every retry and failover decision over one table of exception shapes. The six decisions used to be six
    /// hand-written walks over the exception chain; they are now set membership over <see cref="FailureClassifier"/>,
    /// and this table holds the answers each walk gave before that move, so a new exception type, or a change to one
    /// predicate, shows up here for all of them at once.
    /// </summary>
    /// <remarks>
    /// The columns: <c>transport</c> is <see cref="UploadRetryPolicy.IsTransportFailure"/> (a whole package upload),
    /// <c>chunk</c> is <see cref="UploadRetryPolicy.IsChunkTransportFailure"/> (and the session complete, which shares
    /// it), <c>gateway</c> is <see cref="UploadRetryPolicy.IsGatewayTransient"/>, <c>verdict</c> is the chunked failure
    /// policy's server-verdict test, <c>failover</c> is <see cref="ApiHostSelector.IsFailoverRetryable"/> and
    /// <c>token</c> is score submission's token retry on a new host.
    /// </remarks>
    [TestFixture]
    public class FailureClassifierTest
    {
        public static IEnumerable<TestCaseData> Table()
        {
            var rows = new[]
            {
                row("null", () => null,
                    transport: false, chunk: false, gateway: false, verdict: false, failover: false, token: false),
                row("unrelated FormatException", () => new FormatException("unrelated"),
                    transport: false, chunk: false, gateway: false, verdict: false, failover: false, token: false),
                row("InvalidOperationException", () => new InvalidOperationException("unrelated"),
                    transport: false, chunk: false, gateway: false, verdict: false, failover: false, token: false),
                row("InvalidOperationException > HttpRequestException", () => new InvalidOperationException("wrapper", new HttpRequestException("aborted")),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("OperationCanceledException", () => new OperationCanceledException("Request cancelled"),
                    transport: false, chunk: false, gateway: false, verdict: false, failover: false, token: false),
                row("TaskCanceledException", () => new TaskCanceledException(),
                    transport: false, chunk: false, gateway: false, verdict: false, failover: false, token: false),
                row("OperationCanceledException > HttpRequestException", () => new OperationCanceledException("cancelled", new HttpRequestException("aborted")),
                    transport: false, chunk: false, gateway: false, verdict: false, failover: false, token: false),
                row("OperationCanceledException > APIException 400", () => new OperationCanceledException("cancelled", new APIException("bad", null, HttpStatusCode.BadRequest)),
                    transport: false, chunk: false, gateway: false, verdict: true, failover: false, token: false),
                row("APIException 400", () => new APIException("beatmap is too large", null, HttpStatusCode.BadRequest),
                    transport: false, chunk: false, gateway: false, verdict: true, failover: false, token: false),
                row("APIException 404 > HttpRequestException", () => new APIException("not found", new HttpRequestException("Not Found"), HttpStatusCode.NotFound),
                    transport: false, chunk: false, gateway: false, verdict: true, failover: false, token: false),
                row("APIException 502", () => new APIException("bad gateway", null, HttpStatusCode.BadGateway),
                    transport: false, chunk: false, gateway: true, verdict: true, failover: false, token: false),
                row("APIException 503", () => new APIException("shutting down", null, HttpStatusCode.ServiceUnavailable),
                    transport: false, chunk: false, gateway: true, verdict: true, failover: false, token: false),
                row("APIException 504", () => new APIException("gateway timeout", null, HttpStatusCode.GatewayTimeout),
                    transport: false, chunk: false, gateway: true, verdict: true, failover: false, token: false),
                row("APIException no status", () => new APIException("aborted", null),
                    transport: false, chunk: false, gateway: false, verdict: true, failover: false, token: false),
                row("APIException no status > WebException BadGateway", () => new APIException("aborted", new WebException("BadGateway")),
                    transport: false, chunk: false, gateway: true, verdict: true, failover: false, token: false),
                row("APIException no status > HttpRequestException", () => new APIException("aborted", new HttpRequestException("aborted")),
                    transport: false, chunk: false, gateway: false, verdict: true, failover: false, token: false),
                row("WebException BadGateway", () => new WebException("BadGateway"),
                    transport: false, chunk: true, gateway: true, verdict: false, failover: true, token: true),
                row("WebException ServiceUnavailable", () => new WebException("ServiceUnavailable"),
                    transport: false, chunk: true, gateway: true, verdict: false, failover: true, token: true),
                row("WebException 504 (numeric)", () => new WebException("504"),
                    transport: false, chunk: true, gateway: true, verdict: false, failover: true, token: true),
                row("WebException NotFound", () => new WebException("NotFound"),
                    transport: false, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("WebException idle timeout", () => new WebException("Request timed out after 60 seconds idle", WebExceptionStatus.Timeout),
                    transport: false, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("WebException not logged in", () => new WebException(APIAccess.NOT_LOGGED_IN_MESSAGE),
                    transport: false, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("WebException > HttpRequestException", () => new WebException("timed out", new HttpRequestException("aborted")),
                    transport: false, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("HttpRequestException", () => new HttpRequestException("Error while copying content to a stream."),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("HttpRequestException > IOException > SocketException", () => new HttpRequestException("Error while copying content to a stream.", new IOException("Unable to write data to the transport connection.", new SocketException((int)SocketError.ConnectionReset))),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("HttpRequestException > WebException", () => new HttpRequestException("aborted", new WebException("timed out")),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("IOException", () => new IOException("The response ended prematurely."),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: false, token: false),
                row("IOException > SocketException", () => new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionReset)),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("HttpIOException (an IOException)", () => new HttpIOException(HttpRequestError.ResponseEnded),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: false, token: false),
                row("FileNotFoundException (an IOException)", () => new FileNotFoundException("missing"),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: false, token: false),
                row("SocketException", () => new SocketException((int)SocketError.ConnectionReset),
                    transport: false, chunk: false, gateway: false, verdict: false, failover: true, token: true),
                row("InvalidOperationException > SocketException", () => new InvalidOperationException("wrapper", new SocketException((int)SocketError.TimedOut)),
                    transport: false, chunk: false, gateway: false, verdict: false, failover: true, token: true),
                row("WebRequestFlushedException", () => new APIAccess.WebRequestFlushedException(APIState.Failing),
                    transport: false, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                row("RequestStalledException (token wait expired)", () => new RequestStalledException("Token retrieval timed out (request never run)"),
                    transport: false, chunk: false, gateway: false, verdict: false, failover: false, token: true),
                row("AggregateException > IOException", () => new AggregateException(new IOException("aborted")),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: false, token: false),
                row("InvalidOperationException > AggregateException > HttpRequestException", () => new InvalidOperationException("outer", new AggregateException(new HttpRequestException("aborted"))),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                // changed on purpose: the old walks looked at only the first of several inner exceptions (no code path builds this shape today).
                row("AggregateException > (HttpRequestException, APIException 400)", () => new AggregateException(new HttpRequestException("aborted"), new APIException("bad", null, HttpStatusCode.BadRequest)),
                    transport: true, chunk: true, gateway: false, verdict: true, failover: true, token: true),
                row("AggregateException > (APIException 400, HttpRequestException)", () => new AggregateException(new APIException("bad", null, HttpStatusCode.BadRequest), new HttpRequestException("aborted")),
                    transport: false, chunk: false, gateway: false, verdict: true, failover: false, token: false),
                // changed on purpose: the old walks looked at only the first of several inner exceptions (no code path builds this shape today).
                row("AggregateException > (FormatException, HttpRequestException)", () => new AggregateException(new FormatException("unrelated"), new HttpRequestException("aborted")),
                    transport: true, chunk: true, gateway: false, verdict: false, failover: true, token: true),
                // changed on purpose: the old walks looked at only the first of several inner exceptions (no code path builds this shape today).
                row("AggregateException > (FormatException, WebException BadGateway)", () => new AggregateException(new FormatException("unrelated"), new WebException("BadGateway")),
                    transport: false, chunk: true, gateway: true, verdict: false, failover: true, token: true)
            };

            return rows.Select(r => new TestCaseData(r.Make, r.Expected).SetName($"Table({r.Name})"));
        }

        [TestCaseSource(nameof(Table))]
        public void EveryDecisionKeepsItsAnswer(Func<Exception?> make, bool[] expected)
        {
            var exception = make();

            Assert.Multiple(() =>
            {
                Assert.That(UploadRetryPolicy.IsTransportFailure(exception), Is.EqualTo(expected[0]), "transport");
                Assert.That(UploadRetryPolicy.IsChunkTransportFailure(exception), Is.EqualTo(expected[1]), "chunk");
                Assert.That(UploadRetryPolicy.IsCompleteTransportFailure(exception), Is.EqualTo(expected[1]), "complete");
                Assert.That(UploadRetryPolicy.IsGatewayTransient(exception), Is.EqualTo(expected[2]), "gateway");
                Assert.That(FailureClassifier.SERVER_VERDICT.Matches(exception), Is.EqualTo(expected[3]), "verdict");
                Assert.That(ApiHostSelector.IsFailoverRetryable(exception), Is.EqualTo(expected[4]), "failover");
                Assert.That(FailureClassifier.TOKEN_FAILOVER.Matches(exception), Is.EqualTo(expected[5]), "token");
            });
        }

        /// <summary>
        /// Every class the classifier can produce is reached by some row of the table, so a class added without a row
        /// (and so without its answer pinned for every decision) fails here.
        /// </summary>
        [Test]
        public void TheTableCoversEveryClass()
        {
            var reached = Table()
                          .Select(t => ((Func<Exception?>)t.Arguments[0]!)())
                          .SelectMany(chainOf)
                          .Select(FailureClassifier.Classify)
                          .ToHashSet();

            var all = Enum.GetValues<FailureClass>()
                          .Where(c => c != FailureClass.None && c != FailureClass.AnyApi && c != FailureClass.AnyWeb);

            Assert.That(all.Where(c => !reached.Contains(c)), Is.Empty);
        }

        [Test]
        public void OneLinkIsClassifiedOnItsOwn()
        {
            Assert.Multiple(() =>
            {
                Assert.That(FailureClassifier.Classify(new TaskCanceledException()), Is.EqualTo(FailureClass.Cancelled));
                Assert.That(FailureClassifier.Classify(new APIException("x", null, HttpStatusCode.Conflict)), Is.EqualTo(FailureClass.ApiVerdict));
                Assert.That(FailureClassifier.Classify(new APIException("x", null, HttpStatusCode.GatewayTimeout)), Is.EqualTo(FailureClass.ApiGateway));
                Assert.That(FailureClassifier.Classify(new APIException("x", null)), Is.EqualTo(FailureClass.ApiNoStatus));
                Assert.That(FailureClassifier.Classify(new WebException("ServiceUnavailable")), Is.EqualTo(FailureClass.WebGateway));
                Assert.That(FailureClassifier.Classify(new WebException("Request timed out after 30 seconds idle")), Is.EqualTo(FailureClass.WebOther));

                // the wrapped cause is not looked at: this is the single-link overload.
                Assert.That(FailureClassifier.Classify(new HttpRequestException("aborted", new IOException("reset"))), Is.EqualTo(FailureClass.SendFailed));
                Assert.That(FailureClassifier.Classify(new InvalidOperationException("wrapper", new IOException("reset"))), Is.EqualTo(FailureClass.Other));
            });
        }

        private static IEnumerable<Exception> chainOf(Exception? exception)
        {
            if (exception == null)
                yield break;

            yield return exception;

            IEnumerable<Exception> inner = exception is AggregateException aggregate
                ? aggregate.InnerExceptions
                : exception.InnerException != null ? new[] { exception.InnerException } : Array.Empty<Exception>();

            foreach (var e in inner.SelectMany(chainOf))
                yield return e;
        }

        private static (string Name, Func<Exception?> Make, bool[] Expected) row(string name, Func<Exception?> make,
                                                                               bool transport, bool chunk, bool gateway, bool verdict, bool failover, bool token)
            => (name, make, new[] { transport, chunk, gateway, verdict, failover, token });
    }
}
