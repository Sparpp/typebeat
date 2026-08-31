// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using NUnit.Framework;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.Solo;
using typebeat.Game.Scoring;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers backlog 250's API host failover: the game talks to the Cloudflare-proxied production
    /// host by default and pins the direct-origin one for the rest of the session the first time the
    /// proxied path fails in a transport-class way (the throttled cohort's 30 second stall).
    /// </summary>
    [TestFixture]
    public class ApiHostFailoverTest
    {
        private const string proxied_root = "https://typebeat.mingda.sh";
        private const string direct_root = "https://bss.typebeat.mingda.sh";

        /// <summary>
        /// Cloudflare stays the default: it absorbs the traffic of the vast majority of players who
        /// have no problem with the edge, and the direct host only exists for the ones who do.
        /// </summary>
        [Test]
        public void SessionStartsOnTheProxiedProductionRoot()
        {
            var selector = new ApiHostSelector(new TypebeatEndpointConfiguration().APIUrl);

            Assert.Multiple(() =>
            {
                Assert.That(selector.CurrentApiRoot, Is.EqualTo(proxied_root));
                Assert.That(selector.HasFailedOver, Is.False);
                Assert.That(selector.FallbackApiRoot, Is.EqualTo(direct_root));
            });
        }

        [Test]
        public void TransportFailurePinsTheDirectOriginHost()
        {
            var selector = new ApiHostSelector(proxied_root);

            Assert.Multiple(() =>
            {
                Assert.That(selector.NotifyTransportFailure(), Is.True, "the first transport failure must move the session");
                Assert.That(selector.CurrentApiRoot, Is.EqualTo(direct_root));
                Assert.That(selector.HasFailedOver, Is.True);
            });
        }

        /// <summary>
        /// The pin is permanent for the session and there is no mid-session re-probe, so nothing
        /// after the first failure moves the selection: a failure on the fallback says nothing new,
        /// and APIAccess's own consecutive-failure machinery keeps handling it.
        /// </summary>
        [Test]
        public void FurtherTransportFailuresChangeNothing()
        {
            var selector = new ApiHostSelector(proxied_root);
            selector.NotifyTransportFailure();

            Assert.Multiple(() =>
            {
                Assert.That(selector.NotifyTransportFailure(), Is.False);
                Assert.That(selector.NotifyTransportFailure(), Is.False);
                Assert.That(selector.CurrentApiRoot, Is.EqualTo(direct_root), "the selection must be sticky");
            });
        }

        /// <summary>
        /// Regression guard for the dev / TYPEBEAT_API_URL-override path, mirroring the one on
        /// <c>BeatmapSubmissionServiceUrl</c>: a non-production root names the server actually under
        /// test, and swinging it over to a production host on the first hiccup would silently test
        /// the wrong machine.
        /// </summary>
        [Test]
        public void NonProductionRootHasNoFallback()
        {
            var selector = new ApiHostSelector(new TypebeatEndpointConfiguration("http://localhost:5089").APIUrl);

            Assert.Multiple(() =>
            {
                Assert.That(selector.FallbackApiRoot, Is.Null);
                Assert.That(selector.NotifyTransportFailure(), Is.False);
                Assert.That(selector.CurrentApiRoot, Is.EqualTo("http://localhost:5089"));
                Assert.That(selector.HasFailedOver, Is.False);
            });
        }

        /// <summary>
        /// Literals on purpose, as with the update feed: changing either host is a deliberate,
        /// visible edit. A fallback equal to the primary would make the whole failover a no-op.
        /// </summary>
        [Test]
        public void HostsArePinnedAndDistinct()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ApiHostSelector.PRIMARY_ROOT, Is.EqualTo(proxied_root));
                Assert.That(ApiHostSelector.FALLBACK_ROOT, Is.EqualTo(direct_root));
                Assert.That(ApiHostSelector.FALLBACK_ROOT, Is.Not.EqualTo(ApiHostSelector.PRIMARY_ROOT));

                Assert.That(ApiHostSelector.PRIMARY_ROOT, Does.StartWith("https://"));
                Assert.That(ApiHostSelector.FALLBACK_ROOT, Does.StartWith("https://"));

                // the fallback is the direct origin submission already rides, not a third host.
                Assert.That(ApiHostSelector.FALLBACK_ROOT, Is.EqualTo(TypebeatEndpointConfiguration.PRODUCTION_BSS_ROOT));
            });
        }

        /// <summary>
        /// The taxonomy the retry is allowed to act on: a request that did not arrive intact, never
        /// a server verdict and never the cancel path.
        /// </summary>
        [Test]
        public void FailoverRetryableNamesTransportFailuresOnly()
        {
            Assert.Multiple(() =>
            {
                // the stall this whole seam exists for surfaces as an idle timeout.
                Assert.That(ApiHostSelector.IsFailoverRetryable(new WebException("The request timed out")), Is.True);
                Assert.That(ApiHostSelector.IsFailoverRetryable(new HttpRequestException("Error while copying content to a stream.")), Is.True);
                Assert.That(ApiHostSelector.IsFailoverRetryable(new SocketException()), Is.True);

                // three consecutive transport failures flush the queue, so a flush is a normal
                // event on this path rather than an exotic one.
                Assert.That(ApiHostSelector.IsFailoverRetryable(new APIAccess.WebRequestFlushedException(APIState.Failing)), Is.True);

                // the server answered and the answer decoded: the payload is at fault, not the route,
                // even when a transport exception is wrapped inside it.
                Assert.That(ApiHostSelector.IsFailoverRetryable(new APIException("invalid token", null, HttpStatusCode.Unauthorized)), Is.False);
                Assert.That(ApiHostSelector.IsFailoverRetryable(new APIException("expired token", new WebException("boom"))), Is.False);

                // APIRequest.Cancel fails a request with exactly this and runs the same path a 404
                // does, so retrying it would resurrect a request the caller killed.
                Assert.That(ApiHostSelector.IsFailoverRetryable(new OperationCanceledException("Request cancelled")), Is.False);

                Assert.That(ApiHostSelector.IsFailoverRetryable(new InvalidOperationException("Non-user playable mod selected.")), Is.False);
                Assert.That(ApiHostSelector.IsFailoverRetryable(new IOException("disk")), Is.False);
                Assert.That(ApiHostSelector.IsFailoverRetryable(null), Is.False);
            });
        }

        /// <summary>
        /// Both halves of the retry gate are load-bearing: without the route change the repeat just
        /// stalls on the same dead path, and without the exception check a request carrying a genuine
        /// server verdict would be repeated because some OTHER request happened to pin the fallback.
        /// </summary>
        [Test]
        public void RetryNeedsBothATransportFailureAndARouteChange()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ApiHostSelector.ShouldRetryOnNewHost(new WebException("timeout"), proxied_root, direct_root), Is.True);

                Assert.That(ApiHostSelector.ShouldRetryOnNewHost(new WebException("timeout"), proxied_root, proxied_root), Is.False,
                    "a transport failure that did not move the session must not be repeated");

                Assert.That(ApiHostSelector.ShouldRetryOnNewHost(new APIException("invalid token", null), proxied_root, direct_root), Is.False,
                    "a server verdict must not be repeated just because another request moved the session");

                Assert.That(ApiHostSelector.ShouldRetryOnNewHost(new OperationCanceledException(), proxied_root, direct_root), Is.False);
            });
        }

        /// <summary>
        /// THE seam. <c>APIRequest.Uri</c> reads <c>Endpoints.APIUrl</c> inside <c>CreateWebRequest</c>,
        /// which runs at perform time, so a request object built long before the failover still leaves
        /// on the host the session has since moved to. That is what makes one swap cover every request
        /// without re-creating or re-queueing anything, the score token and the score PUT included.
        /// </summary>
        [Test]
        public void EveryApiRequestFollowsALiveRootChange()
        {
            var api = new DummyAPIAccess();
            api.Endpoints.APIUrl = proxied_root;

            var token = new CreateSoloScoreRequest(new BeatmapInfo { OnlineID = 14 }, 0, "hash");
            var submission = new SubmitSoloScoreRequest(new ScoreInfo(), 13543, 14);
            var notifications = new GetNotificationsRequest();

            token.AttachAPI(api);
            submission.AttachAPI(api);
            notifications.AttachAPI(api);

            Assert.Multiple(() =>
            {
                Assert.That(urlOf(token), Is.EqualTo($"{proxied_root}/api/v2/beatmaps/14/solo/scores"));
                Assert.That(urlOf(submission), Is.EqualTo($"{proxied_root}/api/v2/beatmaps/14/solo/scores/13543"));
                Assert.That(urlOf(notifications), Is.EqualTo($"{proxied_root}/api/v2/notifications"));
            });

            // exactly what APIAccess does when the selector pins the fallback.
            api.Endpoints.APIUrl = direct_root;

            Assert.Multiple(() =>
            {
                Assert.That(urlOf(token), Is.EqualTo($"{direct_root}/api/v2/beatmaps/14/solo/scores"),
                    "an already-built request must pick up the pin at perform time");
                Assert.That(urlOf(submission), Is.EqualTo($"{direct_root}/api/v2/beatmaps/14/solo/scores/13543"));

                // the notifications websocket needs no client-side rewriting: the server derives
                // notification_endpoint from the host this request arrives on, and the connector
                // re-runs the request on every reconnect.
                Assert.That(urlOf(notifications), Is.EqualTo($"{direct_root}/api/v2/notifications"));
            });
        }

        /// <summary>
        /// Auth follows the pin too. Bearer tokens are host-agnostic so a pinned session keeps
        /// working on the token it holds, but a REFRESH aimed at the host that just proved
        /// unreachable would kill the session at the token's expiry, which is the failure the pin
        /// exists to avoid.
        /// </summary>
        [Test]
        public void OAuthTokenEndpointFollowsALiveRootChange()
        {
            var endpoints = new TypebeatEndpointConfiguration();
            var auth = new OAuth(endpoints.APIClientID, endpoints.APIClientSecret, endpoints);

            Assert.That(auth.TokenEndpoint, Is.EqualTo($"{proxied_root}/oauth/token"));

            endpoints.APIUrl = direct_root;

            Assert.That(auth.TokenEndpoint, Is.EqualTo($"{direct_root}/oauth/token"));
        }

        /// <summary>
        /// <c>CreateWebRequest</c> is protected all the way up the hierarchy, so this pin needs
        /// reflection (the same seam <c>ChunkedPackageUploadTest</c> uses and documents). Invoking the
        /// base <see cref="MethodInfo"/> still dispatches virtually, so this observes exactly what
        /// <c>Perform</c> would build.
        /// </summary>
        private static string urlOf(APIRequest request)
        {
            var method = typeof(APIRequest).GetMethod(@"CreateWebRequest", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "APIRequest.CreateWebRequest has been renamed; this pin needs updating.");
            return ((osu.Framework.IO.Network.WebRequest)method!.Invoke(request, Array.Empty<object>())!).Url;
        }
    }
}
