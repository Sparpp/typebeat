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
    /// The shapes a failed request's exception takes, as far as any retry or failover decision cares.
    /// </summary>
    /// <remarks>
    /// A set of these is what every retry and failover predicate is written over (see <see cref="FailurePredicate"/>),
    /// so a new exception type gets classified once, here, and every decision sees it.
    /// </remarks>
    [Flags]
    public enum FailureClass
    {
        None = 0,

        /// <summary>
        /// None of the shapes below, such as a plain wrapper. Never decisive: the walk looks past it.
        /// </summary>
        Other = 1,

        /// <summary>
        /// An <see cref="OperationCanceledException"/> (or <see cref="System.Threading.Tasks.TaskCanceledException"/>):
        /// the cancel path. <see cref="APIRequest.Cancel"/> fails a request with exactly this.
        /// </summary>
        Cancelled = 1 << 1,

        /// <summary>
        /// An <see cref="APIException"/> carrying a status that is not a gateway one: the origin answered with a verdict.
        /// </summary>
        ApiVerdict = 1 << 2,

        /// <summary>
        /// An <see cref="APIException"/> carrying 502, 503 or 504: an origin that is up enough to render its own
        /// JSON error while it shuts down behind a healthy proxy.
        /// </summary>
        ApiGateway = 1 << 3,

        /// <summary>
        /// An <see cref="APIException"/> with no status: the body decoded, but the request was aborted before the
        /// response status landed.
        /// </summary>
        ApiNoStatus = 1 << 4,

        /// <summary>
        /// A <see cref="WebException"/> for 502, 503 or 504: an edge answering for an origin that did not.
        /// </summary>
        WebGateway = 1 << 5,

        /// <summary>
        /// Any other <see cref="WebException"/>: the framework's idle timeout, a request queued while logged out,
        /// or a non-gateway status that did not decode into an <see cref="APIException"/>.
        /// </summary>
        WebOther = 1 << 6,

        /// <summary>
        /// An <see cref="HttpRequestException"/>: the send itself failed.
        /// </summary>
        SendFailed = 1 << 7,

        /// <summary>
        /// An <see cref="IOException"/>: the stream copy under a send failed.
        /// </summary>
        StreamFailed = 1 << 8,

        /// <summary>
        /// A <see cref="SocketException"/>: the connection itself failed.
        /// </summary>
        Socket = 1 << 9,

        /// <summary>
        /// An <see cref="APIAccess.WebRequestFlushedException"/>: the API queue was emptied out from under a request
        /// that had not been sent.
        /// </summary>
        QueueFlushed = 1 << 10,

        /// <summary>
        /// A <see cref="RequestStalledException"/>: a caller's own wait expired with nothing reported at all.
        /// </summary>
        Stalled = 1 << 11,

        AnyApi = ApiVerdict | ApiGateway | ApiNoStatus,

        AnyWeb = WebGateway | WebOther,
    }
}
