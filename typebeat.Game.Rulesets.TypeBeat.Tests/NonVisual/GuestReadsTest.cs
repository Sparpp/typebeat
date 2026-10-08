// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;
using osu.Framework.Platform;
using typebeat.Game.Beatmaps;
using typebeat.Game.Configuration;
using typebeat.Game.Online;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.Leaderboards;
using typebeat.Game.Online.Solo;
using typebeat.Game.Rulesets.TypeBeat;
using typebeat.Game.Scoring;
using typebeat.Game.Screens.Play.Leaderboards;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Backlog 406, client half: a guest (no login, <see cref="APIState.Offline"/>) performs the public reads, and
    /// every request that needs a login still fails on the spot with "User not logged in".
    /// </summary>
    [TestFixture]
    public class GuestReadsTest
    {
        private static RulesetInfo ruleset => new TypeBeatRuleset().RulesetInfo;

        private static IEnumerable<TestCaseData> publicReads()
        {
            yield return new TestCaseData(new GetBeatmapRequest(md5Hash: "abc")).SetName("map lookup");
            yield return new TestCaseData(new GetBeatmapSetRequest(1)).SetName("set show");
            yield return new TestCaseData(new GetBeatmapSetRequest(1, BeatmapSetLookupType.BeatmapId)).SetName("set lookup");
            yield return new TestCaseData(new SearchBeatmapSetsRequest("q", ruleset)).SetName("set search");
            yield return new TestCaseData(new GetScoresRequest(new BeatmapInfo { OnlineID = 1 }, ruleset)).SetName("global leaderboard");
            yield return new TestCaseData(new GetUserRequest(2)).SetName("profile by id");
            yield return new TestCaseData(new GetUserRequest("someone")).SetName("profile by name");
            yield return new TestCaseData(new GetUserScoresRequest(2, ScoreType.Best, new PaginationParameters(5))).SetName("profile scores");
            yield return new TestCaseData(new GetUserBeatmapsRequest(2, BeatmapSetType.Ranked, new PaginationParameters(5))).SetName("profile maps");
            yield return new TestCaseData(new GetUserMostPlayedBeatmapsRequest(2, new PaginationParameters(5))).SetName("profile most played");
            yield return new TestCaseData(new GetCommentsRequest(1, CommentableType.Beatmapset)).SetName("comments read");
            yield return new TestCaseData(new GetSeasonalBackgroundsRequest()).SetName("seasonal backgrounds");
            yield return new TestCaseData(new DownloadBeatmapSetRequest(new BeatmapSetInfo { OnlineID = 1 }, false)).SetName("set download");
            yield return new TestCaseData(new DownloadReplayRequest(new ScoreInfo { OnlineID = 1 })).SetName("replay download");
        }

        private static IEnumerable<TestCaseData> signedInOnly()
        {
            yield return new TestCaseData(new GetMeRequest()).SetName("me");
            yield return new TestCaseData(new GetFriendsRequest()).SetName("friends");
            yield return new TestCaseData(new CreateSoloScoreRequest(new BeatmapInfo { OnlineID = 1 }, 0, "hash")).SetName("score token");
            yield return new TestCaseData(new UploadReplayRequest(1, new byte[] { 1 })).SetName("replay upload");
            yield return new TestCaseData(new PostBeatmapFavouriteRequest(1, BeatmapFavouriteAction.Favourite)).SetName("favourite");
            yield return new TestCaseData(new CommentPostRequest(CommentableType.Beatmapset, 1, "hi")).SetName("comment post");
            yield return new TestCaseData(new GetScoresRequest(new BeatmapInfo { OnlineID = 1 }, ruleset, BeatmapLeaderboardScope.Friend)).SetName("friend leaderboard");
            yield return new TestCaseData(new GetScoresRequest(new BeatmapInfo { OnlineID = 1 }, ruleset, BeatmapLeaderboardScope.Country)).SetName("country leaderboard");
            yield return new TestCaseData(new GetScoresRequest(new BeatmapInfo { OnlineID = 1 }, ruleset, BeatmapLeaderboardScope.Team)).SetName("team leaderboard");
            yield return new TestCaseData(new GetUserRequest()).SetName("profile with no lookup");
        }

        [TestCaseSource(nameof(publicReads))]
        public void PublicReadIsAdmittedForAGuest(APIRequest request)
        {
            Assert.Multiple(() =>
            {
                Assert.That(request.AllowsAnonymous, Is.True);
                Assert.That(APIAccess.Admits(APIState.Offline, request), Is.True);
            });
        }

        [TestCaseSource(nameof(signedInOnly))]
        public void SignedInRequestIsRefusedForAGuest(APIRequest request)
        {
            Assert.Multiple(() =>
            {
                Assert.That(request.AllowsAnonymous, Is.False);
                Assert.That(APIAccess.Admits(APIState.Offline, request), Is.False, "a guest's signed-in request must fail fast");

                // once any login exists the queue takes everything, as it always did.
                Assert.That(APIAccess.Admits(APIState.Connecting, request), Is.True);
                Assert.That(APIAccess.Admits(APIState.Online, request), Is.True);
                Assert.That(APIAccess.Admits(APIState.Failing, request), Is.True);
            });
        }

        [TestCase(APIState.Online, true)]
        [TestCase(APIState.Offline, true)]
        [TestCase(APIState.Connecting, false)]
        [TestCase(APIState.Failing, false)]
        [TestCase(APIState.RequiresSecondFactorAuth, false)]
        public void MetadataSourceIsAvailableToAGuest(APIState state, bool available)
            => Assert.That(APIBeatmapMetadataSource.IsAvailableIn(state), Is.EqualTo(available));

        /// <summary>
        /// A guest's failed lookup clears the map's online state only on the server's own "not found"; a lookup
        /// that never got an answer (no network, a server that refuses anonymous reads) leaves the map alone.
        /// A signed-in lookup keeps its old rule, where every failure is the verdict.
        /// </summary>
        [Test]
        public void OnlyANotFoundIsAVerdictForAGuestLookup()
        {
            var notFound = new APIException("not found", null, HttpStatusCode.NotFound);
            var bareNotFound = new WebException(nameof(HttpStatusCode.NotFound));
            var refused = new APIException("authentication failed", null, HttpStatusCode.Unauthorized);
            var noNetwork = new System.Net.Http.HttpRequestException("No such host is known.");

            Assert.Multiple(() =>
            {
                Assert.That(APIBeatmapMetadataSource.IsNotFoundVerdict(APIState.Offline, notFound), Is.True);
                Assert.That(APIBeatmapMetadataSource.IsNotFoundVerdict(APIState.Offline, bareNotFound), Is.True);
                Assert.That(APIBeatmapMetadataSource.IsNotFoundVerdict(APIState.Offline, refused), Is.False);
                Assert.That(APIBeatmapMetadataSource.IsNotFoundVerdict(APIState.Offline, noNetwork), Is.False);
                Assert.That(APIBeatmapMetadataSource.IsNotFoundVerdict(APIState.Offline, null), Is.False);

                Assert.That(APIBeatmapMetadataSource.IsNotFoundVerdict(APIState.Online, noNetwork), Is.True);
                Assert.That(APIBeatmapMetadataSource.IsNotFoundVerdict(APIState.Online, refused), Is.True);
            });
        }

        [TestCase(BeatmapLeaderboardScope.Global, false, false)]
        [TestCase(BeatmapLeaderboardScope.Country, false, true)]
        [TestCase(BeatmapLeaderboardScope.Friend, false, true)]
        [TestCase(BeatmapLeaderboardScope.Team, false, true)]
        [TestCase(BeatmapLeaderboardScope.Global, true, false)]
        [TestCase(BeatmapLeaderboardScope.Friend, true, false)]
        public void OnlyTheGlobalBoardIsOpenToAGuest(BeatmapLeaderboardScope scope, bool isLoggedIn, bool requiresSignIn)
            => Assert.That(LeaderboardManager.RequiresSignIn(scope, isLoggedIn), Is.EqualTo(requiresSignIn));

        /// <summary>
        /// The real <see cref="APIAccess"/>, with no login, against a local stand-in server: the public read goes out
        /// with no Authorization header and succeeds, the signed-in request fails on the spot and never reaches the
        /// server, and a signed-in request that is already in the queue (it got there while a login existed) is
        /// failed by the guest run loop rather than sent without its token.
        /// </summary>
        [Test]
        public void HeadlessGuestPerformsPublicReadsOnly()
        {
            using var server = new StubServer();
            string storagePath = Path.Combine(Path.GetTempPath(), $"typebeat-guest-reads-{Guid.NewGuid():N}");

            try
            {
                var config = new OsuConfigManager(new NativeStorage(storagePath));
                var endpoints = new EndpointConfiguration { APIUrl = server.Root, WebsiteUrl = server.Root };
                var api = new APIAccess(new OsuGameBase(), config, endpoints, "test");

                try
                {
                    Assert.That(api.State.Value, Is.EqualTo(APIState.Offline), "no login was configured");

                    var stuck = new GetFriendsRequest();
                    stuck.AttachAPI(api);

                    var queue = (Queue<APIRequest>)typeof(APIAccess).GetField("queue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(api)!;
                    lock (queue)
                        queue.Enqueue(stuck);

                    var read = new GetSeasonalBackgroundsRequest();
                    api.Queue(read);

                    var refused = new GetMeRequest();
                    api.Queue(refused);

                    Assert.That(refused.CompletionState, Is.EqualTo(APIRequestCompletionState.Failed), "a guest's signed-in request fails inside Queue itself");
                    Assert.That(refused.FailureException?.Message, Is.EqualTo(APIAccess.NOT_LOGGED_IN_MESSAGE));

                    waitFor(() => read.CompletionState != APIRequestCompletionState.Waiting && stuck.CompletionState != APIRequestCompletionState.Waiting);

                    Assert.Multiple(() =>
                    {
                        Assert.That(read.CompletionState, Is.EqualTo(APIRequestCompletionState.Completed), read.FailureException?.ToString());
                        Assert.That(read.Response, Is.Not.Null);

                        Assert.That(stuck.CompletionState, Is.EqualTo(APIRequestCompletionState.Failed));
                        Assert.That(stuck.FailureException?.Message, Is.EqualTo(APIAccess.NOT_LOGGED_IN_MESSAGE));

                        Assert.That(server.Paths, Is.EquivalentTo(new[] { "/api/v2/seasonal-backgrounds" }), "only the public read may reach the server");
                        Assert.That(server.SawAuthorization, Is.False, "a guest has no token to send");
                    });
                }
                finally
                {
                    api.Dispose();
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(storagePath, true);
                }
                catch
                {
                    // best effort: the config manager may still hold its file for a moment.
                }
            }
        }

        private static void waitFor(Func<bool> condition)
        {
            var stopwatch = Stopwatch.StartNew();

            while (!condition())
            {
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(15))
                    Assert.Fail("timed out waiting for the API thread");

                Thread.Sleep(20);
            }
        }

        /// <summary>
        /// A minimal HTTP/1.1 server on a loopback port: answers every request with an empty seasonal backgrounds
        /// payload and records each request's path and whether it carried an Authorization header.
        /// </summary>
        private sealed class StubServer : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread thread;
            private readonly ConcurrentQueue<string> paths = new ConcurrentQueue<string>();
            private volatile bool sawAuthorization;
            private volatile bool disposed;

            public string Root { get; }

            public IEnumerable<string> Paths => paths.ToArray();

            public bool SawAuthorization => sawAuthorization;

            public StubServer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Root = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

                thread = new Thread(serve) { IsBackground = true };
                thread.Start();
            }

            private void serve()
            {
                while (!disposed)
                {
                    TcpClient client;

                    try
                    {
                        client = listener.AcceptTcpClient();
                    }
                    catch
                    {
                        return;
                    }

                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var reader = new StreamReader(stream, Encoding.ASCII);
                        string? requestLine = reader.ReadLine();

                        if (requestLine == null)
                            continue;

                        string path = requestLine.Split(' ')[1];
                        int query = path.IndexOf('?');
                        paths.Enqueue(query >= 0 ? path[..query] : path);

                        string? header;

                        while (!string.IsNullOrEmpty(header = reader.ReadLine()))
                        {
                            if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                                sawAuthorization = true;
                        }

                        byte[] body = Encoding.UTF8.GetBytes(@"{""ends_at"":""2030-01-01T00:00:00+00:00"",""backgrounds"":[]}");
                        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

                        stream.Write(head);
                        stream.Write(body);
                        stream.Flush();
                    }
                }
            }

            public void Dispose()
            {
                disposed = true;
                listener.Stop();
            }
        }
    }
}
