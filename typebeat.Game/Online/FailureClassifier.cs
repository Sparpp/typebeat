// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using typebeat.Game.Online.API;

namespace typebeat.Game.Online
{
    /// <summary>
    /// The one place a failed request's exception is classified, shared by the beatmap upload retry policy, the API
    /// host failover and score submission's token retry.
    /// </summary>
    public static class FailureClassifier
    {
        /// <summary>
        /// Whether a whole package upload is repeated on the same route (<c>UploadRetryPolicy.IsTransportFailure</c>).
        /// A <see cref="WebException"/> is decisive and refused: the idle timeout retry is switched off globally.
        /// </summary>
        public static readonly FailurePredicate UPLOAD_TRANSPORT = new FailurePredicate(
            FailureClass.Cancelled | FailureClass.AnyApi | FailureClass.AnyWeb | FailureClass.SendFailed | FailureClass.StreamFailed,
            FailureClass.SendFailed | FailureClass.StreamFailed);

        /// <summary>
        /// Whether one upload-session chunk, or the request that completes the session, is repeated
        /// (<c>UploadRetryPolicy.IsChunkTransportFailure</c>). The upload set plus the idle timeout and a queue flush.
        /// </summary>
        public static readonly FailurePredicate CHUNK_TRANSPORT = new FailurePredicate(
            FailureClass.Cancelled | FailureClass.AnyApi | FailureClass.AnyWeb | FailureClass.SendFailed | FailureClass.StreamFailed | FailureClass.QueueFlushed,
            FailureClass.AnyWeb | FailureClass.SendFailed | FailureClass.StreamFailed | FailureClass.QueueFlushed);

        /// <summary>
        /// Whether an edge answered for an origin that did not (<c>UploadRetryPolicy.IsGatewayTransient</c>). An
        /// <see cref="APIException"/> with no status is looked past, since it says nothing about the origin.
        /// </summary>
        public static readonly FailurePredicate GATEWAY = new FailurePredicate(
            FailureClass.Cancelled | FailureClass.ApiVerdict | FailureClass.ApiGateway | FailureClass.AnyWeb,
            FailureClass.ApiGateway | FailureClass.WebGateway);

        /// <summary>
        /// Whether the origin answered with a body that decoded, anywhere in the chain (the chunked failure policy's
        /// server-verdict test). Only <see cref="APIException"/> is decisive, so even the cancel path is looked past.
        /// </summary>
        public static readonly FailurePredicate SERVER_VERDICT = new FailurePredicate(FailureClass.AnyApi, FailureClass.AnyApi);

        /// <summary>
        /// Whether a request is sent again once the session has moved to another API host
        /// (<c>ApiHostSelector.IsFailoverRetryable</c>). The chunk set with <see cref="SocketException"/> in place of
        /// <see cref="IOException"/>: the API's own transport failures surface as either a send failure or a socket one.
        /// </summary>
        public static readonly FailurePredicate FAILOVER = new FailurePredicate(
            FailureClass.Cancelled | FailureClass.AnyApi | FailureClass.AnyWeb | FailureClass.SendFailed | FailureClass.Socket | FailureClass.QueueFlushed,
            FailureClass.AnyWeb | FailureClass.SendFailed | FailureClass.Socket | FailureClass.QueueFlushed);

        /// <summary>
        /// Whether a score token request is sent again on a new host: <see cref="FAILOVER"/>, plus the token wait
        /// itself expiring, which is transport-class by construction (nothing answered and nothing was refused).
        /// </summary>
        public static readonly FailurePredicate TOKEN_FAILOVER = new FailurePredicate(
            FAILOVER.Decisive | FailureClass.Stalled,
            FAILOVER.Matching | FailureClass.Stalled);

        /// <summary>
        /// The class of the first link of <paramref name="exception"/>'s chain that falls in <paramref name="decisive"/>,
        /// or <see cref="FailureClass.None"/> when no link does.
        /// </summary>
        /// <remarks>
        /// The chain is walked outermost first through <see cref="Exception.InnerException"/>, and through every
        /// one of an <see cref="AggregateException"/>'s inner exceptions in order, depth first.
        /// </remarks>
        public static FailureClass Classify(Exception? exception, FailureClass decisive)
        {
            if (exception == null)
                return FailureClass.None;

            var own = Classify(exception);

            if ((own & decisive) != 0)
                return own;

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var found = Classify(inner, decisive);

                    if (found != FailureClass.None)
                        return found;
                }

                return FailureClass.None;
            }

            return Classify(exception.InnerException, decisive);
        }

        /// <summary>
        /// The class of <paramref name="exception"/> itself, not looking at what it wraps.
        /// </summary>
        public static FailureClass Classify(Exception exception)
        {
            switch (exception)
            {
                case OperationCanceledException:
                    return FailureClass.Cancelled;

                case APIException api:
                    if (api.StatusCode == null)
                        return FailureClass.ApiNoStatus;

                    return IsGatewayStatus(api.StatusCode.Value) ? FailureClass.ApiGateway : FailureClass.ApiVerdict;

                case WebException web:
                    return isGatewayWebException(web) ? FailureClass.WebGateway : FailureClass.WebOther;

                case HttpRequestException:
                    return FailureClass.SendFailed;

                case IOException:
                    return FailureClass.StreamFailed;

                case SocketException:
                    return FailureClass.Socket;

                case APIAccess.WebRequestFlushedException:
                    return FailureClass.QueueFlushed;

                case RequestStalledException:
                    return FailureClass.Stalled;

                default:
                    return FailureClass.Other;
            }
        }

        /// <summary>
        /// The status codes an edge answers with when it cannot reach the origin, or reached it and gave up waiting.
        /// None of them is a verdict on the request: the origin never answered.
        /// </summary>
        public static bool IsGatewayStatus(HttpStatusCode statusCode)
            => statusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

        private static bool isGatewayWebException(WebException exception)
        {
            // checked first for the shapes that do carry a response, even though the framework's own
            // status failure does not.
            if ((exception.Response as HttpWebResponse)?.StatusCode is HttpStatusCode fromResponse)
                return IsGatewayStatus(fromResponse);

            // otherwise the message is the status enum's name, or its number if some other caller built it.
            // That couples this decision to the framework's message text: the exception carries no typed
            // status, and the tests build the same shape by hand, so a change there would go unnoticed.
            return Enum.TryParse(exception.Message, out HttpStatusCode fromMessage) && IsGatewayStatus(fromMessage);
        }
    }
}
