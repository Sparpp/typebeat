// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using typebeat.Game.Online.API;

namespace typebeat.Game.Online
{
    /// <summary>
    /// Decides which host the game API talks to for the rest of a session: the Cloudflare-proxied
    /// production root by default, the direct-origin host once the proxied path has proven itself
    /// dead for this machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS. Backlog 188 to 193 pinned a failure that is specific to a network path rather
    /// than to the server: for some users (the reported cohort is behind Russian ISPs) short requests
    /// to the Cloudflare-proxied production host pass while anything sustained stalls dead, with no
    /// answer, no reset the client can see, and nothing at all in CF Security Events. Backlog 189
    /// moved beatmap submission off that path onto a grey-clouded (DNS-only) subdomain of the same
    /// origin, and backlog 191 did the same for update downloads. Backlog 250 is the same failure
    /// reaching the SCORE path: a score token that "timed out (request never run)" and a score PUT
    /// that "timed out after 30 seconds idle (read 0 bytes)", which loses the play outright.
    /// </para>
    /// <para>
    /// WHY CLOUDFLARE STAYS THE DEFAULT. Not for speed (authed JSON is uncacheable and free-plan
    /// edge-to-origin routing is a wash) but for DDoS absorption: the origin stays out of the hot
    /// path for the vast majority of players, who have no problem with the edge at all.
    /// </para>
    /// <para>
    /// WHY THE FAILOVER IS STICKY, which is the detail that makes that ordering survivable. The
    /// throttled cohort's failure mode is a STALL, not a fast refusal, so a per-request
    /// "try Cloudflare, fall back on failure" policy would cost those users the full idle timeout on
    /// every single call. Instead the first transport-class failure pins the direct host for the
    /// whole session and every later request is built against it, paying the stall once.
    /// </para>
    /// <para>
    /// WHY THERE IS NO MID-SESSION RE-PROBE, and therefore no clock here. The cohort's signature is
    /// precisely that SHORT requests pass while sustained ones stall, so a small probe coming back
    /// healthy cannot prove the failing class is over; flipping back on a passing probe would just
    /// re-tax the user another stall to rediscover it. This type holds no persisted state, so the
    /// reset is a fresh launch, which starts on Cloudflare by construction.
    /// </para>
    /// <para>
    /// Both hosts are the same origin behind the same reverse proxy, which serves the whole
    /// application on either, so no server-side change is needed for the fallback to answer any
    /// route. Bearer auth is host-agnostic, so a token minted through one host works on the other.
    /// </para>
    /// </remarks>
    public class ApiHostSelector
    {
        /// <summary>
        /// The host tried first, and the only one a fresh session starts on.
        /// </summary>
        public const string PRIMARY_ROOT = TypebeatEndpointConfiguration.PRODUCTION_ROOT;

        /// <summary>
        /// The host pinned for the rest of the session once <see cref="PRIMARY_ROOT"/> has failed
        /// in a transport-class way.
        /// </summary>
        /// <remarks>
        /// This is the same direct-origin subdomain beatmap submission has used since backlog 189
        /// (see <see cref="TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT"/>): it already exists,
        /// is already grey-clouded to the same origin, and its vhost already serves every route.
        /// The name is honest about what created it rather than about what it now carries; if
        /// `api.typebeat.mingda.sh` is ever added beside it as a naming-honesty alias, only this
        /// constant moves.
        /// </remarks>
        public const string FALLBACK_ROOT = TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT;

        /// <summary>
        /// The API root this session started on.
        /// </summary>
        public string PrimaryApiRoot { get; }

        /// <summary>
        /// The API root this session falls over to, or <see langword="null"/> when there is nothing
        /// to fall over to.
        /// </summary>
        /// <remarks>
        /// Non-null only for the production root, mirroring the conditional on
        /// <see cref="EndpointConfiguration.BeatmapSubmissionServiceUrl"/>. A dev target or a
        /// `TYPEBEAT_API_URL` override names the server actually under test, and silently swinging
        /// those over to a production host on the first hiccup would test the wrong machine.
        /// </remarks>
        public string? FallbackApiRoot { get; }

        /// <summary>
        /// The API root every request should be built against right now.
        /// </summary>
        public string CurrentApiRoot { get; private set; }

        /// <summary>
        /// Whether this session has already been pinned to <see cref="FallbackApiRoot"/>.
        /// </summary>
        public bool HasFailedOver => CurrentApiRoot != PrimaryApiRoot;

        public ApiHostSelector(string apiRoot)
        {
            ArgumentNullException.ThrowIfNull(apiRoot);

            PrimaryApiRoot = CurrentApiRoot = apiRoot;
            FallbackApiRoot = apiRoot == PRIMARY_ROOT ? FALLBACK_ROOT : null;
        }

        /// <summary>
        /// Report that a request failed in a transport-class way (see
        /// <see cref="IsFailoverRetryable"/> for what that means and does not mean).
        /// </summary>
        /// <returns>
        /// Whether this call changed <see cref="CurrentApiRoot"/>, meaning the caller now has to
        /// publish the new root. False when there is no fallback configured and false for every
        /// failure after the first, since the pin is permanent for the session: a transport failure
        /// on the fallback says nothing new, and the ordinary consecutive-failure machinery in
        /// <see cref="APIAccess"/> keeps handling it.
        /// </returns>
        public bool NotifyTransportFailure()
        {
            if (HasFailedOver || FallbackApiRoot == null)
                return false;

            CurrentApiRoot = FallbackApiRoot;
            return true;
        }

        /// <summary>
        /// Whether <paramref name="exception"/> is a failure worth sending a request again for once
        /// the session has moved to a different host, meaning the request did not arrive intact
        /// rather than the server rejecting what it received.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the same set <c>UploadRetryPolicy.IsChunkTransportFailure</c> names, for the same
        /// reason, and it is named separately because the decision it encodes is a different one:
        /// there the question is whether to repeat a request on the SAME route, here it is only ever
        /// asked once the route has already changed under the caller.
        /// </para>
        /// <para>
        /// Retried:
        /// <list type="bullet">
        /// <item><see cref="WebException"/>: the shape an idle timeout takes, which is the whole
        /// failure this seam exists for. <c>WebRequest.AllowRetryOnTimeout</c> is deliberately false
        /// for every API request, but that switch is about repeating a timeout on the host that just
        /// produced it; a different host is a different question.</item>
        /// <item><see cref="HttpRequestException"/> and <see cref="SocketException"/>: the send
        /// itself failed, which is what a mid-body reset on the proxied path looks like.</item>
        /// <item><see cref="APIAccess.WebRequestFlushedException"/>: the queue was emptied out from
        /// under a request that had not been sent, so nothing was rejected and nothing left the
        /// machine. Three consecutive transport failures are exactly what causes that flush, so it
        /// is a NORMAL event on the path this seam is built for.</item>
        /// </list>
        /// </para>
        /// <para>
        /// Not retried:
        /// <list type="bullet">
        /// <item><see cref="OperationCanceledException"/>: the cancel path. <c>APIRequest.Cancel</c>
        /// fails a request with exactly this and runs the same failure path a 404 does, so retrying
        /// it would resurrect a request the caller killed.</item>
        /// <item><see cref="APIException"/>: the server answered and the answer decoded, so the
        /// request reached an origin and the payload is what is at fault. Its inner exception may
        /// well be a transport one, which is why the chain is walked outermost first.</item>
        /// </list>
        /// </para>
        /// </remarks>
        public static bool IsFailoverRetryable(Exception? exception)
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
                    case SocketException:
                    case APIAccess.WebRequestFlushedException:
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a request that was built against <paramref name="routeWhenSent"/> and failed with
        /// <paramref name="failure"/> should be sent once more, now that the session is on
        /// <paramref name="routeNow"/>.
        /// </summary>
        /// <remarks>
        /// BOTH conditions are load-bearing. The route having changed is what makes a repeat worth
        /// anything at all (the same request on the same dead path just stalls again), and it is not
        /// on its own enough: some OTHER request may have been the one that pinned the fallback,
        /// while this one carries a genuine server verdict that a repeat would only collect twice.
        /// The caller is responsible for repeating at most once, and for doing it with a FRESH
        /// request object: a completed <see cref="APIRequest"/> can never fire again, since both
        /// <c>TriggerSuccess</c> and <c>TriggerFailure</c> are gated on its completion state.
        /// </remarks>
        public static bool ShouldRetryOnNewHost(Exception? failure, string routeWhenSent, string routeNow)
            => routeNow != routeWhenSent && IsFailoverRetryable(failure);
    }
}
