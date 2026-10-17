// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
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
            string body = Encoding.UTF8.GetString(payload.ToArray());

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
            string body = Encoding.UTF8.GetString(payload.ToArray());

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

            using var stream = new MemoryStream(payload.ToArray());
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
                Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()));
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
                Assert.That(second.ToArray(), Is.EqualTo(first.ToArray()));
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

        /// <summary>
        /// Sizes past 2 GB slice with long arithmetic: the chunk count and each chunk's length still fit an int,
        /// the offsets do not, and the last chunk carries exactly the remainder.
        /// </summary>
        [Test]
        public void PayloadsPastTwoGigabytesSliceExactly()
        {
            const long two_gb = 1L << 31;
            const long five_gb = 5L * 1024 * 1024 * 1024;

            Assert.Multiple(() =>
            {
                Assert.That(ChunkedPackageUpload.TotalChunks(two_gb, chunk_bytes), Is.EqualTo(262144));
                Assert.That(ChunkedPackageUpload.ChunkLength(262143, two_gb, chunk_bytes), Is.EqualTo(chunk_bytes), "2 GB divides evenly");

                Assert.That(ChunkedPackageUpload.TotalChunks(two_gb + 100, chunk_bytes), Is.EqualTo(262145));
                Assert.That(ChunkedPackageUpload.ChunkOffset(262144, chunk_bytes), Is.EqualTo(two_gb));
                Assert.That(ChunkedPackageUpload.ChunkLength(262144, two_gb + 100, chunk_bytes), Is.EqualTo(100));

                Assert.That(ChunkedPackageUpload.TotalChunks(five_gb + 1, chunk_bytes), Is.EqualTo(655361));
                Assert.That(ChunkedPackageUpload.ChunkOffset(655360, chunk_bytes), Is.EqualTo(five_gb));
                Assert.That(ChunkedPackageUpload.ChunkLength(655359, five_gb + 1, chunk_bytes), Is.EqualTo(chunk_bytes));
                Assert.That(ChunkedPackageUpload.ChunkLength(655360, five_gb + 1, chunk_bytes), Is.EqualTo(1));
                Assert.Throws<ArgumentOutOfRangeException>(() => ChunkedPackageUpload.ChunkLength(655361, five_gb + 1, chunk_bytes));

                // a chunk count the session protocol cannot index is refused rather than wrapped.
                Assert.Throws<OverflowException>(() => ChunkedPackageUpload.TotalChunks(long.MaxValue, 1));
            });
        }

        /// <summary>
        /// The payload is the exact bytes the BCL's multipart content serialises to, which is what the server
        /// reassembles and parses and what session creation hashes: building it from segments must not move a byte.
        /// </summary>
        [Test]
        public async Task PayloadBytesAreWhatTheMultipartContentSerialises()
        {
            byte[] package = Encoding.ASCII.GetBytes("PK-not-really-a-zip");
            var changed = new Dictionary<string, byte[]>
            {
                { "beatmap.typb", Encoding.ASCII.GetBytes("[General]") },
                { "audio.mp3", Encoding.ASCII.GetBytes("ID3") },
            };
            string[] deleted = { "old-background.jpg" };

            var full = await ChunkedPackageUpload.BuildFullPayloadAsync(package).ConfigureAwait(false);
            var patch = await ChunkedPackageUpload.BuildPatchPayloadAsync(changed, deleted).ConfigureAwait(false);

            using var fullContent = new MultipartFormDataContent(boundaryOf(full));
            fullContent.Add(octets(package), @"beatmapArchive", @"package.osz");

            using var patchContent = new MultipartFormDataContent(boundaryOf(patch));
            foreach ((string filename, byte[] contents) in changed)
                patchContent.Add(octets(contents), @"filesChanged", filename);
            patchContent.Add(new StringContent(deleted[0]), @"filesDeleted");

            byte[] fullExpected = await fullContent.ReadAsByteArrayAsync().ConfigureAwait(false);
            byte[] patchExpected = await patchContent.ReadAsByteArrayAsync().ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(full.ToArray(), Is.EqualTo(fullExpected));
                Assert.That(full.Length, Is.EqualTo(fullExpected.LongLength));
                Assert.That(full.ContentType, Is.EqualTo(fullContent.Headers.ContentType!.ToString()));

                Assert.That(patch.ToArray(), Is.EqualTo(patchExpected));
                Assert.That(patch.Length, Is.EqualTo(patchExpected.LongLength));
                Assert.That(patch.ContentType, Is.EqualTo(patchContent.Headers.ContentType!.ToString()));
            });

            static ByteArrayContent octets(byte[] contents)
            {
                var part = new ByteArrayContent(contents);
                part.Headers.ContentType = new MediaTypeHeaderValue(@"application/octet-stream");
                return part;
            }

            static string boundaryOf(ChunkedPackageUpload.UploadPayload payload)
                => MediaTypeHeaderValue.Parse(payload.ContentType).Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        }

        /// <summary>
        /// Chunks are cut from the segments only when sent, and every chunk size, including ones that straddle a
        /// segment boundary, reads back the same bytes as the whole payload.
        /// </summary>
        [Test]
        public async Task ChunksReadAcrossSegmentsMatchTheWholePayload()
        {
            var changed = new Dictionary<string, byte[]>
            {
                { "beatmap.typb", Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray() },
                { "audio.mp3", Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray() },
            };

            var payload = await ChunkedPackageUpload.BuildPatchPayloadAsync(changed, new[] { "gone.jpg" }).ConfigureAwait(false);
            byte[] whole = payload.ToArray();

            foreach (int size in new[] { 1, 7, 64, 1000, chunk_bytes, whole.Length })
            {
                var reassembled = new List<byte>();

                for (int i = 0; i < ChunkedPackageUpload.TotalChunks(payload.Length, size); i++)
                    reassembled.AddRange(payload.ReadChunk(ChunkedPackageUpload.ChunkOffset(i, size), ChunkedPackageUpload.ChunkLength(i, payload.Length, size)));

                Assert.That(reassembled, Is.EqualTo(whole), $"chunk size {size}");
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => payload.ReadChunk(payload.Length - 1, 2));
        }

        /// <summary>
        /// The payload refers to the package the caller already holds instead of taking another copy of it, which is
        /// the memory a large package used to cost a third time over.
        /// </summary>
        [Test]
        public async Task PayloadSharesThePackageBuffer()
        {
            byte[] package = Encoding.ASCII.GetBytes("PK-not-really-a-zip");

            var payload = await ChunkedPackageUpload.BuildFullPayloadAsync(package).ConfigureAwait(false);

            package[0] = (byte)'Q';

            Assert.That(Encoding.ASCII.GetString(payload.ToArray()), Does.Contain("QK-not-really-a-zip"));
        }

        /// <summary>
        /// The chunked fallback's payload is only built when it is first asked for, and only once however often it is
        /// asked for after that; a failed build stays failed.
        /// </summary>
        [Test]
        public async Task DeferredPayloadBuildsOnFirstUseOnly()
        {
            int builds = 0;

            var deferred = new ChunkedPackageUpload.DeferredPayload(() =>
            {
                Interlocked.Increment(ref builds);
                return ChunkedPackageUpload.BuildFullPayloadAsync(Encoding.ASCII.GetBytes("package"));
            });

            Assert.Multiple(() =>
            {
                Assert.That(deferred.Started, Is.False);
                Assert.That(builds, Is.Zero, "nothing is assembled while the direct upload has not failed");
            });

            var first = await deferred.GetAsync().ConfigureAwait(false);
            var second = await deferred.GetAsync().ConfigureAwait(false);

            Assert.Multiple(() =>
            {
                Assert.That(deferred.Started, Is.True);
                Assert.That(builds, Is.EqualTo(1));
                Assert.That(second, Is.SameAs(first), "a resumed session must send the very same payload");
            });

            var failing = new ChunkedPackageUpload.DeferredPayload(() => throw new InvalidOperationException("export gone"));

            Assert.ThrowsAsync<InvalidOperationException>(() => failing.GetAsync());
            Assert.ThrowsAsync<InvalidOperationException>(() => failing.GetAsync());
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
