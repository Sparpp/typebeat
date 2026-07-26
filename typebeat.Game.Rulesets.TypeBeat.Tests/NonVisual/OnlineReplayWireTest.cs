// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using osu.Framework.IO.Network;
using typebeat.Game.Beatmaps;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Online.API.Requests.Responses;
using typebeat.Game.Replays;
using typebeat.Game.Scoring;
using typebeat.Game.Scoring.Legacy;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Pins the online replay wire contract the client is built against:
    /// <list type="bullet">
    /// <item><c>PUT /api/v2/scores/{scoreId}/replay</c>, bearer auth, raw .osr as <c>application/octet-stream</c>, 5MB cap.</item>
    /// <item><c>GET /api/v2/scores/{scoreId}/replay</c>, raw .osr bytes.</item>
    /// <item>Score rows carry an additive boolean <c>has_replay</c>; an old server omits it, and absent must read as false.</item>
    /// </list>
    /// Both routes changed shape once already (download used to be <c>/download</c>), so the paths are
    /// asserted literally rather than through the protected members that build them.
    /// </summary>
    [TestFixture]
    public class OnlineReplayWireTest
    {
        private static readonly byte[] sample_replay = { 0x00, 0x01, 0x02, 0x03 };

        [Test]
        public void UploadTargetsTheReplayRoute()
        {
            Assert.That(UploadReplayRequest.TargetFor(4242), Is.EqualTo("scores/4242/replay"));
        }

        [Test]
        public void DownloadTargetsTheSameRoute()
        {
            // Upload and download must address the same resource, or a replay is written where nothing reads it.
            Assert.That(DownloadReplayRequest.TargetFor(4242), Is.EqualTo(UploadReplayRequest.TargetFor(4242)));
        }

        [Test]
        public void UploadRequestIsAPutOfRawReplayBytes()
        {
            var request = new UploadReplayRequest(4242, sample_replay);
            request.AttachAPI(new DummyAPIAccess());

            var webRequest = createWebRequest(request);

            Assert.Multiple(() =>
            {
                Assert.That(webRequest.Method, Is.EqualTo(HttpMethod.Put));
                Assert.That(webRequest.ContentType, Is.EqualTo("application/octet-stream"));
                Assert.That(webRequest.Url, Is.EqualTo("http://localhost/api/v2/scores/4242/replay"));
                Assert.That(request.ReplayData, Is.EqualTo(sample_replay));
                Assert.That(request.ScoreId, Is.EqualTo(4242));
            });
        }

        [Test]
        public void DownloadRequestFetchesOsrForTheOnlineId()
        {
            var request = new DownloadReplayRequest(new ScoreInfo { OnlineID = 4242 });
            request.AttachAPI(new DummyAPIAccess());

            var webRequest = createWebRequest(request);

            Assert.Multiple(() =>
            {
                Assert.That(webRequest.Method, Is.EqualTo(HttpMethod.Get));
                Assert.That(webRequest.Url, Is.EqualTo("http://localhost/api/v2/scores/4242/replay"));
            });
        }

        /// <summary>
        /// The cap exists to stop a doomed request leaving the machine; the server rejects above 5MB.
        /// </summary>
        [Test]
        public void UploadRefusesPayloadsTheServerWouldReject()
        {
            Assert.That(UploadReplayRequest.MAX_REPLAY_BYTES, Is.EqualTo(5 * 1024 * 1024));

            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentException>(() => new UploadReplayRequest(1, new byte[UploadReplayRequest.MAX_REPLAY_BYTES + 1]));
                Assert.Throws<ArgumentException>(() => new UploadReplayRequest(1, Array.Empty<byte>()));
                Assert.Throws<ArgumentOutOfRangeException>(() => new UploadReplayRequest(0, sample_replay));
                Assert.Throws<ArgumentOutOfRangeException>(() => new UploadReplayRequest(-1, sample_replay));
            });

            // Exactly at the cap is fine.
            Assert.DoesNotThrow(() => new UploadReplayRequest(1, new byte[UploadReplayRequest.MAX_REPLAY_BYTES]));
        }

        /// <summary>
        /// <c>has_replay</c> is additive: a server that predates it simply omits the key, and the client
        /// must read that as "no replay" rather than throwing or defaulting to true.
        /// </summary>
        [TestCase(@"{""id"":7}", false)]
        [TestCase(@"{""id"":7,""has_replay"":false}", false)]
        [TestCase(@"{""id"":7,""has_replay"":true}", true)]
        [TestCase(@"{""id"":7,""has_replay"":true,""some_future_field"":42}", true)]
        public void HasReplayParsesWithAbsentMeaningFalse(string body, bool expected)
        {
            var score = JsonConvert.DeserializeObject<SoloScoreInfo>(body);

            Assert.That(score, Is.Not.Null);
            Assert.That(score!.HasReplay, Is.EqualTo(expected));
        }

        /// <summary>
        /// The flag has to survive the hop into the realm-shaped <see cref="ScoreInfo"/> the leaderboard
        /// rows and the replay button actually read.
        /// </summary>
        [TestCase(@"{""id"":7}", false)]
        [TestCase(@"{""id"":7,""has_replay"":true}", true)]
        public void HasReplayReachesTheScoreInfoTheUiReads(string body, bool expected)
        {
            var score = JsonConvert.DeserializeObject<SoloScoreInfo>(body)!.ToScoreInfo(Array.Empty<Rulesets.Mods.Mod>());

            Assert.That(score.HasOnlineReplay, Is.EqualTo(expected));
            Assert.That(score.OnlineID, Is.EqualTo(7));
        }

        /// <summary>
        /// The server's PUT does a structural sanity check on the payload before storing it: byte 0 is
        /// the ruleset id and must be at most 3, and byte 5 must be an osu-string marker (0x0b for a
        /// present string, 0x00 for a null one) because that is where the beatmap MD5 begins in the
        /// legacy layout. This pins that a real <see cref="LegacyScoreEncoder"/> payload satisfies it,
        /// and by extension that the upload must send the .osr bytes raw: any wrapping, compression or
        /// re-encoding on the way out would fail the check server side.
        /// </summary>
        [Test]
        public void EncodedReplayPassesTheServerStructuralCheck()
        {
            byte[] osr = encodeSampleReplay();

            Assert.Multiple(() =>
            {
                Assert.That(osr.Length, Is.GreaterThan(5));
                Assert.That(osr[0], Is.EqualTo(0), "TypeBeat's LegacyID is 0, which is what lands in the ruleset byte.");
                Assert.That(osr[0], Is.LessThanOrEqualTo(3));
                Assert.That(osr[5], Is.AnyOf((byte)0x0b, (byte)0x00));
            });
        }

        /// <summary>
        /// What is uploaded has to be the same array that is handed to the local importer, or the
        /// server's copy stops being the bit-exact replay the desktop client plays back.
        /// </summary>
        [Test]
        public void UploadCarriesThePayloadUnmodified()
        {
            byte[] osr = encodeSampleReplay();

            var request = new UploadReplayRequest(4242, osr);

            Assert.That(request.ReplayData, Is.SameAs(osr));
        }

        private static byte[] encodeSampleReplay()
        {
            var ruleset = new TypeBeatRuleset();

            var score = new Score
            {
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = ruleset.RulesetInfo,
                    BeatmapInfo = new BeatmapInfo { MD5Hash = new string('a', 32) },
                    User = new APIUser { Username = "Pointless" },
                    Date = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
                },
                Replay = new Replay(),
            };

            using (var stream = new MemoryStream())
            {
                // No frames, so no beatmap is needed to convert them; the header is what matters here.
                new LegacyScoreEncoder(score, null).Encode(stream);
                return stream.ToArray();
            }
        }

        /// <remarks>
        /// <c>CreateWebRequest</c> is protected all the way up the hierarchy, so there is no public seam
        /// to assert the method and content type on. Invoking the base <see cref="MethodInfo"/> still
        /// dispatches virtually, so this observes exactly what <c>Perform</c> would build.
        /// </remarks>
        private static WebRequest createWebRequest(APIRequest request)
        {
            var method = typeof(APIRequest).GetMethod(@"CreateWebRequest", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null, "APIRequest.CreateWebRequest has been renamed; this pin needs updating.");

            return (WebRequest)method!.Invoke(request, Array.Empty<object>())!;
        }
    }
}
