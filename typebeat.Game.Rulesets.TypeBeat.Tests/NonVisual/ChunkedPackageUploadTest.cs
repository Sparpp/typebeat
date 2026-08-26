// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Extensions;
using osu.Framework.IO.Network;
using typebeat.Game.Online.API;
using typebeat.Game.Online.API.Requests;
using typebeat.Game.Screens.Edit.Submission;

namespace typebeat.Game.Rulesets.TypeBeat.Tests.NonVisual
{
    /// <summary>
    /// Covers the pure parts of the chunked upload fallback: how a payload is sliced, and how the payload
    /// itself is assembled. The observed failure was a 37714 byte PATCH black-holed at 20099 bytes every
    /// time, so the slicing has to hold for that exact size as well as for the tidy ones.
    /// </summary>
    [TestFixture]
    public class ChunkedPackageUploadTest
    {
        private const int chunk_bytes = 8192;

        [Test]
        public void ExactMultipleSplitsEvenly()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ChunkedPackageUpload.TotalChunks(2 * chunk_bytes, chunk_bytes), Is.EqualTo(2));

                Assert.That(ChunkedPackageUpload.ChunkOffset(0, chunk_bytes), Is.EqualTo(0));
                Assert.That(ChunkedPackageUpload.ChunkOffset(1, chunk_bytes), Is.EqualTo(chunk_bytes));

                Assert.That(ChunkedPackageUpload.ChunkLength(0, 2 * chunk_bytes, chunk_bytes), Is.EqualTo(chunk_bytes));
                Assert.That(ChunkedPackageUpload.ChunkLength(1, 2 * chunk_bytes, chunk_bytes), Is.EqualTo(chunk_bytes));
            });
        }

        [Test]
        public void RemainderLandsInTheLastChunk()
        {
            // the size that actually failed: 4 full chunks and a 4946 byte tail.
            const int total = 37714;

            Assert.Multiple(() =>
            {
                Assert.That(ChunkedPackageUpload.TotalChunks(total, chunk_bytes), Is.EqualTo(5));
                Assert.That(ChunkedPackageUpload.ChunkOffset(4, chunk_bytes), Is.EqualTo(32768));
                Assert.That(ChunkedPackageUpload.ChunkLength(3, total, chunk_bytes), Is.EqualTo(chunk_bytes));
                Assert.That(ChunkedPackageUpload.ChunkLength(4, total, chunk_bytes), Is.EqualTo(4946));
            });
        }

        [Test]
        public void SlicesCoverThePayloadExactlyOnce()
        {
            foreach (int total in new[] { 1, chunk_bytes - 1, chunk_bytes, chunk_bytes + 1, 20099, 37714 })
            {
                int chunks = ChunkedPackageUpload.TotalChunks(total, chunk_bytes);
                int covered = 0;

                for (int i = 0; i < chunks; i++)
                {
                    Assert.That(ChunkedPackageUpload.ChunkOffset(i, chunk_bytes), Is.EqualTo(covered), $"offset of chunk {i} of {total}");
                    covered += ChunkedPackageUpload.ChunkLength(i, total, chunk_bytes);
                }

                Assert.That(covered, Is.EqualTo(total), $"coverage of {total}");
            }
        }

        [Test]
        public void EmptyPayloadHasNoChunks()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ChunkedPackageUpload.TotalChunks(0, chunk_bytes), Is.EqualTo(0));

                // and no chunk of it can be asked for, rather than an empty one being handed back.
                Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedPackageUpload.ChunkLength(0, 0, chunk_bytes));
                Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedPackageUpload.ChunkLength(1, chunk_bytes, chunk_bytes));
            });
        }

        [Test]
        public void UnusableChunkSizesAreRefused()
        {
            Assert.Multiple(() =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedPackageUpload.TotalChunks(100, 0));
                Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedPackageUpload.TotalChunks(100, -1));
                Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedPackageUpload.TotalChunks(-1, chunk_bytes));
                Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedPackageUpload.ChunkOffset(-1, chunk_bytes));
            });
        }

        [Test]
        public async Task FullPayloadCarriesTheArchivePart()
        {
            byte[] package = Encoding.ASCII.GetBytes("PK-not-really-a-zip");

            var payload = await ChunkedPackageUpload.BuildFullPayloadAsync(package).ConfigureAwait(false);
            string body = Encoding.UTF8.GetString(payload.Bytes);

            Assert.Multiple(() =>
            {
                Assert.That(payload.Kind, Is.EqualTo(CreateUploadSessionRequest.KIND_FULL));

                // the boundary is generated per payload, so the server can only know it from the content type.
                Assert.That(payload.ContentType, Does.StartWith("multipart/form-data; boundary="));

                Assert.That(body, Does.Contain("beatmapArchive"));
                Assert.That(body, Does.Contain("package.osz"));
                Assert.That(body, Does.Contain("application/octet-stream"));
                Assert.That(body, Does.Contain("PK-not-really-a-zip"));
            });
        }

        [Test]
        public async Task PatchPayloadCarriesChangedAndDeletedFiles()
        {
            var changed = new Dictionary<string, byte[]>
            {
                { "beatmap.typb", Encoding.ASCII.GetBytes("[General]") },
                { "audio.mp3", Encoding.ASCII.GetBytes("ID3") },
            };

            var payload = await ChunkedPackageUpload.BuildPatchPayloadAsync(changed, new[] { "old-background.jpg" }).ConfigureAwait(false);
            string body = Encoding.UTF8.GetString(payload.Bytes);

            Assert.Multiple(() =>
            {
                Assert.That(payload.Kind, Is.EqualTo(CreateUploadSessionRequest.KIND_PATCH));
                Assert.That(payload.ContentType, Does.StartWith("multipart/form-data; boundary="));

                Assert.That(body, Does.Contain("filesChanged"));
                Assert.That(body, Does.Contain("filesDeleted"));

                // the multipart filename is the archive path, which is how the server keys the update.
                Assert.That(body, Does.Contain("beatmap.typb"));
                Assert.That(body, Does.Contain("audio.mp3"));
                Assert.That(body, Does.Contain("old-background.jpg"));
            });
        }

        [Test]
        public async Task PayloadHashIsLowercaseHexOfTheAssembledBytes()
        {
            var payload = await ChunkedPackageUpload.BuildFullPayloadAsync(Encoding.ASCII.GetBytes("package")).ConfigureAwait(false);

            using var stream = new MemoryStream(payload.Bytes);
            string expected = stream.ComputeSHA2Hash();

            Assert.Multiple(() =>
            {
                Assert.That(payload.Sha256, Is.EqualTo(expected));
                Assert.That(payload.Sha256, Has.Length.EqualTo(64));
                Assert.That(payload.Sha256, Does.Match("^[0-9a-f]{64}$"));
            });
        }

        [Test]
        public async Task PayloadsWithDifferentContentHashDifferently()
        {
            // session creation is idempotent on the hash, so two different packages must never collide
            // onto one session, and the same package must keep hashing the same way.
            var first = await ChunkedPackageUpload.BuildFullPayloadAsync(Encoding.ASCII.GetBytes("one")).ConfigureAwait(false);
            var second = await ChunkedPackageUpload.BuildFullPayloadAsync(Encoding.ASCII.GetBytes("two")).ConfigureAwait(false);

            Assert.That(first.Sha256, Is.Not.EqualTo(second.Sha256));
        }

        [Test]
        public async Task IdenticalFullPayloadsAreByteIdentical()
        {
            // the whole point of deriving the boundary from the content: two builds of the same archive
            // have to be the same bytes, or session creation (idempotent on the hash) never recognises a
            // session left half-uploaded by a previous run and resume across runs cannot work at all.
            byte[] package = Encoding.ASCII.GetBytes("PK-not-really-a-zip");

            var first = await ChunkedPackageUpload.BuildFullPayloadAsync(package).ConfigureAwait(false);
            var second = await ChunkedPackageUpload.BuildFullPayloadAsync(package.ToArray()).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(second.Bytes, Is.EqualTo(first.Bytes));
                Assert.That(second.Sha256, Is.EqualTo(first.Sha256));
                Assert.That(second.ContentType, Is.EqualTo(first.ContentType));
            });
        }

        [Test]
        public async Task IdenticalPatchPayloadsAreByteIdentical()
        {
            var first = await buildPatch().ConfigureAwait(false);
            var second = await buildPatch().ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(second.Bytes, Is.EqualTo(first.Bytes));
                Assert.That(second.Sha256, Is.EqualTo(first.Sha256));
                Assert.That(second.ContentType, Is.EqualTo(first.ContentType));
            });

            static Task<ChunkedPackageUpload.UploadPayload> buildPatch()
            {
                var changed = new Dictionary<string, byte[]>
                {
                    { "beatmap.typb", Encoding.ASCII.GetBytes("[General]") },
                    { "audio.mp3", Encoding.ASCII.GetBytes("ID3") },
                };

                return ChunkedPackageUpload.BuildPatchPayloadAsync(changed, new[] { "old-background.jpg" });
            }
        }

        [Test]
        public async Task DifferentContentBuildsDifferentBoundaries()
        {
            // a shared boundary across different payloads would be harmless on the wire but would mean
            // the boundary is not derived from the content, which is what makes the bytes reproducible.
            var first = await ChunkedPackageUpload.BuildFullPayloadAsync(Encoding.ASCII.GetBytes("one")).ConfigureAwait(false);
            var second = await ChunkedPackageUpload.BuildFullPayloadAsync(Encoding.ASCII.GetBytes("two")).ConfigureAwait(false);

            var patchFirst = await ChunkedPackageUpload.BuildPatchPayloadAsync(
                new Dictionary<string, byte[]> { { "a.txt", Encoding.ASCII.GetBytes("x") } }, Array.Empty<string>()).ConfigureAwait(false);
            var patchSecond = await ChunkedPackageUpload.BuildPatchPayloadAsync(
                new Dictionary<string, byte[]> { { "a.txt", Encoding.ASCII.GetBytes("x") } }, new[] { "gone.jpg" }).ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(second.ContentType, Is.Not.EqualTo(first.ContentType));

                // a deletion with no changed file touched is still a different payload.
                Assert.That(patchSecond.ContentType, Is.Not.EqualTo(patchFirst.ContentType));
                Assert.That(patchSecond.Sha256, Is.Not.EqualTo(patchFirst.Sha256));
            });
        }

        [Test]
        public async Task BoundaryStaysWithinTheAllowedShape()
        {
            var payload = await ChunkedPackageUpload.BuildFullPayloadAsync(Encoding.ASCII.GetBytes("package")).ConfigureAwait(false);

            var contentType = MediaTypeHeaderValue.Parse(payload.ContentType);
            string boundary = contentType.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');

            Assert.Multiple(() =>
            {
                // RFC 2046 caps a boundary at 70 characters from a restricted set; lowercase hex with an
                // ASCII prefix is inside it, and the length has to stay inside it as the prefix changes.
                Assert.That(boundary, Has.Length.LessThanOrEqualTo(70));
                Assert.That(boundary, Does.Match("^typebeat-[0-9a-f]{40}$"));
            });
        }

        [Test]
        public void RemainingChunksDropsWhatTheServerHolds()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ChunkedPackageUpload.RemainingChunks(5, new[] { 0, 2 }), Is.EqualTo(new[] { 1, 3, 4 }));

                // ascending regardless of how the server ordered its answer.
                Assert.That(ChunkedPackageUpload.RemainingChunks(5, new[] { 4, 1, 3 }), Is.EqualTo(new[] { 0, 2 }));

                Assert.That(ChunkedPackageUpload.RemainingChunks(3, Array.Empty<int>()), Is.EqualTo(new[] { 0, 1, 2 }));
                Assert.That(ChunkedPackageUpload.RemainingChunks(3, new[] { 0, 1, 2 }), Is.Empty);
                Assert.That(ChunkedPackageUpload.RemainingChunks(0, new[] { 0 }), Is.Empty);
            });
        }

        [Test]
        public void RemainingChunksIgnoresIndexesItCannotAnswerFor()
        {
            // an index outside the local slicing cannot be mapped onto the payload, so it is dropped
            // rather than trusted. Duplicates are harmless for the same reason.
            Assert.Multiple(() =>
            {
                Assert.That(ChunkedPackageUpload.RemainingChunks(3, new[] { -1, 7, 1 }), Is.EqualTo(new[] { 0, 2 }));
                Assert.That(ChunkedPackageUpload.RemainingChunks(3, new[] { 1, 1, 1 }), Is.EqualTo(new[] { 0, 2 }));
                Assert.That(ChunkedPackageUpload.RemainingChunks(3, null), Is.EqualTo(new[] { 0, 1, 2 }));
            });

            Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedPackageUpload.RemainingChunks(-1, Array.Empty<int>()));
        }

        /// <summary>
        /// Pins the reconciliation the chunk retry runs: a chunk whose RESPONSE was lost is already held
        /// by the server, so re-fetching the session status has to skip it instead of re-sending it.
        /// </summary>
        [Test]
        public void ReconciliationSkipsAChunkWhoseResponseWasLost()
        {
            const int total = 5;

            // the client believes 2 failed; the server stored it and answered into a black hole.
            var remaining = ChunkedPackageUpload.RemainingChunks(total, new[] { 0, 1, 2 });

            Assert.Multiple(() =>
            {
                Assert.That(remaining, Is.EqualTo(new[] { 3, 4 }));
                Assert.That(remaining, Does.Not.Contain(2));

                // and a chunk that genuinely never arrived stays at the front, so its attempt cap still runs out.
                Assert.That(ChunkedPackageUpload.RemainingChunks(total, new[] { 0, 1 })[0], Is.EqualTo(2));
            });
        }

        /// <summary>
        /// Every session request must ask for its own connection (backlog 201). The ceiling this
        /// protocol exists for is per CONNECTION, and field logs proved the pooled client connection
        /// carried create + chunk after chunk until it crossed that ceiling mid-chunk,
        /// deterministically, every ~2.5 chunks: the origin's own close on chunk responses only
        /// bounded the proxy-to-origin hop. The client asking is what makes the proxy echo the close
        /// and .NET retire the socket, so the byte budget becomes per request.
        /// </summary>
        [Test]
        public void EverySessionRequestAsksForItsOwnConnection()
        {
            var api = new DummyAPIAccess();
            api.Endpoints.BeatmapSubmissionServiceUrl = @"http://localhost/bss";

            var requests = new APIRequest[]
            {
                new CreateUploadSessionRequest(7, CreateUploadSessionRequest.KIND_FULL, @"multipart/form-data; boundary=x", 100, new string('a', 64)),
                new UploadSessionChunkRequest(new string('b', 32), 0, new byte[16]),
                new GetUploadSessionRequest(new string('b', 32)),
                new CompleteUploadSessionRequest(new string('b', 32)),
            };

            Assert.Multiple(() =>
            {
                foreach (var request in requests)
                {
                    request.AttachAPI(api);
                    Assert.That(headersOf(createWebRequest(request)), Does.ContainKey(@"Connection").WithValue(@"close"), request.GetType().Name);
                }
            });
        }

        /// <summary>
        /// The session requests' timeouts are not transfer budgets, they are how long the flow waits
        /// before deciding a request is gone, which is the only way the black hole this protocol exists
        /// for can be discovered at all. Backlog 206 cut the chunk's from 30s to 5s on that reading: a
        /// healthy 8KB chunk on its own fresh connection answers in well under a second, so the other 25
        /// seconds were pure latency on every probe, once per failed chunk. Gateway 5xx answers are
        /// unaffected either way, since an edge answering for an absent origin answers immediately.
        /// </summary>
        [Test]
        public void SessionRequestTimeoutsMatchWhatTheyAreWaitingFor()
        {
            var api = new DummyAPIAccess();
            api.Endpoints.BeatmapSubmissionServiceUrl = @"http://localhost/bss";

            Assert.Multiple(() =>
            {
                Assert.That(timeoutOf(api, new UploadSessionChunkRequest(new string('b', 32), 0, new byte[16])), Is.EqualTo(5_000));

                // longer than a chunk deliberately: this answer grows with the session (905 indices for
                // the upload this was built for) and giving up on it early costs the reconcile itself,
                // which is what stops a lost 204 from burning a chunk's attempt cap.
                Assert.That(timeoutOf(api, new GetUploadSessionRequest(new string('b', 32))), Is.EqualTo(10_000));

                // the outlier stays an outlier: completing is the server assembling and ingesting the
                // whole package, so its wait is server work rather than dead air.
                Assert.That(timeoutOf(api, new CompleteUploadSessionRequest(new string('b', 32))), Is.EqualTo(600_000));
            });
        }

        private static int timeoutOf(DummyAPIAccess api, APIRequest request)
        {
            request.AttachAPI(api);
            return createWebRequest(request).Timeout;
        }

        /// <summary>
        /// <c>CreateWebRequest</c> is protected all the way up the hierarchy and the built request's
        /// header set is private, so both ends of this pin need reflection (the same seam
        /// <c>OnlineReplayWireTest</c> uses and documents). Invoking the base <see cref="System.Reflection.MethodInfo"/>
        /// still dispatches virtually, so this observes exactly what <c>Perform</c> would build.
        /// </summary>
        private static WebRequest createWebRequest(APIRequest request)
        {
            var method = typeof(APIRequest).GetMethod(@"CreateWebRequest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "APIRequest.CreateWebRequest has been renamed; this pin needs updating.");
            return (WebRequest)method!.Invoke(request, Array.Empty<object>())!;
        }

        private static IDictionary<string, string> headersOf(WebRequest webRequest)
        {
            var field = typeof(WebRequest).GetField(@"headers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "WebRequest.headers has been renamed; this pin needs updating.");
            return (IDictionary<string, string>)field!.GetValue(webRequest)!;
        }
    }
}
