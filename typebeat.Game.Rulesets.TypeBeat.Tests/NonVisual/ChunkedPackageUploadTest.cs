// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Framework.Extensions;
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
    }
}
